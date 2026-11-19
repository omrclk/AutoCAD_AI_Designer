using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace CadAiDesigner
{
    public class FitType
    {
        public string state { get; set; }
        public string w1 { get; set; }
        public string w2 { get; set; }
        public string flip { get; set; }
        /// <summary>How far the tray is cut back past the other tray's edge, to the fitting's connection face.
        /// Not the block insertion offset (clearance). For the stock fittings block that face is 60
        /// (insertion 164, trays were 104 short of the face).</summary>
        public double? leg { get; set; }
    }

    /// <summary>fittings.json (LocalAppData\CadAiDesigner). Defaults match the user's 'cabletray' + 'fittings' blocks.</summary>
    public class FitConfig
    {
        public string straight_block { get; set; } = "cabletray";
        public string length_param { get; set; } = "BOY";
        public string width_param { get; set; } = "EN";
        public string fitting_block { get; set; } = "fittings";
        public string visibility_param { get; set; } = "Visibility1";
        public string layer { get; set; } = "E_CT_YESIL";
        /// <summary>How far each fitting leg reaches past the other tray's edge (block geometry; 164 for 'fittings').</summary>
        public double clearance { get; set; } = 164;
        public double tolerance { get; set; } = 20;
        public bool trim { get; set; } = true;
        /// <summary>Fitting used when branch width != main width: "YARIM_T" or "YT".</summary>
        public string unequal_t { get; set; } = "YARIM_T";
        public Dictionary<string, FitType> types { get; set; } = new Dictionary<string, FitType>
        {
            { "YD", new FitType { leg = 60, state = "YD", w1 = "YD1", w2 = "YD2", flip = "YD flip" } },
            { "YT", new FitType { leg = 60, state = "YT", w1 = "YT1", w2 = "YT2" } },
            { "YARIM_T", new FitType { state = "YT-Yarım", w1 = "YT Yarım A", w2 = null, leg = 60 } },
            { "AD", new FitType { leg = 60, state = "AD", w1 = "AD1", w2 = "AD2" } },
        };

        /// <summary>Metraj/keşif: candidate property names for height and thickness (matched case/Turkish-insensitive, prefix).</summary>
        public List<string> height_props { get; set; } = new List<string> { "Yükseklik", "Yukseklik", "Height", "H" };
        public List<string> thickness_props { get; set; } = new List<string> { "Kalınlık", "Kalinlik", "Thickness", "Et", "t" };
        public string tray_label { get; set; } = "Kablo tavası";
        /// <summary>Keşif item names per fitting type key (types). Unknown visibility states use the state name.</summary>
        public Dictionary<string, string> fitting_labels { get; set; } = new Dictionary<string, string>
        {
            { "YD", "Yatay dönüş (YD)" }, { "YT", "Yatay T (YT)" }, { "YARIM_T", "Yarım T" }, { "AD", "Haç (AD)" }
        };

        public static string FilePath { get { return Path.Combine(AppPaths.Dir, "fittings.json"); } }

        public static FitConfig Load()
        {
            JavaScriptSerializer js = new JavaScriptSerializer();
            try
            {
                if (File.Exists(FilePath))
                {
                    FitConfig c = js.Deserialize<FitConfig>(File.ReadAllText(FilePath)) ?? new FitConfig();
                    // older files: add newly supported types (e.g. AD) without touching the user's edits
                    if (c.types != null) { FitType y; if (c.types.TryGetValue("YARIM_T", out y) && y != null && y.w1 == "YT1" && !y.leg.HasValue) c.types["YARIM_T"] = new FitConfig().types["YARIM_T"]; }
                    if (c.types != null) foreach (KeyValuePair<string, FitType> kv in c.types) if (kv.Value != null && (!kv.Value.leg.HasValue || kv.Value.leg.Value == 50)) kv.Value.leg = 60;
                    if (c.types != null) foreach (KeyValuePair<string, FitType> kv in new FitConfig().types) if (!c.types.ContainsKey(kv.Key)) c.types[kv.Key] = kv.Value;
                    return c;
                }
            }
            catch (System.Exception ex) { throw new InvalidOperationException("fittings.json okunamadı: " + ex.Message); }
            FitConfig d = new FitConfig();
            try { File.WriteAllText(FilePath, Pretty(js.Serialize(d)), new System.Text.UTF8Encoding(false)); } catch (System.Exception) { }
            return d;
        }

        static string Pretty(string json)
        {
            System.Text.StringBuilder b = new System.Text.StringBuilder();
            int ind = 0; bool q = false;
            foreach (char ch in json)
            {
                if (ch == '"' && (b.Length == 0 || b[b.Length - 1] != '\\')) q = !q;
                if (q) { b.Append(ch); continue; }
                if (ch == '{' || ch == '[') { b.Append(ch).Append('\n').Append(' ', ++ind * 2); }
                else if (ch == '}' || ch == ']') { b.Append('\n').Append(' ', --ind * 2).Append(ch); }
                else if (ch == ',') { b.Append(ch).Append('\n').Append(' ', ind * 2); }
                else if (ch == ':') b.Append(": ");
                else b.Append(ch);
            }
            return b.ToString();
        }

        public static void OpenInEditor()
        {
            Load();
            Process.Start("notepad.exe", "\"" + FilePath + "\"");
        }
    }

    /// <summary>Deterministic cable-tray fitting placement (YD corners, YT / yarım T junctions) on straight tray blocks.</summary>
    internal static class Fittings
    {
        internal class TrayRun
        {
            public BlockReference Br;
            public Point3d A; public Vector3d U, Perp; public double L, W;
            public List<double[]> Cuts = new List<double[]>();
            /// <summary>New start/end along U (may be &lt;0 or &gt;L = extension) when a tray END sits at a fitting.</summary>
            public double? NewStart, NewEnd;
            public bool Changed { get { return Cuts.Count > 0 || NewStart.HasValue || NewEnd.HasValue; } }
            public void Reset() { Cuts.Clear(); NewStart = null; NewEnd = null; }
        }

        internal class Node { public Point3d P; public Dictionary<int, int> Kinds = new Dictionary<int, int>(); }

        internal class Placement
        {
            public string Type; public Point3d P; public double Rot; public short Flip; public double W1, W2; public string Layer;
        }

        internal class Plan
        {
            public List<Placement> Places = new List<Placement>();
            public List<string> Notes = new List<string>();
            public int Existing, Runs;
            List<TrayRun> runs;
            internal List<TrayRun> RunList { get { return runs; } set { runs = value; } }

            public string Summary()
            {
                IEnumerable<string> g = Places.GroupBy(p => p.Type + " " + W(p.W1) + (p.Type == "YD" || Math.Abs(p.W1 - p.W2) < 0.5 ? "" : "/" + W(p.W2)))
                    .OrderBy(x => x.Key).Select(x => x.Key + " × " + x.Count());
                int trimmed = RunList == null ? 0 : RunList.Count(r => r.Changed);
                return Runs + " düz tava incelendi. Yerleştirilecek: " + (Places.Count == 0 ? "yok" : string.Join(", ", g)) +
                       (Existing > 0 ? "; zaten mevcut (atlandı): " + Existing : "") +
                       (trimmed > 0 ? "; kısaltılacak/bölünecek tava: " + trimmed : "") +
                       (Notes.Count > 0 ? "\n  Not: " + string.Join("\n  Not: ", Notes.Distinct().Take(15)) : "");
            }
        }

        static string W(double v) { return Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture); }
        static string Pt(Point3d p) { return "(" + Math.Round(p.X) + "," + Math.Round(p.Y) + ")"; }

        internal static string EffName(Transaction tx, BlockReference br)
        {
            try { return ((BlockTableRecord)tx.GetObject(br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord, OpenMode.ForRead)).Name; }
            catch (System.Exception) { return br.Name; }
        }

        internal static DynamicBlockReferenceProperty Prop(BlockReference br, string name)
        {
            if (br == null || !br.IsDynamicBlock || string.IsNullOrEmpty(name)) return null;
            foreach (DynamicBlockReferenceProperty p in br.DynamicBlockReferencePropertyCollection)
                if (!p.ReadOnly && string.Equals(p.PropertyName, name, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        static double PropNum(BlockReference br, string name)
        {
            DynamicBlockReferenceProperty p = Prop(br, name);
            return p == null ? double.NaN : Convert.ToDouble(p.Value, CultureInfo.InvariantCulture);
        }

        static void SetProp(BlockReference br, string name, object v, List<string> notes)
        {
            if (string.IsNullOrEmpty(name)) return;
            DynamicBlockReferenceProperty p = Prop(br, name);
            if (p == null) { notes.Add("'" + name + "' özelliği blokta yok."); return; }
            object cur = p.Value;
            try
            {
                if (v is string)
                {
                    string want = Norm((string)v);
                    object hit = null;
                    try { hit = p.GetAllowedValues().FirstOrDefault(x => Norm(Convert.ToString(x, CultureInfo.InvariantCulture)) == want); } catch (System.Exception) { }
                    if (hit == null) { notes.Add("'" + name + "' için '" + v + "' durumu bulunamadı."); return; }
                    p.Value = hit;
                }
                else if (cur is short) p.Value = Convert.ToInt16(v);
                else if (cur is int) p.Value = Convert.ToInt32(v);
                else p.Value = Convert.ToDouble(v);
            }
            catch (System.Exception ex) { notes.Add("'" + name + "' = " + Convert.ToString(v, CultureInfo.InvariantCulture) + " ayarlanamadı (" + ex.Message + ")."); }
        }

        static string Norm(string s)
        {
            return new string((s ?? "").ToLowerInvariant().Replace('ı', 'i').Replace('İ', 'i').Where(char.IsLetterOrDigit).ToArray());
        }

        internal static TrayRun MakeRun(BlockReference br, FitConfig cfg)
        {
            double L = PropNum(br, cfg.length_param), Wd = PropNum(br, cfg.width_param);
            if (double.IsNaN(L) || double.IsNaN(Wd) || L <= 0 || Wd <= 0) return null;
            double sx = Math.Sign(br.ScaleFactors.X), sy = Math.Sign(br.ScaleFactors.Y);
            double ax = Math.Abs(br.ScaleFactors.X);
            Vector3d u = new Vector3d(Math.Cos(br.Rotation), Math.Sin(br.Rotation), 0) * sx;
            Vector3d pp = new Vector3d(-Math.Sin(br.Rotation), Math.Cos(br.Rotation), 0) * sy;
            TrayRun r = new TrayRun { Br = br, U = u, Perp = pp, L = L * ax, W = Wd * Math.Abs(br.ScaleFactors.Y) };
            r.A = br.Position + pp * (r.W / 2);
            return r;
        }

        static double Cross(Vector3d a, Vector3d b) { return a.X * b.Y - a.Y * b.X; }
        static Vector3d Rot(double x, double y, double r) { return new Vector3d(x * Math.Cos(r) - y * Math.Sin(r), x * Math.Sin(r) + y * Math.Cos(r), 0); }

        /// <summary>Analyses straight tray blocks (selection or all) and computes fitting placements and trims. Read-only.</summary>
        internal static Plan Analyze(Transaction tx, Database db, IEnumerable<ObjectId> only, FitConfig cfg)
        {
            Plan plan = new Plan();
            BlockTable bt = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
            if (!bt.Has(cfg.fitting_block)) throw new InvalidOperationException("Ek parça bloğu '" + cfg.fitting_block + "' çizimde yok (fittings.json'da fitting_block).");
            BlockTableRecord ms = (BlockTableRecord)tx.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            IEnumerable<ObjectId> ids = only != null && only.Any() ? only : ms.Cast<ObjectId>();
            List<TrayRun> runs = new List<TrayRun>();
            List<Point3d> existing = new List<Point3d>();
            foreach (ObjectId id in ms)
            {
                BlockReference br = tx.GetObject(id, OpenMode.ForRead) as BlockReference;
                if (br != null && string.Equals(EffName(tx, br), cfg.fitting_block, StringComparison.OrdinalIgnoreCase)) existing.Add(br.Position);
            }
            foreach (ObjectId id in ids)
            {
                if (id.IsErased) continue;
                BlockReference br = tx.GetObject(id, OpenMode.ForRead) as BlockReference;
                if (br == null || !string.Equals(EffName(tx, br), cfg.straight_block, StringComparison.OrdinalIgnoreCase)) continue;
                TrayRun r = MakeRun(br, cfg);
                if (r == null) plan.Notes.Add(br.Handle + ": " + cfg.length_param + "/" + cfg.width_param + " okunamadı, atlandı.");
                else runs.Add(r);
            }
            plan.Runs = runs.Count;
            plan.RunList = runs;
            if (runs.Count == 0) { plan.Notes.Add("'" + cfg.straight_block + "' düz tava bloğu bulunamadı."); return plan; }

            double tol = cfg.tolerance;
            List<Node> nodes = new List<Node>();
            Action<Point3d, int, int, int, int> hit = (p, i, ki, j, kj) =>
            {
                Node n = nodes.FirstOrDefault(x => x.P.DistanceTo(p) <= 2 * tol + 1);
                if (n == null) nodes.Add(n = new Node { P = p });
                n.Kinds[i] = ki; n.Kinds[j] = kj;
            };
            Func<double, TrayRun, double, int> kind = (t, r, te) => t < -te || t > r.L + te ? -1 : Math.Abs(t) <= te ? 0 : Math.Abs(t - r.L) <= te ? 1 : 2;

            for (int i = 0; i < runs.Count; i++)
                for (int j = i + 1; j < runs.Count; j++)
                {
                    TrayRun a = runs[i], b = runs[j];
                    double cr = Cross(a.U, b.U);
                    if (Math.Abs(cr) < 0.05)
                    {
                        // collinear, end to end: width change (redüksiyon) check
                        Vector3d off = b.A - a.A;
                        if (Math.Abs(Cross(a.U, off)) > tol) continue;
                        foreach (Point3d pa in new[] { a.A, a.A + a.U * a.L })
                            foreach (Point3d pb in new[] { b.A, b.A + b.U * b.L })
                                if (pa.DistanceTo(pb) <= tol && Math.Abs(a.W - b.W) > 0.5)
                                    plan.Notes.Add("Genişlik değişimi (redüksiyon) " + W(a.W) + "→" + W(b.W) + " " + Pt(pa) + ": otomatik parça yok.");
                        continue;
                    }
                    Vector3d w = b.A - a.A;
                    double t = Cross(w, b.U) / cr, s = Cross(w, a.U) / cr;
                    // A branch drawn up to the other tray's edge stops half a width short of that centreline.
                    // Farther than that (plus tolerance) is a dead end and is not extended.
                    int ki = kind(t, a, b.W * 0.5 + tol), kj = kind(s, b, a.W * 0.5 + tol);
                    if (ki < 0 || kj < 0) continue;
                    hit(a.A + a.U * t, i, ki, j, kj);
                }

            foreach (Node n in nodes)
            {
                List<Tuple<int, int, Vector3d>> arms = new List<Tuple<int, int, Vector3d>>();
                foreach (KeyValuePair<int, int> kv in n.Kinds)
                {
                    TrayRun r = runs[kv.Key];
                    if (kv.Value == 0 || kv.Value == 2) arms.Add(Tuple.Create(kv.Key, kv.Value, r.U));
                    if (kv.Value == 1 || kv.Value == 2) arms.Add(Tuple.Create(kv.Key, kv.Value, -r.U));
                }
                arms = UniqueArms(arms);
                if (arms.Count < 2) continue;
                Placement pl = null;
                Point3d C = n.P;
                string layer = runs[arms[0].Item1].Br.Layer;
                if (arms.Count == 2)
                {
                    Vector3d a = arms[0].Item3, b = arms[1].Item3;
                    double dot = a.DotProduct(b);
                    if (dot < -0.95) continue;
                    if (Math.Abs(dot) > 0.1) { plan.Notes.Add("Açılı dönüş " + Pt(C) + " (" + Math.Round(Math.Acos(dot) * 180 / Math.PI) + "°): otomatik parça yok."); continue; }
                    TrayRun ra = runs[arms[0].Item1], rb = runs[arms[1].Item1];
                    double r = Math.Atan2(a.Y, a.X) + Math.PI / 2;
                    short flip = (short)(Rot(-1, 0, r).DotProduct(b) > 0 ? 1 : 0);
                    pl = new Placement { Type = "YD", P = C - b * (ra.W / 2) + a * (cfg.clearance + rb.W / 2), Rot = r, Flip = flip, W1 = ra.W, W2 = rb.W, Layer = layer };
                    AddCut(ra, arms[0].Item2, a, C, Reach(cfg, "YD") + rb.W / 2);
                    AddCut(rb, arms[1].Item2, b, C, Reach(cfg, "YD") + ra.W / 2);
                }
                else if (arms.Count == 3)
                {
                    int bi = -1;
                    for (int k = 0; k < 3 && bi < 0; k++)
                    {
                        Vector3d x = arms[(k + 1) % 3].Item3, y = arms[(k + 2) % 3].Item3;
                        if (x.DotProduct(y) < -0.95) bi = k;
                    }
                    if (bi < 0 || Math.Abs(arms[bi].Item3.DotProduct(arms[(bi + 1) % 3].Item3)) > 0.1) { plan.Notes.Add("Açılı/düzensiz birleşim " + Pt(C) + ": otomatik parça yok."); continue; }
                    Tuple<int, int, Vector3d> br = arms[bi], m1 = arms[(bi + 1) % 3], m2 = arms[(bi + 2) % 3];
                    TrayRun rb = runs[br.Item1];
                    double wm = Math.Max(runs[m1.Item1].W, runs[m2.Item1].W), wb = rb.W;
                    if (Math.Abs(runs[m1.Item1].W - runs[m2.Item1].W) > 0.5) plan.Notes.Add("T " + Pt(C) + ": ana hattın iki tarafı farklı genişlikte (" + W(runs[m1.Item1].W) + "/" + W(runs[m2.Item1].W) + ").");
                    Vector3d a = br.Item3;
                    double r = Math.Atan2(a.Y, a.X) + Math.PI / 2;
                    Vector3d lx = Rot(1, 0, r);
                    TrayRun mainRun = runs[m1.Item1];
                    double tHere, sHere; Point3d ignored;
                    bool offsetMate = TryHit(mainRun, rb, tol, out tHere, out sHere, out ignored)
                        && OffsetOpposite(runs, mainRun, rb, tHere, tol);
                    string type = offsetMate ? "YARIM_T" : (Math.Abs(wb - wm) <= 0.5 ? "YT" : (cfg.unequal_t ?? "YARIM_T").ToUpperInvariant());
                    pl = new Placement { Type = type, P = C - lx * (wb / 2) + a * (cfg.clearance + wm / 2), Rot = r, W1 = wb, W2 = wm, Layer = layer };
                    AddCut(rb, br.Item2, a, C, Reach(cfg, type) + wm / 2);
                    if (type != "YT")
                    {
                        // half-T sits on the main tray's edge: the main stays continuous. Two main pieces ending at the
                        // junction (e.g. an edge polyline broken at the branch opening) are extended to meet exactly.
                        if (m1.Item2 != 2) AddCut(runs[m1.Item1], m1.Item2, m1.Item3, C, 0);
                        if (m2.Item2 != 2) AddCut(runs[m2.Item1], m2.Item2, m2.Item3, C, 0);
                    }
                    if (type == "YT")
                    {
                        AddCut(runs[m1.Item1], m1.Item2, m1.Item3, C, Reach(cfg, type) + wb / 2);
                        AddCut(runs[m2.Item1], m2.Item2, m2.Item3, C, Reach(cfg, type) + wb / 2);
                    }
                }
                else if (arms.Count == 4)
                {
                    // haç: two opposite pairs, perpendicular. AD1 = width of the tray along local Y (u), AD2 = the crossing one.
                    Vector3d u = arms[0].Item3;
                    int opp = -1;
                    for (int k = 1; k < 4; k++) if (u.DotProduct(arms[k].Item3) < -0.95) opp = k;
                    List<int> others = Enumerable.Range(1, 3).Where(k => k != opp).ToList();
                    if (opp < 0 || arms[others[0]].Item3.DotProduct(arms[others[1]].Item3) > -0.95 || Math.Abs(u.DotProduct(arms[others[0]].Item3)) > 0.1)
                    { plan.Notes.Add("Açılı/düzensiz kesişim (haç) " + Pt(C) + ": otomatik parça yok."); continue; }
                    double offA = AxisOffset(runs[arms[0].Item1], runs[arms[opp].Item1]);
                    double offB = AxisOffset(runs[arms[others[0]].Item1], runs[arms[others[1]].Item1]);
                    bool colA = offA <= tol, colB = offB <= tol;
                    if (colA != colB)
                    {
                        int m0 = colA ? 0 : others[0], m1i = colA ? opp : others[1];
                        int b0 = colA ? others[0] : 0, b1 = colA ? others[1] : opp;
                        PlaceHalf(plan, cfg, existing, tol, runs, arms[b0], arms[m0], arms[m1i], layer);
                        PlaceHalf(plan, cfg, existing, tol, runs, arms[b1], arms[m0], arms[m1i], layer);
                        continue;
                    }
                    if (!colA || !colB) { plan.Notes.Add("Açılı/düzensiz kesişim (haç) " + Pt(C) + ": otomatik parça yok."); continue; }
                    double wu1 = runs[arms[0].Item1].W, wu2 = runs[arms[opp].Item1].W, wv1 = runs[arms[others[0]].Item1].W, wv2 = runs[arms[others[1]].Item1].W;
                    double w1 = Math.Max(wu1, wu2), w2 = Math.Max(wv1, wv2);
                    if (Math.Abs(wu1 - wu2) > 0.5 || Math.Abs(wv1 - wv2) > 0.5)
                        plan.Notes.Add("Haç " + Pt(C) + ": aynı hattın iki tarafı farklı genişlikte; her hat için büyük genişlik kullanıldı (AD1=" + W(w1) + ", AD2=" + W(w2) + ").");
                    else if (Math.Abs(w1 - w2) > 0.5)
                        plan.Notes.Add("Haç " + Pt(C) + ": farklı genişlikler; AD1=" + W(w1) + " bir hatta, AD2=" + W(w2) + " kesen hatta atandı.");
                    double r = Math.Atan2(u.Y, u.X) - Math.PI / 2;
                    Vector3d lx = Rot(1, 0, r);
                    pl = new Placement { Type = "AD", P = C - lx * (w1 / 2) - u * (cfg.clearance + w2 / 2), Rot = r, W1 = w1, W2 = w2, Layer = layer };
                    AddCut(runs[arms[0].Item1], arms[0].Item2, arms[0].Item3, C, Reach(cfg, "AD") + w2 / 2);
                    AddCut(runs[arms[opp].Item1], arms[opp].Item2, arms[opp].Item3, C, Reach(cfg, "AD") + w2 / 2);
                    foreach (int k in others) AddCut(runs[arms[k].Item1], arms[k].Item2, arms[k].Item3, C, Reach(cfg, "AD") + w1 / 2);
                }
                else { plan.Notes.Add("Tanınmayan birleşim " + Pt(C) + " (" + arms.Count + " kol)."); continue; }

                if (existing.Any(e => e.DistanceTo(pl.P) <= 2 * tol + 1))
                {
                    plan.Existing++;
                    foreach (int ri in n.Kinds.Keys) runs[ri].Reset(); // already fitted by hand: leave trays as they are
                    continue;
                }
                plan.Places.Add(pl);
            }
            if (!cfg.trim) foreach (TrayRun r in runs) r.Reset();
            // duplicate / overlapping straight trays (e.g. placement run twice) are reported, not touched
            for (int i = 0; i < runs.Count; i++)
                for (int j = i + 1; j < runs.Count; j++)
                {
                    TrayRun a = runs[i], b = runs[j];
                    if (Math.Abs(Cross(a.U, b.U)) > 0.05 || Math.Abs(Cross(a.U, b.A - a.A)) > Math.Max(a.W, b.W) / 2) continue;
                    double b0 = (b.A - a.A).DotProduct(a.U), b1 = b0 + b.U.DotProduct(a.U) * b.L;
                    double lo = Math.Max(0, Math.Min(b0, b1)), hi = Math.Min(a.L, Math.Max(b0, b1));
                    if (hi - lo > cfg.tolerance) plan.Notes.Add("Üst üste binen tava " + a.Br.Handle + " / " + b.Br.Handle + " (" + W(hi - lo) + " mm): biri fazla olabilir.");
                }
            return plan;
        }

        static double Leg(FitConfig cfg, string type)
        {
            FitType ft = null; if (cfg.types != null) cfg.types.TryGetValue(type, out ft);
            return ft != null && ft.leg.HasValue ? ft.leg.Value : 60;
        }

        /// <summary>Cut distance past the other tray edge. The block is inserted at <c>clearance</c> (164);
        /// the connection face is closer (60). Each end of a run is cut once, so a tray between two fittings meets both.</summary>
        static double Reach(FitConfig cfg, string type)
        {
            return Leg(cfg, type);
        }

        /// <summary>Perpendicular distance between two centre lines.</summary>
        static double AxisOffset(TrayRun a, TrayRun b)
        {
            return Math.Abs(Cross(a.U, b.A - a.A));
        }

        static int Side(TrayRun main, TrayRun br)
        {
            Point3d e0 = br.A, e1 = br.A + br.U * br.L;
            double d0 = Math.Abs(Cross(main.U, e0 - main.A)), d1 = Math.Abs(Cross(main.U, e1 - main.A));
            double s = Cross(main.U, (d0 >= d1 ? e0 : e1) - main.A);
            return s >= 0 ? 1 : -1;
        }

        static bool TryHit(TrayRun main, TrayRun br, double tol, out double t, out double s, out Point3d at)
        {
            t = 0; s = 0; at = Point3d.Origin;
            double cr = Cross(main.U, br.U);
            if (Math.Abs(cr) < 0.05) return false;
            Vector3d w = br.A - main.A;
            t = Cross(w, br.U) / cr;
            s = Cross(w, main.U) / cr;
            double lim = Math.Max(main.W, br.W) * 0.5 + tol;
            if (s < -lim || s > br.L + lim) return false;
            at = main.A + main.U * t;
            return true;
        }

        /// <summary>Another branch on the opposite side of <paramref name="main"/>, close along it, but not on this branch's axis.</summary>
        static bool OffsetOpposite(List<TrayRun> runs, TrayRun main, TrayRun branch, double tHere, double tol)
        {
            double near = Math.Max(main.W, branch.W) * 1.25 + tol;
            int side = Side(main, branch);
            foreach (TrayRun o in runs)
            {
                if (o == branch || o == main) continue;
                double t, s; Point3d at;
                if (!TryHit(main, o, tol, out t, out s, out at)) continue;
                if (Math.Abs(t - tHere) > near) continue;
                if (AxisOffset(branch, o) <= tol) continue;
                if (Side(main, o) != -side) continue;
                return true;
            }
            return false;
        }

        /// <summary>One YT-Yarım on <paramref name="branch"/>'s own axis. The main stays continuous; a broken main is closed up to this junction.</summary>
        static void PlaceHalf(Plan plan, FitConfig cfg, List<Point3d> existing, double tol, List<TrayRun> runs,
            Tuple<int, int, Vector3d> branch, Tuple<int, int, Vector3d> mainA, Tuple<int, int, Vector3d> mainB, string layer)
        {
            TrayRun br = runs[branch.Item1], m1 = runs[mainA.Item1], m2 = runs[mainB.Item1];
            double t, s; Point3d C;
            if (!TryHit(m1, br, tol, out t, out s, out C)) return;
            double wm = Math.Max(m1.W, m2.W), wb = br.W;
            Vector3d a = branch.Item3;
            double rot = Math.Atan2(a.Y, a.X) + Math.PI / 2;
            Vector3d lx = Rot(1, 0, rot);
            Point3d P = C - lx * (wb / 2) + a * (cfg.clearance + wm / 2);
            if (existing.Any(e => e.DistanceTo(P) <= 2 * tol + 1)) { plan.Existing++; return; }
            plan.Places.Add(new Placement { Type = "YARIM_T", P = P, Rot = rot, W1 = wb, W2 = wm, Layer = layer });
            AddCut(br, branch.Item2, a, C, Reach(cfg, "YARIM_T") + wm / 2);
            MeetMain(m1, mainA.Item2, mainA.Item3, C, tol);
            if (mainB.Item1 != mainA.Item1 || mainB.Item2 != mainA.Item2) MeetMain(m2, mainB.Item2, mainB.Item3, C, tol);
        }

        static void MeetMain(TrayRun r, int kind, Vector3d arm, Point3d c, double tol)
        {
            if (kind == 2) return;
            double tc = (c - r.A).DotProduct(r.U);
            double end = kind == 0 ? 0 : r.L;
            if (Math.Abs(tc - end) > r.W * 0.5 + tol) return;
            AddCut(r, kind, arm, c, 0);
        }

        /// <summary>Overlapping trays on the same run produce two arms in one direction. Keep one per direction.</summary>
        static List<Tuple<int, int, Vector3d>> UniqueArms(List<Tuple<int, int, Vector3d>> arms)
        {
            List<Tuple<int, int, Vector3d>> u = new List<Tuple<int, int, Vector3d>>();
            foreach (Tuple<int, int, Vector3d> a in arms)
            {
                bool same = false;
                foreach (Tuple<int, int, Vector3d> b in u)
                    if (a.Item3.DotProduct(b.Item3) > 0.95) { same = true; break; }
                if (!same) u.Add(a);
            }
            return u;
        }

        static void AddCut(TrayRun r, int kind, Vector3d arm, Point3d c, double d)
        {
            double tc = (c - r.A).DotProduct(r.U);
            bool plus = arm.DotProduct(r.U) > 0;
            // One end only: +U moves the start, -U moves the end. A tray between two fittings keeps both.
            if (kind == 0 || kind == 1)
            {
                if (plus) r.NewStart = tc + d; else r.NewEnd = tc - d;
                return;
            }
            if (plus) r.Cuts.Add(new[] { kind == 2 ? tc : double.NegativeInfinity, tc + d });
            else r.Cuts.Add(new[] { tc - d, kind == 2 ? tc : double.PositiveInfinity });
        }

        static List<double[]> Pieces(TrayRun r, double minLen)
        {
            List<double[]> pieces = new List<double[]> { new[] { r.NewStart ?? 0.0, r.NewEnd ?? r.L } };
            foreach (double[] c in r.Cuts)
            {
                List<double[]> next = new List<double[]>();
                foreach (double[] p in pieces)
                {
                    if (c[1] <= p[0] || c[0] >= p[1]) { next.Add(p); continue; }
                    if (c[0] > p[0]) next.Add(new[] { p[0], c[0] });
                    if (c[1] < p[1]) next.Add(new[] { c[1], p[1] });
                }
                pieces = next;
            }
            return pieces.Where(p => p[1] - p[0] >= minLen).ToList();
        }

        /// <summary>Writes the plan: inserts fittings and trims/splits straight trays. Caller owns the transaction (one undo).</summary>
        internal static string Apply(Transaction tx, Database db, Plan plan, FitConfig cfg, ApplyResult res)
        {
            BlockTable bt = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
            LayerTable lt = (LayerTable)tx.GetObject(db.LayerTableId, OpenMode.ForRead);
            BlockTableRecord ms = (BlockTableRecord)tx.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
            ObjectId fit = bt[cfg.fitting_block];
            List<string> notes = new List<string>();
            foreach (Placement p in plan.Places)
            {
                FitType ft;
                if (cfg.types == null || !cfg.types.TryGetValue(p.Type, out ft)) { notes.Add(p.Type + " eşlemesi yok (fittings.json)."); continue; }
                BlockReference b = new BlockReference(p.P, fit);
                b.SetDatabaseDefaults(db);
                b.Rotation = p.Rot;
                b.Layer = !string.IsNullOrEmpty(cfg.layer) && lt.Has(cfg.layer) ? cfg.layer : p.Layer;
                ms.AppendEntity(b);
                tx.AddNewlyCreatedDBObject(b, true);
                Executor.AddAttributes(tx, b);
                SetProp(b, cfg.visibility_param, ft.state, notes);
                SetProp(b, ft.w1, p.W1, notes);
                if (!string.IsNullOrEmpty(ft.w2)) SetProp(b, ft.w2, p.W2, notes);
                if (!string.IsNullOrEmpty(ft.flip)) SetProp(b, ft.flip, p.Flip, notes);
                BlockAlong.RefreshHatches(tx, b);
                res.Created++;
            }
            int trimmed = 0;
            foreach (TrayRun r in plan.RunList ?? new List<TrayRun>())
            {
                if (!r.Changed) continue;
                List<double[]> pieces = Pieces(r, Math.Max(1, cfg.tolerance / 2));
                BlockReference src = (BlockReference)tx.GetObject(r.Br.ObjectId, OpenMode.ForWrite);
                if (pieces.Count == 0) { src.Erase(); res.Erased++; continue; }
                Point3d basePos = src.Position;
                for (int k = 0; k < pieces.Count; k++)
                {
                    // every piece is a FRESH insert of the dynamic block with its length set once (copies of an
                    // already re-sized reference keep a stale hatch and break later grip stretching)
                    double len = (pieces[k][1] - pieces[k][0]) / Math.Abs(src.ScaleFactors.X);
                    BlockReference t = BlockAlong.Recreate(tx, ms, src, cfg.length_param, len, notes, k == pieces.Count - 1);
                    t.Position = r.A + r.U * pieces[k][0] - r.Perp * (r.W / 2);
                    if (k > 0) res.Copied++;
                }
                res.Modified++;
                trimmed++;
            }
            foreach (string n in notes.Distinct().Take(10)) res.Notes.Add(n);
            return plan.Places.Count + " ek parça yerleştirildi, " + trimmed + " tava kısaltıldı/bölündü" + (plan.Existing > 0 ? ", " + plan.Existing + " mevcut parça atlandı" : "");
        }

        static IEnumerable<ObjectId> Ids(Database db, Dictionary<string, object> a)
        {
            List<object> hs = J.Arr(J.Get(a, "handles"));
            if (hs == null || hs.Count == 0) return null;
            List<ObjectId> r = new List<ObjectId>();
            foreach (object h in hs)
            {
                ObjectId id;
                if (db.TryGetObjectId(new Handle(Convert.ToInt64(J.NormalizeHandle(Convert.ToString(h)), 16)), out id)) r.Add(id);
            }
            return r;
        }

        static FitConfig Cfg(Dictionary<string, object> a)
        {
            FitConfig cfg = FitConfig.Load();
            if (J.Get(a, "trim") != null) cfg.trim = J.Bool(a, "trim");
            return cfg;
        }

        public static string Summary(Document doc, Dictionary<string, object> a)
        {
            try
            {
                using (Transaction tx = doc.Database.TransactionManager.StartOpenCloseTransaction())
                    return Analyze(tx, doc.Database, Ids(doc.Database, a), Cfg(a)).Summary();
            }
            catch (System.Exception ex) { return "(analiz yapılamadı: " + ex.Message + ")"; }
        }

        public static void Run(Transaction tx, Database db, Dictionary<string, object> a, ApplyResult res)
        {
            FitConfig cfg = Cfg(a);
            Plan p = Analyze(tx, db, Ids(db, a), cfg);
            res.Notes.Add(Apply(tx, db, p, cfg, res) + ".");
        }
    }

    public class FittingCommands
    {
        [CommandMethod("AIDBAGLANTI", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void PlaceFittings()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            try
            {
                FitConfig cfg = FitConfig.Load();
                List<ObjectId> ids = null;
                PromptSelectionResult sr = ed.SelectImplied();
                if (sr.Status != PromptStatus.OK)
                {
                    PromptSelectionOptions o = new PromptSelectionOptions { MessageForAdding = "\nKablo tavalarını seçin <Enter = çizimdeki tüm '" + cfg.straight_block + "' blokları>: " };
                    sr = ed.GetSelection(o);
                    if (sr.Status == PromptStatus.Cancel) return;
                }
                if (sr.Status == PromptStatus.OK) ids = sr.Value.GetObjectIds().ToList();

                Fittings.Plan plan;
                using (Transaction tx = doc.Database.TransactionManager.StartTransaction())
                {
                    plan = Fittings.Analyze(tx, doc.Database, ids, cfg);
                    tx.Commit();
                }
                string sum = plan.Summary();
                ed.WriteMessage("\n" + sum + "\n");
                Metraj.Raise("Ek parçalar: " + sum);
                if (plan.Places.Count == 0 && plan.RunList.All(r => r.Cuts.Count == 0)) return;

                PromptKeywordOptions ko = new PromptKeywordOptions("\nUygulansın mı? [Evet/Hayir/Ayarlar] <Evet>: ", "Evet Hayir Ayarlar") { AllowNone = true };
                PromptResult kr = ed.GetKeywords(ko);
                if (kr.Status == PromptStatus.Cancel || (kr.Status == PromptStatus.OK && kr.StringResult == "Hayir")) return;
                if (kr.Status == PromptStatus.OK && kr.StringResult == "Ayarlar") { FitConfig.OpenInEditor(); ed.WriteMessage("\nfittings.json açıldı; kaydedip AIDBAGLANTI'yı tekrar çalıştırın.\n"); return; }

                ApplyResult res = new ApplyResult();
                using (Transaction tx = doc.Database.TransactionManager.StartTransaction())
                {
                    plan = Fittings.Analyze(tx, doc.Database, ids, cfg);
                    Fittings.Apply(tx, doc.Database, plan, cfg, res);
                    tx.Commit();
                }
                string msg = "Uygulandı: " + res + ". Geri almak için UNDO (tek adım).";
                ed.WriteMessage("\n" + msg + "\n");
                Metraj.Raise(msg);
                AppLog.Write("AIDBAGLANTI [" + Path.GetFileName(doc.Name) + "] " + res);
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nEk parça hatası: " + ex.Message + "\n");
                Metraj.Raise("Ek parça hatası: " + ex.Message);
            }
        }
    }
}