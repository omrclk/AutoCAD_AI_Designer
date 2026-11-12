using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.ApplicationServices;
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
        public string layers { get; set; } = "*";
        public string layer { get; set; } = "E_CT";

        static string FilePath { get { return Path.Combine(AppPaths.Dir, "tava.json"); } }
        public static TavaSettings Load()
        {
            try { if (File.Exists(FilePath)) return new JavaScriptSerializer().Deserialize<TavaSettings>(File.ReadAllText(FilePath)) ?? new TavaSettings(); }
            catch (System.Exception) { }
            return new TavaSettings();
        }
        public void Save() { try { File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(this)); } catch (System.Exception) { } }
    }

    /// <summary>Pure 2D geometry of AIDTAVA: double-line pairing, EN snapping, centrelines (no AutoCAD objects).</summary>
    internal static class TavaGeo
    {
        public static readonly double[] Standard = { 50, 100, 150, 200, 300, 400, 500, 600 };
        public const double MinGap = 30, MaxGap = 700;

        public class Line2 { public Point2d S, E; public double W; public Line2(Point2d s, Point2d e, double w = 0) { S = s; E = e; W = w; } public double Len { get { return S.GetDistanceTo(E); } } }
        public class Center { public Point2d S, E; public double En; public bool Pair; }

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
            return res;
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

                // 1) selection: pickfirst, picked lines/polylines, or Tumu (all on plan layers)
                List<ObjectId> ids = null;
                TypedValue[] tv = { new TypedValue((int)DxfCode.Start, "LINE,LWPOLYLINE,POLYLINE") };
                PromptSelectionResult sr = ed.SelectImplied();
                if (sr.Status == PromptStatus.OK)
                {
                    using (Transaction tx = db.TransactionManager.StartTransaction())
                    {
                        ids = sr.Value.GetObjectIds().Where(id => { Entity e = tx.GetObject(id, OpenMode.ForRead) as Entity; return e is Line || e is Polyline || e is Polyline2d; }).ToList();
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

                // 2) options loop
                bool autoEn = true;
                while (true)
                {
                    string msg = "\nEN=" + (autoEn ? "otomatik (çift çizgi aralığı/polyline kalınlığı, yoksa " + F(ts.en) + ")" : F(ts.en)) +
                                 ", Y=" + ts.yukseklik + ", t=" + ts.kalinlik + ", Mod=" + ts.mod + ", Bağlantı=" + (ts.baglanti ? "Evet" : "Hayir") +
                                 "\n[En/Yukseklik/Kalinlik/Kopyala/Mod/Baglanti/Devam] <Devam>: ";
                    PromptKeywordOptions ko = new PromptKeywordOptions(msg, "En Yukseklik Kalinlik Kopyala Mod Baglanti Devam") { AllowNone = true };
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

                bool withPictures = true;
                if (kesif)
                {
                    bool? pics = BlockPreview.Ask(ed);
                    if (pics == null) return;
                    withPictures = pics.Value;
                }

                // 3) everything in one transaction = one UNDO step
                ApplyResult res = new ApplyResult();
                int pairs, singles, ignored, dups = 0, arcs = 0;
                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
                    if (!bt.Has(cfg.straight_block)) throw new InvalidOperationException("'" + cfg.straight_block + "' bloğu çizimde yok.");
                    ObjectId btrId = bt[cfg.straight_block];
                    BlockTableRecord ms = (BlockTableRecord)tx.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                    HashSet<ObjectId> before = new HashSet<ObjectId>(ms.Cast<ObjectId>());

                    List<TavaGeo.Line2> lines = new List<TavaGeo.Line2>();
                    foreach (ObjectId id in ids)
                    {
                        Entity e = tx.GetObject(id, OpenMode.ForRead) as Entity;
                        if (e == null || e.Layer.Equals(ts.layer, StringComparison.OrdinalIgnoreCase) && e is BlockReference) continue;
                        double w = 0; Polyline pl = e as Polyline; if (pl != null) { try { w = pl.ConstantWidth; } catch (System.Exception) { } }
                        foreach (BlockAlong.Seg s in BlockAlong.Segments(tx, e)) { if (s.Arc) arcs++; lines.Add(new TavaGeo.Line2(new Point2d(s.S.X, s.S.Y), new Point2d(s.E.X, s.E.Y), w)); }
                    }
                    List<TavaGeo.Center> cl = TavaGeo.Build(lines, ts.mod, ts.en, TavaGeo.UnitsPerMm(db.Insunits), out pairs, out singles, out ignored);
                    if (!autoEn) foreach (TavaGeo.Center c in cl) c.En = ts.en;

                    // existing trays (duplicate check)
                    List<Tuple<Point2d, Point2d, double>> have = new List<Tuple<Point2d, Point2d, double>>();
                    foreach (ObjectId id in ms)
                    {
                        BlockReference br = tx.GetObject(id, OpenMode.ForRead) as BlockReference;
                        if (br == null || !string.Equals(Fittings.EffName(tx, br), cfg.straight_block, StringComparison.OrdinalIgnoreCase)) continue;
                        Fittings.TrayRun r = Fittings.MakeRun(br, cfg);
                        if (r == null) continue;
                        Point3d b = r.A + r.U * r.L;
                        have.Add(Tuple.Create(new Point2d(r.A.X, r.A.Y), new Point2d(b.X, b.Y), r.W));
                    }

                    if (kesif) have.Clear();   // plan keşif: count the whole plan, ignore trays already drawn
                    string layer = EnsureLayer(tx, db, ts.layer);
                    List<ObjectId> created = new List<ObjectId>();
                    foreach (TavaGeo.Center c in cl)
                    {
                        if (c.S.GetDistanceTo(c.E) < 1) continue;
                        if (have.Any(h => TavaGeo.Covered(c.S, c.E, h.Item1, h.Item2, Math.Max(h.Item3, c.En) / 2))) { dups++; continue; }
                        Vector2d u = (c.E - c.S).GetNormal(); Vector2d n = new Vector2d(-u.Y, u.X);
                        Point2d p = c.S - n * (c.En / 2);
                        BlockReference br = new BlockReference(new Point3d(p.X, p.Y, 0), btrId);
                        br.SetDatabaseDefaults(db);
                        br.Rotation = Math.Atan2(u.Y, u.X);
                        br.Layer = layer;
                        ms.AppendEntity(br); tx.AddNewlyCreatedDBObject(br, true);
                        Executor.AddAttributes(tx, br);
                        List<string> notes = new List<string>();
                        SetNum(br, cfg.length_param, c.S.GetDistanceTo(c.E), res.Notes);
                        SetNum(br, cfg.width_param, c.En, res.Notes);
                        SetAttr(tx, br, cfg.height_props, ts.yukseklik);
                        SetAttr(tx, br, cfg.thickness_props, ts.kalinlik);
                        BlockAlong.RefreshHatches(tx, br);
                        have.Add(Tuple.Create(c.S, c.E, c.En));
                        created.Add(br.ObjectId);
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
                            List<Extents3d> boxes = created.Select(id => Grow(((Entity)tx.GetObject(id, OpenMode.ForRead)).GeometricExtents, cfg.clearance + cfg.tolerance)).ToList();
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
                    if (kesif)
                    {
                        // simulate AIDTAVA + AIDBAGLANTI, measure the result, then roll everything back (drawing untouched)
                        List<string> kn = new List<string>();
                        Dictionary<string, string> pics = withPictures ? new Dictionary<string, string>() : null;
                        TakeoffRequest rq = new TakeoffRequest { Only = new HashSet<ObjectId>(ms.Cast<ObjectId>().Where(i => !before.Contains(i))), Images = withPictures };
                        List<Dictionary<string, object>> items = Takeoff.Compute(db, rq, kn, tx, pics);
                        tx.Abort();
                        kn.RemoveAll(x => x.Contains("Model alan"));
                        kn.Add("Plandan keşif: " + created.Count + " tava hattı (çift çizgi " + pairs + ", tek çizgi " + singles + (ignored > 0 ? ", eşleşmeyen " + ignored + " çizgi yok sayıldı" : "") + "); çizime hiçbir şey eklenmedi." + fit);
                        string ks = Metraj.Send(doc, items, kn, "plankesif", pics);
                        ed.WriteMessage("\nPlandan keşif: " + ks + "\n");
                        Metraj.Raise("Plandan keşif: " + ks);
                        return;
                    }
                    tx.Commit();
                    string sum = created.Count + " tava yerleştirildi (katman " + layer + ")" +
                                 (pairs > 0 ? ", çift çizgi: " + pairs : "") + (singles > 0 ? ", tek çizgi: " + singles : "") +
                                 (dups > 0 ? ", mevcut tavayla çakışan " + dups + " atlandı" : "") +
                                 (ignored > 0 ? ", eşleşmeyen " + ignored + " çizgi yok sayıldı" : "") +
                                 (arcs > 0 ? ", " + arcs + " yay kiriş olarak alındı" : "") + "." + fit +
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

        static void TrayInfo(BlockReference br, FitConfig cfg, ref TavaSettings ts)
        {
            Fittings.TrayRun r = Fittings.MakeRun(br, cfg);
            if (r != null && r.W > 0) ts.en = r.W;
        }
    }
}
