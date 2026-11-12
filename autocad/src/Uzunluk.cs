using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace CadAiDesigner
{
    /// <summary>Remembered AIDUZUNLUK defaults (%LOCALAPPDATA%\CadAiDesigner\uzunluk.json).</summary>
    public class UzunlukSettings
    {
        public double Tolerans { get; set; }
        public bool Bloklar { get; set; }

        public UzunlukSettings() { Tolerans = 0.001; Bloklar = false; }

        static string FilePath { get { return Path.Combine(AppPaths.Dir, "uzunluk.json"); } }

        public static UzunlukSettings Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new UzunlukSettings();
                UzunlukSettings s = new JavaScriptSerializer().Deserialize<UzunlukSettings>(File.ReadAllText(FilePath));
                if (s == null) return new UzunlukSettings();
                if (s.Tolerans <= 0 || double.IsNaN(s.Tolerans) || double.IsInfinity(s.Tolerans)) s.Tolerans = 0.001;
                return s;
            }
            catch (System.Exception) { return new UzunlukSettings(); }
        }

        public void Save()
        {
            try { File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(this)); }
            catch (System.Exception) { }
        }
    }

    /// <summary>AIDUZUNLUK / UZT: read-only net length of selected curves, overlaps counted once.</summary>
    public class UzunlukCommands
    {
        const double ChordTol = 0.01;
        const double HalfStep = 1.0 * Math.PI / 180.0; // turn between half-chords; stored step is about 2°
        const int MaxDepth = 12;

        [CommandMethod("AIDUZUNLUK", CommandFlags.Modal | CommandFlags.UsePickSet)]
        [CommandMethod("UZT", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void Uzunluk()
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            try
            {
                UzunlukSettings opt = UzunlukSettings.Load();
                string cur = opt.Bloklar ? "Evet" : "Hayir";
                PromptKeywordOptions ko = new PromptKeywordOptions("\nBloklar [Evet/Hayir] <" + cur + ">: ", "Evet Hayir") { AllowNone = true };
                PromptResult kr = ed.GetKeywords(ko);
                if (kr.Status == PromptStatus.Cancel || kr.Status == PromptStatus.Error) return;
                if (kr.Status == PromptStatus.OK) opt.Bloklar = kr.StringResult == "Evet";

                PromptDoubleOptions po = new PromptDoubleOptions("\nTolerans <" + F(opt.Tolerans) + ">: ")
                {
                    AllowNone = true, AllowNegative = false, AllowZero = false,
                    DefaultValue = opt.Tolerans, UseDefaultValue = true
                };
                PromptDoubleResult dr = ed.GetDouble(po);
                if (dr.Status == PromptStatus.Cancel || dr.Status == PromptStatus.Error) return;
                if (dr.Status == PromptStatus.OK) opt.Tolerans = dr.Value;
                opt.Save();

                ObjectId[] ids = null;
                PromptSelectionResult sr = ed.SelectImplied();
                if (sr.Status == PromptStatus.OK && sr.Value != null && sr.Value.Count > 0)
                    ids = sr.Value.GetObjectIds();
                else
                {
                    PromptSelectionOptions so = new PromptSelectionOptions { MessageForAdding = "\nUzunluğu ölçülecek hatları seçin: " };
                    sr = ed.GetSelection(so);
                    if (sr.Status != PromptStatus.OK) return;
                    ids = sr.Value.GetObjectIds();
                }
                if (ids == null || ids.Length == 0) { ed.WriteMessage("\nSeçim yok.\n"); return; }

                LengthUnion union = new LengthUnion(opt.Tolerans, 1e-6);
                Tally tally = new Tally();
                // OpenCloseTransaction does not start an undo group. Only ForRead; Explode copies are not database-resident.
                using (Transaction tx = doc.Database.TransactionManager.StartOpenCloseTransaction())
                {
                    for (int i = 0; i < ids.Length; i++)
                    {
                        Entity e = null;
                        try { e = tx.GetObject(ids[i], OpenMode.ForRead) as Entity; }
                        catch (System.Exception) { tally.Skipped++; continue; }
                        if (e == null) { tally.Skipped++; continue; }
                        Hit h = Absorb(e, LayerOf(e, "0"), tx, union, tally, opt.Bloklar, 0, new List<ObjectId>());
                        if (h == Hit.Skip) tally.Skipped++;
                        else if (h == Hit.Infinite) tally.Infinite++;
                    }
                    tx.Commit();
                }

                LengthReport result = union.Compute();
                string text = Report(doc.Database, result, tally, opt);
                ed.WriteMessage("\n" + text + "\n");
                bool copied = false;
                try { System.Windows.Forms.Clipboard.SetText(text); copied = true; }
                catch (System.Exception) { }
                if (copied) ed.WriteMessage("Özet panoya kopyalandı.\n");
                AppLog.Write("UZUNLUK [" + Path.GetFileName(doc.Name) + "] net=" + result.Net.ToString("0.###", CultureInfo.InvariantCulture)
                    + " curves=" + tally.Curves + " skipped=" + tally.Skipped);
            }
            catch (System.Exception ex)
            {
                AppLog.Write("UZUNLUK FAILED: " + ex.Message);
                try { ed.WriteMessage("\nHat uzunluğu hesaplanamadı: " + ex.Message + "\n"); } catch (System.Exception) { }
            }
        }

        enum Hit { Curve, Skip, Infinite }

        sealed class Tally
        {
            public int Skipped, Infinite, Curves;
            public Dictionary<string, int> Types = new Dictionary<string, int>();
            public void Bump(string key)
            {
                int n;
                Types.TryGetValue(key, out n);
                Types[key] = n + 1;
                Curves++;
            }
        }

        static Hit Absorb(Entity e, string layer, Transaction tx, LengthUnion u, Tally tally, bool blocks, int depth, List<ObjectId> stack)
        {
            if (e is Ray || e is Xline) return Hit.Infinite;
            BlockReference br = e as BlockReference;
            if (br != null)
            {
                if (!blocks || depth > MaxDepth) return Hit.Skip;
                ObjectId def;
                try { def = br.BlockTableRecord; }
                catch (System.Exception) { return Hit.Skip; }
                if (def.IsNull || stack.Contains(def)) return Hit.Skip;
                stack.Add(def);
                DBObjectCollection col = new DBObjectCollection();
                try
                {
                    try { br.Explode(col); }
                    catch (System.Exception) { return Hit.Skip; }
                    foreach (DBObject o in col)
                    {
                        Entity c = o as Entity;
                        if (c == null) { tally.Skipped++; continue; }
                        try
                        {
                            Hit h = Absorb(c, LayerOf(c, layer), tx, u, tally, blocks, depth + 1, stack);
                            if (h == Hit.Skip) tally.Skipped++;
                            else if (h == Hit.Infinite) tally.Infinite++;
                        }
                        catch (System.Exception) { tally.Skipped++; }
                    }
                    return Hit.Curve;
                }
                finally
                {
                    foreach (DBObject o in col) { try { if (o != null) o.Dispose(); } catch (System.Exception) { } }
                    try { col.Dispose(); } catch (System.Exception) { }
                    stack.RemoveAt(stack.Count - 1);
                }
            }
            try
            {
                if (e is Line) { AddLine((Line)e, u, layer); tally.Bump("Çizgi"); return Hit.Curve; }
                if (e is Polyline) { AddPolyline((Polyline)e, u, layer); tally.Bump("Polyline"); return Hit.Curve; }
                if (e is Polyline2d) { AddPolyline2d((Polyline2d)e, tx, u, layer); tally.Bump("Polyline2d"); return Hit.Curve; }
                if (e is Polyline3d) { AddPolyline3d((Polyline3d)e, tx, u, layer); tally.Bump("Polyline3d"); return Hit.Curve; }
                if (e is Arc) { AddArc((Arc)e, u, layer); tally.Bump("Yay"); return Hit.Curve; }
                if (e is Circle) { AddCircle((Circle)e, u, layer); tally.Bump("Daire"); return Hit.Curve; }
                if (e is Ellipse) { AddEllipse((Ellipse)e, u, layer); tally.Bump("Elips"); return Hit.Curve; }
                if (e is Helix) { AddSampledCurve((Curve)e, u, layer); tally.Bump("Helix"); return Hit.Curve; }
                if (e is Spline) { AddSampledCurve((Spline)e, u, layer); tally.Bump("Spline"); return Hit.Curve; }
            }
            catch (System.Exception) { return Hit.Skip; }
            return Hit.Skip;
        }

        static string LayerOf(Entity e, string parent)
        {
            string layer = null;
            try { layer = e.Layer; } catch (System.Exception) { }
            if (string.IsNullOrEmpty(layer) || layer == "0") return string.IsNullOrEmpty(parent) ? "0" : parent;
            return layer;
        }

        static LuPoint Vp(Point3d p) { return new LuPoint(p.X, p.Y, p.Z); }
        static LuPoint Vv(Vector3d p) { return new LuPoint(p.X, p.Y, p.Z); }

        static void AddLine(Line line, LengthUnion u, string layer)
        {
            u.AddLine(Vp(line.StartPoint), Vp(line.EndPoint), layer);
        }

        static void AddPolyline(Polyline pl, LengthUnion u, string layer)
        {
            int n = pl.NumberOfVertices;
            int segN = pl.Closed ? n : n - 1;
            for (int i = 0; i < segN; i++)
            {
                SegmentType st = pl.GetSegmentType(i);
                if (st == SegmentType.Line)
                {
                    LineSegment3d ls = pl.GetLineSegmentAt(i);
                    u.AddLine(Vp(ls.StartPoint), Vp(ls.EndPoint), layer);
                }
                else if (st == SegmentType.Arc)
                    AddCircular(pl.GetArcSegmentAt(i), u, layer);
            }
        }

        static void AddCircular(CircularArc3d a, LengthUnion u, string layer)
        {
            if (a == null || a.Radius <= 1e-12) return;
            double sweep = a.EndAngle - a.StartAngle;
            if (sweep < 0) sweep += Math.PI * 2;
            if (sweep <= 1e-12)
            {
                if (a.StartPoint.DistanceTo(a.EndPoint) <= Math.Max(1e-8, a.Radius * 1e-8))
                    u.AddCircle(Vp(a.Center), Vv(a.Normal), a.Radius, layer);
                return;
            }
            u.AddArcFromPoint(Vp(a.Center), Vv(a.Normal), a.Radius, Vp(a.StartPoint), sweep, layer);
        }

        static void AddArc(Arc arc, LengthUnion u, string layer)
        {
            double sweep = arc.TotalAngle;
            if (sweep <= 1e-12 || arc.Radius <= 1e-12) return;
            u.AddArcFromPoint(Vp(arc.Center), Vv(arc.Normal), arc.Radius, Vp(arc.StartPoint), sweep, layer);
        }

        static void AddCircle(Circle c, LengthUnion u, string layer)
        {
            if (c.Radius <= 1e-12) return;
            u.AddCircle(Vp(c.Center), Vv(c.Normal), c.Radius, layer);
        }

        static void AddEllipse(Ellipse el, LengthUnion u, string layer)
        {
            double maj = el.MajorRadius, minr = el.MinorRadius;
            bool circular = Math.Abs(maj - minr) <= Math.Max(u.Tolerance, Math.Abs(maj) * 1e-6);
            double sweep = el.EndAngle - el.StartAngle;
            if (sweep < 0) sweep += Math.PI * 2;
            bool full = el.Closed || Math.Abs(el.EndParam - el.StartParam) >= Math.PI * 2 - 1e-6;
            if (circular && maj > 1e-12)
            {
                if (full || sweep >= Math.PI * 2 - 1e-6) u.AddCircle(Vp(el.Center), Vv(el.Normal), maj, layer);
                else u.AddArcFromPoint(Vp(el.Center), Vv(el.Normal), maj, Vp(el.StartPoint), sweep, layer);
                return;
            }
            AddSampledCurve(el, u, layer);
        }

        static void AddPolyline2d(Polyline2d pl, Transaction tx, LengthUnion u, string layer)
        {
            if (pl.PolyType != Poly2dType.SimplePoly) { AddSampledCurve(pl, u, layer); return; }
            try
            {
                List<Point3d> pts = new List<Point3d>();
                List<double> bulges = new List<double>();
                int idx = 0;
                foreach (ObjectId id in IdsOf(pl))
                {
                    Vertex2d v = tx.GetObject(id, OpenMode.ForRead) as Vertex2d;
                    if (v == null) throw new InvalidOperationException("vertex");
                    Point3d wcs;
                    try { wcs = pl.GetPointAtParameter(idx); }
                    catch (System.Exception)
                    {
                        Point3d ocs = new Point3d(v.Position.X, v.Position.Y, pl.Elevation);
                        wcs = ocs.TransformBy(Matrix3d.PlaneToWorld(pl.Normal));
                    }
                    pts.Add(wcs);
                    bulges.Add(v.Bulge);
                    idx++;
                }
                if (pts.Count < 2) throw new InvalidOperationException("vertex");
                AddBulged(pts, bulges, pl.Closed, pl.Normal, u, layer);
            }
            catch (System.Exception) { AddSampledCurve(pl, u, layer); }
        }

        static void AddPolyline3d(Polyline3d pl, Transaction tx, LengthUnion u, string layer)
        {
            if (pl.PolyType != Poly3dType.SimplePoly) { AddSampledCurve(pl, u, layer); return; }
            try
            {
                List<LuPoint> pts = new List<LuPoint>();
                foreach (ObjectId id in IdsOf(pl))
                {
                    PolylineVertex3d v = tx.GetObject(id, OpenMode.ForRead) as PolylineVertex3d;
                    if (v == null) throw new InvalidOperationException("vertex");
                    pts.Add(Vp(v.Position));
                }
                if (pts.Count < 2) throw new InvalidOperationException("vertex");
                int segN = pl.Closed ? pts.Count : pts.Count - 1;
                for (int i = 0; i < segN; i++)
                    u.AddLine(pts[i], pts[(i + 1) % pts.Count], layer);
            }
            catch (System.Exception) { AddSampledCurve(pl, u, layer); }
        }

        static void AddBulged(List<Point3d> pts, List<double> bulges, bool closed, Vector3d normal, LengthUnion u, string layer)
        {
            int n = pts.Count;
            if (n < 2) return;
            int segN = closed ? n : n - 1;
            LuPoint nrm = Vv(normal);
            for (int i = 0; i < segN; i++)
            {
                LuPoint p1 = Vp(pts[i]), p2 = Vp(pts[(i + 1) % n]);
                double b = i < bulges.Count ? bulges[i] : 0;
                LuPoint center, arcN;
                double radius, sweep;
                if (!LengthUnion.BulgeArc(p1, p2, b, nrm, out center, out arcN, out radius, out sweep))
                {
                    u.AddLine(p1, p2, layer);
                    continue;
                }
                if (sweep >= Math.PI * 2 - 1e-8) u.AddCircle(center, arcN, radius, layer);
                else u.AddArcFromPoint(center, arcN, radius, p1, sweep, layer);
            }
        }

        /// <summary>ObjectIds from a Polyline2d/Polyline3d enumerator (vertices are separate objects).</summary>
        static List<ObjectId> IdsOf(System.Collections.IEnumerable source)
        {
            List<ObjectId> ids = new List<ObjectId>();
            System.Collections.IEnumerator en = source.GetEnumerator();
            try
            {
                while (en.MoveNext())
                    if (en.Current is ObjectId) ids.Add((ObjectId)en.Current);
            }
            finally
            {
                IDisposable d = en as IDisposable;
                if (d != null) d.Dispose();
            }
            return ids;
        }

        static void AddSampledCurve(Curve c, LengthUnion u, string layer)
        {
            double exact = 0;
            try
            {
                exact = c.GetDistanceAtParameter(c.EndParam) - c.GetDistanceAtParameter(c.StartParam);
                if (exact < 0) exact = -exact;
            }
            catch (System.Exception) { exact = 0; }
            List<LuPoint> pts = new List<LuPoint>();
            pts.Add(Vp(c.StartPoint));
            Walk(c, c.StartParam, c.EndParam, c.StartPoint, c.EndPoint, pts, 0);
            bool closed = false;
            try { closed = c.Closed; } catch (System.Exception) { }
            if (closed && pts.Count > 2 && Dist(pts[0], pts[pts.Count - 1]) <= 1e-8) pts.RemoveAt(pts.Count - 1);
            u.AddSampled(pts, exact, closed, layer);
        }

        static void Walk(Curve c, double a, double b, Point3d pa, Point3d pb, List<LuPoint> pts, int depth)
        {
            if (pts.Count > 8000 || depth >= 18) { pts.Add(Vp(pb)); return; }
            double midp = (a + b) * 0.5;
            Point3d pm;
            try { pm = c.GetPointAtParameter(midp); }
            catch (System.Exception) { pts.Add(Vp(pb)); return; }
            double err = DistToSeg(pm, pa, pb);
            double turn = 0;
            Vector3d d1 = pm - pa, d2 = pb - pm;
            if (d1.Length > 1e-12 && d2.Length > 1e-12) turn = d1.GetAngleTo(d2);
            if ((err > ChordTol || turn > HalfStep) && pa.DistanceTo(pb) > 1e-9)
            {
                Walk(c, a, midp, pa, pm, pts, depth + 1);
                Walk(c, midp, b, pm, pb, pts, depth + 1);
            }
            else pts.Add(Vp(pb));
        }

        static double DistToSeg(Point3d p, Point3d a, Point3d b)
        {
            Vector3d ab = b - a;
            double l2 = ab.LengthSqrd;
            if (l2 < 1e-18) return p.DistanceTo(a);
            double t = (p - a).DotProduct(ab) / l2;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            return p.DistanceTo(a + ab * t);
        }

        static double Dist(LuPoint a, LuPoint b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        static CultureInfo Tr()
        {
            try { return CultureInfo.GetCultureInfo("tr-TR"); }
            catch (System.Exception) { return CultureInfo.InvariantCulture; }
        }

        static string F(double v) { return v.ToString("N2", Tr()); }

        static bool MetersOf(UnitsValue units, out double factor, out string name, out string note)
        {
            factor = 0; note = null;
            switch (units)
            {
                case UnitsValue.Millimeters: name = "mm"; factor = 0.001; return true;
                case UnitsValue.Centimeters: name = "cm"; factor = 0.01; return true;
                case UnitsValue.Decimeters: name = "dm"; factor = 0.1; return true;
                case UnitsValue.Meters: name = "m"; factor = 1; return true;
                case UnitsValue.Kilometers: name = "km"; factor = 1000; return true;
                case UnitsValue.Inches: name = "inç"; factor = 0.0254; return true;
                case UnitsValue.Feet: name = "fit"; factor = 0.3048; return true;
                case UnitsValue.Undefined:
                    name = "tanımsız";
                    note = "INSUNITS tanımsız; dönüşüm yapılmadı, uzunluklar çizim birimidir.";
                    return false;
                default:
                    name = units.ToString();
                    note = "INSUNITS=" + units + " için metre dönüşümü yok; uzunluklar çizim birimidir.";
                    return false;
            }
        }

        static string Both(double drawing, bool hasM, double factor)
        {
            string s = F(drawing) + " çizim birimi";
            if (hasM) s += " (" + F(drawing * factor) + " m)";
            return s;
        }

        static string Report(Database db, LengthReport r, Tally tally, UzunlukSettings opt)
        {
            double factor; string uname, note;
            bool hasM = MetersOf(db.Insunits, out factor, out uname, out note);
            StringBuilder sb = new StringBuilder();
            sb.Append("—— Hat uzunluğu (AIDUZUNLUK) ——\n");
            sb.Append("Bloklar: " + (opt.Bloklar ? "Evet" : "Hayır") + "    Tolerans: " + opt.Tolerans.ToString("0.######", Tr()) + " çizim birimi\n");
            sb.Append("Nesne sayıları:\n");
            string[] order = { "Çizgi", "Polyline", "Polyline2d", "Polyline3d", "Yay", "Daire", "Elips", "Spline", "Helix" };
            for (int i = 0; i < order.Length; i++)
            {
                int n;
                if (tally.Types.TryGetValue(order[i], out n) && n > 0) sb.Append("  " + order[i] + ": " + n + "\n");
            }
            foreach (KeyValuePair<string, int> kv in tally.Types)
            {
                bool known = false;
                for (int i = 0; i < order.Length; i++) if (order[i] == kv.Key) known = true;
                if (!known && kv.Value > 0) sb.Append("  " + kv.Key + ": " + kv.Value + "\n");
            }
            sb.Append("  Eğri toplam: " + tally.Curves + "\n");
            sb.Append("  Atlanan: " + tally.Skipped + "\n");
            if (tally.Infinite > 0) sb.Append("  Sonsuz (Ray/Xline, atlandı): " + tally.Infinite + "\n");
            sb.Append("Ham toplam uzunluk: " + Both(r.Raw, hasM, factor) + "\n");
            sb.Append("Çakışan (tekrar eden) uzunluk: " + Both(r.Overlap, hasM, factor) + "\n");
            sb.Append("****************************************\n");
            sb.Append("NET HAT UZUNLUĞU: " + Both(r.Net, hasM, factor) + "\n");
            sb.Append("****************************************\n");
            if (r.Layers.Count > 0)
            {
                sb.Append("Katman net uzunluk (çakışma önce katman içinde çıkarılır):\n");
                for (int i = 0; i < r.Layers.Count; i++)
                {
                    LayerLength L = r.Layers[i];
                    sb.Append("  " + L.Name + "    " + Both(L.Net, hasM, factor) + "\n");
                }
                sb.Append("Genel net, farklı katmanlardaki çakışmayı da bir kez sayar.\n");
            }
            sb.Append("Uzunluklar 3 boyutludur; çakışma da 3B doğrultuda birleştirilir. Çizim değiştirilmedi.\n");
            if (hasM)
            {
                string how = factor == 0.001 ? "çizim birimi / 1000" : factor == 0.01 ? "çizim birimi / 100" : factor == 1 ? "çizim birimi = metre" : "çizim birimi × " + factor.ToString("0.########", CultureInfo.InvariantCulture);
                sb.Append("Birim: " + uname + " (INSUNITS). Metre = " + how + ".\n");
            }
            else if (note != null) sb.Append(note + "\n");
            return sb.ToString().TrimEnd();
        }
    }
}
