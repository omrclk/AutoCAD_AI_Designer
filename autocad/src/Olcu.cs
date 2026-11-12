using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace CadAiDesigner
{
    /// <summary>Last AIDOLCU choices. Json is the cross-session copy; the drawing NOD wins when its revision is newer.</summary>
    public class OlcuSettings
    {
        public double mesafe { get; set; }
        public bool merkez { get; set; } = true;
        public bool toplam { get; set; } = true;
        public string taraf { get; set; } = "UstSol";
        public string mod { get; set; } = "Zincir";
        public double tolerans { get; set; }
        public long rev { get; set; }

        const string NodKey = "CADAI_OLCU";
        const string RecKey = "AYAR";
        static string FilePath { get { return Path.Combine(AppPaths.Dir, "olcu.json"); } }

        public void Normalize()
        {
            if (taraf != "AltSag" && taraf != "Tumu") taraf = "UstSol";
            if (mod != "Satir") mod = "Zincir";
            if (double.IsNaN(mesafe) || double.IsInfinity(mesafe) || mesafe < 0) mesafe = 0;
            if (double.IsNaN(tolerans) || double.IsInfinity(tolerans) || tolerans < 0) tolerans = 0;
            if (rev < 0) rev = 0;
        }

        public static OlcuSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    OlcuSettings s = new JavaScriptSerializer().Deserialize<OlcuSettings>(File.ReadAllText(FilePath, Encoding.UTF8));
                    if (s != null) { s.Normalize(); return s; }
                }
            }
            catch (System.Exception) { }
            return new OlcuSettings();
        }

        public void Save()
        {
            try { Normalize(); File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(this), Encoding.UTF8); }
            catch (System.Exception) { }
        }

        public void BumpSave()
        {
            Normalize();
            rev = rev + 1;
            Save();
        }

        public static OlcuSettings Active(Database db)
        {
            OlcuSettings file = Load();
            OlcuSettings draw = Read(db);
            if (draw != null && draw.rev > file.rev) return draw;
            return file;
        }

        public static OlcuSettings Read(Database db)
        {
            try
            {
                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    DBDictionary nod = (DBDictionary)tx.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
                    if (!nod.Contains(NodKey)) { tx.Commit(); return null; }
                    DBDictionary d = tx.GetObject(nod.GetAt(NodKey), OpenMode.ForRead) as DBDictionary;
                    if (d == null || !d.Contains(RecKey)) { tx.Commit(); return null; }
                    Xrecord xr = tx.GetObject(d.GetAt(RecKey), OpenMode.ForRead) as Xrecord;
                    if (xr == null || xr.Data == null) { tx.Commit(); return null; }
                    OlcuSettings s = new OlcuSettings();
                    bool any = false;
                    foreach (TypedValue tv in xr.Data)
                    {
                        if (tv.TypeCode != (int)DxfCode.Text || tv.Value == null) continue;
                        string raw = Convert.ToString(tv.Value) ?? "";
                        int eq = raw.IndexOf('=');
                        if (eq <= 0) continue;
                        string key = raw.Substring(0, eq);
                        string val = raw.Substring(eq + 1);
                        any = true;
                        if (key == "mesafe") s.mesafe = Parse(val);
                        else if (key == "merkez") s.merkez = val == "1";
                        else if (key == "toplam") s.toplam = val == "1";
                        else if (key == "taraf") s.taraf = val;
                        else if (key == "mod") s.mod = val;
                        else if (key == "tolerans") s.tolerans = Parse(val);
                        else if (key == "rev") { long n; if (long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) s.rev = n; }
                    }
                    tx.Commit();
                    if (!any) return null;
                    s.Normalize();
                    return s;
                }
            }
            catch (System.Exception) { return null; }
        }

        public void Write(Transaction tx, Database db)
        {
            Normalize();
            DBDictionary nod = (DBDictionary)tx.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForWrite);
            DBDictionary d;
            if (nod.Contains(NodKey))
                d = (DBDictionary)tx.GetObject(nod.GetAt(NodKey), OpenMode.ForWrite);
            else
            {
                d = new DBDictionary();
                nod.SetAt(NodKey, d);
                tx.AddNewlyCreatedDBObject(d, true);
            }
            Xrecord xr;
            if (d.Contains(RecKey))
                xr = (Xrecord)tx.GetObject(d.GetAt(RecKey), OpenMode.ForWrite);
            else
            {
                xr = new Xrecord();
                d.SetAt(RecKey, xr);
                tx.AddNewlyCreatedDBObject(xr, true);
            }
            xr.Data = new ResultBuffer(
                new TypedValue((int)DxfCode.Text, "mesafe=" + Num(mesafe)),
                new TypedValue((int)DxfCode.Text, "merkez=" + (merkez ? "1" : "0")),
                new TypedValue((int)DxfCode.Text, "toplam=" + (toplam ? "1" : "0")),
                new TypedValue((int)DxfCode.Text, "taraf=" + taraf),
                new TypedValue((int)DxfCode.Text, "mod=" + mod),
                new TypedValue((int)DxfCode.Text, "tolerans=" + Num(tolerans)),
                new TypedValue((int)DxfCode.Text, "rev=" + rev.ToString(CultureInfo.InvariantCulture)));
        }

        static double Parse(string s)
        {
            double v;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            return 0;
        }

        static string Num(double v) { return v.ToString("G17", CultureInfo.InvariantCulture); }
    }

    /// <summary>AIDOLCU / OLC: horizontal and vertical dimension chains through selected blocks.</summary>
    public class OlcuCommands
    {
        const double MergeTol = 1.0;

        struct Sides
        {
            public bool Ust, Alt, Sol, Sag;
            public static Sides From(string taraf)
            {
                if (taraf == "AltSag") return new Sides { Alt = true, Sag = true };
                if (taraf == "Tumu") return new Sides { Ust = true, Alt = true, Sol = true, Sag = true };
                return new Sides { Ust = true, Sol = true };
            }
        }

        class Item
        {
            public Point3d P;
            public double MinX, MinY, MaxX, MaxY;
        }

        class Pending
        {
            public List<Point3d> Origins;
            public bool Horizontal;
            public double DimCoord;
            public int OutSign;
            public int Segments;
        }

        /// <summary>Center of the block's own geometry (attributes excluded) in WCS; falls back to the reference extents.</summary>
        internal static Point3d Center(Transaction tx, BlockReference br)
        {
            try
            {
                BlockTableRecord btr = (BlockTableRecord)tx.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                bool any = false; Extents3d ex = new Extents3d();
                foreach (ObjectId id in btr)
                {
                    Entity e = tx.GetObject(id, OpenMode.ForRead) as Entity;
                    if (e == null || e is AttributeDefinition || !e.Visible) continue;
                    try { Extents3d x = e.GeometricExtents; if (!any) { ex = x; any = true; } else ex.AddExtents(x); } catch (System.Exception) { }
                }
                if (any)
                {
                    Point3d c = new Point3d((ex.MinPoint.X + ex.MaxPoint.X) / 2, (ex.MinPoint.Y + ex.MaxPoint.Y) / 2, (ex.MinPoint.Z + ex.MaxPoint.Z) / 2);
                    return c.TransformBy(br.BlockTransform);
                }
            }
            catch (System.Exception) { }
            try { Extents3d x = br.GeometricExtents; return x.MinPoint + (x.MaxPoint - x.MinPoint) / 2; } catch (System.Exception) { return br.Position; }
        }

        static string TarafLabel(string taraf)
        {
            if (taraf == "AltSag") return "Alt-Sa\u011f";
            if (taraf == "Tumu") return "T\u00fcm\u00fc";
            return "\u00dcst-Sol";
        }

        static string NormTaraf(string s)
        {
            if (string.IsNullOrEmpty(s)) return "UstSol";
            string t = s.Trim().ToUpperInvariant();
            if (t.StartsWith("ALT")) return "AltSag";
            if (t.StartsWith("TUM") || t.StartsWith("T\u00dcM")) return "Tumu";
            return "UstSol";
        }

        static string NormMod(string s)
        {
            if (!string.IsNullOrEmpty(s) && s.Trim().StartsWith("Sat", StringComparison.OrdinalIgnoreCase)) return "Satir";
            return "Zincir";
        }

        static bool AskKeywords(Editor ed, PromptKeywordOptions k, out string word)
        {
            word = null;
            PromptResult r = ed.GetKeywords(k);
            if (r.Status == PromptStatus.OK && !string.IsNullOrEmpty(r.StringResult)) { word = r.StringResult; return true; }
            return false;
        }

        [CommandMethod("AIDOLCU", CommandFlags.Modal | CommandFlags.UsePickSet)]
        [CommandMethod("OLC", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void Olcu()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor; Database db = doc.Database;
            OlcuSettings cfg = OlcuSettings.Active(db);
            SelectionFilter filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "INSERT") });

            List<ObjectId> ids = null;
            PromptSelectionResult sr = ed.SelectImplied();
            if (sr.Status == PromptStatus.OK)
            {
                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    ids = new List<ObjectId>();
                    if (sr.Value != null)
                    {
                        foreach (ObjectId i in sr.Value.GetObjectIds())
                        {
                            try { if (tx.GetObject(i, OpenMode.ForRead) is BlockReference) ids.Add(i); }
                            catch (System.Exception) { }
                        }
                    }
                    tx.Commit();
                }
                if (ids.Count < 2) ids = null;
            }
            while (ids == null)
            {
                bool kw = false; string word = null;
                PromptSelectionOptions so = new PromptSelectionOptions
                {
                    MessageForAdding = "\n\u00d6l\u00e7\u00fclecek bloklar\u0131 se\u00e7in [Nokta/Toplam/Taraf/Mod/Tolerans/Katman]: "
                };
                so.Keywords.Add("Nokta"); so.Keywords.Add("Toplam"); so.Keywords.Add("Taraf");
                so.Keywords.Add("Mod"); so.Keywords.Add("Tolerans"); so.Keywords.Add("Katman");
                so.KeywordInput += (o, e) => { kw = true; word = (e.Input ?? "").Trim(); throw new Autodesk.AutoCAD.Runtime.Exception(ErrorStatus.OK); };
                try { sr = ed.GetSelection(so, filter); } catch (Autodesk.AutoCAD.Runtime.Exception) { if (!kw) throw; }
                if (kw)
                {
                    if (string.Equals(word, "Nokta", StringComparison.OrdinalIgnoreCase))
                    {
                        PromptKeywordOptions k = new PromptKeywordOptions("\n\u00d6l\u00e7\u00fc noktas\u0131 [Merkez/Ekleme] <" + (cfg.merkez ? "Merkez" : "Ekleme") + ">: ", "Merkez Ekleme") { AllowNone = true };
                        string picked;
                        if (AskKeywords(ed, k, out picked)) { cfg.merkez = picked == "Merkez"; cfg.BumpSave(); }
                        continue;
                    }
                    if (string.Equals(word, "Toplam", StringComparison.OrdinalIgnoreCase))
                    {
                        PromptKeywordOptions k = new PromptKeywordOptions("\nToplam \u00f6l\u00e7\u00fc eklensin mi? [Evet/Hayir] <" + (cfg.toplam ? "Evet" : "Hayir") + ">: ", "Evet Hayir") { AllowNone = true };
                        string picked;
                        if (AskKeywords(ed, k, out picked)) { cfg.toplam = picked == "Evet"; cfg.BumpSave(); }
                        continue;
                    }
                    if (string.Equals(word, "Taraf", StringComparison.OrdinalIgnoreCase))
                    {
                        PromptKeywordOptions k = new PromptKeywordOptions("\nTaraf [\u00dcst-Sol/Alt-Sa\u011f/T\u00fcm\u00fc] <" + TarafLabel(cfg.taraf) + ">: ") { AllowNone = true };
                        k.AppendKeywordsToMessage = false;
                        k.Keywords.Add("UstSol", "\u00dcst-Sol", "\u00dcst-Sol");
                        k.Keywords.Add("AltSag", "Alt-Sa\u011f", "Alt-Sa\u011f");
                        k.Keywords.Add("Tumu", "T\u00fcm\u00fc", "T\u00fcm\u00fc");
                        k.Keywords.Default = cfg.taraf;
                        string picked;
                        if (AskKeywords(ed, k, out picked)) { cfg.taraf = NormTaraf(picked); cfg.BumpSave(); }
                        continue;
                    }
                    if (string.Equals(word, "Mod", StringComparison.OrdinalIgnoreCase))
                    {
                        PromptKeywordOptions k = new PromptKeywordOptions("\nMod [Zincir/Satir] <" + cfg.mod + ">: ") { AllowNone = true };
                        k.AppendKeywordsToMessage = false;
                        k.Keywords.Add("Zincir"); k.Keywords.Add("Satir");
                        k.Keywords.Default = cfg.mod;
                        string picked;
                        if (AskKeywords(ed, k, out picked)) { cfg.mod = NormMod(picked); cfg.BumpSave(); }
                        continue;
                    }
                    if (string.Equals(word, "Tolerans", StringComparison.OrdinalIgnoreCase))
                    {
                        PromptDistanceOptions to = new PromptDistanceOptions("\nSat\u0131r/s\u00fctun tolerans\u0131 (0 = otomatik)")
                        {
                            AllowNone = true, AllowNegative = false, AllowZero = true, Only2d = true,
                            UseDefaultValue = true, DefaultValue = cfg.tolerans > 0 ? cfg.tolerans : 0
                        };
                        PromptDoubleResult tr = ed.GetDistance(to);
                        if (tr.Status == PromptStatus.OK) { cfg.tolerans = tr.Value < 0 ? 0 : tr.Value; cfg.BumpSave(); }
                        continue;
                    }
                    if (!string.Equals(word, "Katman", StringComparison.OrdinalIgnoreCase)) continue;
                    PromptResult lr = ed.GetString(new PromptStringOptions("\nKatman ad\u0131(lar\u0131) (virg\u00fclle): ") { AllowSpaces = true });
                    if (lr.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(lr.StringResult)) return;
                    sr = ed.SelectAll(new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "INSERT"), new TypedValue((int)DxfCode.LayerName, lr.StringResult.Trim()) }));
                }
                if (sr == null || sr.Status != PromptStatus.OK) return;
                ids = sr.Value.GetObjectIds().ToList();
                if (ids.Count < 2) { ed.WriteMessage("\nEn az 2 blok gerekli."); ids = null; }
            }

            double dist = cfg.mesafe;
            if (dist <= 1e-9)
            {
                double sc = db.Dimscale > 1e-9 ? db.Dimscale : 1.0;
                dist = 4.0 * db.Dimtxt * sc;
                if (dist <= 1e-9) dist = 1.0;
            }
            PromptDistanceOptions po = new PromptDistanceOptions("\n\u00d6l\u00e7\u00fc mesafesi")
            {
                AllowNone = true, AllowNegative = false, AllowZero = false, Only2d = true,
                UseDefaultValue = true, DefaultValue = dist
            };
            PromptDoubleResult dr = ed.GetDistance(po);
            if (dr.Status == PromptStatus.OK) dist = dr.Value;
            else if (dr.Status != PromptStatus.None) return;
            if (dist <= 1e-9) { ed.WriteMessage("\nMesafe s\u0131f\u0131rdan b\u00fcy\u00fck olmal\u0131."); return; }
            cfg.mesafe = dist;
            cfg.BumpSave();

            int h = 0, v = 0;
            using (doc.LockDocument())
            using (Transaction tx = db.TransactionManager.StartTransaction())
            {
                LayerTableRecord cl = tx.GetObject(db.Clayer, OpenMode.ForRead) as LayerTableRecord;
                if (cl != null && (cl.IsLocked || cl.IsFrozen))
                {
                    ed.WriteMessage("\nGe\u00e7erli katman kilitli veya donuk (" + cl.Name + "). \u00d6l\u00e7\u00fc eklenemedi.\n");
                    return;
                }

                List<Item> items = new List<Item>();
                foreach (ObjectId id in ids)
                {
                    BlockReference br = null;
                    try { br = tx.GetObject(id, OpenMode.ForRead) as BlockReference; }
                    catch (System.Exception) { continue; }
                    if (br == null) continue;
                    Point3d p = cfg.merkez ? Center(tx, br) : br.Position;
                    p = new Point3d(p.X, p.Y, 0);
                    Item it = new Item { P = p, MinX = p.X, MinY = p.Y, MaxX = p.X, MaxY = p.Y };
                    try
                    {
                        Extents3d ex = br.GeometricExtents;
                        it.MinX = ex.MinPoint.X; it.MaxX = ex.MaxPoint.X;
                        it.MinY = ex.MinPoint.Y; it.MaxY = ex.MaxPoint.Y;
                    }
                    catch (System.Exception) { }
                    items.Add(it);
                }
                List<Point3d> pts = MergePoints(items.ConvertAll(i => i.P));
                if (pts.Count < 2) { ed.WriteMessage("\nFarkl\u0131 konumda en az 2 blok gerekli."); return; }

                BlockTableRecord space = (BlockTableRecord)tx.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                List<double> usedH = new List<double>(), usedV = new List<double>();
                List<Pending> pending = new List<Pending>();
                Measure(tx, space, db, items, pts, cfg, dist, true, true, usedH, usedV, pending, ref h, ref v);
                if (cfg.mod == "Satir")
                {
                    double rowTol = GroupTol(items, true, cfg.tolerans);
                    double colTol = GroupTol(items, false, cfg.tolerans);
                    foreach (List<Item> row in Group(items, true, rowTol))
                    {
                        List<Point3d> rp = MergePoints(row.ConvertAll(i => i.P));
                        if (rp.Count < 2) continue;
                        Measure(tx, space, db, row, rp, cfg, dist, true, false, usedH, usedV, pending, ref h, ref v);
                    }
                    foreach (List<Item> col in Group(items, false, colTol))
                    {
                        List<Point3d> cp = MergePoints(col.ConvertAll(i => i.P));
                        if (cp.Count < 2) continue;
                        Measure(tx, space, db, col, cp, cfg, dist, false, true, usedH, usedV, pending, ref h, ref v);
                    }
                }
                foreach (Pending p in pending)
                {
                    int extra = AddTotal(tx, space, db, p, dist, cfg.toplam, p.Horizontal ? usedH : usedV);
                    if (p.Horizontal) h += extra; else v += extra;
                }

                if (h + v > 0)
                {
                    try { cfg.Write(tx, db); } catch (System.Exception) { }
                    tx.Commit();
                }
                if (h == 0) ed.WriteMessage("\nYatay \u00f6l\u00e7\u00fc atland\u0131: en az 2 farkl\u0131 X gerekli.");
                if (v == 0) ed.WriteMessage("\nD\u00fc\u015fey \u00f6l\u00e7\u00fc atland\u0131: en az 2 farkl\u0131 Y gerekli.");
                ed.WriteMessage("\nYatay: " + h + " \u00f6l\u00e7\u00fc, D\u00fc\u015fey: " + v + " \u00f6l\u00e7\u00fc.\n");
            }
        }

        static void Measure(Transaction tx, BlockTableRecord space, Database db, List<Item> items, List<Point3d> pts, OlcuSettings cfg, double offset, bool horizontal, bool vertical, List<double> usedH, List<double> usedV, List<Pending> pending, ref int h, ref int v)
        {
            if (items.Count == 0 || pts.Count < 2) return;
            double minX = items.Min(i => i.MinX), maxX = items.Max(i => i.MaxX);
            double minY = items.Min(i => i.MinY), maxY = items.Max(i => i.MaxY);
            Sides s = Sides.From(cfg.taraf);
            if (horizontal)
            {
                if (s.Ust) h += Chain(tx, space, db, Stations(pts, true, true), true, maxY + offset, +1, usedH, pending);
                if (s.Alt) h += Chain(tx, space, db, Stations(pts, true, false), true, minY - offset, -1, usedH, pending);
            }
            if (vertical)
            {
                if (s.Sol) v += Chain(tx, space, db, Stations(pts, false, false), false, minX - offset, -1, usedV, pending);
                if (s.Sag) v += Chain(tx, space, db, Stations(pts, false, true), false, maxX + offset, +1, usedV, pending);
            }
        }

        static int Chain(Transaction tx, BlockTableRecord space, Database db, List<Point3d> origins, bool horizontal, double dimCoord, int outSign, List<double> used, List<Pending> pending)
        {
            if (origins == null || origins.Count < 2) return 0;
            if (used.Any(c => SameCoord(c, dimCoord))) return 0;
            double rot = horizontal ? 0.0 : Math.PI / 2.0;
            int n = 0;
            for (int i = 0; i + 1 < origins.Count; i++)
            {
                Point3d a = origins[i], b = origins[i + 1];
                double len = horizontal ? Math.Abs(b.X - a.X) : Math.Abs(b.Y - a.Y);
                if (len <= MergeTol) continue;
                AddDim(tx, space, db, rot, a, b, DimPoint(a, b, horizontal, dimCoord));
                n++;
            }
            if (n == 0) return 0;
            used.Add(dimCoord);
            pending.Add(new Pending { Origins = origins, Horizontal = horizontal, DimCoord = dimCoord, OutSign = outSign, Segments = n });
            return n;
        }

        static int AddTotal(Transaction tx, BlockTableRecord space, Database db, Pending p, double offset, bool want, List<double> used)
        {
            if (!want || p == null || p.Segments < 2 || p.Origins == null || p.Origins.Count < 3) return 0;
            double tc = p.DimCoord + p.OutSign * offset;
            if (used.Any(c => SameCoord(c, tc))) return 0;
            Point3d a = p.Origins[0], b = p.Origins[p.Origins.Count - 1];
            double len = p.Horizontal ? Math.Abs(b.X - a.X) : Math.Abs(b.Y - a.Y);
            if (len <= MergeTol) return 0;
            double rot = p.Horizontal ? 0.0 : Math.PI / 2.0;
            AddDim(tx, space, db, rot, a, b, DimPoint(a, b, p.Horizontal, tc));
            used.Add(tc);
            return 1;
        }

        static void AddDim(Transaction tx, BlockTableRecord space, Database db, double rotation, Point3d a, Point3d b, Point3d line)
        {
            a = new Point3d(a.X, a.Y, 0);
            b = new Point3d(b.X, b.Y, 0);
            line = new Point3d(line.X, line.Y, 0);
            RotatedDimension d = new RotatedDimension(rotation, a, b, line, "", db.Dimstyle);
            d.SetDatabaseDefaults(db);
            d.XLine1Point = a;
            d.XLine2Point = b;
            d.DimLinePoint = line;
            d.Rotation = rotation;
            d.DimensionText = "";
            d.DimensionStyle = db.Dimstyle;
            d.LayerId = db.Clayer;
            d.Normal = Vector3d.ZAxis;
            d.Elevation = 0.0;
            space.AppendEntity(d);
            tx.AddNewlyCreatedDBObject(d, true);
            try { d.UsingDefaultTextPosition = true; } catch (System.Exception) { }
        }

        static Point3d DimPoint(Point3d a, Point3d b, bool horizontal, double dimCoord)
        {
            if (horizontal) return new Point3d((a.X + b.X) / 2.0, dimCoord, 0);
            return new Point3d(dimCoord, (a.Y + b.Y) / 2.0, 0);
        }

        static List<Point3d> MergePoints(List<Point3d> raw)
        {
            List<Point3d> pts = new List<Point3d>();
            if (raw == null) return pts;
            foreach (Point3d p in raw)
            {
                Point3d q = new Point3d(p.X, p.Y, 0);
                bool hit = false;
                for (int i = 0; i < pts.Count; i++) if (pts[i].DistanceTo(q) <= MergeTol) { hit = true; break; }
                if (!hit) pts.Add(q);
            }
            return pts;
        }

        /// <summary>One station per coordinate within <see cref="MergeTol"/>. The kept point is the block nearest the dimension line.</summary>
        static List<Point3d> Stations(List<Point3d> pts, bool horizontal, bool nearPositive)
        {
            List<Point3d> sorted = horizontal
                ? pts.OrderBy(p => p.X).ThenBy(p => p.Y).ToList()
                : pts.OrderBy(p => p.Y).ThenBy(p => p.X).ToList();
            var clusters = new List<List<Point3d>>();
            foreach (Point3d p in sorted)
            {
                double key = horizontal ? p.X : p.Y;
                if (clusters.Count == 0 || key - (horizontal ? clusters[clusters.Count - 1][0].X : clusters[clusters.Count - 1][0].Y) > MergeTol)
                    clusters.Add(new List<Point3d>());
                clusters[clusters.Count - 1].Add(p);
            }
            List<Point3d> reps = new List<Point3d>();
            foreach (List<Point3d> c in clusters)
            {
                Point3d best = c[0];
                for (int i = 1; i < c.Count; i++) if (Nearer(c[i], best, horizontal, nearPositive)) best = c[i];
                reps.Add(best);
            }
            // Diameter merge: a station joins the open group only when it is within tolerance of that group's first coordinate.
            List<Point3d> merged = new List<Point3d>();
            List<Point3d> bucket = new List<Point3d>();
            foreach (Point3d p in reps)
            {
                if (bucket.Count > 0)
                {
                    double anchor = horizontal ? bucket[0].X : bucket[0].Y;
                    double key = horizontal ? p.X : p.Y;
                    if (key - anchor > MergeTol)
                    {
                        merged.Add(Pick(bucket, horizontal, nearPositive));
                        bucket.Clear();
                    }
                }
                bucket.Add(p);
            }
            if (bucket.Count > 0) merged.Add(Pick(bucket, horizontal, nearPositive));
            return merged;
        }

        static Point3d Pick(List<Point3d> c, bool horizontal, bool nearPositive)
        {
            Point3d best = c[0];
            for (int i = 1; i < c.Count; i++) if (Nearer(c[i], best, horizontal, nearPositive)) best = c[i];
            return best;
        }

        static bool Nearer(Point3d p, Point3d best, bool horizontal, bool nearPositive)
        {
            double v = horizontal ? p.Y : p.X;
            double b = horizontal ? best.Y : best.X;
            return nearPositive ? v > b : v < b;
        }

        static bool SameCoord(double a, double b)
        {
            return Math.Abs(a - b) <= 1e-4 * Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b)));
        }

        /// <summary>User tolerance, otherwise half the smallest block height (rows) or width (columns).</summary>
        static double GroupTol(List<Item> items, bool height, double user)
        {
            if (user > 1e-9) return user;
            double min = double.MaxValue;
            foreach (Item it in items)
            {
                double s = height ? it.MaxY - it.MinY : it.MaxX - it.MinX;
                if (s > 1e-9 && s < min) min = s;
            }
            if (min == double.MaxValue) return MergeTol;
            return min * 0.5;
        }

        /// <summary>Groups whose key (Y for a row, X for a column) stays within tolerance of the group's first value.</summary>
        static List<List<Item>> Group(List<Item> items, bool byY, double tol)
        {
            List<Item> ordered = byY
                ? items.OrderBy(i => i.P.Y).ThenBy(i => i.P.X).ToList()
                : items.OrderBy(i => i.P.X).ThenBy(i => i.P.Y).ToList();
            var groups = new List<List<Item>>();
            foreach (Item it in ordered)
            {
                double key = byY ? it.P.Y : it.P.X;
                if (groups.Count == 0) { groups.Add(new List<Item> { it }); continue; }
                List<Item> g = groups[groups.Count - 1];
                double anchor = byY ? g[0].P.Y : g[0].P.X;
                if (key - anchor <= tol) g.Add(it);
                else groups.Add(new List<Item> { it });
            }
            return groups;
        }
    }
}
