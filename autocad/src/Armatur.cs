using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace CadAiDesigner
{
    /// <summary>Pure 2D layout for AIDARMATUR: row along an open path, grid inside a closed boundary (x from ends/walls, 2x between).</summary>
    internal static class ArmaturGeo
    {
        public struct Place { public Point2d P; public double Angle; }

        /// <summary>Distances along a path of length L: x from both ends, ~2x between, evenly adjusted.</summary>
        public static List<double> RowStations(double L, double x)
        {
            List<double> r = new List<double>();
            if (L <= 0 || x <= 0) return r;
            if (L <= 2 * x + 1e-9) { r.Add(L / 2); return r; }
            int n = (int)Math.Ceiling((L - 2 * x) / (2 * x) - 1e-9) + 1;
            double step = (L - 2 * x) / (n - 1);
            for (int i = 0; i < n; i++) r.Add(x + i * step);
            return r;
        }

        /// <summary>1D grid across width w: count so spacing <= 2x with x margin, centred (equal margins).</summary>
        public static List<double> Axis(double w, double x)
        {
            List<double> r = new List<double>();
            if (w <= 2 * x + 1e-9) { r.Add(w / 2); return r; }
            int n = (int)Math.Ceiling((w - 2 * x) / (2 * x) - 1e-9) + 1;
            double step = (w - 2 * x) / (n - 1);
            for (int i = 0; i < n; i++) r.Add(x + i * step);
            return r;
        }

        public static bool Inside(List<Point2d> poly, Point2d p)
        {
            bool c = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
                if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y) && p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X) c = !c;
            return c;
        }

        public static double EdgeDist(List<Point2d> poly, Point2d p)
        {
            double best = double.MaxValue;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                Vector2d e = poly[i] - poly[j]; double l2 = e.DotProduct(e);
                double t = l2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, (p - poly[j]).DotProduct(e) / l2));
                best = Math.Min(best, p.GetDistanceTo(poly[j] + e * t));
            }
            return best;
        }

        /// <summary>Grid oriented to the longest edge; keeps points inside (and >= 0.9x from walls when any such point exists).</summary>
        public static List<Place> Room(List<Point2d> poly, double x, out string note)
        {
            note = null;
            List<Place> res = new List<Place>();
            if (poly.Count < 3) return res;
            double ang = 0, best = -1;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                double l = poly[i].GetDistanceTo(poly[j]);
                if (l > best + 1e-9) { best = l; Vector2d v = poly[i] - poly[j]; ang = Math.Atan2(v.Y, v.X); }
            }
            while (ang > Math.PI / 2 + 1e-9) ang -= Math.PI; while (ang <= -Math.PI / 2 + 1e-9) ang += Math.PI;
            double c = Math.Cos(-ang), s = Math.Sin(-ang);
            Func<Point2d, Point2d> to = p => new Point2d(p.X * c - p.Y * s, p.X * s + p.Y * c);
            Func<Point2d, Point2d> back = p => new Point2d(p.X * Math.Cos(ang) - p.Y * Math.Sin(ang), p.X * Math.Sin(ang) + p.Y * Math.Cos(ang));
            List<Point2d> lp = poly.Select(to).ToList();
            double minx = lp.Min(p => p.X), maxx = lp.Max(p => p.X), miny = lp.Min(p => p.Y), maxy = lp.Max(p => p.Y);
            List<Point2d> cand = new List<Point2d>();
            foreach (double u in Axis(maxx - minx, x)) foreach (double v in Axis(maxy - miny, x)) cand.Add(new Point2d(minx + u, miny + v));
            List<Point2d> inside = cand.Where(p => Inside(lp, p)).ToList();
            List<Point2d> good = inside.Where(p => EdgeDist(lp, p) >= 0.9 * x).ToList();
            List<Point2d> use = good.Count > 0 ? good : inside;
            if (good.Count > 0 && good.Count < inside.Count) note = (inside.Count - good.Count) + " nokta duvara x'ten yak\u0131n oldu\u011fu i\u00e7in at\u0131ld\u0131";
            if (use.Count == 0)
            {
                Point2d ctr = new Point2d((minx + maxx) / 2, (miny + maxy) / 2);
                if (Inside(lp, ctr)) use.Add(ctr); else note = "alan x'e g\u00f6re \u00e7ok dar, armat\u00fcr konamad\u0131";
            }
            foreach (Point2d p in use) res.Add(new Place { P = back(p), Angle = ang });
            return res;
        }
    }

    /// <summary>DIALux-style field/line arrangement: n luminaires over length L -> d = L/(2n), spacing 2d, half spacing to walls/ends.</summary>
    internal static class ArmaturField
    {
        public static List<double> Line(double L, int n)
        {
            List<double> r = new List<double>();
            if (L <= 0 || n < 1) return r;
            double d = L / (2.0 * n);
            for (int i = 0; i < n; i++) r.Add(d + i * 2 * d);
            return r;
        }

        public static int CountFor(double L, double maxSpacing) { return Math.Max(1, (int)Math.Ceiling(L / maxSpacing - 1e-9)); }

        static double Area(List<Point2d> p)
        {
            double a = 0; for (int i = 0, j = p.Count - 1; i < p.Count; j = i++) a += (p[j].X + p[i].X) * (p[j].Y - p[i].Y); return Math.Abs(a) / 2;
        }

        public class Result { public List<ArmaturGeo.Place> Places = new List<ArmaturGeo.Place>(); public bool Rect; public int NA, NB, Nudged, Dropped; public double A, B, DA, DB; }

        /// <summary>Frame aligned to the longest edge; counts from (nA, nB) or from max spacing (when nA/nB <= 0).</summary>
        public static Result Room(List<Point2d> poly, int nA, int nB, double maxSpacing, int total)
        {
            Result r = new Result();
            if (poly.Count < 3) return r;
            double ang = 0, best = -1;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                double l = poly[i].GetDistanceTo(poly[j]);
                if (l > best + 1e-9) { best = l; Vector2d v = poly[i] - poly[j]; ang = Math.Atan2(v.Y, v.X); }
            }
            while (ang > Math.PI / 2 + 1e-9) ang -= Math.PI; while (ang <= -Math.PI / 2 + 1e-9) ang += Math.PI;
            double c = Math.Cos(ang), s = Math.Sin(ang);
            Func<Point2d, Point2d> to = p => new Point2d(p.X * c + p.Y * s, -p.X * s + p.Y * c);
            Func<Point2d, Point2d> back = p => new Point2d(p.X * c - p.Y * s, p.X * s + p.Y * c);
            List<Point2d> lp = poly.Select(to).ToList();
            double minx = lp.Min(p => p.X), maxx = lp.Max(p => p.X), miny = lp.Min(p => p.Y), maxy = lp.Max(p => p.Y);
            r.A = maxx - minx; r.B = maxy - miny;
            r.Rect = Area(lp) >= 0.995 * r.A * r.B;
            if (total > 0 && (nA <= 0 || nB <= 0))
            {
                double bboxShare = r.Rect ? 1 : Math.Max(0.2, Area(lp) / (r.A * r.B));
                double tot = total / bboxShare;
                nA = Math.Max(1, (int)Math.Round(Math.Sqrt(tot * r.A / Math.Max(r.B, 1e-9))));
                nB = Math.Max(1, (int)Math.Ceiling(tot / nA - 1e-9));
                if (r.Rect)
                {   // prefer an exact NxM = total when its spacings stay balanced (ratio 0.5..2)
                    double bq = double.MaxValue; int bA = 0;
                    for (int i = 1; i <= total; i++)
                    {
                        if (total % i != 0) continue;
                        double q = Math.Abs(Math.Log((r.A / i) / (r.B / (total / i))));
                        if (q <= Math.Log(2) + 1e-9 && q < bq) { bq = q; bA = i; }
                    }
                    if (bA > 0) { nA = bA; nB = total / bA; }
                }
            }
            if (nA <= 0) nA = CountFor(r.A, maxSpacing);
            if (nB <= 0) nB = CountFor(r.B, maxSpacing);
            r.NA = nA; r.NB = nB; r.DA = r.A / (2.0 * nA); r.DB = r.B / (2.0 * nB);
            double dmin = Math.Min(r.DA, r.DB), sp = 2 * dmin;
            List<Point2d> kept = new List<Point2d>(), pend = new List<Point2d>();
            foreach (double u in Line(r.A, nA))
                foreach (double v in Line(r.B, nB))
                {
                    Point2d p = new Point2d(minx + u, miny + v);
                    if (r.Rect) { kept.Add(p); continue; }
                    bool inside = ArmaturGeo.Inside(lp, p);
                    double dist = inside ? ArmaturGeo.EdgeDist(lp, p) : 0;
                    if (inside && dist >= dmin * 0.999) { kept.Add(p); continue; }
                    pend.Add(p);
                }
            foreach (Point2d p in pend)   // second pass: nudge only after all regular points are known
                {
                    // nudge inward: step away from the nearest wall point until the wall distance is d
                    Point2d q = p; bool ok = false;
                    for (int it = 0; it < 6 && !ok; it++)
                    {
                        Point2d w = Nearest(lp, q);
                        Vector2d dir = q - w;
                        if (!ArmaturGeo.Inside(lp, q)) dir = -dir;
                        if (dir.Length < 1e-9) break;
                        double cur = ArmaturGeo.Inside(lp, q) ? ArmaturGeo.EdgeDist(lp, q) : -q.GetDistanceTo(w);
                        q = q + dir.GetNormal() * (dmin - cur + dmin * 0.001);
                        ok = ArmaturGeo.Inside(lp, q) && ArmaturGeo.EdgeDist(lp, q) >= dmin * 0.999;
                    }
                    if (ok && q.GetDistanceTo(p) <= dmin && kept.All(k => k.GetDistanceTo(q) >= 0.75 * sp)) { kept.Add(q); r.Nudged++; }
                    else r.Dropped++;
                }
            foreach (Point2d p in kept) r.Places.Add(new ArmaturGeo.Place { P = back(p), Angle = ang });
            return r;
        }

        static Point2d Nearest(List<Point2d> poly, Point2d p)
        {
            double best = double.MaxValue; Point2d bp = p;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                Vector2d e = poly[i] - poly[j]; double l2 = e.DotProduct(e);
                double t = l2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, (p - poly[j]).DotProduct(e) / l2));
                Point2d q = poly[j] + e * t; double d = p.GetDistanceTo(q);
                if (d < best) { best = d; bp = q; }
            }
            return bp;
        }
    }

    public class ArmaturCommands
    {
        [CommandMethod("AIDARMATUR", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.NoBlockEditor)]
        public void Run() { Do(); }

        [CommandMethod("ARM", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.NoBlockEditor)]
        public void RunShort() { Do(); }

        static double Mm(Database db)
        {
            switch (db.Insunits)
            {
                case UnitsValue.Centimeters: return 0.1;
                case UnitsValue.Meters: return 0.001;
                case UnitsValue.Decimeters: return 0.01;
                case UnitsValue.Inches: return 1 / 25.4;
                case UnitsValue.Feet: return 1 / 304.8;
                default: return 1;
            }
        }

        static List<Point2d> Sample(Curve c)
        {
            List<Point2d> pts = new List<Point2d>();
            Polyline pl = c as Polyline;
            if (pl != null && Enumerable.Range(0, pl.NumberOfVertices).All(i => Math.Abs(pl.GetBulgeAt(i)) < 1e-9))
                for (int i = 0; i < pl.NumberOfVertices; i++) pts.Add(pl.GetPoint2dAt(i));
            else
            {
                double L = c.GetDistanceAtParameter(c.EndParam);
                for (int i = 0; i < 360; i++) { Point3d p = c.GetPointAtDist(L * i / 360); pts.Add(new Point2d(p.X, p.Y)); }
            }
            while (pts.Count > 1 && pts[0].GetDistanceTo(pts[pts.Count - 1]) < 1e-9) pts.RemoveAt(pts.Count - 1);
            for (int i = pts.Count - 1; i > 0; i--) if (pts[i].GetDistanceTo(pts[i - 1]) < 1e-9) pts.RemoveAt(i);
            return pts;
        }

        static bool IsClosed(Curve c)
        {
            if (c is Circle) return true;
            try { if (c.Closed) return true; } catch (System.Exception) { }
            try { return !(c is Line) && c.StartPoint.DistanceTo(c.EndPoint) < 1e-6 && c.GetDistanceAtParameter(c.EndParam) > 1e-6; } catch (System.Exception) { return false; }
        }

        /// <summary>Fallback for pick point: smallest closed curve in the current space containing the point.</summary>
        static ObjectId Enclosing(Database db, Point3d pt)
        {
            ObjectId best = ObjectId.Null; double ba = double.MaxValue;
            using (Transaction tx = db.TransactionManager.StartOpenCloseTransaction())
            {
                BlockTableRecord sp = (BlockTableRecord)tx.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
                foreach (ObjectId id in sp)
                {
                    Curve c = tx.GetObject(id, OpenMode.ForRead) as Curve;
                    if (c == null || !IsClosed(c)) continue;
                    double a; try { a = c.Area; } catch (System.Exception) { continue; }
                    if (a >= ba || a <= 0) continue;
                    if (ArmaturGeo.Inside(Sample(c), new Point2d(pt.X, pt.Y))) { ba = a; best = id; }
                }
                tx.Commit();
            }
            return best;
        }

        internal static ObjectId EnclosingPublic(Database db, Point3d pt) { return Enclosing(db, pt); }
        internal static double MmPublic(Database db) { return Mm(db); }
        internal static bool IsClosedPublic(Curve c) { return IsClosed(c); }
        internal static List<Point2d> SamplePublic(Curve c) { return Sample(c); }

        static string F(double v) { return v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ','); }

        static void Do()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor; Database db = doc.Database;
            List<DBObjectCollection> traced = new List<DBObjectCollection>();
            try
            {
                PromptEntityOptions eo = new PromptEntityOptions("\nArmat\u00fcr blo\u011funu se\u00e7in: ");
                eo.SetRejectMessage("\nBlok de\u011fil."); eo.AddAllowedClass(typeof(BlockReference), false);
                PromptEntityResult er = ed.GetEntity(eo);
                if (er.Status != PromptStatus.OK) return;

                PromptKeywordOptions ao = new PromptKeywordOptions("\nAlan [Polyline/Icnokta] <Polyline>: ", "Polyline Icnokta") { AllowNone = true };
                PromptResult ar = ed.GetKeywords(ao);
                if (ar.Status != PromptStatus.OK && ar.Status != PromptStatus.None) return;
                bool pick = ar.Status == PromptStatus.OK && ar.StringResult == "Icnokta";
                List<Curve> curves = new List<Curve>(); List<ObjectId> ids = new List<ObjectId>();
                if (pick)
                {
                    while (true)
                    {
                        PromptPointResult pr = ed.GetPoint(new PromptPointOptions("\nOda i\u00e7inde bir nokta t\u0131klay\u0131n (Enter = bitti): ") { AllowNone = true });
                        if (pr.Status != PromptStatus.OK) break;
                        DBObjectCollection col = null;
                        try { col = ed.TraceBoundary(pr.Value, true); } catch (System.Exception) { }
                        Curve best = null;
                        if (col != null) { traced.Add(col); foreach (DBObject o in col) { Curve cv = o as Curve; if (cv != null && (best == null || cv.Area > best.Area)) best = cv; } }
                        if (best == null) { ObjectId cid = Enclosing(db, pr.Value); if (!cid.IsNull) ids.Add(cid); else ed.WriteMessage("\nKapal\u0131 s\u0131n\u0131r bulunamad\u0131 (alan a\u00e7\u0131k olabilir)."); if (!cid.IsNull) ed.WriteMessage("\nS\u0131n\u0131r: noktay\u0131 i\u00e7eren kapal\u0131 polyline kullan\u0131ld\u0131."); continue; }
                        curves.Add(best); ed.WriteMessage("\nS\u0131n\u0131r bulundu (alan " + F(best.Area) + ").");
                    }
                    if (curves.Count == 0 && ids.Count == 0) return;
                }
                else
                {
                    PromptSelectionResult sr = ed.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nA\u00e7\u0131k (koridor) / kapal\u0131 (oda) polyline'lar\u0131 se\u00e7in: " },
                        new SelectionFilter(new[] { new TypedValue(-4, "<OR"), new TypedValue(0, "LWPOLYLINE"), new TypedValue(0, "POLYLINE"), new TypedValue(0, "LINE"), new TypedValue(0, "ARC"), new TypedValue(0, "CIRCLE"), new TypedValue(0, "SPLINE"), new TypedValue(-4, "OR>") }));
                    if (sr.Status != PromptStatus.OK) return;
                    ids.AddRange(sr.Value.GetObjectIds());
                }

                bool hasOpen = false, hasClosed = false; double lmDef = 0; string lmSrc = null;
                using (Transaction qt = db.TransactionManager.StartOpenCloseTransaction())
                {
                    foreach (ObjectId id in ids) { Curve cv = qt.GetObject(id, OpenMode.ForRead) as Curve; if (cv == null) continue; if (IsClosed(cv)) hasClosed = true; else hasOpen = true; }
                    foreach (Curve cv in curves) hasClosed = true;
                    lmDef = Ayd.LumenFrom(qt, (BlockReference)qt.GetObject(er.ObjectId, OpenMode.ForRead), out lmSrc);
                    qt.Commit();
                }
                double k = Mm(db);
                // "Aralik" and "Adet" both start with A: give Aralik the R shortcut so typing/clicking A = Adet
                PromptKeywordOptions mo = new PromptKeywordOptions("\nY\u00f6ntem") { AllowNone = true };
                mo.Keywords.Add("Aralik", "aRalik", "aRalik");
                mo.Keywords.Add("Adet", "Adet", "Adet");
                mo.Keywords.Add("Mesafe", "Mesafe", "Mesafe");
                mo.Keywords.Add("Lux", "Lux", "Lux");
                mo.Keywords.Default = "Aralik"; mo.AppendKeywordsToMessage = true;
                PromptResult mr = ed.GetKeywords(mo);
                if (mr.Status != PromptStatus.OK && mr.Status != PromptStatus.None) return;
                string mode = mr.Status == PromptStatus.OK ? mr.StringResult : "Aralik";
                double maxSp = 3000 * k, x = 1500 * k; int cn = 0, cA = 0, cB = 0, ctot = 0;
                if (mode == "Aralik")
                {
                    PromptDoubleResult dr = ed.GetDistance(new PromptDistanceOptions("\nMaksimum armat\u00fcr aral\u0131\u011f\u0131 (2d; iki nokta da t\u0131klanabilir)") { AllowNone = true, AllowNegative = false, AllowZero = false, DefaultValue = maxSp, UseDefaultValue = true });
                    if (dr.Status != PromptStatus.OK && dr.Status != PromptStatus.None) return;
                    if (dr.Status == PromptStatus.OK) maxSp = dr.Value;
                }
                else if (mode == "Adet")
                {
                    if (hasOpen)
                    {
                        PromptIntegerResult ir = ed.GetInteger(new PromptIntegerOptions("\nKoridor armat\u00fcr adedi n") { AllowNone = true, AllowNegative = false, AllowZero = false, LowerLimit = 1, DefaultValue = 4, UseDefaultValue = true });
                        if (ir.Status == PromptStatus.OK) cn = ir.Value; else if (ir.Status == PromptStatus.None) cn = 4; else return;
                    }
                    if (hasClosed)
                    {
                        while (true)
                        {
                            PromptResult nr = ed.GetString(new PromptStringOptions("\nOda adedi (toplam, \u00f6rn. 10, veya NxM, \u00f6rn. 5x2) <4>: ") { AllowSpaces = false });
                            if (nr.Status != PromptStatus.OK) return;
                            string s = (nr.StringResult ?? "").Trim().ToLowerInvariant(); if (s.Length == 0) s = "4";
                            string[] parts = s.Split('x', '*', '\u00d7');
                            int a, b;
                            if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out a) && int.TryParse(parts[1].Trim(), out b) && a > 0 && b > 0) { cA = a; cB = b; ctot = 0; break; }
                            if (parts.Length == 1 && int.TryParse(s, out a) && a > 0) { ctot = a; cA = cB = 0; break; }
                            ed.WriteMessage("\nGe\u00e7ersiz adet: " + s + " (\u00f6rn. 10 veya 5x2)");
                        }
                    }
                }
                else if (mode == "Mesafe")
                {
                    PromptDoubleResult dr = ed.GetDistance(new PromptDistanceOptions("\nx mesafesi (u\u00e7/duvar = x, aral\u0131k = 2x)") { AllowNone = true, AllowNegative = false, AllowZero = false, DefaultValue = x, UseDefaultValue = true });
                    if (dr.Status != PromptStatus.OK && dr.Status != PromptStatus.None) return;
                    if (dr.Status == PromptStatus.OK) x = dr.Value;
                }

                // lighting calc parameters (Lux mode always; other modes optional)
                double eRoom = 500, eCorr = 100; Ayd.Params ap = null;
                if (mode == "Lux")
                {
                    if (hasClosed) { PromptDoubleResult e1 = ed.GetDouble(new PromptDoubleOptions("\nHedef aydınlık (oda, lx): ") { AllowNone = true, AllowNegative = false, AllowZero = false, DefaultValue = 500, UseDefaultValue = true }); if (e1.Status == PromptStatus.OK) eRoom = e1.Value; else if (e1.Status != PromptStatus.None) return; }
                    if (hasOpen) { PromptDoubleResult e2 = ed.GetDouble(new PromptDoubleOptions("\nHedef aydınlık (koridor, lx): ") { AllowNone = true, AllowNegative = false, AllowZero = false, DefaultValue = 100, UseDefaultValue = true }); if (e2.Status == PromptStatus.OK) eCorr = e2.Value; else if (e2.Status != PromptStatus.None) return; }
                    ap = Ayd.Ask(ed, lmDef, lmSrc, hasOpen); if (ap == null) return;
                }
                else
                {
                    PromptResult hr = ed.GetKeywords(new PromptKeywordOptions("\nAydınlatma hesabı (lümen yöntemi) yapılsın mı? [Evet/Hayir] <Evet>: ", "Evet Hayir") { AllowNone = true });
                    if (hr.Status == PromptStatus.Cancel) return;
                    if (!(hr.Status == PromptStatus.OK && hr.StringResult == "Hayir")) { ap = Ayd.Ask(ed, lmDef, lmSrc, hasOpen); if (ap == null) return; }
                }
                double upm = 1000 * k; List<Ayd.Res> calc = new List<Ayd.Res>();
                Func<Point2d, Point2d> M = q => new Point2d(q.X / upm, q.Y / upm);

                int rows = 0, rooms = 0, total = 0; List<string> notes = new List<string>(), lines = new List<string>();
                using (doc.LockDocument())
                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    BlockReference src = (BlockReference)tx.GetObject(er.ObjectId, OpenMode.ForRead);
                    LayerTableRecord ltr = (LayerTableRecord)tx.GetObject(src.LayerId, OpenMode.ForRead);
                    if (ltr.IsLocked) throw new InvalidOperationException("Armat\u00fcr blo\u011funun katman\u0131 kilitli (" + src.Layer + ").");
                    BlockTableRecord space = (BlockTableRecord)tx.GetObject(src.OwnerId, OpenMode.ForWrite);
                    foreach (ObjectId id in ids) { Curve cv = tx.GetObject(id, OpenMode.ForRead) as Curve; if (cv != null) curves.Add(cv); }
                    int idx = 0;
                    foreach (Curve c in curves)
                    {
                        idx++;
                        string tag = c.Handle.Value != 0 ? c.Handle.ToString() : "s\u0131n\u0131r" + idx;
                        List<ArmaturGeo.Place> places = new List<ArmaturGeo.Place>();
                        if (IsClosed(c))
                        {
                            rooms++;
                            List<Point2d> poly = Sample(c);
                            if (mode == "Mesafe")
                            {
                                string note; places = ArmaturGeo.Room(poly, x, out note);
                                if (note != null) notes.Add(tag + ": " + note);
                                lines.Add(tag + " oda: " + places.Count + " (x=" + F(x) + ")");
                            }
                            else
                            {
                                int want = ctot;
                                if (mode == "Lux")
                                {
                                    List<Point2d> pm = poly.Select(M).ToList();
                                    double uf = Ayd.UF(Ayd.RoomIndex(Ayd.Area(pm), Ayd.Perim(pm), ap.H - ap.Wp), ap.Rc, ap.Rw);
                                    want = Ayd.CountFor(eRoom, Ayd.Area(pm), ap.Lumen, uf, ap.MF);
                                }
                                ArmaturField.Result r = ArmaturField.Room(poly, mode == "Lux" ? 0 : cA, mode == "Lux" ? 0 : cB, maxSp, want);
                                if (mode == "Lux" && r.Places.Count < want) notes.Add(tag + ": hesaplanan " + want + ", yerle\u015fen " + r.Places.Count + " (d\u00fczensiz alan; Eort buna g\u00f6re)");
                                places = r.Places;
                                lines.Add(tag + (r.Rect ? " dikd\u00f6rtgen oda " : " d\u00fczensiz oda ") + F(r.A) + "x" + F(r.B) + ": " + r.NA + "x" + r.NB
                                    + ", d=" + F(r.DA) + "/" + F(r.DB) + ", aral\u0131k=" + F(2 * r.DA) + "/" + F(2 * r.DB) + " -> " + places.Count
                                    + (r.Nudged > 0 ? ", " + r.Nudged + " i\u00e7e kayd\u0131r\u0131ld\u0131" : "") + (r.Dropped > 0 ? ", " + r.Dropped + " at\u0131ld\u0131 (alan d\u0131\u015f\u0131/duvara yak\u0131n)" : ""));
                            }
                        }
                        else
                        {
                            rows++;
                            double L = c.GetDistanceAtParameter(c.EndParam);
                            List<double> st;
                            if (mode == "Mesafe") st = ArmaturGeo.RowStations(L, x);
                            else
                            {
                                int n = mode == "Adet" ? cn : ArmaturField.CountFor(L, maxSp);
                                if (mode == "Lux")
                                {
                                    double Lm = L / upm, A = Lm * ap.CorrW;
                                    n = Ayd.CountFor(eCorr, A, ap.Lumen, Ayd.UF(Ayd.RoomIndex(A, 2 * (Lm + ap.CorrW), ap.H - ap.Wp), ap.Rc, ap.Rw), ap.MF);
                                }
                                st = ArmaturField.Line(L, n);
                            }
                            foreach (double d in st)
                            {
                                Point3d p = c.GetPointAtDist(d);
                                Vector3d t = c.GetFirstDerivative(c.GetParameterAtDistance(d));
                                places.Add(new ArmaturGeo.Place { P = new Point2d(p.X, p.Y), Angle = Math.Atan2(t.Y, t.X) });
                            }
                            lines.Add(tag + " koridor L=" + F(L) + ": " + st.Count + (st.Count > 0 ? ", u\u00e7lara " + F(st[0]) + ", aral\u0131k " + F(st.Count > 1 ? st[1] - st[0] : 0) : ""));
                        }
                        if (ap != null && places.Count > 0)
                        {
                            List<Point2d> lumM = places.Select(q => M(q.P)).ToList();
                            Ayd.Res res;
                            if (IsClosed(c)) { res = Ayd.Room(tag, Sample(c).Select(M).ToList(), lumM, ap); res.Target = mode == "Lux" ? eRoom : 0; }
                            else
                            {
                                double Lu = c.GetDistanceAtParameter(c.EndParam); List<Point2d> path = new List<Point2d>();
                                for (int si = 0; si <= 200; si++) { Point3d q = c.GetPointAtDist(Lu * si / 200); path.Add(M(new Point2d(q.X, q.Y))); }
                                res = Ayd.Corridor(tag, Lu / upm, ap.CorrW, path, lumM, ap); res.Target = mode == "Lux" ? eCorr : 0;
                            }
                            calc.Add(res);
                            if (ap.Label) Ayd.AddLabel(tx, space, db, res, upm, src.Layer);
                        }
                        foreach (ArmaturGeo.Place pl in places)
                        {
                            BlockReference b = BlockAlong.Recreate(tx, space, src, null, null, notes, false);
                            b.TransformBy(Matrix3d.Rotation(pl.Angle, Vector3d.ZAxis, src.Position));
                            b.TransformBy(Matrix3d.Displacement(new Point3d(pl.P.X, pl.P.Y, src.Position.Z) - src.Position));
                            total++;
                        }
                    }
                    tx.Commit();
                }
                string msg = "Armat\u00fcr yerle\u015fimi (" + mode + "): " + total + " armat\u00fcr (" + rows + " koridor, " + rooms + " oda). Geri almak i\u00e7in U.\n  " + string.Join("\n  ", lines);
                if (calc.Count > 0) msg += "\nAyd\u0131nlatma (\u03a6 kayna\u011f\u0131: " + ap.LumenSrc + "):\n  " + string.Join("\n  ", calc.Select(Ayd.Line)) + "\n" + Ayd.Disclaimer;
                if (calc.Count > 0 && ap.Excel) { try { msg += "\nExcel: " + Ayd.Export(doc, calc, ap); } catch (System.Exception ex) { msg += "\nExcel hatas\u0131: " + ex.Message; } }
                if (notes.Count > 0) msg += "\nNot: " + string.Join("; ", notes.Distinct().Take(8));
                ed.WriteMessage("\n" + msg + "\n");
                try { Metraj.Raise(msg); } catch (System.Exception) { }
            }
            catch (System.Exception ex) { ed.WriteMessage("\nArmat\u00fcr yerle\u015fimi hatas\u0131: " + ex.Message + "\n"); }
            finally { foreach (DBObjectCollection col in traced) foreach (DBObject o in col) { try { o.Dispose(); } catch (System.Exception) { } } }
        }
    }
}
