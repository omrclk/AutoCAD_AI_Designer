using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace CadAiDesigner
{
    /// <summary>Remembered AIDTAVA defaults (%LOCALAPPDATA%\CadAiDesigner\tava.json).</summary>
    public class TavaSettings
    {
        public double en { get; set; } = 300;
        public string yukseklik { get; set; } = "40";
        public string kalinlik { get; set; } = "2";
        public bool baglanti { get; set; } = true;
        public string mod { get; set; } = "Otomatik";
        /// <summary>Drawing unit: Oto, Mm, Cm or Metre. Widths in this file stay in millimetres.</summary>
        public string birim { get; set; } = "Oto";
        /// <summary>Last confident or manually chosen unit. Oto falls back to this when two scores are close.</summary>
        public string birimSon { get; set; }
        public string layers { get; set; } = "*";
        public string layer { get; set; } = "E_CT";
        /// <summary>ACI color → tray width. ByLayer is matched by the layer's visible ACI, same as an explicit color.</summary>
        public List<RenkOlcu> renkler { get; set; } = new List<RenkOlcu>();
        /// <summary>Fixed record of the Standart map (1=100 … 6=600). The command always uses that map, not edits here.</summary>
        public List<RenkOlcu> standartRenkler { get; set; }

        public double EnForRenk(int aci, out bool mapped)
        {
            mapped = false;
            if (renkler != null)
                foreach (RenkOlcu r in renkler)
                    if (r != null && r.renk == aci && r.en > 0) { mapped = true; return r.en; }
            return en > 0 ? en : 300;
        }

        static string FilePath { get { return Path.Combine(AppPaths.Dir, "tava.json"); } }
        public static TavaSettings Load()
        {
            try { if (File.Exists(FilePath)) return new JavaScriptSerializer().Deserialize<TavaSettings>(File.ReadAllText(FilePath)) ?? new TavaSettings(); }
            catch (System.Exception) { }
            return new TavaSettings();
        }
        public void Save() { try { File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(this)); } catch (System.Exception) { } }
    }

    public class RenkOlcu
    {
        public int renk { get; set; }
        public double en { get; set; }
    }

    /// <summary>Pure 2D geometry of AIDTAVA: double-line pairing, EN snapping, centrelines (no AutoCAD objects).</summary>
    internal static class TavaGeo
    {
        public static readonly double[] Standard = { 50, 100, 150, 200, 300, 400, 500, 600 };
        public const double MinGap = 30, MaxGap = 700;

        public class Line2 { public Point2d S, E; public double W; public Line2(Point2d s, Point2d e, double w = 0) { S = s; E = e; W = w; } public double Len { get { return S.GetDistanceTo(E); } } }
        public class Center { public Point2d S, E; public double En; public double EnMm; public bool Pair; public string Renk; }

        /// <summary>Nearest standard width when within 15 %, otherwise the measured value rounded to 1.</summary>
        public static double Snap(double d) { return Snap(d, 1); }

        /// <summary>k = drawing units per mm (1 mm, 0.1 cm, 0.001 m). Standard list and rounding follow the units.</summary>
        public static double Snap(double d, double k)
        {
            double best = Standard.Select(s => s * k).OrderBy(s => Math.Abs(s - d)).First();
            return Math.Abs(best - d) <= best * 0.15 ? Math.Round(best, 6) : Math.Round(Math.Round(d / k) * k, 6);
        }

        public static double UnitsPerMm(Autodesk.AutoCAD.DatabaseServices.UnitsValue u)
        {
            switch (u)
            {
                case Autodesk.AutoCAD.DatabaseServices.UnitsValue.Centimeters: return 0.1;
                case Autodesk.AutoCAD.DatabaseServices.UnitsValue.Decimeters: return 0.01;
                case Autodesk.AutoCAD.DatabaseServices.UnitsValue.Meters: return 0.001;
                default: return 1;
            }
        }

        /// <summary>Direction normalised so trays read left-to-right (or bottom-to-top).</summary>
        public static void Normalize(ref Point2d s, ref Point2d e)
        {
            Vector2d v = e - s;
            if (v.X < -1e-6 || (Math.Abs(v.X) <= 1e-6 && v.Y < 0)) { Point2d t = s; s = e; e = t; }
        }

        public static List<Center> Build(List<Line2> lines, string mode, double defEn, out int pairs, out int singles, out int ignored)
        { return Build(lines, mode, defEn, 1, out pairs, out singles, out ignored); }

        public static List<Center> Build(List<Line2> lines, string mode, double defEn, double k, out int pairs, out int singles, out int ignored)
        {
            double minGap = MinGap * k, maxGap0 = MaxGap * k, eps = Math.Min(1, 1 * k * 10);
            pairs = singles = ignored = 0;
            List<Center> res = new List<Center>();
            if (string.Equals(mode, "Eksen", StringComparison.OrdinalIgnoreCase))
            {
                // Centreline mode: every edge is a tray axis at defEn. No double-line pairing.
                // Overlapping copies of the same edge (shared sides of adjacent closed polylines) collapse to one.
                foreach (Line2 a in MergeOverlaps(lines, Math.Max(k, 1e-4)))
                {
                    if (a.Len < 1e-6) continue;
                    Point2d s = a.S, e = a.E; Normalize(ref s, ref e);
                    res.Add(new Center { S = s, E = e, En = defEn });
                    singles++;
                }
                CollapseRuns(res, false);
                return res;
            }
            bool[] used = new bool[lines.Count];
            double maxGap = 0;
            if (mode != "Tek")
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    Line2 a = lines[i];
                    Vector2d u = (a.E - a.S).GetNormal();
                    Vector2d n = new Vector2d(-u.Y, u.X);
                    double best = double.MaxValue; List<int> cand = new List<int>();
                    for (int j = 0; j < lines.Count; j++)
                    {
                        if (j == i) continue;
                        Line2 b = lines[j];
                        Vector2d ub = (b.E - b.S).GetNormal();
                        if (Math.Abs(u.X * ub.Y - u.Y * ub.X) > 0.01) continue;           // not parallel (~0.6°)
                        double d = Math.Abs((b.S - a.S).DotProduct(n));
                        if (d < minGap || d > maxGap0) continue;
                        if (Overlap(a, b) < Math.Min(minGap, 0.5 * Math.Min(a.Len, b.Len))) continue;
                        if (d < best - eps) { best = d; cand.Clear(); cand.Add(j); } else if (Math.Abs(d - best) <= eps) cand.Add(j);
                    }
                    foreach (int j in cand)
                    {
                        Line2 b = lines[j];
                        double s0 = 0, s1 = a.Len;
                        double t0 = (b.S - a.S).DotProduct(u), t1 = (b.E - a.S).DotProduct(u);
                        double lo = Math.Max(s0, Math.Min(t0, t1)), hi = Math.Min(s1, Math.Max(t0, t1));
                        if (hi - lo < eps) continue;
                        double side = (b.S - a.S).DotProduct(n);
                        Point2d cs = a.S + u * lo + n * (side / 2), ce = a.S + u * hi + n * (side / 2);
                        used[i] = used[j] = true;
                        maxGap = Math.Max(maxGap, best);
                        if (!Dup(res, cs, ce, best)) { Normalize(ref cs, ref ce); res.Add(new Center { S = cs, E = ce, En = Snap(best, k), Pair = true }); pairs++; }
                    }
                }
            }
            for (int i = 0; i < lines.Count; i++)
            {
                if (used[i]) continue;
                Line2 a = lines[i];
                if (mode == "Cift" || (pairs > 0 && mode == "Otomatik")) { ignored++; continue; }   // end caps / unrelated lines in a double-line plan
                Point2d s = a.S, e = a.E; Normalize(ref s, ref e);
                res.Add(new Center { S = s, E = e, En = a.W > 1e-6 ? Snap(a.W, k) : defEn });
                singles++;
            }
            CollapseRuns(res, true);
            return res;
        }

        /// <summary>One tray per run. wide: centre lines up to one tray-width apart are the same double-line
        /// pair (outer polyline edge + inner line). Otherwise only near-coincident copies collapse.</summary>
        static void CollapseRuns(List<Center> res, bool wide)
        {
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = 0; i < res.Count && !changed; i++)
                {
                    for (int j = i + 1; j < res.Count; j++)
                    {
                        Center a = res[i], b = res[j];
                        double len = a.S.GetDistanceTo(a.E), bl = b.S.GetDistanceTo(b.E);
                        if (len < 1e-6 || bl < 1e-6) continue;
                        if (bl > len) { Center tmp = a; a = b; b = tmp; double tl = len; len = bl; bl = tl; }
                        Vector2d u = (a.E - a.S) / len, ub = (b.E - b.S) / bl;
                        if (Math.Abs(u.X * ub.Y - u.Y * ub.X) > 0.01) continue;
                        Vector2d n = new Vector2d(-u.Y, u.X);
                        double lat = Math.Max(Math.Abs((b.S - a.S).DotProduct(n)), Math.Abs((b.E - a.S).DotProduct(n)));
                        double allow = wide ? Math.Max(a.En, b.En) : Math.Max(a.En, b.En) * 0.05;
                        if (allow < 1e-4) allow = 1e-4;
                        if (lat > allow) continue;
                        double t0 = (b.S - a.S).DotProduct(u), t1 = (b.E - a.S).DotProduct(u);
                        double lo = Math.Min(t0, t1), hi = Math.Max(t0, t1);
                        double ov = Math.Min(len, hi) - Math.Max(0, lo);
                        if (ov < 0.5 * Math.Min(len, bl)) continue;
                        double u0 = Math.Min(0, lo), u1 = Math.Max(len, hi);
                        Point2d s = a.S + u * u0, e = a.S + u * u1;
                        double en = a.Pair && !b.Pair ? a.En : b.Pair && !a.Pair ? b.En : Math.Max(a.En, b.En);
                        Normalize(ref s, ref e);
                        res[i] = new Center { S = s, E = e, En = en, Pair = a.Pair || b.Pair };
                        res.RemoveAt(j);
                        changed = true;
                        break;
                    }
                }
            }
        }

        /// <summary>Collinear segments that overlap (not merely touch) become their union. Parallel rails stay separate.</summary>
        static List<Line2> MergeOverlaps(List<Line2> src, double tol)
        {
            List<Line2> cur = new List<Line2>();
            foreach (Line2 ln in src) if (ln != null && ln.Len > 1e-6) cur.Add(ln);
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = 0; i < cur.Count && !changed; i++)
                {
                    for (int j = i + 1; j < cur.Count; j++)
                    {
                        Line2 a = cur[i], b = cur[j];
                        Vector2d d = a.E - a.S;
                        double len = Math.Sqrt(d.X * d.X + d.Y * d.Y);
                        if (len < 1e-9) continue;
                        Vector2d u = d / len;
                        Vector2d db = b.E - b.S;
                        double bl = Math.Sqrt(db.X * db.X + db.Y * db.Y);
                        if (bl < 1e-9) continue;
                        Vector2d ub = db / bl;
                        if (Math.Abs(u.X * ub.Y - u.Y * ub.X) > 0.01) continue;
                        Vector2d n = new Vector2d(-u.Y, u.X);
                        if (Math.Abs((b.S - a.S).DotProduct(n)) > tol) continue;
                        if (Math.Abs((b.E - a.S).DotProduct(n)) > tol) continue;
                        double t0 = (b.S - a.S).DotProduct(u), t1 = (b.E - a.S).DotProduct(u);
                        double lo = Math.Min(t0, t1), hi = Math.Max(t0, t1);
                        double ov = Math.Min(len, hi) - Math.Max(0, lo);
                        if (ov <= tol) continue;
                        double u0 = Math.Min(0, lo), u1 = Math.Max(len, hi);
                        cur[i] = new Line2(a.S + u * u0, a.S + u * u1, Math.Max(a.W, b.W));
                        cur.RemoveAt(j);
                        changed = true;
                        break;
                    }
                }
            }
            return cur;
        }

        static double Overlap(Line2 a, Line2 b)
        {
            Vector2d u = (a.E - a.S).GetNormal();
            double t0 = (b.S - a.S).DotProduct(u), t1 = (b.E - a.S).DotProduct(u);
            return Math.Min(a.Len, Math.Max(t0, t1)) - Math.Max(0, Math.Min(t0, t1));
        }

        /// <summary>True when an existing centreline (new or an existing tray) already covers most of s-e.</summary>
        public static bool Covered(Point2d s, Point2d e, Point2d os, Point2d oe, double tol)
        {
            double len = s.GetDistanceTo(e), olen = os.GetDistanceTo(oe);
            if (len < 1e-6 || olen < 1e-6) return false;
            Vector2d u = (e - s) / len, uo = (oe - os) / olen;
            if (Math.Abs(u.X * uo.Y - u.Y * uo.X) > 0.01) return false;
            Vector2d n = new Vector2d(-uo.Y, uo.X);
            if (Math.Abs((s - os).DotProduct(n)) > tol) return false;
            double t0 = (s - os).DotProduct(uo), t1 = (e - os).DotProduct(uo);
            double ov = Math.Min(olen, Math.Max(t0, t1)) - Math.Max(0, Math.Min(t0, t1));
            return ov >= 0.5 * len;
        }

        static bool Dup(List<Center> res, Point2d s, Point2d e, double w) { return res.Any(c => Covered(s, e, c.S, c.E, w / 4)); }
    }

    /// <summary>Millimetres per drawing unit. Blocks stay defined in mm and are inserted at scale 1/factor.</summary>
    internal static class CizimBirimi
    {
        internal class Sonuc
        {
            public string Stored = "Oto", Ad = "Mm", Not = "", Metin = "", Skor = "";
            public double Factor = 1, K = 1;
            public bool Oto, Emin = true;
            public string Prompt { get { return Oto ? "Oto→" + Ad : Ad; } }
        }

        public static Sonuc Active;

        public static string Canon(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "Oto";
            string t = s.Trim();
            if (string.Equals(t, "Oto", StringComparison.OrdinalIgnoreCase) || string.Equals(t, "Auto", StringComparison.OrdinalIgnoreCase)) return "Oto";
            if (string.Equals(t, "Mm", StringComparison.OrdinalIgnoreCase)) return "Mm";
            if (string.Equals(t, "Cm", StringComparison.OrdinalIgnoreCase)) return "Cm";
            if (string.Equals(t, "Metre", StringComparison.OrdinalIgnoreCase) || string.Equals(t, "m", StringComparison.OrdinalIgnoreCase)) return "Metre";
            return "Oto";
        }

        public static double FactorOf(string canon) { return BirimOlcu.MmPerUnit(canon); }

        public static Sonuc Peek(Database db)
        {
            if (Active != null) return Active;
            return Resolve(db, null, null);
        }

        public static Sonuc Resolve(Database db, IEnumerable<ObjectId> sample, string stored)
        {
            string canon = Canon(stored ?? TavaSettings.Load().birim);
            Sonuc s = new Sonuc { Stored = canon, Oto = canon == "Oto" };
            if (!s.Oto)
            {
                s.Ad = canon;
                s.Factor = FactorOf(canon);
                s.K = s.Factor > 0 ? 1.0 / s.Factor : 1;
                s.Emin = true;
                s.Metin = "Birim: " + canon;
                return s;
            }
            string insAd = FromIns(db == null ? UnitsValue.Millimeters : db.Insunits);
            TavaSettings saved = TavaSettings.Load();
            string son = Canon(saved.birimSon);
            string fallback = (son == "Mm" || son == "Cm" || son == "Metre") ? son : (insAd == "Mm" || insAd == "Cm" || insAd == "Metre" ? insAd : "Mm");
            double[] lens = null;
            double x0 = 0, y0 = 0, x1 = 0, y1 = 0;
            bool haveBox = false;
            if (db != null) haveBox = Collect(db, sample, out lens, out x0, out y0, out x1, out y1);
            bool savedFallback = son == "Mm" || son == "Cm" || son == "Metre";
            BirimOlcu.BirimKarar karar = BirimOlcu.Decide(haveBox ? lens : new double[0], x0, y0, x1, y1, insAd, fallback, savedFallback);
            s.Emin = karar.Emin;
            s.Ad = karar.Ad;
            s.Factor = FactorOf(karar.Ad);
            if (s.Factor <= 0) s.Factor = 1;
            s.K = 1.0 / s.Factor;
            s.Metin = karar.Metin;
            s.Skor = karar.Skor;
            s.Not = karar.Metin;
            return s;
        }

        static string FromIns(UnitsValue u)
        {
            switch (u)
            {
                case UnitsValue.Centimeters: return "Cm";
                case UnitsValue.Meters: return "Metre";
                default: return "Mm";
            }
        }

        /// <summary>Segment lengths and the selection box, in drawing units. Coordinates themselves are not scaled.</summary>
        static bool Collect(Database db, IEnumerable<ObjectId> sample, out double[] lens, out double x0, out double y0, out double x1, out double y1)
        {
            lens = new double[0];
            x0 = y0 = x1 = y1 = 0;
            List<double> found = new List<double>();
            bool any = false;
            double minX = 0, minY = 0, maxX = 0, maxY = 0;
            try
            {
                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    IEnumerable<ObjectId> ids = sample;
                    if (ids == null)
                    {
                        BlockTable bt = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
                        BlockTableRecord ms = (BlockTableRecord)tx.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                        ids = ms.Cast<ObjectId>().Take(2000).ToList();
                    }
                    int seen = 0;
                    foreach (ObjectId id in ids)
                    {
                        if (++seen > 2000 || id.IsErased) continue;
                        Entity ent = tx.GetObject(id, OpenMode.ForRead) as Entity;
                        if (!(ent is Line || ent is Polyline || ent is Polyline2d || ent is Polyline3d)) continue;
                        foreach (BlockAlong.Seg sg in BlockAlong.Segments(tx, ent))
                        {
                            double segLen = sg.S.DistanceTo(sg.E);
                            if (segLen <= 1e-4) continue;
                            found.Add(segLen);
                            Grow(sg.S.X, sg.S.Y, ref any, ref minX, ref minY, ref maxX, ref maxY);
                            Grow(sg.E.X, sg.E.Y, ref any, ref minX, ref minY, ref maxX, ref maxY);
                        }
                    }
                    tx.Commit();
                }
            }
            catch (System.Exception) { return false; }
            lens = found.ToArray();
            if (!any) return lens.Length > 0;
            x0 = minX; y0 = minY; x1 = maxX; y1 = maxY;
            return true;
        }

        static void Grow(double x, double y, ref bool any, ref double minX, ref double minY, ref double maxX, ref double maxY)
        {
            if (!any) { minX = maxX = x; minY = maxY = y; any = true; return; }
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }

        public static FitConfig Scale(FitConfig cfg, double k)
        {
            if (cfg == null || Math.Abs(k - 1) < 1e-12) return cfg;
            FitConfig c = new FitConfig();
            c.straight_block = cfg.straight_block;
            c.length_param = cfg.length_param;
            c.width_param = cfg.width_param;
            c.fitting_block = cfg.fitting_block;
            c.visibility_param = cfg.visibility_param;
            c.layer = cfg.layer;
            c.clearance = cfg.clearance * k;
            c.tolerance = cfg.tolerance * k;
            c.trim = cfg.trim;
            c.unequal_t = cfg.unequal_t;
            c.height_props = cfg.height_props;
            c.thickness_props = cfg.thickness_props;
            c.tray_label = cfg.tray_label;
            c.fitting_labels = cfg.fitting_labels;
            c.types = new Dictionary<string, FitType>();
            if (cfg.types != null)
                foreach (KeyValuePair<string, FitType> kv in cfg.types)
                {
                    FitType src = kv.Value;
                    if (src == null) continue;
                    c.types[kv.Key] = new FitType { state = src.state, w1 = src.w1, w2 = src.w2, flip = src.flip, leg = (src.leg ?? 60) * k };
                }
            return c;
        }
    }

    public class TavaCommands
    {
        [CommandMethod("AIDTAVA", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void Tava() { Run(false); }
        [CommandMethod("TAVA", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void TavaAlias() { Run(false); }
        [CommandMethod("KT", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void KtAlias() { Run(false); }
        [CommandMethod("AIDPLANKESIF", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void PlanKesif() { Run(true); }
        [CommandMethod("PK", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void PkAlias() { Run(true); }

        static string F(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }

        static void RememberBirim(TavaSettings ts, CizimBirimi.Sonuc bir)
        {
            if (bir == null || !bir.Oto || !bir.Emin) return;
            if (ts.birimSon == bir.Ad) return;
            ts.birimSon = bir.Ad;
            ts.Save();
        }

        static void Run(bool kesif)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            Database db = doc.Database;
            try
            {
                FitConfig cfg = FitConfig.Load();
                TavaSettings ts = TavaSettings.Load();
                if (ts.renkler == null) ts.renkler = new List<RenkOlcu>();
                if (ts.standartRenkler == null || ts.standartRenkler.Count == 0) ts.standartRenkler = StandartVarsayilan();

                // 1) selection: pickfirst, picked lines/polylines, or Tumu (all on plan layers)
                List<ObjectId> ids = null;
                TypedValue[] tv = { new TypedValue((int)DxfCode.Start, "LINE,LWPOLYLINE,POLYLINE") };
                PromptSelectionResult sr = ed.SelectImplied();
                if (sr.Status == PromptStatus.OK)
                {
                    using (Transaction tx = db.TransactionManager.StartTransaction())
                    {
                        ids = sr.Value.GetObjectIds().Where(id => { Entity e = tx.GetObject(id, OpenMode.ForRead) as Entity; return e is Line || e is Polyline || e is Polyline2d || e is Polyline3d; }).ToList();
                        tx.Commit();
                    }
                    if (ids.Count == 0) ids = null;
                }
                if (ids == null)
                {
                    bool all = false;
                    PromptSelectionOptions o = new PromptSelectionOptions { MessageForAdding = "\nTava planı çizgi/polyline'larını seçin veya [Tumu]: " };
                    o.Keywords.Add("Tumu");
                    o.KeywordInput += (s, e) => { all = true; throw new Autodesk.AutoCAD.Runtime.Exception(ErrorStatus.OK); };
                    try { sr = ed.GetSelection(o, new SelectionFilter(tv)); }
                    catch (Autodesk.AutoCAD.Runtime.Exception) { if (!all) throw; }
                    if (all)
                    {
                        PromptStringOptions lo = new PromptStringOptions("\nPlan katman(lar)ı (virgülle, * = tümü): ") { AllowSpaces = true, DefaultValue = ts.layers, UseDefaultValue = true };
                        PromptResult lr = ed.GetString(lo);
                        if (lr.Status != PromptStatus.OK) return;
                        string ls = string.IsNullOrWhiteSpace(lr.StringResult) ? ts.layers : lr.StringResult.Trim();
                        ts.layers = ls;
                        List<TypedValue> f = tv.ToList();
                        if (ls != "*") f.Add(new TypedValue((int)DxfCode.LayerName, ls));
                        sr = ed.SelectAll(new SelectionFilter(f.ToArray()));
                    }
                    if (sr.Status != PromptStatus.OK) { if (sr.Status != PromptStatus.Cancel) ed.WriteMessage("\nUygun çizgi/polyline bulunamadı.\n"); return; }
                    ids = sr.Value.GetObjectIds().ToList();
                }

                // 2) options loop. renkGrup != null means Renk/Tumu: one EN per color.
                CizimBirimi.Sonuc bir = CizimBirimi.Resolve(db, ids, ts.birim);
                CizimBirimi.Active = bir;
                RememberBirim(ts, bir);
                ed.WriteMessage("\n" + bir.Metin + "\n");
                AppLog.Write("BIRIM " + bir.Metin + (string.IsNullOrEmpty(bir.Skor) ? "" : " | " + bir.Skor));
                bool autoEn = true;
                bool standart = false;
                bool renk = false;
                int standartAtlanan = 0;
                List<RenkGrup> renkGrup = null;
                while (true)
                {
                    bool eksen = string.Equals(ts.mod, "Eksen", StringComparison.OrdinalIgnoreCase);
                    string msg = "\nEN=" + (eksen ? F(ts.en) + " (eksen, çift çizgi yok)" : (autoEn ? "otomatik (çift çizgi aralığı/polyline kalınlığı, yoksa " + F(ts.en) + ")" : F(ts.en))) +
                                 ", Y=" + ts.yukseklik + ", t=" + ts.kalinlik + ", Mod=" + ts.mod + ", Bağlantı=" + (ts.baglanti ? "Evet" : "Hayir") +
                                 ", Birim=" + bir.Prompt +
                                 (standart ? ", Standart=1..6 eksen" : (renkGrup != null ? ", Renk=tablo (" + renkGrup.Count + " renk)" : "")) +
                                 "\n[En/Yukseklik/kaLinlik/kOpyala/Mod/eKsen/Renk/Tablo/Standart/bIrim/Baglanti/Devam] <Devam>: ";
                    PromptKeywordOptions ko = new PromptKeywordOptions(msg) { AllowNone = true, AppendKeywordsToMessage = false };
                    ko.Keywords.Add("En", "En", "En");
                    ko.Keywords.Add("Yukseklik", "Yukseklik", "Yukseklik");
                    ko.Keywords.Add("Kalinlik", "kaLinlik", "kaLinlik");
                    ko.Keywords.Add("Kopyala", "kOpyala", "kOpyala");
                    ko.Keywords.Add("Mod", "Mod", "Mod");
                    ko.Keywords.Add("Eksen", "eKsen", "eKsen");
                    ko.Keywords.Add("Renk", "Renk", "Renk");
                    ko.Keywords.Add("Tablo", "Tablo", "Tablo");
                    ko.Keywords.Add("Standart", "Standart", "Standart");
                    ko.Keywords.Add("Birim", "bIrim", "bIrim");
                    ko.Keywords.Add("Baglanti", "Baglanti", "Baglanti");
                    ko.Keywords.Add("Devam", "Devam", "Devam");
                    ko.Keywords.Default = "Devam";
                    PromptResult kr = ed.GetKeywords(ko);
                    if (kr.Status == PromptStatus.Cancel) return;
                    if (kr.Status == PromptStatus.None || kr.StringResult == "Devam") break;
                    switch (kr.StringResult)
                    {
                        case "En":
                            {
                                PromptDoubleOptions d = new PromptDoubleOptions("\nTava EN (0 = otomatik): ") { AllowNone = true, AllowNegative = false, DefaultValue = ts.en, UseDefaultValue = true };
                                PromptDoubleResult r = ed.GetDouble(d);
                                if (r.Status == PromptStatus.Cancel) return;
                                if (r.Status == PromptStatus.OK) { if (r.Value <= 0) autoEn = true; else { ts.en = r.Value; autoEn = false; } }
                                break;
                            }
                        case "Yukseklik":
                        case "Kalinlik":
                            {
                                bool h = kr.StringResult == "Yukseklik";
                                string cur = h ? ts.yukseklik : ts.kalinlik;
                                PromptStringOptions so = new PromptStringOptions("\n" + (h ? "YUKSEKLIK" : "KALINLIK") + ": ") { AllowSpaces = false, DefaultValue = cur, UseDefaultValue = true };
                                PromptResult r = ed.GetString(so);
                                if (r.Status == PromptStatus.Cancel) return;
                                if (r.Status == PromptStatus.OK && !string.IsNullOrWhiteSpace(r.StringResult)) { if (h) ts.yukseklik = r.StringResult.Trim(); else ts.kalinlik = r.StringResult.Trim(); }
                                break;
                            }
                        case "Kopyala":
                            {
                                PromptEntityOptions eo = new PromptEntityOptions("\nÖrnek '" + cfg.straight_block + "' bloğunu seçin: ");
                                eo.SetRejectMessage("\nBlok seçin."); eo.AddAllowedClass(typeof(BlockReference), true);
                                PromptEntityResult er = ed.GetEntity(eo);
                                if (er.Status == PromptStatus.Cancel) return;
                                if (er.Status != PromptStatus.OK) break;
                                using (Transaction tx = db.TransactionManager.StartTransaction())
                                {
                                    BlockReference br = (BlockReference)tx.GetObject(er.ObjectId, OpenMode.ForRead);
                                    string y = AttrValue(tx, br, cfg.height_props), k = AttrValue(tx, br, cfg.thickness_props);
                                    if (y != null) ts.yukseklik = y; if (k != null) ts.kalinlik = k;
                                    TrayInfo(br, cfg, ref ts);
                                    tx.Commit();
                                }
                                autoEn = false;
                                ed.WriteMessage("\nKopyalandı: EN=" + F(ts.en) + ", Y=" + ts.yukseklik + ", t=" + ts.kalinlik + " (EN otomatiğe dönmek için En > 0).");
                                break;
                            }
                        case "Mod":
                            {
                                PromptKeywordOptions mo = new PromptKeywordOptions("\nPlan çizimi [Otomatik/Cift/Tek] <" + ts.mod + ">: ", "Otomatik Cift Tek") { AllowNone = true };
                                PromptResult r = ed.GetKeywords(mo);
                                if (r.Status == PromptStatus.Cancel) return;
                                if (r.Status == PromptStatus.OK) ts.mod = r.StringResult;
                                break;
                            }
                        case "Eksen":
                            {
                                double def = ts.en > 0 ? ts.en : 300;
                                PromptDoubleOptions d = new PromptDoubleOptions("\nEksen EN (çizgi/polyline = tava ekseni, 100/200/300 veya serbest): ") { AllowNone = true, AllowNegative = false, AllowZero = false, DefaultValue = def, UseDefaultValue = true };
                                PromptDoubleResult r = ed.GetDouble(d);
                                if (r.Status == PromptStatus.Cancel) return;
                                if (r.Status == PromptStatus.OK) ts.en = r.Value;
                                else if (ts.en <= 0) ts.en = def;
                                ts.mod = "Eksen";
                                break;
                            }
                        case "Standart":
                            {
                                int atlanan = 0;
                                RenkSecim sec = StandartSec(ed, db, out atlanan);
                                if (sec == null) return;
                                if (sec.Ids.Count == 0) { ed.WriteMessage("\nStandart renk (1-6) bulunamadı. Atlanan: " + atlanan + ".\n"); break; }
                                ids = sec.Ids;
                                renkGrup = sec.Gruplar;
                                standart = true;
                                renk = false;
                                standartAtlanan = atlanan;
                                ed.WriteMessage("\n" + sec.Ozet + "\n");
                                break;
                            }
                        case "Renk":
                            {
                                standart = false;
                                renk = true;
                                RenkSecim sec = RenkSec(ed, db, ts);
                                if (sec == null) return;
                                if (sec.Ids.Count == 0) { ed.WriteMessage("\nBu renk/katmanda çizgi bulunamadı.\n"); break; }
                                ids = sec.Ids;
                                renkGrup = sec.Gruplar;
                                ed.WriteMessage("\n" + sec.Ozet + "\n");
                                break;
                            }
                        case "Tablo":
                            {
                                RenkTablosuDuzenle(ed, ts);
                                ts.Save();
                                break;
                            }
                        case "Birim":
                            {
                                string cur = CizimBirimi.Canon(ts.birim);
                                PromptKeywordOptions uo = new PromptKeywordOptions("\nÇizim birimi [Oto/Mm/Cm/MEtre] <" + cur + ">: ") { AllowNone = true, AppendKeywordsToMessage = false };
                                uo.Keywords.Add("Oto", "Oto", "Oto");
                                uo.Keywords.Add("Mm", "MM", "MM");
                                uo.Keywords.Add("Cm", "Cm", "Cm");
                                uo.Keywords.Add("Metre", "mEtre", "mEtre");
                                uo.Keywords.Default = cur;
                                PromptResult r = ed.GetKeywords(uo);
                                if (r.Status == PromptStatus.Cancel) return;
                                if (r.Status == PromptStatus.OK) ts.birim = CizimBirimi.Canon(r.StringResult);
                                if (ts.birim != "Oto") ts.birimSon = ts.birim;
                                ts.Save();
                                bir = CizimBirimi.Resolve(db, ids, ts.birim);
                                CizimBirimi.Active = bir;
                                RememberBirim(ts, bir);
                                ed.WriteMessage("\n" + bir.Metin + "\n");
                                AppLog.Write("BIRIM " + bir.Metin + (string.IsNullOrEmpty(bir.Skor) ? "" : " | " + bir.Skor));
                                break;
                            }
                        case "Baglanti":
                            {
                                PromptKeywordOptions bo = new PromptKeywordOptions("\nArdından ek parçalar (AIDBAGLANTI) [Evet/Hayir] <" + (ts.baglanti ? "Evet" : "Hayir") + ">: ", "Evet Hayir") { AllowNone = true };
                                PromptResult r = ed.GetKeywords(bo);
                                if (r.Status == PromptStatus.Cancel) return;
                                if (r.Status == PromptStatus.OK) ts.baglanti = r.StringResult == "Evet";
                                break;
                            }
                    }
                }
                ts.Save();

                // 3) everything in one transaction = one UNDO step
                ApplyResult res = new ApplyResult();
                int pairs = 0, singles = 0, ignored = 0, dups = 0, arcs = 0;
                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
                    if (!bt.Has(cfg.straight_block)) throw new InvalidOperationException("'" + cfg.straight_block + "' bloğu çizimde yok.");
                    ObjectId btrId = bt[cfg.straight_block];
                    BlockTableRecord ms = (BlockTableRecord)tx.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                    HashSet<ObjectId> before = new HashSet<ObjectId>(ms.Cast<ObjectId>());

                    double k = bir.K > 0 ? bir.K : 1;
                    bool eksen = standart || string.Equals(ts.mod, "Eksen", StringComparison.OrdinalIgnoreCase);
                    string buildMod = standart ? "Eksen" : ts.mod;
                    if (eksen && ts.en <= 0) ts.en = 300;
                    List<TavaGeo.Center> cl = new List<TavaGeo.Center>();
                    List<RenkGrup> passes = renkGrup != null ? renkGrup : new List<RenkGrup> { new RenkGrup { En = ts.en, Ids = ids } };
                    bool forceEn = renkGrup != null;
                    foreach (RenkGrup g in passes)
                    {
                        if (g.Ids == null || g.Ids.Count == 0) continue;
                        List<TavaGeo.Line2> lines = new List<TavaGeo.Line2>();
                        foreach (ObjectId id in g.Ids)
                        {
                            Entity e = tx.GetObject(id, OpenMode.ForRead) as Entity;
                            if (e == null || e.Layer.Equals(ts.layer, StringComparison.OrdinalIgnoreCase) && e is BlockReference) continue;
                            double w = 0; Polyline pl = e as Polyline; if (pl != null) { try { w = pl.ConstantWidth; } catch (System.Exception) { } }
                            foreach (BlockAlong.Seg s in BlockAlong.Segments(tx, e)) { if (s.Arc) arcs++; lines.Add(new TavaGeo.Line2(new Point2d(s.S.X, s.S.Y), new Point2d(s.E.X, s.E.Y), w)); }
                        }
                        int p = 0, sng = 0, ign = 0;
                        double useEnMm = forceEn ? g.En : ts.en;
                        double useEn = useEnMm * k;
                        List<TavaGeo.Center> part = TavaGeo.Build(lines, buildMod, useEn, k, out p, out sng, out ign);
                        foreach (TavaGeo.Center c in part)
                        {
                            if (forceEn || eksen || !autoEn) c.En = useEn;
                            c.EnMm = k > 1e-12 ? c.En / k : c.En;
                            if (standart) c.Renk = g.Ad;
                        }
                        cl.AddRange(part);
                        pairs += p; singles += sng; ignored += ign;
                    }

                    // existing trays (duplicate check). Item4 = placed in this Eksen run (only exact overlaps count).
                    double axisTol = Math.Max(k, 1e-4);
                    List<Tuple<Point2d, Point2d, double, bool>> have = new List<Tuple<Point2d, Point2d, double, bool>>();
                    foreach (ObjectId id in ms)
                    {
                        BlockReference br = tx.GetObject(id, OpenMode.ForRead) as BlockReference;
                        if (br == null || !string.Equals(Fittings.EffName(tx, br), cfg.straight_block, StringComparison.OrdinalIgnoreCase)) continue;
                        Fittings.TrayRun r = Fittings.MakeRun(br, cfg);
                        if (r == null) continue;
                        Point3d b = r.A + r.U * r.L;
                        have.Add(Tuple.Create(new Point2d(r.A.X, r.A.Y), new Point2d(b.X, b.Y), r.W, false));
                    }

                    if (kesif) have.Clear();   // plan keşif: count the whole plan, ignore trays already drawn
                    string layer = EnsureLayer(tx, db, ts.layer);
                    List<ObjectId> created = new List<ObjectId>();
                    Dictionary<string, int> renkAdet = new Dictionary<string, int>();
                    if (standart && renkGrup != null)
                        foreach (RenkGrup g in renkGrup)
                            if (g != null && g.Ad != null && !renkAdet.ContainsKey(g.Ad)) renkAdet[g.Ad] = 0;
                    foreach (TavaGeo.Center c in cl)
                    {
                        if (c.S.GetDistanceTo(c.E) < Math.Max(k, 1e-6)) continue;
                        if (have.Any(h => TavaGeo.Covered(c.S, c.E, h.Item1, h.Item2, (eksen && h.Item4) ? axisTol : Math.Max(h.Item3, c.En) / 2))) { dups++; continue; }
                        Vector2d u = (c.E - c.S).GetNormal(); Vector2d n = new Vector2d(-u.Y, u.X);
                        bool axis = eksen || renk;
                        // Double-line mode keeps the edge-based insertion. An axis line is the centre; SeatOnAxis corrects the block's real base point.
                        Point2d p = axis ? c.S : c.S - n * (c.En / 2);
                        BlockReference br = new BlockReference(new Point3d(p.X, p.Y, 0), btrId);
                        br.SetDatabaseDefaults(db);
                        br.Rotation = Math.Atan2(u.Y, u.X);
                        br.Layer = layer;
                        ms.AppendEntity(br); tx.AddNewlyCreatedDBObject(br, true);
                        Executor.AddAttributes(tx, br);
                        List<string> notes = new List<string>();
                        double boyMm = BirimOlcu.BoyMm(c.S.GetDistanceTo(c.E), 1.0 / k);
                        // Parameters stay in millimetres. Scale is applied after, then the mm values are written again
                        // so a host that multiplies distances by ScaleFactors cannot shrink them a second time.
                        SetBlockMm(br, cfg.length_param, boyMm, cfg.width_param, c.EnMm, k, res.Notes);
                        SetAttr(tx, br, cfg.height_props, ts.yukseklik);
                        SetAttr(tx, br, cfg.thickness_props, ts.kalinlik);
                        BlockAlong.RefreshHatches(tx, br);
                        if (axis) SeatOnAxis(tx, br, c.S, c.E, c.En, k);
                        have.Add(Tuple.Create(c.S, c.E, c.En, eksen));
                        created.Add(br.ObjectId);
                        if (c.Renk != null && renkAdet.ContainsKey(c.Renk)) renkAdet[c.Renk] = renkAdet[c.Renk] + 1;
                        res.Created++;
                    }

                    string fit = "";
                    if ((ts.baglanti || kesif) && created.Count > 0)
                    {
                        if (!bt.Has(cfg.fitting_block)) fit = " Ek parça bloğu '" + cfg.fitting_block + "' yok, bağlantı atlandı.";
                        else
                        {
                            // only the new trays and existing trays touching them (never re-fit the rest of the drawing)
                            List<ObjectId> scope = new List<ObjectId>(created);
                            List<Extents3d> boxes = created.Select(id => Grow(((Entity)tx.GetObject(id, OpenMode.ForRead)).GeometricExtents, (cfg.clearance + cfg.tolerance) * k)).ToList();
                            foreach (ObjectId id in ms)
                            {
                                if (kesif || created.Contains(id)) continue;
                                BlockReference br = tx.GetObject(id, OpenMode.ForRead) as BlockReference;
                                if (br == null || !string.Equals(Fittings.EffName(tx, br), cfg.straight_block, StringComparison.OrdinalIgnoreCase)) continue;
                                Extents3d x; try { x = br.GeometricExtents; } catch (System.Exception) { continue; }
                                if (boxes.Any(b => b.MinPoint.X <= x.MaxPoint.X && x.MinPoint.X <= b.MaxPoint.X && b.MinPoint.Y <= x.MaxPoint.Y && x.MinPoint.Y <= b.MaxPoint.Y)) scope.Add(id);
                            }
                            Fittings.Plan plan = Fittings.Analyze(tx, db, scope, cfg);
                            fit = " Ek parçalar: " + plan.Summary() + " " + Fittings.Apply(tx, db, plan, cfg, new ApplyResult()) + ".";
                        }
                    }
                    string renkSatir = "";
                    if (standart && renkGrup != null)
                    {
                        List<string> bits = new List<string>();
                        foreach (RenkGrup g in renkGrup)
                        {
                            int n = 0;
                            if (g != null && g.Ad != null) renkAdet.TryGetValue(g.Ad, out n);
                            bits.Add((g == null ? "?" : g.Ad) + " EN " + F(g == null ? 0 : g.En) + ": " + n + " tava");
                        }
                        renkSatir = " Standart: " + string.Join("; ", bits) + ". Atlanan (1-6 dışı): " + standartAtlanan + ".";
                    }
                    if (kesif)
                    {
                        // simulate AIDTAVA + AIDBAGLANTI, measure the result, then roll everything back (drawing untouched)
                        List<string> kn = new List<string>();
                        TakeoffRequest rq = new TakeoffRequest { Only = new HashSet<ObjectId>(ms.Cast<ObjectId>().Where(i => !before.Contains(i))) };
                        List<Dictionary<string, object>> items = Takeoff.Compute(db, rq, kn, tx);
                        tx.Abort();
                        kn.RemoveAll(x => x.Contains("Model alan"));
                        kn.Add("Plandan keşif: " + created.Count + " tava hattı (çift çizgi " + pairs + ", tek çizgi " + singles + (ignored > 0 ? ", eşleşmeyen " + ignored + " çizgi yok sayıldı" : "") + "); çizime hiçbir şey eklenmedi." + renkSatir + " " + bir.Metin + "." + fit);
                        string ks = Metraj.Send(doc, items, kn, "plankesif");
                        ed.WriteMessage("\nPlandan keşif: " + ks + "\n");
                        Metraj.Raise("Plandan keşif: " + ks);
                        return;
                    }
                    tx.Commit();
                    string sum = created.Count + " tava yerleştirildi (katman " + layer + ")" +
                                 (pairs > 0 ? ", çift çizgi: " + pairs : "") + (singles > 0 ? ", tek çizgi: " + singles : "") +
                                 (dups > 0 ? ", mevcut tavayla çakışan " + dups + " atlandı" : "") +
                                 (ignored > 0 ? ", eşleşmeyen " + ignored + " çizgi yok sayıldı" : "") +
                                 (arcs > 0 ? ", " + arcs + " yay kiriş olarak alındı" : "") + "." + renkSatir + " " + bir.Metin + "." + fit +
                                 (res.Notes.Count > 0 ? " (" + string.Join(" ", res.Notes.Distinct()) + ")" : "") + " Geri almak için UNDO (tek adım).";
                    ed.WriteMessage("\n" + sum + "\n");
                    Metraj.Raise("Tava: " + sum);
                    AppLog.Write("AIDTAVA [" + Path.GetFileName(doc.Name) + "] " + sum);
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nTava hatası: " + ex.Message + "\n");
                Metraj.Raise("Tava hatası: " + ex.Message);
            }
            finally { CizimBirimi.Active = null; }
        }

        static Extents3d Grow(Extents3d e, double d) { return new Extents3d(new Point3d(e.MinPoint.X - d, e.MinPoint.Y - d, 0), new Point3d(e.MaxPoint.X + d, e.MaxPoint.Y + d, 0)); }

        static string EnsureLayer(Transaction tx, Database db, string name)
        {
            LayerTable lt = (LayerTable)tx.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(name))
            {
                LayerTableRecord l = (LayerTableRecord)tx.GetObject(lt[name], OpenMode.ForRead);
                if (l.IsFrozen || l.IsLocked) throw new InvalidOperationException("Katman " + name + " donuk/kilitli.");
                return l.Name;
            }
            lt.UpgradeOpen();
            LayerTableRecord r = new LayerTableRecord { Name = name };
            lt.Add(r); tx.AddNewlyCreatedDBObject(r, true);
            return name;
        }

        /// <summary>Write millimetre distance parameters, then scale the reference by <paramref name="unitsPerMm"/>.
        /// The mm values are written again after the scale so they are not multiplied by it.</summary>
        static void SetBlockMm(BlockReference br, string lengthName, double boyMm, string widthName, double enMm, double unitsPerMm, List<string> notes)
        {
            double sc = unitsPerMm > 1e-12 ? unitsPerMm : 1;
            br.ScaleFactors = new Scale3d(1, 1, 1);
            SetNum(br, lengthName, boyMm, notes);
            SetNum(br, widthName, enMm, notes);
            if (Math.Abs(sc - 1) < 1e-12) return;
            br.ScaleFactors = new Scale3d(sc, sc, sc);
            SetNum(br, lengthName, boyMm, notes);
            SetNum(br, widthName, enMm, notes);
        }

        /// <summary>Shift the block perpendicular to S–E so its geometric centre lies on that axis. The shift is in drawing units and is never larger than the tray width.</summary>
        static void SeatOnAxis(Transaction tx, BlockReference br, Point2d s, Point2d e, double widthDu, double unitsPerMm)
        {
            Vector2d d = e - s;
            double len = d.Length;
            if (len < 1e-6) return;
            Vector2d n = new Vector2d(-d.Y / len, d.X / len);
            double limit = Math.Max(widthDu * 2, 1e-4);
            double lat = 0;
            bool unitScale = Math.Abs(unitsPerMm - 1) < 1e-9;
            if (unitScale)
            {
                Extents3d ex;
                try { ex = br.GeometricExtents; }
                catch (System.Exception) { return; }
                double midX = (ex.MinPoint.X + ex.MaxPoint.X) * 0.5, midY = (ex.MinPoint.Y + ex.MaxPoint.Y) * 0.5;
                double wantX = (s.X + e.X) * 0.5, wantY = (s.Y + e.Y) * 0.5;
                lat = (midX - wantX) * n.X + (midY - wantY) * n.Y;
            }
            else
            {
                double localCy = 0;
                bool ok = Fittings.TryLocalCenterY(tx, br, out localCy);
                // Block units are millimetres; the reference scale turns that into drawing units.
                lat = ok ? localCy * br.ScaleFactors.Y : widthDu * 0.5;
                if (!ok || Math.Abs(lat) > limit) lat = widthDu * 0.5;
            }
            if (!unitScale && Math.Abs(lat) > limit) lat = widthDu * 0.5;
            if (Math.Abs(lat) < 1e-6) return;
            br.Position = new Point3d(br.Position.X - n.X * lat, br.Position.Y - n.Y * lat, br.Position.Z);
        }

        static void SetNum(BlockReference br, string name, double v, List<string> notes)
        {
            DynamicBlockReferenceProperty p = Fittings.Prop(br, name);
            if (p == null) { notes.Add("'" + name + "' özelliği yok."); return; }
            try { p.Value = v; } catch (System.Exception ex) { notes.Add(name + "=" + F(v) + " ayarlanamadı (" + ex.Message + ")."); }
        }

        static bool Match(string tag, List<string> names)
        {
            string t = TrayKesif.Norm(tag);
            return names != null && names.Select(TrayKesif.Norm).Any(n => n.Length > 0 && (t == n || (n.Length >= 3 && t.StartsWith(n))));
        }

        static void SetAttr(Transaction tx, BlockReference br, List<string> names, string v)
        {
            foreach (ObjectId id in br.AttributeCollection)
            {
                AttributeReference ar = tx.GetObject(id, OpenMode.ForRead) as AttributeReference;
                if (ar != null && Match(ar.Tag, names)) { ar.UpgradeOpen(); ar.TextString = v; }
            }
            if (br.IsDynamicBlock)
                foreach (DynamicBlockReferenceProperty p in br.DynamicBlockReferencePropertyCollection)
                    if (!p.ReadOnly && Match(p.PropertyName, names))
                    {
                        double d; if (!double.TryParse(v.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) continue;
                        try { if (p.Value is short) p.Value = (short)d; else if (p.Value is int) p.Value = (int)d; else if (p.Value is double) p.Value = d; else p.Value = v; } catch (System.Exception) { }
                    }
        }

        static string AttrValue(Transaction tx, BlockReference br, List<string> names)
        {
            foreach (ObjectId id in br.AttributeCollection)
            {
                AttributeReference ar = tx.GetObject(id, OpenMode.ForRead) as AttributeReference;
                if (ar != null && Match(ar.Tag, names) && !string.IsNullOrWhiteSpace(ar.TextString)) return ar.TextString.Trim();
            }
            if (br.IsDynamicBlock)
                foreach (DynamicBlockReferenceProperty p in br.DynamicBlockReferencePropertyCollection)
                    if (Match(p.PropertyName, names) && p.Value != null) return Convert.ToString(p.Value, CultureInfo.InvariantCulture);
            return null;
        }

        class RenkGrup
        {
            public string Ad;
            public double En;
            public List<ObjectId> Ids = new List<ObjectId>();
        }

        class RenkSecim
        {
            public List<ObjectId> Ids = new List<ObjectId>();
            public List<RenkGrup> Gruplar;
            public string Ozet = "";
        }

        class RenkBilgi
        {
            public bool ByLayer, ByBlock, TrueColor;
            public short Aci;
            public int Rgb;
            /// <summary>Visible color. ByLayer is the layer ACI, so it matches an explicit ACI of that number.</summary>
            public string Key
            {
                get
                {
                    if (ByBlock) return "BB";
                    if (TrueColor) return "RGB:" + Rgb.ToString(CultureInfo.InvariantCulture);
                    return "ACI:" + Aci.ToString(CultureInfo.InvariantCulture);
                }
            }
        }

        static RenkBilgi RenkOku(Entity e, Transaction tx)
        {
            RenkBilgi b = new RenkBilgi();
            Color c = e.Color;
            LayerTableRecord lr = (LayerTableRecord)tx.GetObject(e.LayerId, OpenMode.ForRead);
            short layerAci = 7;
            try { layerAci = lr.Color.ColorIndex; } catch (System.Exception) { }
            if (c == null || c.IsByLayer) { b.ByLayer = true; b.Aci = layerAci; return b; }
            if (c.IsByBlock) { b.ByBlock = true; b.Aci = 0; return b; }
            if (c.ColorMethod == ColorMethod.ByColor)
            {
                b.TrueColor = true;
                try { b.Rgb = c.ColorValue.ToArgb(); } catch (System.Exception) { b.Rgb = 0; }
                try { b.Aci = c.ColorIndex; } catch (System.Exception) { b.Aci = 0; }
                return b;
            }
            try { b.Aci = c.ColorIndex; } catch (System.Exception) { b.Aci = 7; }
            return b;
        }

        static SelectionFilter HatFiltre()
        {
            return new SelectionFilter(new TypedValue[] { new TypedValue((int)DxfCode.Start, "LINE,LWPOLYLINE,POLYLINE") });
        }

        static ObjectId[] Secilen(Editor ed, SelectionFilter filter, bool window, out bool cancel)
        {
            cancel = false;
            PromptSelectionResult pr = window
                ? ed.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nÇizgi/polyline seçin (tüm katmanlar, görünen renk): " }, filter)
                : ed.SelectAll(filter);
            if (pr != null && pr.Status == PromptStatus.Cancel) { cancel = true; return new ObjectId[0]; }
            if (pr == null || pr.Status != PromptStatus.OK || pr.Value == null) return new ObjectId[0];
            return pr.Value.GetObjectIds();
        }

        static RenkSecim RenkSec(Editor ed, Database db, TavaSettings ts)
        {
            PromptEntityOptions eo = new PromptEntityOptions("\nÖrnek çizgi veya polyline seçin: ");
            eo.SetRejectMessage("\nÇizgi veya polyline seçin.");
            eo.AddAllowedClass(typeof(Line), false);
            eo.AddAllowedClass(typeof(Polyline), false);
            eo.AddAllowedClass(typeof(Polyline2d), false);
            eo.AddAllowedClass(typeof(Polyline3d), false);
            PromptEntityResult er = ed.GetEntity(eo);
            if (er.Status == PromptStatus.Cancel || er.Status != PromptStatus.OK) return er.Status == PromptStatus.Cancel ? null : new RenkSecim();

            RenkBilgi want = new RenkBilgi();
            using (Transaction tx = db.TransactionManager.StartTransaction())
            {
                Entity ent = tx.GetObject(er.ObjectId, OpenMode.ForRead) as Entity;
                if (ent == null) return new RenkSecim();
                want = RenkOku(ent, tx);
                tx.Commit();
            }
            string renkAd = want.ByLayer ? "görünen renk " + want.Aci + " (ByLayer)" : (want.TrueColor ? "RGB" : "renk " + want.Aci);
            PromptKeywordOptions ko = new PromptKeywordOptions("\n" + renkAd + ", tüm katmanlar [Pencere/Tumu] <Enter = tüm alan, bu renk>: ", "Pencere Tumu") { AllowNone = true };
            PromptResult kr = ed.GetKeywords(ko);
            if (kr.Status == PromptStatus.Cancel) return null;
            string sub = kr.Status == PromptStatus.OK ? kr.StringResult : "Alan";
            bool tumu = sub == "Tumu";
            bool window = sub == "Pencere";
            if (tumu)
            {
                PromptKeywordOptions ko2 = new PromptKeywordOptions("\nTüm renkler, tüm katmanlar [Pencere] <Enter = tüm alan>: ", "Pencere") { AllowNone = true };
                PromptResult kr2 = ed.GetKeywords(ko2);
                if (kr2.Status == PromptStatus.Cancel) return null;
                window = kr2.Status == PromptStatus.OK && kr2.StringResult == "Pencere";
            }
            bool cancel = false;
            ObjectId[] raw = Secilen(ed, HatFiltre(), window, out cancel);
            if (cancel) return null;
            if (window && raw.Length == 0) return new RenkSecim();

            RenkSecim sec = new RenkSecim();
            Dictionary<string, RenkGrup> groups = new Dictionary<string, RenkGrup>();
            using (Transaction tx = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in raw)
                {
                    Entity ent = tx.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    if (!(ent is Line || ent is Polyline || ent is Polyline2d || ent is Polyline3d)) continue;
                    RenkBilgi b = RenkOku(ent, tx);
                    if (!tumu && b.Key != want.Key) continue;
                    sec.Ids.Add(id);
                    if (!tumu) continue;
                    RenkGrup g;
                    if (!groups.TryGetValue(b.Key, out g))
                    {
                        bool mapped = false;
                        short look = b.Aci;
                        double en = ts.EnForRenk(look, out mapped);
                        string ad = b.TrueColor ? "RGB" : (b.ByBlock ? "ByBlock" : b.Aci.ToString(CultureInfo.InvariantCulture));
                        if (!mapped) ad += " (tabloda yok)";
                        g = new RenkGrup { Ad = ad, En = en, Ids = new List<ObjectId>() };
                        groups[b.Key] = g;
                    }
                    g.Ids.Add(id);
                }
                tx.Commit();
            }
            if (tumu) sec.Gruplar = new List<RenkGrup>(groups.Values);
            string how = tumu ? "tüm katmanlar, her görünen renk" : (window ? "pencere, tüm katmanlar, bu renk" : "tüm alan, tüm katmanlar, bu renk");
            sec.Ozet = "Renk: " + sec.Ids.Count + " nesne, " + how + (sec.Gruplar != null ? " (" + string.Join(", ", sec.Gruplar.Select(g => g.Ad + "=" + F(g.En))) + ")" : "") + ".";
            return sec;
        }

        static void RenkTablosuDuzenle(Editor ed, TavaSettings ts)
        {
            if (ts.renkler == null) ts.renkler = new List<RenkOlcu>();
            while (true)
            {
                string cur = ts.renkler.Count == 0 ? "boş" : string.Join(", ", ts.renkler.Where(x => x != null).OrderBy(x => x.renk).Select(x => x.renk.ToString(CultureInfo.InvariantCulture) + "=" + F(x.en)));
                PromptIntegerOptions io = new PromptIntegerOptions("\nRenk tablosu (" + cur + "). ACI no 1-255, Enter = bitir: ") { AllowNone = true, AllowNegative = false, AllowZero = false };
                PromptIntegerResult ir = ed.GetInteger(io);
                if (ir.Status == PromptStatus.Cancel || ir.Status == PromptStatus.None || ir.Status != PromptStatus.OK) return;
                int no = ir.Value;
                if (no < 1 || no > 255) { ed.WriteMessage("\n1-255 arası ACI renk.\n"); continue; }
                bool mapped = false;
                double curEn = ts.EnForRenk(no, out mapped);
                PromptDoubleOptions dopt = new PromptDoubleOptions("\nRenk " + no + " EN (0 = sil): ") { AllowNone = true, AllowNegative = false, AllowZero = true, DefaultValue = curEn > 0 ? curEn : 300, UseDefaultValue = true };
                PromptDoubleResult dr = ed.GetDouble(dopt);
                if (dr.Status == PromptStatus.Cancel) return;
                if (dr.Status != PromptStatus.OK) continue;
                ts.renkler.RemoveAll(x => x != null && x.renk == no);
                if (dr.Value > 0) ts.renkler.Add(new RenkOlcu { renk = no, en = dr.Value });
            }
        }

        static List<RenkOlcu> StandartVarsayilan()
        {
            return new List<RenkOlcu>
            {
                new RenkOlcu { renk = 1, en = 100 }, new RenkOlcu { renk = 2, en = 200 }, new RenkOlcu { renk = 3, en = 300 },
                new RenkOlcu { renk = 4, en = 400 }, new RenkOlcu { renk = 5, en = 500 }, new RenkOlcu { renk = 6, en = 600 }
            };
        }

        static bool StandartEnBul(short aci, out double en)
        {
            switch (aci)
            {
                case 1: en = 100; return true;
                case 2: en = 200; return true;
                case 3: en = 300; return true;
                case 4: en = 400; return true;
                case 5: en = 500; return true;
                case 6: en = 600; return true;
                default: en = 0; return false;
            }
        }

        static string StandartAd(short aci)
        {
            switch (aci)
            {
                case 1: return "1 kırmızı";
                case 2: return "2 sarı";
                case 3: return "3 yeşil";
                case 4: return "4 cyan";
                case 5: return "5 mavi";
                case 6: return "6 magenta";
                default: return aci.ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <summary>All layers, effective color, fixed 1=100 … 6=600, axis mode. Other colors are counted and skipped.</summary>
        static RenkSecim StandartSec(Editor ed, Database db, out int atlanan)
        {
            atlanan = 0;
            PromptKeywordOptions ko = new PromptKeywordOptions("\nStandart (1=100, 2=200, 3=300, 4=400, 5=500, 6=600, eksen, tüm katmanlar) [Pencere] <Enter = tüm alan>: ", "Pencere") { AllowNone = true };
            PromptResult kr = ed.GetKeywords(ko);
            if (kr.Status == PromptStatus.Cancel) return null;
            bool window = kr.Status == PromptStatus.OK && kr.StringResult == "Pencere";
            bool cancel = false;
            ObjectId[] raw = Secilen(ed, HatFiltre(), window, out cancel);
            if (cancel) return null;

            RenkSecim sec = new RenkSecim();
            Dictionary<short, RenkGrup> groups = new Dictionary<short, RenkGrup>();
            using (Transaction tx = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in raw)
                {
                    Entity ent = tx.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    if (!(ent is Line || ent is Polyline || ent is Polyline2d || ent is Polyline3d)) continue;
                    RenkBilgi b = RenkOku(ent, tx);
                    double en = 0;
                    if (b.ByBlock || b.TrueColor || !StandartEnBul(b.Aci, out en)) { atlanan++; continue; }
                    RenkGrup g;
                    if (!groups.TryGetValue(b.Aci, out g))
                    {
                        g = new RenkGrup { Ad = StandartAd(b.Aci), En = en, Ids = new List<ObjectId>() };
                        groups[b.Aci] = g;
                    }
                    g.Ids.Add(id);
                    sec.Ids.Add(id);
                }
                tx.Commit();
            }
            sec.Gruplar = new List<RenkGrup>();
            for (short a = 1; a <= 6; a++)
            {
                RenkGrup g;
                if (groups.TryGetValue(a, out g)) sec.Gruplar.Add(g);
            }
            sec.Ozet = "Standart: " + sec.Ids.Count + " çizgi/polyline, atlanan " + atlanan + " (ACI 1-6 dışı). Eksen: 1=100, 2=200, 3=300, 4=400, 5=500, 6=600.";
            return sec;
        }

        static void TrayInfo(BlockReference br, FitConfig cfg, ref TavaSettings ts)
        {
            Fittings.TrayRun r = Fittings.MakeRun(br, cfg);
            if (r != null && r.W > 0) ts.en = r.W;
        }
    }
}
