using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

namespace CadAiDesigner
{
    internal class TakeoffRequest
    {
        public HashSet<ObjectId> Only;          // null = whole model space
        public HashSet<string> Layers;          // null = all layers (case-insensitive)
        public bool Texts;
        public string SaveRevision, CompareTo, CompareDwg;
        public string[] CompareRevisions;
        public bool Export = true;
    }

    /// <summary>Read-only quantity takeoff (metraj) of model space. Lengths/areas in metres via INSUNITS.</summary>
    internal static class Takeoff
    {
        static readonly Regex GroupAttr = new Regex(@"^(TAG|TIP|TYPE|TUR|TÜR|OLCU|ÖLÇÜ|OLCÜ|EBAT|BOYUT|SIZE|MODEL|GUC|GÜÇ|POWER|KESIT|KESİT|CINS|CİNS|MARKA|AMPER|KW|W|IP)$", RegexOptions.IgnoreCase);
        static readonly string[] LengthNames = { "Length", "Uzunluk", "Boy", "L", "Distance", "Distance1", "Mesafe", "Len" };
        static readonly string[] WidthNames = { "Width", "Genislik", "Genişlik", "En", "W", "Distance2" };

        public static double UnitFactor(Database db, List<string> notes)
        {
            CizimBirimi.Sonuc s = CizimBirimi.Peek(db);
            if (s != null && s.Oto && notes != null && !string.IsNullOrEmpty(s.Not))
                notes.Add("Birim: " + s.Ad + " (oto, " + s.Not + ").");
            double factor = s != null && s.Factor > 0 ? s.Factor : 1;
            return factor / 1000.0;
        }

        class Acc { public string Kat, Kalem, Birim, Katman; public double Miktar; public int Adet; }

        static string R(double v) { return Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture); }

        static string EffName(Transaction tx, BlockReference br)
        {
            try
            {
                ObjectId id = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                return ((BlockTableRecord)tx.GetObject(id, OpenMode.ForRead)).Name;
            }
            catch (System.Exception) { return br.Name; }
        }

        static DynamicBlockReferenceProperty Prop(BlockReference br, string[] names)
        {
            if (!br.IsDynamicBlock) return null;
            foreach (DynamicBlockReferenceProperty p in br.DynamicBlockReferencePropertyCollection)
                if (names.Any(n => string.Equals(n, p.PropertyName, StringComparison.OrdinalIgnoreCase))) return p;
            return null;
        }

        public static List<Dictionary<string, object>> Compute(Database db, TakeoffRequest rq, List<string> notes, Transaction ext = null)
        {
            double f = UnitFactor(db, notes), f2 = f * f;
            Dictionary<string, Acc> acc = new Dictionary<string, Acc>();
            Dictionary<ObjectId, double[]> defSize = new Dictionary<ObjectId, double[]>();
            Action<string, string, string, string, double, int> add = (kat, kalem, birim, katman, q, n) =>
            {
                string k = kat + "|" + kalem + "|" + katman;
                Acc a;
                if (!acc.TryGetValue(k, out a)) acc[k] = a = new Acc { Kat = kat, Kalem = kalem, Birim = birim, Katman = katman };
                a.Miktar += q; a.Adet += n;
            };
            int skipped = 0;
            FitConfig fcfg; try { fcfg = FitConfig.Load(); } catch (System.Exception ex) { fcfg = new FitConfig(); notes.Add("Not: fittings.json okunamadı, varsayılanlar kullanıldı (" + ex.Message + ")."); }
            Transaction own = ext == null ? db.TransactionManager.StartOpenCloseTransaction() : null;
            Transaction tx = ext ?? own;
            try
            {
                BlockTable bt = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tx.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (rq.Only != null && !rq.Only.Contains(id)) continue;
                    Entity e = tx.GetObject(id, OpenMode.ForRead) as Entity;
                    if (e == null) continue;
                    if (rq.Layers != null && !rq.Layers.Contains(e.Layer)) continue;
                    try
                    {
                        BlockReference br = e as BlockReference;
                        if (br != null)
                        {
                            string name = EffName(tx, br);
                            if (name.StartsWith("*")) continue;
                            if (TrayKesif.Try(tx, br, name, fcfg, add, f)) continue;
                            List<string> grp = new List<string>();
                            DynamicBlockReferenceProperty lp = Prop(br, LengthNames), wp = Prop(br, WidthNames);
                            if (br.IsDynamicBlock)
                                foreach (DynamicBlockReferenceProperty p in br.DynamicBlockReferencePropertyCollection)
                                    if (p != lp && p.PropertyName != "Origin" && (p == wp || p.UnitsType == DynamicBlockReferencePropertyUnitsType.NoUnits))
                                    {
                                        string v = p.Value is double ? R((double)p.Value) : Convert.ToString(p.Value, CultureInfo.InvariantCulture);
                                        if (!string.IsNullOrEmpty(v)) grp.Add(p.PropertyName + "=" + v);
                                    }
                            foreach (ObjectId aid in br.AttributeCollection)
                            {
                                AttributeReference ar = tx.GetObject(aid, OpenMode.ForRead) as AttributeReference;
                                if (ar != null && GroupAttr.IsMatch(ar.Tag) && !string.IsNullOrWhiteSpace(ar.TextString)) grp.Add(ar.Tag + "=" + ar.TextString.Trim());
                            }
                            bool tray = lp != null || Regex.IsMatch(name, "tava|tray|kanal|trunk", RegexOptions.IgnoreCase);
                            string label = name + (grp.Count > 0 ? " (" + string.Join(", ", grp) + ")" : "");
                            if (tray)
                            {
                                double len;
                                if (lp != null && lp.Value is double) len = (double)lp.Value;
                                else
                                {
                                    ObjectId did = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                                    double[] sz;
                                    if (!defSize.TryGetValue(did, out sz))
                                    {
                                        Extents3d? x = BlockAlong.DefExtents(tx, did);
                                        defSize[did] = sz = x == null ? new double[] { 0, 0 } : new[] { x.Value.MaxPoint.X - x.Value.MinPoint.X, x.Value.MaxPoint.Y - x.Value.MinPoint.Y };
                                    }
                                    len = sz[0] * Math.Abs(br.ScaleFactors.X);
                                    if (wp == null && sz[1] > 0) label = name + " (Genişlik=" + R(sz[1] * Math.Abs(br.ScaleFactors.Y)) + (grp.Count > 0 ? ", " + string.Join(", ", grp) : "") + ")";
                                }
                                add("Kablo tavası", label, "m", "", len * f, 1);
                            }
                            else add("Blok", label, "adet", "", 1, 1);
                            continue;
                        }
                        if (e is Line || e is Polyline || e is Polyline2d || e is Polyline3d || e is Arc || e is Spline)
                        {
                            Curve c = (Curve)e;
                            double len = c.GetDistanceAtParameter(c.EndParam) - c.GetDistanceAtParameter(c.StartParam);
                            add("Uzunluk", "Hat uzunluğu", "m", e.Layer, len * f, 1);
                            if (c.Closed && (e is Polyline || e is Polyline2d)) add("Alan", "Kapalı alan", "m²", e.Layer, Math.Abs(c.Area) * f2, 1);
                            continue;
                        }
                        Hatch h = e as Hatch;
                        if (h != null) { add("Alan", "Tarama alanı", "m²", e.Layer, h.Area * f2, 1); continue; }
                        if (rq.Texts && (e is DBText || e is MText)) add("Yazı", "Yazı", "adet", e.Layer, 1, 1);
                    }
                    catch (System.Exception) { skipped++; }
                }
                if (own != null) own.Commit();
            }
            finally { if (own != null) own.Dispose(); }
            if (skipped > 0) notes.Add("Not: " + skipped + " nesne ölçülemedi ve atlandı.");
            notes.Add("Not: yalnızca Model alanı sayıldı; iç içe (nested) bloklar ve XREF'ler ayrıca açılmadı.");
            return acc.Values.OrderBy(a => a.Kat).ThenBy(a => a.Kalem).ThenBy(a => a.Katman)
                .Select(a => new Dictionary<string, object>
                {
                    { "kategori", a.Kat }, { "kalem", a.Kalem }, { "birim", a.Birim }, { "katman", a.Katman },
                    { "miktar", Math.Round(a.Miktar, 4) }, { "adet", a.Adet }
                }).ToList();
        }

        public static List<Dictionary<string, object>> ComputeFile(string path, List<string> notes)
        {
            using (Database side = new Database(false, true))
            {
                side.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, "");
                side.CloseInput(true);
                return Compute(side, new TakeoffRequest(), notes);
            }
        }
    }

    /// <summary>Keşif rows for the cable tray ('cabletray') and fitting ('fittings') blocks, driven by fittings.json.</summary>
    internal static class TrayKesif
    {
        internal static string Norm(string s)
        {
            if (s == null) return "";
            System.Text.StringBuilder b = new System.Text.StringBuilder();
            foreach (char c0 in s.ToLowerInvariant())
            {
                char c = c0;
                switch (c) { case 'ı': case 'i': c = 'i'; break; case 'ü': c = 'u'; break; case 'ö': c = 'o'; break; case 'ş': c = 's'; break; case 'ç': c = 'c'; break; case 'ğ': c = 'g'; break; case 'İ': c = 'i'; break; }
                if (char.IsLetterOrDigit(c)) b.Append(c);
            }
            return b.ToString();
        }

        static Dictionary<string, DynamicBlockReferenceProperty> Props(BlockReference br)
        {
            Dictionary<string, DynamicBlockReferenceProperty> d = new Dictionary<string, DynamicBlockReferenceProperty>();
            if (br.IsDynamicBlock)
                foreach (DynamicBlockReferenceProperty p in br.DynamicBlockReferencePropertyCollection)
                    if (p.PropertyName != "Origin" && !d.ContainsKey(Norm(p.PropertyName))) d[Norm(p.PropertyName)] = p;
            return d;
        }

        /// <summary>Exact normalized match first; for candidates of 3+ chars also a prefix match (Kalinlik_mm).</summary>
        static DynamicBlockReferenceProperty Find(Dictionary<string, DynamicBlockReferenceProperty> d, IEnumerable<string> names)
        {
            if (names == null) return null;
            List<string> ns = names.Where(n => !string.IsNullOrEmpty(n)).Select(Norm).ToList();
            foreach (string n in ns) { DynamicBlockReferenceProperty p; if (d.TryGetValue(n, out p)) return p; }
            foreach (string n in ns.Where(n => n.Length >= 3)) { DynamicBlockReferenceProperty p = d.FirstOrDefault(kv => kv.Key.StartsWith(n)).Value; if (p != null) return p; }
            return null;
        }

        static string V(DynamicBlockReferenceProperty p)
        {
            if (p == null || p.Value == null) return null;
            if (p.Value is double || p.Value is int || p.Value is short || p.Value is float) return Fmt(Convert.ToDouble(p.Value, CultureInfo.InvariantCulture));
            string s = Convert.ToString(p.Value, CultureInfo.InvariantCulture).Trim();
            return s.Length == 0 ? null : Num(s);
        }

        static Dictionary<string, string> Attrs(Transaction tx, BlockReference br)
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            foreach (ObjectId aid in br.AttributeCollection)
            {
                AttributeReference ar = tx.GetObject(aid, OpenMode.ForRead) as AttributeReference;
                if (ar == null || string.IsNullOrWhiteSpace(ar.TextString)) continue;
                string k = Norm(ar.Tag); if (!d.ContainsKey(k)) d[k] = ar.TextString.Trim();
            }
            return d;
        }

        /// <summary>"2", "2,5", "2.50 mm" -> "2" / "2.5"; non-numeric text is returned as is.</summary>
        internal static string Num(string s)
        {
            if (s == null) return null;
            Match m = Regex.Match(s, @"-?\d+(?:[.,]\d+)?");
            double v;
            if (m.Success && double.TryParse(m.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                return Fmt(v);
            return s.Trim();
        }

        /// <summary>Label number: up to 3 decimals, Turkish comma ("1,5"); 60 == 60.0 == "60,00".</summary>
        internal static string Fmt(double v)
        {
            return Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture).Replace('.', ',');
        }

        /// <summary>Dynamic property first, then attribute tag (same candidate list).</summary>
        static string Get(Dictionary<string, DynamicBlockReferenceProperty> d, Dictionary<string, string> at, IEnumerable<string> names)
        {
            string v = V(Find(d, names));
            if (v != null) return Num(v);
            if (names == null) return null;
            List<string> ns = names.Where(n => !string.IsNullOrEmpty(n)).Select(Norm).ToList();
            foreach (string n in ns) { string x; if (at.TryGetValue(n, out x)) return Num(x); }
            foreach (string n in ns.Where(n => n.Length >= 3)) { string x = at.FirstOrDefault(kv => kv.Key.StartsWith(n)).Value; if (x != null) return Num(x); }
            return null;
        }

        static string HT(Transaction tx, BlockReference br, Dictionary<string, DynamicBlockReferenceProperty> d, FitConfig c, bool trayStyle)
        {
            Dictionary<string, string> at = Attrs(tx, br);
            string h = Get(d, at, c.height_props), t = Get(d, at, c.thickness_props);
            if (trayStyle) return "x" + (h ?? "?") + " t=" + (t ?? "?");   // missing values stay a separate group
            return (h != null ? " h=" + h : "") + (t != null ? " t=" + t : "");
        }

        internal static bool Try(Transaction tx, BlockReference br, string name, FitConfig c, Action<string, string, string, string, double, int> add, double f)
        {
            if (!br.IsDynamicBlock) return false;
            if (string.Equals(name, c.straight_block, StringComparison.OrdinalIgnoreCase))
            {
                Dictionary<string, DynamicBlockReferenceProperty> d = Props(br);
                DynamicBlockReferenceProperty lp, wp;
                d.TryGetValue(Norm(c.length_param), out lp); d.TryGetValue(Norm(c.width_param), out wp);
                if (lp == null || !(lp.Value is double)) return false;
                string label = (c.tray_label ?? "Kablo tavası") + " " + (V(wp) ?? "?") + HT(tx, br, d, c, true);
                // grouped only by EN x YUKSEKLIK x KALINLIK (layer-independent)
                add(c.tray_label ?? "Kablo tavası", label, "m", "", (double)lp.Value * Math.Abs(br.ScaleFactors.X) * f, 1);
                return true;
            }
            if (string.Equals(name, c.fitting_block, StringComparison.OrdinalIgnoreCase))
            {
                Dictionary<string, DynamicBlockReferenceProperty> d = Props(br);
                DynamicBlockReferenceProperty vp; d.TryGetValue(Norm(c.visibility_param), out vp);
                string state = V(vp) ?? "?";
                KeyValuePair<string, FitType> ft = (c.types ?? new Dictionary<string, FitType>()).FirstOrDefault(kv => kv.Value != null && Norm(kv.Value.state) == Norm(state));
                string label;
                if (ft.Value != null)
                {
                    string lbl; if (c.fitting_labels == null || !c.fitting_labels.TryGetValue(ft.Key, out lbl)) lbl = ft.Value.state;
                    DynamicBlockReferenceProperty p1 = null, p2 = null;
                    if (!string.IsNullOrEmpty(ft.Value.w1)) d.TryGetValue(Norm(ft.Value.w1), out p1);
                    if (!string.IsNullOrEmpty(ft.Value.w2)) d.TryGetValue(Norm(ft.Value.w2), out p2);
                    string a = V(p1), b = V(p2);
                    string size = a == null ? (b ?? "") : (b == null || b == a ? a : a + "x" + b);
                    label = lbl + (size.Length > 0 ? " " + size : "");
                }
                else label = state;
                add("Ek parça", label + HT(tx, br, d, c, false), "adet", "", 1, 1);
                return true;
            }
            return false;
        }
    }

    internal static class Metraj
    {
        public static event Action<string> Finished;
        static readonly JavaScriptSerializer Js = new JavaScriptSerializer { MaxJsonLength = 32000000, RecursionLimit = 40 };

        static bool Saved(Document doc) { return Path.IsPathRooted(doc.Name) && File.Exists(doc.Name); }
        static string DwgName(Document doc) { return Path.GetFileNameWithoutExtension(doc.Name); }

        public static string SnapshotDir(Document doc)
        {
            if (Saved(doc)) return Path.Combine(Path.GetDirectoryName(doc.Name), DwgName(doc) + "_revizyonlar");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CadAiDesigner", "revizyonlar", DwgName(doc));
        }

        public static string PricesPath()
        {
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CadAiDesigner");
            string x = Path.Combine(d, "birim_fiyat.xlsx");
            return File.Exists(x) ? x : Path.Combine(d, "birim_fiyat.csv");
        }

        public static List<string> Revisions(Document doc)
        {
            try
            {
                string d = SnapshotDir(doc);
                return Directory.Exists(d) ? Directory.GetFiles(d, "*.json").Select(Path.GetFileNameWithoutExtension).OrderBy(s => s).ToList() : new List<string>();
            }
            catch (System.Exception) { return new List<string>(); }
        }

        static string ExportPath(Document doc, string kind = "metraj")
        {
            string dir = Saved(doc) ? Path.GetDirectoryName(doc.Name) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CadAiDesigner", "metraj");
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".cadai_w"); File.WriteAllText(probe, ""); File.Delete(probe);
            }
            catch (System.Exception) { dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CadAiDesigner", "metraj"); Directory.CreateDirectory(dir); }
            return Path.Combine(dir, DwgName(doc) + "_" + kind + "_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".xlsx");
        }

        /// <summary>Computes the takeoff (read-only) and lets the service price/compare/export it. Returns the summary text.</summary>
        public static string Run(Document doc, TakeoffRequest rq)
        {
            List<string> notes = new List<string>();
            List<Dictionary<string, object>> items;
            using (doc.LockDocument()) items = Takeoff.Compute(doc.Database, rq, notes);
            Dictionary<string, object> body = new Dictionary<string, object>
            {
                { "items", items }, { "notes", notes }, { "dwg", doc.Name }, { "snapshot_dir", SnapshotDir(doc) },
                { "prices_path", PricesPath() }
            };
            if (rq.Export) body["export_path"] = ExportPath(doc);
            if (!string.IsNullOrEmpty(rq.SaveRevision)) body["save_revision"] = rq.SaveRevision;
            if (!string.IsNullOrEmpty(rq.CompareTo)) body["compare_to"] = rq.CompareTo;
            if (rq.CompareRevisions != null) body["compare_revisions"] = rq.CompareRevisions;
            if (!string.IsNullOrEmpty(rq.CompareDwg))
            {
                if (!File.Exists(rq.CompareDwg)) throw new InvalidOperationException("Karşılaştırma DWG bulunamadı: " + rq.CompareDwg);
                List<string> n2 = new List<string>();
                body["compare_items"] = Takeoff.ComputeFile(rq.CompareDwg, n2);
                body["compare_name"] = Path.GetFileName(rq.CompareDwg);
            }
            ServiceHost.EnsureStarted(Settings.Load());
            Dictionary<string, object> resp = Js.Deserialize<Dictionary<string, object>>(ServiceHost.Post("/metraj", Js.Serialize(body)));
            if (resp == null) throw new InvalidOperationException("Servisten boş yanıt.");
            if (resp.ContainsKey("error")) throw new InvalidOperationException(Convert.ToString(resp["error"]));
            AppLog.Write("METRAJ [" + Path.GetFileName(doc.Name) + "] " + items.Count + " kalem");
            return J.Str(resp, "summary");
        }

        /// <summary>Prices/exports precomputed items (plan keşif). Returns the summary text.</summary>
        public static string Send(Document doc, List<Dictionary<string, object>> items, List<string> notes, string kind)
        {
            Dictionary<string, object> body = new Dictionary<string, object>
            {
                { "items", items }, { "notes", notes }, { "dwg", doc.Name }, { "snapshot_dir", SnapshotDir(doc) },
                { "prices_path", PricesPath() }, { "export_path", ExportPath(doc, kind) }
            };
            string xlsx = (string)body["export_path"];
            ServiceHost.EnsureStarted(Settings.Load());
            Dictionary<string, object> resp = Js.Deserialize<Dictionary<string, object>>(ServiceHost.Post("/metraj", Js.Serialize(body)));
            if (resp == null) throw new InvalidOperationException("Servisten boş yanıt.");
            if (resp.ContainsKey("error")) throw new InvalidOperationException(Convert.ToString(resp["error"]));
            if (File.Exists(xlsx) && Environment.GetEnvironmentVariable("CADAI_NOOPEN") == null)
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(xlsx) { UseShellExecute = true }); } catch (System.Exception) { }
            AppLog.Write(kind.ToUpperInvariant() + " [" + Path.GetFileName(doc.Name) + "] " + items.Count + " kalem");
            return J.Str(resp, "summary");
        }

        public static TakeoffRequest FromAction(Dictionary<string, object> a, Document doc, IEnumerable<ObjectId> selection)
        {
            TakeoffRequest rq = new TakeoffRequest
            {
                Texts = J.Get(a, "include_texts") != null && J.Bool(a, "include_texts"),
                Export = J.Get(a, "export") == null || J.Bool(a, "export"),
                SaveRevision = J.Str(a, "save_revision"), CompareTo = J.Str(a, "compare_to"), CompareDwg = J.Str(a, "compare_dwg")
            };
            List<object> pair = J.Arr(J.Get(a, "compare_revisions"));
            if (pair != null && pair.Count == 2) rq.CompareRevisions = pair.Select(Convert.ToString).ToArray();
            string scope = J.Str(a, "scope");
            if (scope == "selection")
            {
                rq.Only = new HashSet<ObjectId>(selection ?? Enumerable.Empty<ObjectId>());
                if (rq.Only.Count == 0) throw new InvalidOperationException("Seçim kapsamı istendi ama seçili nesne yok.");
            }
            else if (scope == "layer")
            {
                List<object> ls = J.Arr(J.Get(a, "layers"));
                rq.Layers = new HashSet<string>((ls ?? new List<object>()).Select(Convert.ToString), StringComparer.OrdinalIgnoreCase);
            }
            return rq;
        }

        internal static void Raise(string s) { Action<string> h = Finished; if (h != null) h(s); }
    }

    public class MetrajCommands
    {
        [CommandMethod("AIDMETRAJ", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void Metraj()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            try
            {
                TakeoffRequest rq = new TakeoffRequest();
                PromptKeywordOptions ko = new PromptKeywordOptions("\nMetraj kapsamı [Tumu/Secim/Katman] <Tumu>: ", "Tumu Secim Katman") { AllowNone = true };
                PromptResult kr = ed.GetKeywords(ko);
                if (kr.Status == PromptStatus.Cancel) return;
                string scope = kr.Status == PromptStatus.OK ? kr.StringResult : "Tumu";
                if (scope == "Secim")
                {
                    PromptSelectionResult sr = ed.GetSelection();
                    if (sr.Status != PromptStatus.OK) return;
                    rq.Only = new HashSet<ObjectId>(sr.Value.GetObjectIds());
                }
                else if (scope == "Katman")
                {
                    PromptResult lr = ed.GetString(new PromptStringOptions("\nKatman adları (virgülle): ") { AllowSpaces = true });
                    if (lr.Status != PromptStatus.OK) return;
                    rq.Layers = new HashSet<string>(lr.StringResult.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0), StringComparer.OrdinalIgnoreCase);
                }
                PromptResult tr = ed.GetKeywords(new PromptKeywordOptions("\nYazılar sayılsın mı? [Evet/Hayir] <Hayir>: ", "Evet Hayir") { AllowNone = true });
                rq.Texts = tr.Status == PromptStatus.OK && tr.StringResult == "Evet";
                PromptResult rv = ed.GetString(new PromptStringOptions("\nRevizyon olarak kaydet (etiket, örn. R1; boş = kaydetme): ") { AllowSpaces = false });
                if (rv.Status == PromptStatus.Cancel) return;
                if (rv.Status == PromptStatus.OK && rv.StringResult.Trim().Length > 0) rq.SaveRevision = rv.StringResult.Trim();
                List<string> revs = CadAiDesigner.Metraj.Revisions(doc);
                PromptKeywordOptions co = new PromptKeywordOptions("\nKarşılaştır [Yok/Revizyon/Dwg] <Yok>" + (revs.Count > 0 ? " (kayıtlı: " + string.Join(", ", revs) + ")" : "") + ": ", "Yok Revizyon Dwg") { AllowNone = true };
                PromptResult cr = ed.GetKeywords(co);
                if (cr.Status == PromptStatus.Cancel) return;
                if (cr.Status == PromptStatus.OK && cr.StringResult == "Revizyon")
                {
                    PromptResult l = ed.GetString("\nKarşılaştırılacak revizyon etiketi: ");
                    if (l.Status != PromptStatus.OK) return;
                    rq.CompareTo = l.StringResult.Trim();
                }
                else if (cr.StringResult == "Dwg")
                {
                    PromptFileNameResult fr = ed.GetFileNameForOpen(new PromptOpenFileOptions("Karşılaştırılacak DWG") { Filter = "Çizim (*.dwg)|*.dwg" });
                    if (fr.Status != PromptStatus.OK) return;
                    rq.CompareDwg = fr.StringResult;
                }
                ed.WriteMessage("\nMetraj hesaplanıyor...");
                string sum = CadAiDesigner.Metraj.Run(doc, rq);
                ed.WriteMessage("\n" + sum + "\n");
                CadAiDesigner.Metraj.Raise(sum);
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nMetraj hatası: " + ex.Message + "\n");
                CadAiDesigner.Metraj.Raise("Metraj hatası: " + ex.Message);
            }
        }
    }
}