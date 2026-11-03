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

namespace CadAiDesigner
{
    /// <summary>Lumen (utilisation factor) method + approximate point calculation.
    /// UF table: APPROXIMATE generic values for a direct, wide-distribution recessed/surface luminaire (typical of
    /// CIBSE/LiTG manufacturer UF tables, floor reflectance 0.2). Real projects must use the luminaire's own UF/photometry.</summary>
    internal static class Ayd
    {
        public class Params { public double H = 3.0, Wp = 0.85, Lumen, MF = 0.8, Rc = 0.7, Rw = 0.5, Rf = 0.2, CorrW = 2.0; public bool Label = true, Excel; public string LumenSrc = "girildi"; }

        public class Res
        {
            public string Tag, Kind; public double A, P, a, b, Hm, K, UF, Eavg, Emin, Emax, Uo, Target, Lumen, MF; public int N, Pts;
            public Point2d Center;
        }

        static readonly double[] Ks = { 0.6, 0.8, 1.0, 1.25, 1.5, 2.0, 2.5, 3.0, 4.0, 5.0 };
        static readonly Dictionary<string, double[]> T = new Dictionary<string, double[]>
        {
            { "752", new[] { .40, .48, .54, .59, .63, .69, .73, .76, .80, .82 } },
            { "552", new[] { .38, .45, .51, .56, .60, .66, .70, .73, .77, .79 } },
            { "732", new[] { .33, .41, .47, .53, .57, .64, .68, .72, .76, .79 } },
            { "532", new[] { .32, .40, .46, .51, .55, .62, .66, .70, .74, .77 } },
            { "000", new[] { .25, .32, .38, .43, .47, .53, .57, .60, .64, .67 } },
        };

        static double Row(string key, double k)
        {
            double[] v = T[key];
            if (k <= Ks[0]) return v[0] * Math.Max(0.3, k / Ks[0]);
            if (k >= Ks[Ks.Length - 1]) return v[v.Length - 1];
            for (int i = 1; i < Ks.Length; i++)
                if (k <= Ks[i]) { double t = (k - Ks[i - 1]) / (Ks[i] - Ks[i - 1]); return v[i - 1] + t * (v[i] - v[i - 1]); }
            return v[v.Length - 1];
        }

        /// <summary>Bilinear over ceiling {0.5,0.7} x wall {0.3,0.5}; lower reflectances blend towards the 0/0/0 row.</summary>
        public static double UF(double k, double rc, double rw)
        {
            double c = Math.Max(0, Math.Min(1, (rc - 0.5) / 0.2)), w = Math.Max(0, Math.Min(1, (rw - 0.3) / 0.2));
            double lo = Row("532", k) * (1 - c) + Row("732", k) * c, hi = Row("552", k) * (1 - c) + Row("752", k) * c;
            double u = lo * (1 - w) + hi * w;
            double s = Math.Min(1, Math.Min(rc / 0.5, rw / 0.3));
            if (s < 1) u = Row("000", k) * (1 - s) + u * s;
            return Math.Round(u, 3);
        }

        public static double RoomIndex(double area, double perim, double h) { return h <= 0 || perim <= 0 ? 0 : area / (h * perim / 2); }

        public static int CountFor(double E, double area, double lumen, double uf, double mf)
        {
            return Math.Max(1, (int)Math.Ceiling(E * area / (lumen * uf * mf) - 1e-9));
        }

        public static double Area(List<Point2d> p) { double a = 0; for (int i = 0, j = p.Count - 1; i < p.Count; j = i++) a += (p[j].X + p[i].X) * (p[j].Y - p[i].Y); return Math.Abs(a) / 2; }
        public static double Perim(List<Point2d> p) { double s = 0; for (int i = 0, j = p.Count - 1; i < p.Count; j = i++) s += p[i].GetDistanceTo(p[j]); return s; }

        /// <summary>Direct horizontal illuminance of Lambertian downward luminaires (I = Φ/π·cosθ): E = Φ·h²/(π·d⁴). Relative use only.</summary>
        static double Direct(IList<Point2d> lum, Point2d p, double h, double lumen)
        {
            double e = 0;
            foreach (Point2d q in lum) { double r2 = Math.Pow((q - p).Length, 2) + h * h; e += lumen * h * h / (Math.PI * r2 * r2); }
            return e;
        }

        /// <summary>Room calc in metres. Eavg from lumen method; Uo = Emin/Emean of a point grid (wall zone 0.5 m) — approximate.</summary>
        public static Res Room(string tag, List<Point2d> polyM, List<Point2d> lumM, Params p)
        {
            Res r = new Res { Tag = tag, Kind = "oda", A = Area(polyM), P = Perim(polyM), N = lumM.Count, Lumen = p.Lumen, MF = p.MF };
            r.Hm = p.H - p.Wp; r.K = RoomIndex(r.A, r.P, r.Hm); r.UF = UF(r.K, p.Rc, p.Rw);
            r.Eavg = r.A > 0 ? r.N * p.Lumen * r.UF * p.MF / r.A : 0;
            double minx = polyM.Min(q => q.X), maxx = polyM.Max(q => q.X), miny = polyM.Min(q => q.Y), maxy = polyM.Max(q => q.Y);
            r.a = maxx - minx; r.b = maxy - miny; r.Center = new Point2d((minx + maxx) / 2, (miny + maxy) / 2);
            if (!ArmaturGeo.Inside(polyM, r.Center) && polyM.Count > 0) r.Center = new Point2d(polyM.Average(q => q.X), polyM.Average(q => q.Y));
            double step = Math.Max(0.1, Math.Min(0.5, Math.Max(r.a, r.b) / 40));
            double zone = Math.Min(0.5, Math.Min(r.a, r.b) / 6);
            List<double> es = new List<double>();
            for (double x = minx + step / 2; x < maxx; x += step)
                for (double y = miny + step / 2; y < maxy; y += step)
                {
                    Point2d q = new Point2d(x, y);
                    if (!ArmaturGeo.Inside(polyM, q) || ArmaturGeo.EdgeDist(polyM, q) < zone) continue;
                    es.Add(Direct(lumM, q, r.Hm, p.Lumen));
                }
            Finish(r, es);
            return r;
        }

        /// <summary>Corridor: area = L x width; Uo along the centreline.</summary>
        public static Res Corridor(string tag, double L, double width, List<Point2d> pathM, List<Point2d> lumM, Params p)
        {
            Res r = new Res { Tag = tag, Kind = "koridor", A = L * width, P = 2 * (L + width), a = L, b = width, N = lumM.Count, Lumen = p.Lumen, MF = p.MF };
            r.Hm = p.H - p.Wp; r.K = RoomIndex(r.A, r.P, r.Hm); r.UF = UF(r.K, p.Rc, p.Rw);
            r.Eavg = r.A > 0 ? r.N * p.Lumen * r.UF * p.MF / r.A : 0;
            List<double> es = pathM.Select(q => Direct(lumM, q, r.Hm, p.Lumen)).ToList();
            r.Center = pathM.Count > 0 ? pathM[pathM.Count / 2] : Point2d.Origin;
            Finish(r, es);
            return r;
        }

        static void Finish(Res r, List<double> es)
        {
            r.Pts = es.Count;
            if (es.Count == 0 || r.N == 0) { r.Uo = 0; r.Emin = 0; r.Emax = 0; return; }
            double mean = es.Average();
            r.Uo = Math.Round(es.Min() / mean, 2);
            r.Emin = r.Eavg * es.Min() / mean; r.Emax = r.Eavg * es.Max() / mean;
        }

        public static string F(double v, string f = "0.##") { return v.ToString(f, CultureInfo.InvariantCulture).Replace('.', ','); }

        public static string Line(Res r)
        {
            return r.Tag + " " + r.Kind + ": A=" + F(r.A, "0.0") + " m\u00b2, n=" + r.N + ", \u03a6=" + F(r.Lumen, "0") + " lm, h=" + F(r.Hm) + " m, k=" + F(r.K) + ", UF=" + F(r.UF) + ", MF=" + F(r.MF)
                + " -> Eort=" + F(r.Eavg, "0") + " lx" + (r.Target > 0 ? " (hedef " + F(r.Target, "0") + ")" : "") + ", Uo\u2248" + F(r.Uo) + ", Emin\u2248" + F(r.Emin, "0") + " lx";
        }

        public const string Disclaimer = "Not: Eort l\u00fcmen y\u00f6ntemi (UF tablosu genel/yakla\u015f\u0131k, do\u011frudan-geni\u015f armat\u00fcr); Uo/Emin yakla\u015f\u0131k (Lambert, yaln\u0131z do\u011frudan \u0131\u015f\u0131k, ters kare-kosin\u00fcs). Kesin sonu\u00e7 i\u00e7in DIALux/fotometri kullan\u0131n.";

        public static double LumenFrom(Transaction tx, BlockReference br, out string src)
        {
            src = null;
            foreach (ObjectId aid in br.AttributeCollection)
            {
                AttributeReference ar = tx.GetObject(aid, OpenMode.ForRead) as AttributeReference;
                if (ar == null) continue;
                string t = TrayKesif.Norm(ar.Tag);
                if (t == "lumen" || t == "lm" || t == "flux" || t == "isikakisi" || t == "akim" || t.StartsWith("lumen") || t.EndsWith("lumen"))
                {
                    System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(ar.TextString ?? "", @"\d+(?:[.,]\d+)?");
                    double v;
                    if (m.Success && double.TryParse(m.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0) { src = "attribute " + ar.Tag; return v; }
                }
            }
            if (br.IsDynamicBlock)
                foreach (DynamicBlockReferenceProperty dp in br.DynamicBlockReferencePropertyCollection)
                {
                    string t = TrayKesif.Norm(dp.PropertyName);
                    if ((t == "lumen" || t == "lm") && dp.Value != null) { double v; if (double.TryParse(Convert.ToString(dp.Value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0) { src = "dinamik " + dp.PropertyName; return v; } }
                }
            return 0;
        }

        static bool Num(Editor ed, string msg, ref double v, bool allowZero = false)
        {
            PromptDoubleResult r = ed.GetDouble(new PromptDoubleOptions(msg + ": ") { AllowNone = true, AllowNegative = false, AllowZero = allowZero, DefaultValue = v, UseDefaultValue = true });
            if (r.Status == PromptStatus.OK) v = r.Value;
            return r.Status == PromptStatus.OK || r.Status == PromptStatus.None;
        }

        /// <summary>Asks H, Φ (default from block), then optional MF/reflectances/work plane/corridor width; label + Excel.</summary>
        public static Params Ask(Editor ed, double lumenDefault, string lumenSrc, bool corridor)
        {
            Params p = new Params();
            if (!Num(ed, "\nTutturma y\u00fcksekli\u011fi H (m, zeminden)", ref p.H)) return null;
            p.Lumen = lumenDefault > 0 ? lumenDefault : 3600;
            if (!Num(ed, "\nArmat\u00fcr \u0131\u015f\u0131k ak\u0131s\u0131 \u03a6 (lm" + (lumenDefault > 0 ? ", " + lumenSrc : ", blokta bulunamad\u0131") + ")", ref p.Lumen)) return null;
            p.LumenSrc = lumenDefault > 0 && p.Lumen == lumenDefault ? lumenSrc : "girildi";
            while (true)
            {
                PromptKeywordOptions o = new PromptKeywordOptions("\nParametreler: \u00e7al\u0131\u015fma d\u00fczlemi " + F(p.Wp) + " m, MF " + F(p.MF) + ", yans\u0131ma " + F(p.Rc) + "/" + F(p.Rw) + "/" + F(p.Rf)
                    + (corridor ? ", koridor geni\u015fli\u011fi " + F(p.CorrW) + " m" : "") + " [Calisma/MF/Yansima" + (corridor ? "/Genislik" : "") + "/Tamam] <Tamam>: ", corridor ? "Calisma MF Yansima Genislik Tamam" : "Calisma MF Yansima Tamam") { AllowNone = true };
                PromptResult r = ed.GetKeywords(o);
                if (r.Status == PromptStatus.None || (r.Status == PromptStatus.OK && r.StringResult == "Tamam")) break;
                if (r.Status != PromptStatus.OK) return null;
                if (r.StringResult == "Calisma" && !Num(ed, "\n\u00c7al\u0131\u015fma d\u00fczlemi (m)", ref p.Wp, true)) return null;
                if (r.StringResult == "MF" && !Num(ed, "\nBak\u0131m fakt\u00f6r\u00fc MF", ref p.MF)) return null;
                if (r.StringResult == "Genislik" && !Num(ed, "\nKoridor geni\u015fli\u011fi (m)", ref p.CorrW)) return null;
                if (r.StringResult == "Yansima")
                {
                    if (!Num(ed, "\nTavan yans\u0131mas\u0131", ref p.Rc, true) || !Num(ed, "\nDuvar yans\u0131mas\u0131", ref p.Rw, true) || !Num(ed, "\nZemin yans\u0131mas\u0131", ref p.Rf, true)) return null;
                }
            }
            if (p.H - p.Wp <= 0.1) { ed.WriteMessage("\nH \u00e7al\u0131\u015fma d\u00fczleminden y\u00fcksek olmal\u0131.\n"); return null; }
            PromptResult lr = ed.GetKeywords(new PromptKeywordOptions("\nOdaya etiket (MTEXT) yaz\u0131ls\u0131n m\u0131? [Evet/Hayir] <Evet>: ", "Evet Hayir") { AllowNone = true });
            if (lr.Status == PromptStatus.Cancel) return null;
            p.Label = !(lr.Status == PromptStatus.OK && lr.StringResult == "Hayir");
            PromptResult xr = ed.GetKeywords(new PromptKeywordOptions("\nSonu\u00e7lar Excel'e aktar\u0131ls\u0131n m\u0131? [Evet/Hayir] <Hayir>: ", "Evet Hayir") { AllowNone = true });
            if (xr.Status == PromptStatus.Cancel) return null;
            p.Excel = xr.Status == PromptStatus.OK && xr.StringResult == "Evet";
            return p;
        }

        public static void AddLabel(Transaction tx, BlockTableRecord space, Database db, Res r, double unitsPerM, string layer)
        {
            MText t = new MText();
            t.SetDatabaseDefaults(db);
            t.Location = new Point3d(r.Center.X * unitsPerM, r.Center.Y * unitsPerM, 0);
            t.Attachment = AttachmentPoint.MiddleCenter;
            t.TextHeight = Math.Max(0.15, Math.Min(0.35, Math.Min(r.a, r.b) / 15)) * unitsPerM;
            t.Contents = "Eort\u2248" + F(r.Eavg, "0") + " lx  Uo\u2248" + F(r.Uo) + "\\Pn=" + r.N + " x " + F(r.Lumen, "0") + " lm  (UF " + F(r.UF) + ", MF " + F(r.MF) + ")";
            if (!string.IsNullOrEmpty(layer)) t.Layer = layer;
            space.AppendEntity(t); tx.AddNewlyCreatedDBObject(t, true);
        }

        public static string Export(Document doc, List<Res> rs, Params p)
        {
            List<object> rows = new List<object> { new object[] { "Alan", "T\u00fcr", "A (m\u00b2)", "a (m)", "b (m)", "h (m)", "k", "UF", "MF", "n", "\u03a6 (lm)", "Hedef (lx)", "Eort (lx)", "Emin\u2248 (lx)", "Uo\u2248" } };
            foreach (Res r in rs) rows.Add(new object[] { r.Tag, r.Kind, Math.Round(r.A, 2), Math.Round(r.a, 2), Math.Round(r.b, 2), Math.Round(r.Hm, 2), Math.Round(r.K, 2), r.UF, r.MF, r.N, Math.Round(r.Lumen), Math.Round(r.Target), Math.Round(r.Eavg), Math.Round(r.Emin), r.Uo });
            rows.Add(new object[0]);
            rows.Add(new object[] { "H=" + F(p.H) + " m, \u00e7al\u0131\u015fma d\u00fczlemi " + F(p.Wp) + " m, yans\u0131ma " + F(p.Rc) + "/" + F(p.Rw) + "/" + F(p.Rf) + ", \u03a6 kayna\u011f\u0131: " + p.LumenSrc });
            rows.Add(new object[] { Disclaimer });
            bool saved = Path.IsPathRooted(doc.Name) && File.Exists(doc.Name);
            string dir = saved ? Path.GetDirectoryName(doc.Name) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CadAiDesigner", "metraj");
            string path = Path.Combine(dir, Path.GetFileNameWithoutExtension(doc.Name) + "_aydinlatma_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".xlsx");
            JavaScriptSerializer js = new JavaScriptSerializer { MaxJsonLength = 64000000 };
            ServiceHost.EnsureStarted(Settings.Load());
            Dictionary<string, object> resp = js.Deserialize<Dictionary<string, object>>(ServiceHost.Post("/xlsx", js.Serialize(new Dictionary<string, object> { { "path", path }, { "sheets", new object[] { new object[] { "Aydınlatma", rows } } } })));
            if (resp == null || resp.ContainsKey("error")) throw new InvalidOperationException(resp == null ? "bo\u015f yan\u0131t" : Convert.ToString(resp["error"]));
            if (Environment.GetEnvironmentVariable("CADAI_NOOPEN") == null) { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); } catch (System.Exception) { } }
            return path;
        }
    }

    public class AydHesapCommands
    {
        [CommandMethod("AIDAYDHESAP", CommandFlags.Modal | CommandFlags.NoBlockEditor)]
        public void Run() { Do(); }

        [CommandMethod("AHESAP", CommandFlags.Modal | CommandFlags.NoBlockEditor)]
        public void RunShort() { Do(); }

        static void Do()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor; Database db = doc.Database;
            try
            {
                PromptEntityOptions eo = new PromptEntityOptions("\nOda / koridor polyline'\u0131n\u0131 se\u00e7in (veya Enter ile i\u00e7 nokta): ") { AllowNone = true };
                eo.SetRejectMessage("\nE\u011fri de\u011fil."); eo.AddAllowedClass(typeof(Curve), false);
                PromptEntityResult er = ed.GetEntity(eo);
                ObjectId cid = ObjectId.Null;
                if (er.Status == PromptStatus.OK) cid = er.ObjectId;
                else if (er.Status == PromptStatus.None)
                {
                    PromptPointResult pr = ed.GetPoint("\nOda i\u00e7inde bir nokta: ");
                    if (pr.Status != PromptStatus.OK) return;
                    cid = ArmaturCommands.EnclosingPublic(db, pr.Value);
                    if (cid.IsNull) { ed.WriteMessage("\nNoktay\u0131 i\u00e7eren kapal\u0131 polyline yok.\n"); return; }
                }
                else return;
                PromptSelectionResult sr = ed.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nArmat\u00fcrleri se\u00e7in (blok): " }, new SelectionFilter(new[] { new TypedValue(0, "INSERT") }));
                if (sr.Status != PromptStatus.OK) return;
                double upm = 1000 * ArmaturCommands.MmPublic(db);
                string lsrc = null; double lm;
                List<Point2d> lum = new List<Point2d>(); List<Point2d> poly; bool closed; double L = 0; List<Point2d> path = new List<Point2d>();
                using (Transaction tx = db.TransactionManager.StartOpenCloseTransaction())
                {
                    Curve c = (Curve)tx.GetObject(cid, OpenMode.ForRead);
                    closed = ArmaturCommands.IsClosedPublic(c);
                    poly = ArmaturCommands.SamplePublic(c).Select(q => new Point2d(q.X / upm, q.Y / upm)).ToList();
                    if (!closed) { L = c.GetDistanceAtParameter(c.EndParam) / upm; for (double d = 0; d <= L + 1e-9; d += Math.Max(0.1, L / 200)) { Point3d q = c.GetPointAtDist(Math.Min(d * upm, c.GetDistanceAtParameter(c.EndParam))); path.Add(new Point2d(q.X / upm, q.Y / upm)); } }
                    BlockReference first = null;
                    foreach (ObjectId id in sr.Value.GetObjectIds())
                    {
                        BlockReference br = (BlockReference)tx.GetObject(id, OpenMode.ForRead);
                        Point2d q = new Point2d(br.Position.X / upm, br.Position.Y / upm);
                        if (closed && !ArmaturGeo.Inside(poly, q)) continue;
                        lum.Add(q); if (first == null) first = br;
                    }
                    lm = first != null ? Ayd.LumenFrom(tx, first, out lsrc) : 0; if (first == null) lsrc = null;
                    tx.Commit();
                }
                if (lum.Count == 0) { ed.WriteMessage("\nSe\u00e7imde alan i\u00e7inde armat\u00fcr yok.\n"); return; }
                Ayd.Params p = Ayd.Ask(ed, lm, lsrc, !closed);
                if (p == null) return;
                Ayd.Res r = closed ? Ayd.Room(cid.Handle.ToString(), poly, lum, p) : Ayd.Corridor(cid.Handle.ToString(), L, p.CorrW, path, lum, p);
                string msg = "Ayd\u0131nlatma hesab\u0131:\n  " + Ayd.Line(r) + "\n" + Ayd.Disclaimer;
                if (p.Label)
                    using (doc.LockDocument())
                    using (Transaction tx = db.TransactionManager.StartTransaction())
                    {
                        Ayd.AddLabel(tx, (BlockTableRecord)tx.GetObject(db.CurrentSpaceId, OpenMode.ForWrite), db, r, upm, null);
                        tx.Commit();
                    }
                if (p.Excel) msg += "\nExcel: " + Ayd.Export(doc, new List<Ayd.Res> { r }, p);
                ed.WriteMessage("\n" + msg + "\n");
                try { Metraj.Raise(msg); } catch (System.Exception) { }
            }
            catch (System.Exception ex) { ed.WriteMessage("\nAyd\u0131nlatma hesab\u0131 hatas\u0131: " + ex.Message + "\n"); }
        }
    }
}
