using System;
using System.Collections.Generic;

namespace CadAiDesigner
{
    /// <summary>3D point. Plain struct so length-union math can be tested without AutoCAD.</summary>
    public struct LuPoint
    {
        public double X, Y, Z;
        public LuPoint(double x, double y, double z) { X = x; Y = y; Z = z; }
    }

    /// <summary>Net length of one layer after overlap removal inside that layer only.</summary>
    public sealed class LayerLength
    {
        public string Name;
        public int Count;
        public double Raw, Net;
    }

    /// <summary>Raw length (overlaps included), net length (union), and per-layer nets.</summary>
    public sealed class LengthReport
    {
        public double Raw, Net, Overlap;
        public int Count;
        public List<LayerLength> Layers = new List<LayerLength>();
    }

    /// <summary>
    /// Union length of 3D curves. Collinear straight segments share a 1D interval merge;
    /// arcs on the same circle share an angular merge (a circle is a full turn);
    /// sampled curves (splines, ellipses) keep their exact length when nothing overlaps them,
    /// count an exact copy once (forward or reversed), and approximate partial overlaps
    /// by the union of the sampled chords. No AutoCAD types.
    /// </summary>
    public sealed class LengthUnion
    {
        const double Zero = 1e-12;
        const double MergeGap = 1e-9;
        static readonly double FullSweep = 2 * Math.PI - 1e-8;
        static readonly double ArcChordMax = 10.0 * Math.PI / 180.0;

        public double Tolerance;
        public double AngleTolerance;

        readonly List<Piece> pieces = new List<Piece>();

        public LengthUnion() : this(0.001, 1e-6) { }

        public LengthUnion(double tolerance, double angleTolerance)
        {
            Tolerance = tolerance;
            AngleTolerance = angleTolerance;
        }

        public bool AddLine(LuPoint a, LuPoint b, string layer)
        {
            double len = Dist(a, b);
            if (len <= Zero) return false;
            Piece p = new Piece("line", layer, len);
            p.A = a; p.B = b;
            pieces.Add(p);
            return true;
        }

        public bool AddArc(LuPoint center, LuPoint normal, double radius, double startAngle, double sweep, string layer)
        {
            LuPoint n;
            if (!Unit(normal, out n) || radius <= Zero) return false;
            if (sweep < 0)
            {
                startAngle = startAngle + sweep;
                sweep = -sweep;
            }
            bool full = sweep >= FullSweep;
            if (full) sweep = Math.PI * 2;
            else sweep = Math.Min(sweep, Math.PI * 2);
            if (sweep <= Zero) return false;
            Piece p = new Piece("arc", layer, radius * sweep);
            p.Center = center; p.Normal = n; p.Radius = radius; p.Start = startAngle; p.Sweep = sweep; p.Full = full;
            pieces.Add(p);
            return true;
        }

        /// <summary>Arc given by its start point (WCS) and CCW sweep about <paramref name="normal"/>.</summary>
        public bool AddArcFromPoint(LuPoint center, LuPoint normal, double radius, LuPoint startPoint, double sweep, string layer)
        {
            LuPoint u, v;
            if (!Basis(normal, out u, out v)) return false;
            LuPoint rel = Sub(startPoint, center);
            double start = Math.Atan2(Dot(rel, v), Dot(rel, u));
            return AddArc(center, normal, radius, start, sweep, layer);
        }

        public bool AddCircle(LuPoint center, LuPoint normal, double radius, string layer)
        {
            return AddArc(center, normal, radius, 0, Math.PI * 2, layer);
        }

        public bool AddSampled(IList<LuPoint> points, double exact, bool closed, string layer)
        {
            if (points == null || points.Count < 2) return false;
            List<LuPoint> pts = new List<LuPoint>(points.Count);
            for (int i = 0; i < points.Count; i++) pts.Add(points[i]);
            if (closed && Dist(pts[0], pts[pts.Count - 1]) <= Tolerance) pts.RemoveAt(pts.Count - 1);
            List<LuPoint> clean = new List<LuPoint>();
            clean.Add(pts[0]);
            for (int i = 1; i < pts.Count; i++)
                if (Dist(clean[clean.Count - 1], pts[i]) > Zero) clean.Add(pts[i]);
            if (closed && clean.Count >= 2 && Dist(clean[0], clean[clean.Count - 1]) <= Zero) clean.RemoveAt(clean.Count - 1);
            if (clean.Count < 2) return false;
            if (closed && clean.Count < 3) closed = false;
            double chord = ChordLength(clean, closed);
            if (exact <= Zero) exact = chord;
            Piece p = new Piece("sampled", layer, exact);
            p.Points = clean; p.Closed = closed;
            pieces.Add(p);
            return true;
        }

        /// <summary>Bulge (tan of included angle / 4) to a circular arc. False when the segment is straight.</summary>
        public static bool BulgeArc(LuPoint p1, LuPoint p2, double bulge, LuPoint normal,
            out LuPoint center, out LuPoint arcNormal, out double radius, out double sweep)
        {
            center = new LuPoint(); arcNormal = new LuPoint(); radius = 0; sweep = 0;
            LuPoint n;
            if (!Unit(normal, out n)) n = new LuPoint(0, 0, 1);
            LuPoint dvec = Sub(p2, p1);
            double d = Len(dvec);
            if (d <= Zero || Math.Abs(bulge) < 1e-10) return false;
            double signed = 4.0 * Math.Atan(bulge);
            LuPoint direction = Mul(dvec, 1.0 / d);
            LuPoint left = Cross(n, direction);
            double ln = Len(left);
            if (ln < 1e-12) return false;
            left = Mul(left, 1.0 / ln);
            double offset = (d * 0.5) * ((1.0 - bulge * bulge) / (2.0 * bulge));
            LuPoint mid = Mul(Add(p1, p2), 0.5);
            center = Add(mid, Mul(left, offset));
            radius = Math.Abs(d * (bulge * bulge + 1.0) / (4.0 * bulge));
            arcNormal = signed >= 0 ? n : Mul(n, -1);
            sweep = Math.Abs(signed);
            return true;
        }

        public LengthReport Compute()
        {
            Totals overall = Evaluate(pieces, Tolerance, AngleTolerance);
            List<string> names = new List<string>();
            for (int i = 0; i < pieces.Count; i++)
                if (!names.Contains(pieces[i].Layer)) names.Add(pieces[i].Layer);
            names.Sort(StringComparer.Ordinal);
            LengthReport report = new LengthReport
            {
                Raw = overall.Raw, Net = overall.Net, Overlap = overall.Overlap, Count = overall.Count
            };
            for (int i = 0; i < names.Count; i++)
            {
                List<Piece> sub = new List<Piece>();
                for (int k = 0; k < pieces.Count; k++)
                    if (pieces[k].Layer == names[i]) sub.Add(pieces[k]);
                Totals t = Evaluate(sub, Tolerance, AngleTolerance);
                report.Layers.Add(new LayerLength { Name = names[i], Count = t.Count, Raw = t.Raw, Net = t.Net });
            }
            return report;
        }

        /// <summary>Same cases as tests/test_length_union.py. Empty list means every case passed.</summary>
        public static List<string> SelfTest()
        {
            List<string> err = new List<string>();
            Check(err, "identical", Run(delegate (LengthUnion u) {
                u.AddLine(P(0, 0, 0), P(10, 0, 0), "0");
                u.AddLine(P(0, 0, 0), P(10, 0, 0), "0");
            }), 20, 10);
            Check(err, "partial", Run(delegate (LengthUnion u) {
                u.AddLine(P(0, 0, 0), P(10, 0, 0), "0");
                u.AddLine(P(5, 0, 0), P(15, 0, 0), "0");
            }), 20, 15);
            Check(err, "reversed", Run(delegate (LengthUnion u) {
                u.AddLine(P(0, 0, 0), P(10, 0, 0), "0");
                u.AddLine(P(10, 0, 0), P(0, 0, 0), "0");
            }), 20, 10);
            Check(err, "contained", Run(delegate (LengthUnion u) {
                u.AddLine(P(0, 0, 0), P(10, 0, 0), "0");
                u.AddLine(P(2, 0, 0), P(4, 0, 0), "0");
            }), 12, 10);
            Check(err, "polyline share", Run(delegate (LengthUnion u) {
                u.AddLine(P(0, 0, 0), P(10, 0, 0), "0");
                u.AddLine(P(0, 0, 0), P(10, 0, 0), "0");
                u.AddLine(P(10, 0, 0), P(10, 5, 0), "0");
            }), 25, 15);
            Check(err, "crossing", Run(delegate (LengthUnion u) {
                u.AddLine(P(0, 0, 0), P(10, 0, 0), "0");
                u.AddLine(P(5, -5, 0), P(5, 5, 0), "0");
            }), 20, 20);
            LengthUnion z = new LengthUnion();
            if (z.AddLine(P(1, 1, 1), P(1, 1, 1), "0")) err.Add("zero line was accepted");
            z.AddLine(P(0, 0, 0), P(4, 0, 0), "0");
            Check(err, "zero ignored", z.Compute(), 4, 4);
            LengthUnion zo = new LengthUnion();
            zo.AddLine(P(0, 0, 0), P(0, 0, 0), "0");
            LengthReport zr = zo.Compute();
            if (zr.Count != 0 || Math.Abs(zr.Net) > 1e-12) err.Add("zero only");
            Check(err, "gap", Run(delegate (LengthUnion u) {
                u.AddLine(P(0, 0, 0), P(10, 0, 0), "0");
                u.AddLine(P(10.05, 0, 0), P(20, 0, 0), "0");
            }), 19.95, 19.95);
            Check(err, "parallel out", Run(delegate (LengthUnion u) {
                u.AddLine(P(0, 0, 0), P(10, 0, 0), "0");
                u.AddLine(P(0, 0.01, 0), P(10, 0.01, 0), "0");
            }), 20, 20);
            Check(err, "parallel in", Run(delegate (LengthUnion u) {
                u.AddLine(P(0, 0, 0), P(10, 0, 0), "0");
                u.AddLine(P(0, 0.0005, 0), P(10, 0.0005, 0), "0");
            }), 20, 10);
            Check(err, "chain", Run(delegate (LengthUnion u) {
                u.AddLine(P(0, 0, 0), P(10, 0, 0), "0");
                u.AddLine(P(8, 0, 0), P(18, 0, 0), "0");
                u.AddLine(P(16, 0, 0), P(20, 0, 0), "0");
            }), 24, 20);
            Check(err, "3d partial", Run(delegate (LengthUnion u) {
                u.AddLine(P(0, 0, 0), P(0, 0, 10), "0");
                u.AddLine(P(0, 0, 5), P(0, 0, 15), "0");
            }), 20, 15);
            Check(err, "3d offset", Run(delegate (LengthUnion u) {
                u.AddLine(P(0, 0, 0), P(10, 0, 0), "0");
                u.AddLine(P(0, 0, 1), P(10, 0, 1), "0");
            }), 20, 20);
            double s3 = Math.Sqrt(3);
            Check(err, "diagonal", Run(delegate (LengthUnion u) {
                u.AddLine(P(0, 0, 0), P(1, 1, 1), "0");
                u.AddLine(P(1, 1, 1), P(0, 0, 0), "0");
            }), 2 * s3, s3);
            double half = 10 * Math.PI / 2;
            Check(err, "arc dup", Run(delegate (LengthUnion u) {
                LuPoint zup = P(0, 0, 1);
                u.AddArc(P(0, 0, 0), zup, 10, 0, Math.PI / 2, "0");
                u.AddArc(P(0, 0, 0), zup, 10, 0, Math.PI / 2, "0");
            }), 2 * half, half);
            Check(err, "arc partial", Run(delegate (LengthUnion u) {
                LuPoint zup = P(0, 0, 1);
                u.AddArc(P(0, 0, 0), zup, 2, 0, Math.PI, "0");
                u.AddArc(P(0, 0, 0), zup, 2, Math.PI / 2, Math.PI, "0");
            }), 2 * (2 * Math.PI), 2 * 1.5 * Math.PI);
            Check(err, "arc wrap", Run(delegate (LengthUnion u) {
                LuPoint zup = P(0, 0, 1);
                u.AddArc(P(0, 0, 0), zup, 4, 2 * Math.PI - 0.2, 0.4, "0");
                u.AddArc(P(0, 0, 0), zup, 4, 2 * Math.PI - 0.2, 0.4, "0");
            }), 2 * 4 * 0.4, 4 * 0.4);
            Check(err, "arc opposite", OppositeNormal(), 2 * 10 * (Math.PI / 2), 10 * (Math.PI / 2));
            double circ = 2 * Math.PI * 5, arcLen = 5 * Math.PI;
            Check(err, "circle+arc", Run(delegate (LengthUnion u) {
                LuPoint zup = P(0, 0, 1);
                u.AddCircle(P(1, 2, 3), zup, 5, "0");
                u.AddArc(P(1, 2, 3), zup, 5, 0.3, Math.PI, "0");
            }), circ + arcLen, circ);
            double c3 = 2 * Math.PI * 3;
            Check(err, "two circles", Run(delegate (LengthUnion u) {
                u.AddCircle(P(0, 0, 0), P(0, 0, 1), 3, "0");
                u.AddCircle(P(0, 0, 0), P(0, 0, -1), 3, "0");
            }), 2 * c3, c3);
            Check(err, "adjacent arcs", Run(delegate (LengthUnion u) {
                LuPoint zup = P(0, 0, 1);
                u.AddArc(P(0, 0, 0), zup, 5, 0, Math.PI / 2, "0");
                u.AddArc(P(0, 0, 0), zup, 5, Math.PI / 2, Math.PI / 2, "0");
            }), 5 * Math.PI, 5 * Math.PI);
            double da = 2 * Math.PI, db = 3 * Math.PI / 2;
            Check(err, "two radii", Run(delegate (LengthUnion u) {
                LuPoint zup = P(0, 0, 1);
                u.AddArc(P(0, 0, 0), zup, 2, 0, Math.PI, "0");
                u.AddArc(P(0, 0, 0), zup, 3, 0, Math.PI / 2, "0");
            }), da + db, da + db);
            Check(err, "sampled exact", Run(delegate (LengthUnion u) {
                u.AddSampled(new[] { P(0, 0, 0), P(5, 0, 0), P(5, 5, 0) }, 10.2, false, "0");
            }), 10.2, 10.2);
            LuPoint[] sp = new[] { P(0, 0, 0), P(3, 0, 0), P(3, 4, 0) };
            LuPoint[] spr = new[] { P(3, 4, 0), P(3, 0, 0), P(0, 0, 0) };
            Check(err, "sampled reverse", Run(delegate (LengthUnion u) {
                u.AddSampled(sp, 10.2, false, "0");
                u.AddSampled(spr, 10.2, false, "0");
            }), 20.4, 10.2);
            Check(err, "sampled closed reverse", Run(delegate (LengthUnion u) {
                u.AddSampled(new[] { P(0, 0, 0), P(1, 0, 0), P(1, 1, 0), P(0, 1, 0) }, 4.5, true, "0");
                u.AddSampled(new[] { P(0, 1, 0), P(1, 1, 0), P(1, 0, 0), P(0, 0, 0) }, 4.5, true, "0");
            }), 9, 4.5);
            Check(err, "sampled cyclic", Run(delegate (LengthUnion u) {
                u.AddSampled(new[] { P(0, 0, 0), P(1, 0, 0), P(1, 1, 0), P(0, 1, 0) }, 4.5, true, "0");
                u.AddSampled(new[] { P(1, 0, 0), P(1, 1, 0), P(0, 1, 0), P(0, 0, 0) }, 4.5, true, "0");
            }), 9, 4.5);
            Check(err, "sampled partial", Run(delegate (LengthUnion u) {
                u.AddSampled(new[] { P(0, 0, 0), P(10, 0, 0), P(10, 10, 0) }, 20, false, "0");
                u.AddSampled(new[] { P(10, 0, 0), P(10, 10, 0), P(20, 10, 0) }, 20, false, "0");
            }), 40, 30);
            Check(err, "sampled+line", Run(delegate (LengthUnion u) {
                u.AddLine(P(0, 0, 0), P(10, 0, 0), "0");
                u.AddSampled(new[] { P(0, 0, 0), P(10, 0, 0), P(10, 5, 0) }, 15, false, "0");
            }), 25, 15);
            Check(err, "sampled vertices", Run(delegate (LengthUnion u) {
                u.AddSampled(new[] { P(0, 0, 0), P(10, 0, 0) }, 10, false, "0");
                u.AddSampled(new[] { P(0, 0, 0), P(5, 0, 0), P(10, 0, 0) }, 10, false, "0");
            }), 20, 10);
            LengthReport lay = Run(delegate (LengthUnion u) {
                u.AddLine(P(0, 0, 0), P(10, 0, 0), "A");
                u.AddLine(P(0, 0, 0), P(10, 0, 0), "B");
                u.AddLine(P(0, 5, 0), P(4, 5, 0), "A");
            });
            Check(err, "layers", lay, 24, 14);
            if (lay.Layers.Count != 2 || lay.Layers[0].Name != "A" || lay.Layers[1].Name != "B") err.Add("layer order");
            else
            {
                if (Math.Abs(lay.Layers[0].Net - 14) > 1e-6 || Math.Abs(lay.Layers[1].Net - 10) > 1e-6) err.Add("layer nets");
            }
            Check(err, "tiny z", Run(delegate (LengthUnion u) {
                u.AddLine(P(0, 0, 0), P(10, 0, 0), "0");
                u.AddLine(P(0, 0, 0.0004), P(10, 0, 0.0004), "0");
            }), 20, 10);
            LuPoint bc, bn; double br, bs;
            if (!BulgeArc(P(0, 0, 0), P(2, 0, 0), 1, P(0, 0, 1), out bc, out bn, out br, out bs)
                || Math.Abs(bc.X - 1) > 1e-9 || Math.Abs(bc.Y) > 1e-9 || Math.Abs(br - 1) > 1e-9 || Math.Abs(bs - Math.PI) > 1e-9 || bn.Z <= 0)
                err.Add("bulge semicircle");
            double bq = Math.Tan(Math.PI / 8);
            if (!BulgeArc(P(1, 0, 0), P(0, 1, 0), bq, P(0, 0, 1), out bc, out bn, out br, out bs)
                || Math.Abs(bc.X) > 1e-9 || Math.Abs(bc.Y) > 1e-9 || Math.Abs(br - 1) > 1e-9 || Math.Abs(bs - Math.PI / 2) > 1e-9)
                err.Add("bulge quarter");
            if (!BulgeArc(P(1, 0, 0), P(0, 1, 0), -bq, P(0, 0, 1), out bc, out bn, out br, out bs)
                || Math.Abs(bc.X - 1) > 1e-9 || Math.Abs(bc.Y - 1) > 1e-9 || bn.Z >= 0 || Math.Abs(bs - Math.PI / 2) > 1e-9)
                err.Add("bulge negative");
            return err;
        }

        static LengthReport OppositeNormal()
        {
            LuPoint zup = P(0, 0, 1), zdn = P(0, 0, -1);
            double sweep = Math.PI / 2, radius = 10;
            LuPoint end = ArcPoint(P(0, 0, 0), zup, radius, sweep);
            LuPoint u, v;
            Basis(zdn, out u, out v);
            LuPoint rel = Sub(end, P(0, 0, 0));
            double a0 = Math.Atan2(Dot(rel, v), Dot(rel, u));
            return Run(delegate (LengthUnion un) {
                un.AddArc(P(0, 0, 0), zup, radius, 0, sweep, "0");
                un.AddArc(P(0, 0, 0), zdn, radius, a0, sweep, "0");
            });
        }

        delegate void Build(LengthUnion u);

        static LengthReport Run(Build build)
        {
            LengthUnion u = new LengthUnion();
            build(u);
            return u.Compute();
        }

        static void Check(List<string> err, string name, LengthReport r, double raw, double net)
        {
            if (Math.Abs(r.Raw - raw) > 1e-6 || Math.Abs(r.Net - net) > 1e-6 || Math.Abs(r.Overlap - (raw - net)) > 1e-6)
                err.Add(name + " raw=" + r.Raw.ToString("R") + " net=" + r.Net.ToString("R") + " expected " + raw.ToString("R") + "/" + net.ToString("R"));
        }

        static LuPoint P(double x, double y, double z) { return new LuPoint(x, y, z); }

        sealed class Piece
        {
            public string Kind, Layer;
            public double Exact, Radius, Start, Sweep;
            public bool Full, Closed;
            public LuPoint A, B, Center, Normal;
            public List<LuPoint> Points;
            public Piece(string kind, string layer, double exact)
            {
                Kind = kind; Layer = string.IsNullOrEmpty(layer) ? "0" : layer; Exact = exact;
            }
        }

        struct Iv
        {
            public double A, B;
            public Iv(double a, double b) { A = a; B = b; }
        }

        sealed class LineGroup
        {
            public LuPoint Dir, Origin;
            public List<Iv> Intervals = new List<Iv>();
            public List<Iv> Merged = new List<Iv>();
            public List<Part> Parts = new List<Part>();
        }

        struct Part
        {
            public int Owner;
            public double Lo, Hi;
        }

        sealed class ArcGroup
        {
            public LuPoint Center, Normal, U, V;
            public double Radius;
            public bool Full;
            public List<Iv> Intervals = new List<Iv>();
            public List<Iv> Merged = new List<Iv>();
        }

        struct Totals
        {
            public double Raw, Net, Overlap;
            public int Count;
        }

        static Totals Evaluate(List<Piece> items, double tol, double ang)
        {
            double raw = 0;
            List<Piece> lines = new List<Piece>(), arcs = new List<Piece>(), samples = new List<Piece>();
            for (int i = 0; i < items.Count; i++)
            {
                raw += items[i].Exact;
                if (items[i].Kind == "line") lines.Add(items[i]);
                else if (items[i].Kind == "arc") arcs.Add(items[i]);
                else samples.Add(items[i]);
            }
            List<LineGroup> lineGroups;
            List<ArcGroup> arcGroups;
            double lineNet = LineUnion(lines, tol, ang, out lineGroups);
            double arcNet = ArcUnion(arcs, tol, ang, out arcGroups);
            double sampNet = SampledNet(samples, lineGroups, arcGroups, tol, ang);
            double net = lineNet + arcNet + sampNet;
            if (net < 0) net = 0;
            double overlap = raw - net;
            if (overlap < 0 && overlap > -1e-6) { overlap = 0; net = raw; }
            Totals t;
            t.Raw = raw; t.Net = net; t.Count = items.Count;
            t.Overlap = overlap >= 0 ? raw - net : overlap;
            return t;
        }

        static double LineUnion(List<Piece> lines, double tol, double ang, out List<LineGroup> groups)
        {
            groups = new List<LineGroup>();
            for (int i = 0; i < lines.Count; i++)
            {
                Piece s = lines[i];
                LuPoint d;
                if (!Unit(Sub(s.B, s.A), out d)) continue;
                bool placed = false;
                for (int g = 0; g < groups.Count; g++)
                {
                    LineGroup gr = groups[g];
                    if (!Parallel(d, gr.Dir, ang)) continue;
                    if (PointLineDist(s.A, gr.Origin, gr.Dir) > tol || PointLineDist(s.B, gr.Origin, gr.Dir) > tol) continue;
                    double t0 = Dot(Sub(s.A, gr.Origin), gr.Dir);
                    double t1 = Dot(Sub(s.B, gr.Origin), gr.Dir);
                    gr.Intervals.Add(new Iv(t0, t1));
                    placed = true;
                    break;
                }
                if (!placed)
                {
                    LineGroup gr = new LineGroup();
                    gr.Dir = d; gr.Origin = s.A;
                    gr.Intervals.Add(new Iv(0, Dot(Sub(s.B, s.A), d)));
                    groups.Add(gr);
                }
            }
            double total = 0;
            for (int g = 0; g < groups.Count; g++)
            {
                List<Iv> merged = new List<Iv>();
                double ln = MergeIntervals(groups[g].Intervals, MergeGap, merged);
                groups[g].Merged = merged;
                total += ln;
            }
            return total;
        }

        static double ArcUnion(List<Piece> arcs, double tol, double ang, out List<ArcGroup> groups)
        {
            groups = new List<ArcGroup>();
            for (int i = 0; i < arcs.Count; i++)
            {
                Piece arc = arcs[i];
                LuPoint n;
                if (!Unit(arc.Normal, out n)) continue;
                bool placed = false;
                for (int g = 0; g < groups.Count; g++)
                {
                    ArcGroup gr = groups[g];
                    if (Math.Abs(arc.Radius - gr.Radius) > tol || Dist(arc.Center, gr.Center) > tol) continue;
                    double nd = Dot(n, gr.Normal);
                    if (Math.Abs(nd) < Math.Cos(ang)) continue;
                    AttachArc(gr, arc, nd > 0);
                    placed = true;
                    break;
                }
                if (!placed)
                {
                    LuPoint cn = Canonical(n);
                    ArcGroup gr = NewArcGroup(arc.Center, cn, arc.Radius);
                    AttachArc(gr, arc, Dot(n, cn) > 0);
                    groups.Add(gr);
                }
            }
            double total = 0;
            for (int g = 0; g < groups.Count; g++)
            {
                ArcGroup gr = groups[g];
                if (gr.Full)
                {
                    gr.Merged = new List<Iv>();
                    gr.Merged.Add(new Iv(0, Math.PI * 2));
                    total += Math.PI * 2 * gr.Radius;
                    continue;
                }
                List<Iv> merged = new List<Iv>();
                double sweep = MergeAngles(gr.Intervals, merged);
                gr.Merged = merged;
                if (sweep >= Math.PI * 2 - MergeGap)
                {
                    gr.Full = true;
                    gr.Merged = new List<Iv>();
                    gr.Merged.Add(new Iv(0, Math.PI * 2));
                    total += Math.PI * 2 * gr.Radius;
                }
                else total += gr.Radius * sweep;
            }
            return total;
        }

        static ArcGroup NewArcGroup(LuPoint center, LuPoint normal, double radius)
        {
            ArcGroup g = new ArcGroup();
            g.Center = center; g.Normal = normal; g.Radius = radius;
            Basis(normal, out g.U, out g.V);
            return g;
        }

        static void AttachArc(ArcGroup g, Piece arc, bool same)
        {
            if (arc.Full || arc.Sweep >= FullSweep) { g.Full = true; return; }
            LuPoint sp = ArcPoint(arc.Center, arc.Normal, arc.Radius, arc.Start);
            LuPoint rel = Sub(sp, g.Center);
            double a0 = NormAng(Math.Atan2(Dot(rel, g.V), Dot(rel, g.U)));
            double start = same ? a0 : a0 - arc.Sweep;
            if (AddAngInterval(g.Intervals, start, arc.Sweep)) g.Full = true;
        }

        static double SampledNet(List<Piece> samples, List<LineGroup> lineGroups, List<ArcGroup> arcGroups, double tol, double ang)
        {
            List<Piece> unique = Dedup(samples, tol);
            if (unique.Count == 0) return 0;
            List<PartSeg> tagged = new List<PartSeg>();
            for (int i = 0; i < unique.Count; i++)
            {
                List<Seg> segs = Pairs(unique[i]);
                for (int k = 0; k < segs.Count; k++)
                    tagged.Add(new PartSeg { Owner = i, A = segs[k].A, B = segs[k].B });
            }
            List<LineGroup> sgroups = SegmentGroups(tagged, tol, ang);
            int n = unique.Count;
            int[] parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            bool[] selfOv = new bool[n];
            for (int g = 0; g < sgroups.Count; g++)
            {
                List<Part> parts = sgroups[g].Parts;
                parts.Sort(delegate (Part a, Part b) {
                    int c = a.Lo.CompareTo(b.Lo);
                    return c != 0 ? c : a.Hi.CompareTo(b.Hi);
                });
                for (int i = 0; i < parts.Count; i++)
                {
                    for (int j = i + 1; j < parts.Count; j++)
                    {
                        if (parts[j].Lo > parts[i].Hi + MergeGap) break;
                        double ov = Math.Min(parts[i].Hi, parts[j].Hi) - Math.Max(parts[i].Lo, parts[j].Lo);
                        if (ov <= 1e-8) continue;
                        if (parts[i].Owner == parts[j].Owner) selfOv[parts[i].Owner] = true;
                        else Unite(parent, parts[i].Owner, parts[j].Owner);
                    }
                }
            }
            List<int>[] clusters = new List<int>[n];
            for (int i = 0; i < n; i++)
            {
                int r = Find(parent, i);
                if (clusters[r] == null) clusters[r] = new List<int>();
                clusters[r].Add(i);
            }
            double total = 0;
            for (int r = 0; r < n; r++)
            {
                List<int> members = clusters[r];
                if (members == null) continue;
                bool multi = members.Count > 1;
                if (!multi)
                    for (int m = 0; m < members.Count; m++) if (selfOv[members[m]]) multi = true;
                if (!multi)
                {
                    int i = members[0];
                    double chord = 0, covered = 0;
                    List<Seg> segs = Pairs(unique[i]);
                    for (int k = 0; k < segs.Count; k++)
                    {
                        double sl = Dist(segs[k].A, segs[k].B);
                        chord += sl;
                        covered += CoveredLength(segs[k].A, segs[k].B, lineGroups, arcGroups, tol, ang);
                    }
                    if (chord <= Zero) continue;
                    double frac = covered / chord;
                    if (frac < 0) frac = 0;
                    if (frac > 1) frac = 1;
                    total += unique[i].Exact * (1 - frac);
                    continue;
                }
                List<Seg> all = new List<Seg>();
                List<Seg> cov = new List<Seg>();
                for (int m = 0; m < members.Count; m++)
                {
                    List<Seg> segs = Pairs(unique[members[m]]);
                    for (int k = 0; k < segs.Count; k++)
                    {
                        if (Dist(segs[k].A, segs[k].B) <= Zero) continue;
                        all.Add(segs[k]);
                        List<Seg> parts = CoveredSubsegments(segs[k].A, segs[k].B, lineGroups, arcGroups, tol, ang);
                        for (int t = 0; t < parts.Count; t++) cov.Add(parts[t]);
                    }
                }
                double extra = UnionPairs(all, tol, ang) - UnionPairs(cov, tol, ang);
                if (extra > 0) total += extra;
            }
            return total;
        }

        struct Seg { public LuPoint A, B; }
        struct PartSeg { public int Owner; public LuPoint A, B; }

        static List<Piece> Dedup(List<Piece> samples, double tol)
        {
            List<Piece> unique = new List<Piece>();
            for (int i = 0; i < samples.Count; i++)
            {
                Piece hit = null;
                for (int j = 0; j < unique.Count; j++)
                    if (SameSampled(samples[i], unique[j], tol)) { hit = unique[j]; break; }
                if (hit == null) unique.Add(samples[i]);
                else if (samples[i].Exact > hit.Exact) hit.Exact = samples[i].Exact;
            }
            return unique;
        }

        static bool SameSampled(Piece a, Piece b, double tol)
        {
            if (a.Closed != b.Closed || a.Points.Count != b.Points.Count) return false;
            int n = a.Points.Count;
            if (!a.Closed)
            {
                bool forward = true, backward = true;
                for (int i = 0; i < n; i++)
                {
                    if (Dist(a.Points[i], b.Points[i]) > tol) forward = false;
                    if (Dist(a.Points[i], b.Points[n - 1 - i]) > tol) backward = false;
                }
                return forward || backward;
            }
            return CyclicMatch(a.Points, b.Points, tol);
        }

        static bool CyclicMatch(List<LuPoint> pa, List<LuPoint> pb, double tol)
        {
            int n = pa.Count;
            for (int k = 0; k < n; k++)
            {
                bool ok = true;
                for (int i = 0; i < n; i++)
                    if (Dist(pa[i], pb[(i + k) % n]) > tol) { ok = false; break; }
                if (ok) return true;
            }
            for (int k = 0; k < n; k++)
            {
                bool ok = true;
                for (int i = 0; i < n; i++)
                    if (Dist(pa[i], pb[(n - 1 - ((i + k) % n))]) > tol) { ok = false; break; }
                if (ok) return true;
            }
            return false;
        }

        static List<Seg> Pairs(Piece curve)
        {
            List<Seg> outp = new List<Seg>();
            int n = curve.Points.Count;
            int m = curve.Closed ? n : n - 1;
            for (int i = 0; i < m; i++)
            {
                LuPoint a = curve.Points[i], b = curve.Points[(i + 1) % n];
                if (Dist(a, b) > Zero) outp.Add(new Seg { A = a, B = b });
            }
            return outp;
        }

        static List<LineGroup> SegmentGroups(List<PartSeg> tagged, double tol, double ang)
        {
            List<LineGroup> groups = new List<LineGroup>();
            for (int i = 0; i < tagged.Count; i++)
            {
                LuPoint a = tagged[i].A, b = tagged[i].B;
                LuPoint d;
                if (!Unit(Sub(b, a), out d)) continue;
                bool placed = false;
                for (int g = 0; g < groups.Count; g++)
                {
                    LineGroup gr = groups[g];
                    if (!Parallel(d, gr.Dir, ang)) continue;
                    if (PointLineDist(a, gr.Origin, gr.Dir) > tol || PointLineDist(b, gr.Origin, gr.Dir) > tol) continue;
                    double t0 = Dot(Sub(a, gr.Origin), gr.Dir);
                    double t1 = Dot(Sub(b, gr.Origin), gr.Dir);
                    Part part;
                    part.Owner = tagged[i].Owner;
                    part.Lo = Math.Min(t0, t1); part.Hi = Math.Max(t0, t1);
                    gr.Parts.Add(part);
                    placed = true;
                    break;
                }
                if (!placed)
                {
                    LineGroup gr = new LineGroup();
                    gr.Dir = d; gr.Origin = a;
                    double t1 = Dot(Sub(b, a), d);
                    Part part;
                    part.Owner = tagged[i].Owner;
                    part.Lo = Math.Min(0, t1); part.Hi = Math.Max(0, t1);
                    gr.Parts.Add(part);
                    groups.Add(gr);
                }
            }
            return groups;
        }

        static double CoveredLength(LuPoint a, LuPoint b, List<LineGroup> lineGroups, List<ArcGroup> arcGroups, double tol, double ang)
        {
            double seg = Dist(a, b);
            double cov = LineCovered(a, b, lineGroups, tol, ang) + ArcCovered(a, b, arcGroups, tol);
            if (cov > seg) cov = seg;
            return cov;
        }

        static double LineCovered(LuPoint a, LuPoint b, List<LineGroup> groups, double tol, double ang)
        {
            LuPoint d;
            if (!Unit(Sub(b, a), out d)) return 0;
            double seg = Dist(a, b), best = 0;
            for (int g = 0; g < groups.Count; g++)
            {
                LineGroup gr = groups[g];
                if (!Parallel(d, gr.Dir, ang)) continue;
                if (PointLineDist(a, gr.Origin, gr.Dir) > tol || PointLineDist(b, gr.Origin, gr.Dir) > tol) continue;
                double t0 = Dot(Sub(a, gr.Origin), gr.Dir);
                double t1 = Dot(Sub(b, gr.Origin), gr.Dir);
                double ov = OverlapLength(new Iv(t0, t1), gr.Merged);
                if (ov > best) best = ov;
            }
            if (best > seg) best = seg;
            return best;
        }

        static double ArcCovered(LuPoint a, LuPoint b, List<ArcGroup> groups, double tol)
        {
            double seg = Dist(a, b);
            if (seg <= Zero) return 0;
            LuPoint mid = Mul(Add(a, b), 0.5);
            double half = seg * 0.5, best = 0;
            for (int g = 0; g < groups.Count; g++)
            {
                ArcGroup gr = groups[g];
                if (half > gr.Radius + tol) continue;
                if (Math.Abs(Dist(a, gr.Center) - gr.Radius) > tol || Math.Abs(Dist(b, gr.Center) - gr.Radius) > tol) continue;
                double expected = Math.Sqrt(Math.Max(0, gr.Radius * gr.Radius - Math.Min(half, gr.Radius) * Math.Min(half, gr.Radius)));
                if (Math.Abs(Dist(mid, gr.Center) - expected) > tol) continue;
                double span = gr.Radius > Zero ? 2 * Math.Asin(Math.Min(1, half / gr.Radius)) : 0;
                if (span > ArcChordMax) continue;
                double ta = NormAng(Math.Atan2(Dot(Sub(a, gr.Center), gr.V), Dot(Sub(a, gr.Center), gr.U)));
                double tb = NormAng(Math.Atan2(Dot(Sub(b, gr.Center), gr.V), Dot(Sub(b, gr.Center), gr.U)));
                double ccw = NormAng(tb - ta);
                double start, sweep;
                if (ccw <= Math.PI) { start = ta; sweep = ccw; }
                else { start = tb; sweep = NormAng(ta - tb); }
                if (sweep > ArcChordMax + 1e-8 || sweep <= Zero) continue;
                double coveredAng;
                if (gr.Full) coveredAng = sweep;
                else
                {
                    List<Iv> pieces = new List<Iv>();
                    AddAngInterval(pieces, start, sweep);
                    coveredAng = 0;
                    for (int i = 0; i < pieces.Count; i++) coveredAng += OverlapLength(pieces[i], gr.Merged);
                }
                double frac = coveredAng / sweep;
                if (frac < 0) frac = 0;
                if (frac > 1) frac = 1;
                double got = seg * frac;
                if (got > best) best = got;
            }
            return best;
        }

        static List<Seg> CoveredSubsegments(LuPoint a, LuPoint b, List<LineGroup> lineGroups, List<ArcGroup> arcGroups, double tol, double ang)
        {
            List<Seg> result = new List<Seg>();
            LuPoint d;
            if (!Unit(Sub(b, a), out d)) return result;
            double seg = Dist(a, b);
            List<Iv> portions = new List<Iv>();
            for (int g = 0; g < lineGroups.Count; g++)
            {
                LineGroup gr = lineGroups[g];
                if (!Parallel(d, gr.Dir, ang)) continue;
                if (PointLineDist(a, gr.Origin, gr.Dir) > tol || PointLineDist(b, gr.Origin, gr.Dir) > tol) continue;
                double sign = Dot(d, gr.Dir);
                if (Math.Abs(sign) < 0.5) continue;
                double ta = Dot(Sub(a, gr.Origin), gr.Dir);
                for (int i = 0; i < gr.Merged.Count; i++)
                {
                    double s0 = (gr.Merged[i].A - ta) / sign;
                    double s1 = (gr.Merged[i].B - ta) / sign;
                    double lo = Math.Min(s0, s1), hi = Math.Max(s0, s1);
                    if (lo < 0) lo = 0;
                    if (hi > seg) hi = seg;
                    if (hi - lo > Zero) portions.Add(new Iv(lo, hi));
                }
            }
            double arcCov = ArcCovered(a, b, arcGroups, tol);
            if (arcCov >= seg - 1e-9) portions.Add(new Iv(0, seg));
            else if (arcCov > 1e-8) portions.Add(new Iv(0, arcCov));
            List<Iv> merged = new List<Iv>();
            MergeIntervals(portions, MergeGap, merged);
            for (int i = 0; i < merged.Count; i++)
                result.Add(new Seg { A = Add(a, Mul(d, merged[i].A)), B = Add(a, Mul(d, merged[i].B)) });
            return result;
        }

        static double UnionPairs(List<Seg> pairs, double tol, double ang)
        {
            List<Piece> fake = new List<Piece>();
            for (int i = 0; i < pairs.Count; i++)
            {
                double len = Dist(pairs[i].A, pairs[i].B);
                if (len <= Zero) continue;
                Piece p = new Piece("line", "0", len);
                p.A = pairs[i].A; p.B = pairs[i].B;
                fake.Add(p);
            }
            List<LineGroup> ignore;
            return LineUnion(fake, tol, ang, out ignore);
        }

        static double ChordLength(List<LuPoint> pts, bool closed)
        {
            if (pts.Count < 2) return 0;
            double total = 0;
            int m = closed ? pts.Count : pts.Count - 1;
            for (int i = 0; i < m; i++) total += Dist(pts[i], pts[(i + 1) % pts.Count]);
            return total;
        }

        static int Find(int[] parent, int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }

        static void Unite(int[] parent, int a, int b)
        {
            int ra = Find(parent, a), rb = Find(parent, b);
            if (ra != rb) parent[rb] = ra;
        }

        static double MergeIntervals(List<Iv> intervals, double gap, List<Iv> merged)
        {
            List<Iv> items = new List<Iv>();
            for (int i = 0; i < intervals.Count; i++)
            {
                double lo = Math.Min(intervals[i].A, intervals[i].B);
                double hi = Math.Max(intervals[i].A, intervals[i].B);
                if (hi - lo > Zero) items.Add(new Iv(lo, hi));
            }
            items.Sort(delegate (Iv a, Iv b) { return a.A.CompareTo(b.A); });
            if (items.Count == 0) return 0;
            double cs = items[0].A, ce = items[0].B;
            for (int i = 1; i < items.Count; i++)
            {
                if (items[i].A <= ce + gap) { if (items[i].B > ce) ce = items[i].B; }
                else { merged.Add(new Iv(cs, ce)); cs = items[i].A; ce = items[i].B; }
            }
            merged.Add(new Iv(cs, ce));
            double length = 0;
            for (int i = 0; i < merged.Count; i++) length += merged[i].B - merged[i].A;
            return length;
        }

        static double MergeAngles(List<Iv> intervals, List<Iv> merged)
        {
            double length = MergeIntervals(intervals, MergeGap, merged);
            if (merged.Count == 0) return 0;
            if (merged.Count >= 2)
            {
                double wrap = merged[0].A + Math.PI * 2 - merged[merged.Count - 1].B;
                if (wrap >= 0 && wrap <= MergeGap) length += wrap;
            }
            if (length >= Math.PI * 2 - MergeGap)
            {
                merged.Clear();
                merged.Add(new Iv(0, Math.PI * 2));
                return Math.PI * 2;
            }
            if (length > Math.PI * 2) length = Math.PI * 2;
            return length;
        }

        static bool AddAngInterval(List<Iv> intervals, double start, double sweep)
        {
            if (sweep >= FullSweep) return true;
            sweep = Math.Min(sweep, Math.PI * 2);
            double a = NormAng(start);
            double b = a + sweep;
            if (b <= Math.PI * 2 + 1e-15)
            {
                if (b - a > Zero) intervals.Add(new Iv(a, Math.Min(b, Math.PI * 2)));
            }
            else
            {
                if (Math.PI * 2 - a > Zero) intervals.Add(new Iv(a, Math.PI * 2));
                double end = b - Math.PI * 2;
                if (end > Zero) intervals.Add(new Iv(0, end));
            }
            return false;
        }

        static double OverlapLength(Iv interval, List<Iv> merged)
        {
            double lo = Math.Min(interval.A, interval.B), hi = Math.Max(interval.A, interval.B), total = 0;
            for (int i = 0; i < merged.Count; i++)
            {
                double x0 = Math.Max(lo, merged[i].A), x1 = Math.Min(hi, merged[i].B);
                if (x1 > x0) total += x1 - x0;
            }
            return total;
        }

        static double NormAng(double a)
        {
            double tw = Math.PI * 2;
            a = a % tw;
            if (a < 0) a += tw;
            return a;
        }

        static LuPoint ArcPoint(LuPoint center, LuPoint normal, double radius, double theta)
        {
            LuPoint u, v;
            Basis(normal, out u, out v);
            return Add(center, Mul(Add(Mul(u, Math.Cos(theta)), Mul(v, Math.Sin(theta))), radius));
        }

        static bool Basis(LuPoint normal, out LuPoint u, out LuPoint v)
        {
            u = new LuPoint(); v = new LuPoint();
            LuPoint n;
            if (!Unit(normal, out n)) return false;
            LuPoint helper = Math.Abs(n.X) < 0.9 ? new LuPoint(1, 0, 0) : new LuPoint(0, 1, 0);
            if (!Unit(Cross(n, helper), out u)) return false;
            v = Cross(n, u);
            return true;
        }

        static LuPoint Canonical(LuPoint v)
        {
            const double eps = 1e-12;
            if (v.X < -eps || (Math.Abs(v.X) <= eps && v.Y < -eps) || (Math.Abs(v.X) <= eps && Math.Abs(v.Y) <= eps && v.Z < 0))
                return Mul(v, -1);
            return v;
        }

        static bool Parallel(LuPoint d1, LuPoint d2, double ang)
        {
            double c = Math.Abs(Dot(d1, d2));
            if (c > 1) c = 1;
            return c >= Math.Cos(ang);
        }

        static double PointLineDist(LuPoint p, LuPoint origin, LuPoint direction)
        {
            return Len(Cross(Sub(p, origin), direction));
        }

        static bool Unit(LuPoint a, out LuPoint u)
        {
            double n = Len(a);
            if (n <= Zero) { u = new LuPoint(); return false; }
            u = Mul(a, 1.0 / n);
            return true;
        }

        static LuPoint Add(LuPoint a, LuPoint b) { return new LuPoint(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        static LuPoint Sub(LuPoint a, LuPoint b) { return new LuPoint(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        static LuPoint Mul(LuPoint a, double s) { return new LuPoint(a.X * s, a.Y * s, a.Z * s); }
        static double Dot(LuPoint a, LuPoint b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }
        static LuPoint Cross(LuPoint a, LuPoint b)
        {
            return new LuPoint(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        }
        static double Len(LuPoint a) { return Math.Sqrt(Dot(a, a)); }
        static double Dist(LuPoint a, LuPoint b) { return Len(Sub(a, b)); }
    }
}
