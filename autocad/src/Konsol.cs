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
    /// <summary>Remembered KONSOL defaults (%LOCALAPPDATA%\CadAiDesigner\konsol.json). Drawing units (this project: cm).</summary>
    public class KonsolSettings
    {
        public double aralik { get; set; } = 150;
        public double pay { get; set; } = 30;
        public double ek { get; set; } = 7.5;
        public double atla { get; set; } = 5;
        /// <summary>Edge-to-edge gap (cm) at or under which parallel trays share one konsol.</summary>
        public double yan { get; set; } = 10;

        static string FilePath { get { return Path.Combine(AppPaths.Dir, "konsol.json"); } }

        public static KonsolSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    KonsolSettings s = new JavaScriptSerializer().Deserialize<KonsolSettings>(File.ReadAllText(FilePath));
                    if (s != null)
                    {
                        if (s.aralik < 1) s.aralik = 150;
                        if (s.pay < 0) s.pay = 30;
                        if (s.ek < 0) s.ek = 7.5;
                        if (s.atla < 0) s.atla = 5;
                        if (s.yan < 0) s.yan = 10;
                        return s;
                    }
                }
            }
            catch (System.Exception) { }
            return new KonsolSettings();
        }

        public void Save()
        {
            try { File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(this)); } catch (System.Exception) { }
        }
    }

    /// <summary>Pure 2D layout: collinear tray runs, double-layer overlap, branch gaps, spacing along allowed intervals.</summary>
    internal static class KonsolGeo
    {
        public const double AngTol = 2.0 * Math.PI / 180.0;
        public const double LatTol = 5.0;
        public const double ConnectTol = 15.0;
        public const double EndSnap = 10.0;

        internal class Seg
        {
            public double X0, Y0, X1, Y1, Z, En, VisW, Dx, Dy, Len;
            public bool Selected;
        }

        internal class Cov
        {
            public double A, B, En, VisW;
            public bool Selected;
        }

        internal class FitPt
        {
            public double X, Y, Half;
            /// <summary>Tee/cross. Elbows (YD) are handled by the run's end margin.</summary>
            public bool Branch;
        }

        internal class Run
        {
            public double Ox, Oy, Dx, Dy, Z, L;
            public List<Cov> Covs = new List<Cov>();
            public List<double[]> Zones = new List<double[]>();
        }

        internal class St
        {
            public double X, Y, Z, En, Ang;
            public int Kat;
            /// <summary>Side-by-side span: Dist1 ceilings to the next allowed value (never shorter than the outer width).</summary>
            public bool Span;
        }

        class Lane
        {
            public Run Run;
            public double Lat, T0, Scale, HalfMax;
            public List<double[]> Sel = new List<double[]>();
        }

        public static void SetDir(Seg s)
        {
            double dx = s.X1 - s.X0, dy = s.Y1 - s.Y0;
            double len = Math.Sqrt(dx * dx + dy * dy);
            s.Len = len;
            if (len < 1e-9) { s.Dx = 1; s.Dy = 0; return; }
            s.Dx = dx / len; s.Dy = dy / len;
        }

        public static void Canonical(double dx, double dy, out double cdx, out double cdy)
        {
            cdx = dx; cdy = dy;
            if (cdx < -1e-9 || (Math.Abs(cdx) <= 1e-9 && cdy < 0)) { cdx = -dx; cdy = -dy; }
        }

        static double Cross(double ax, double ay, double bx, double by) { return ax * by - ay * bx; }

        static bool SameLine(Seg a, Seg b)
        {
            if (a.Len < 1 || b.Len < 1) return false;
            if (Math.Abs(Cross(a.Dx, a.Dy, b.Dx, b.Dy)) > Math.Sin(AngTol)) return false;
            return LatLine(a.X0, a.Y0, a.Dx, a.Dy, b.X0, b.Y0) <= LatTol
                && LatLine(a.X0, a.Y0, a.Dx, a.Dy, b.X1, b.Y1) <= LatTol;
        }

        static double LatLine(double ox, double oy, double dx, double dy, double x, double y)
        {
            return Math.Abs(Cross(dx, dy, x - ox, y - oy));
        }

        public static double SegDist(Seg s, double x, double y)
        {
            double t = (x - s.X0) * s.Dx + (y - s.Y0) * s.Dy;
            if (t < 0) t = 0; else if (t > s.Len) t = s.Len;
            double px = s.X0 + s.Dx * t, py = s.Y0 + s.Dy * t;
            double dx = x - px, dy = y - py;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>One run per connected collinear chain of selected trays. Overlapping trays stay in the same run (one konsol for the stack).</summary>
        public static List<Run> Build(List<Seg> segs)
        {
            List<List<Seg>> groups = new List<List<Seg>>();
            foreach (Seg s in segs)
            {
                if (s.Len < 1) continue;
                List<Seg> g = null;
                foreach (List<Seg> gg in groups)
                    if (gg.Any(x => SameLine(x, s))) { g = gg; break; }
                if (g == null) groups.Add(g = new List<Seg>());
                g.Add(s);
            }
            List<Run> runs = new List<Run>();
            foreach (List<Seg> g in groups)
            {
                List<Seg> sel = g.Where(s => s.Selected).ToList();
                if (sel.Count == 0) continue;
                Seg axis = sel.OrderByDescending(s => s.Len).First();
                double dx, dy;
                Canonical(axis.Dx, axis.Dy, out dx, out dy);
                double ox = axis.X0, oy = axis.Y0;
                List<Cov> all = new List<Cov>();
                foreach (Seg s in g)
                {
                    double t0 = (s.X0 - ox) * dx + (s.Y0 - oy) * dy;
                    double t1 = (s.X1 - ox) * dx + (s.Y1 - oy) * dy;
                    if (t1 < t0) { double tmp = t0; t0 = t1; t1 = tmp; }
                    if (t1 - t0 < 1) continue;
                    all.Add(new Cov { A = t0, B = t1, En = s.En, VisW = s.VisW, Selected = s.Selected });
                }
                List<Cov> selectedCov = all.Where(c => c.Selected).OrderBy(c => c.A).ToList();
                List<List<Cov>> chains = new List<List<Cov>>();
                List<Cov> cur = null;
                double curEnd = 0;
                foreach (Cov c in selectedCov)
                {
                    if (cur == null || c.A > curEnd + ConnectTol)
                    {
                        cur = new List<Cov>();
                        chains.Add(cur);
                        curEnd = c.B;
                    }
                    else curEnd = Math.Max(curEnd, c.B);
                    cur.Add(c);
                }
                foreach (List<Cov> chain in chains)
                {
                    double minT = chain.Min(c => c.A);
                    double maxT = chain.Max(c => c.B);
                    Run run = new Run
                    {
                        Ox = ox + dx * minT,
                        Oy = oy + dy * minT,
                        Dx = dx,
                        Dy = dy,
                        Z = axis.Z,
                        L = maxT - minT
                    };
                    foreach (Cov c in chain)
                        run.Covs.Add(new Cov { A = c.A - minT, B = c.B - minT, En = c.En, VisW = c.VisW, Selected = true });
                    foreach (Cov c in all)
                    {
                        if (c.Selected) continue;
                        double a = c.A - minT, b = c.B - minT;
                        if (!run.Covs.Any(s => Math.Min(s.B, b) - Math.Max(s.A, a) > 1)) continue;
                        run.Covs.Add(new Cov { A = a, B = b, En = c.En, VisW = c.VisW, Selected = false });
                    }
                    if (run.L >= 1) runs.Add(run);
                }
            }
            return runs;
        }

        /// <summary>Forbidden band around a branch: the branch body plus <paramref name="pay"/> past each edge.</summary>
        public static void AddBranches(List<Run> runs, List<Seg> segs, List<FitPt> fits, double pay)
        {
            foreach (Run run in runs)
            {
                foreach (Seg s in segs)
                {
                    if (s.Len < 1 || OnRun(run, s)) continue;
                    double tHit;
                    if (CrossRun(run, s, out tHit) && tHit > EndSnap && tHit < run.L - EndSnap)
                        run.Zones.Add(Zone(tHit, s.VisW / 2, pay));
                    AddEnd(run, s, s.X0, s.Y0, pay);
                    AddEnd(run, s, s.X1, s.Y1, pay);
                }
                if (fits == null) continue;
                foreach (FitPt f in fits)
                {
                    if (!f.Branch) continue;
                    double t, lat;
                    Project(run, f.X, f.Y, out t, out lat);
                    if (t <= EndSnap || t >= run.L - EndSnap) continue;
                    double allow = HalfAt(run, t) + f.Half + Math.Max(pay, 20) + 30;
                    if (lat > allow) continue;
                    run.Zones.Add(Zone(t, f.Half, pay));
                }
            }
        }

        static void AddEnd(Run run, Seg s, double x, double y, double pay)
        {
            double t, lat;
            Project(run, x, y, out t, out lat);
            if (t <= EndSnap || t >= run.L - EndSnap) return;
            if (lat > HalfAt(run, t) + s.VisW / 2 + Math.Max(LatTol, 15)) return;
            run.Zones.Add(Zone(t, s.VisW / 2, pay));
        }

        static double[] Zone(double t, double half, double pay)
        {
            if (half < 0) half = 0;
            if (pay < 0) pay = 0;
            return new[] { t - half - pay, t + half + pay };
        }

        static bool OnRun(Run run, Seg s)
        {
            if (Math.Abs(Cross(run.Dx, run.Dy, s.Dx, s.Dy)) > Math.Sin(AngTol)) return false;
            return LatLine(run.Ox, run.Oy, run.Dx, run.Dy, s.X0, s.Y0) <= LatTol
                && LatLine(run.Ox, run.Oy, run.Dx, run.Dy, s.X1, s.Y1) <= LatTol;
        }

        static bool CrossRun(Run run, Seg s, out double t)
        {
            t = 0;
            double cr = Cross(run.Dx, run.Dy, s.Dx, s.Dy);
            if (Math.Abs(cr) < 1e-8) return false;
            double wx = s.X0 - run.Ox, wy = s.Y0 - run.Oy;
            t = Cross(wx, wy, s.Dx, s.Dy) / cr;
            double u = Cross(wx, wy, run.Dx, run.Dy) / cr;
            return t >= -1 && t <= run.L + 1 && u >= -1 && u <= s.Len + 1;
        }

        static void Project(Run run, double x, double y, out double t, out double lat)
        {
            t = (x - run.Ox) * run.Dx + (y - run.Oy) * run.Dy;
            double px = run.Ox + run.Dx * t, py = run.Oy + run.Dy * t;
            double dx = x - px, dy = y - py;
            lat = Math.Sqrt(dx * dx + dy * dy);
        }

        static double HalfAt(Run run, double t)
        {
            double w = 0; bool any = false;
            foreach (Cov c in run.Covs)
            {
                if (t < c.A - 1 || t > c.B + 1) continue;
                any = true;
                if (c.VisW > w) w = c.VisW;
            }
            if (!any) foreach (Cov c in run.Covs) if (c.VisW > w) w = c.VisW;
            return w / 2;
        }

        /// <summary>Both ends of each allowed interval, then evenly filled so the step is at most <paramref name="spacing"/>.</summary>
        public static List<St> Stations(Run run, double spacing, double pay, double minSep, List<double[]> blocked)
        {
            List<St> res = new List<St>();
            List<double[]> body = MergeSelected(run);
            if (body.Count == 0 || run.L < 1) return res;
            if (pay < 0) pay = 0;
            if (spacing < 1) spacing = run.L;
            double ang = Math.Atan2(run.Dy, run.Dx);
            double outerA = body[0][0], outerB = body[body.Count - 1][1];
            List<double[]> pieces = body;
            pieces = Subtract(pieces, outerA, outerA + pay);
            pieces = Subtract(pieces, outerB - pay, outerB);
            foreach (double[] z in run.Zones) pieces = Subtract(pieces, z[0], z[1]);
            if (blocked != null)
                foreach (double[] blk in blocked)
                    if (blk != null && blk.Length >= 2) pieces = Subtract(pieces, blk[0], blk[1]);
            foreach (double[] p in pieces)
            {
                foreach (double t in Fill(p[0], p[1], spacing, minSep))
                {
                    int kat; double en;
                    if (!CoverAt(run, t, out kat, out en)) continue;
                    res.Add(new St
                    {
                        X = run.Ox + run.Dx * t,
                        Y = run.Oy + run.Dy * t,
                        Z = run.Z,
                        En = en,
                        Kat = kat,
                        Ang = ang
                    });
                }
            }
            return res;
        }

        /// <summary>Single-tray stations, plus one shared konsol where parallel trays sit within <paramref name="yanGap"/> edge to edge.</summary>
        public static List<St> Layout(List<Run> runs, double spacing, double pay, double minSep, double yanGap)
        {
            List<St> res = new List<St>();
            if (runs == null || runs.Count == 0) return res;
            if (yanGap < 0) yanGap = 0;
            int n = runs.Count;
            int[] parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                    if (SideBySide(runs[i], runs[j], yanGap)) Union(parent, i, j);
            List<int>[] comp = new List<int>[n];
            for (int i = 0; i < n; i++)
            {
                int r = Find(parent, i);
                if (comp[r] == null) comp[r] = new List<int>();
                comp[r].Add(i);
            }
            for (int i = 0; i < n; i++)
            {
                if (comp[i] == null) continue;
                if (comp[i].Count == 1) { res.AddRange(Stations(runs[comp[i][0]], spacing, pay, minSep, null)); continue; }
                List<Run> group = comp[i].Select(k => runs[k]).ToList();
                res.AddRange(Shared(group, spacing, pay, minSep, yanGap));
            }
            return res;
        }

        static List<St> Shared(List<Run> group, double spacing, double pay, double minSep, double yanGap)
        {
            List<St> res = new List<St>();
            Run axis = group.OrderByDescending(r => r.L).First();
            double ang = Math.Atan2(axis.Dy, axis.Dx);
            List<Lane> lanes = new List<Lane>();
            foreach (Run r in group)
            {
                Lane lane = new Lane { Run = r, HalfMax = HalfW(r) };
                lane.Scale = axis.Dx * r.Dx + axis.Dy * r.Dy;
                if (Math.Abs(lane.Scale) < 1e-6) lane.Scale = 1;
                lane.T0 = (r.Ox - axis.Ox) * axis.Dx + (r.Oy - axis.Oy) * axis.Dy;
                lane.Lat = Cross(axis.Dx, axis.Dy, r.Ox - axis.Ox, r.Oy - axis.Oy);
                List<double[]> raw = new List<double[]>();
                foreach (Cov c in r.Covs)
                {
                    if (!c.Selected || c.B - c.A <= 1e-6) continue;
                    double a = lane.T0 + c.A * lane.Scale, b = lane.T0 + c.B * lane.Scale;
                    if (b < a) { double tmp = a; a = b; b = tmp; }
                    raw.Add(new[] { a, b });
                }
                raw.Sort((p, q) => p[0].CompareTo(q[0]));
                foreach (double[] p in raw)
                {
                    if (lane.Sel.Count == 0 || p[0] > lane.Sel[lane.Sel.Count - 1][1] + 1e-4) lane.Sel.Add(p);
                    else lane.Sel[lane.Sel.Count - 1][1] = Math.Max(lane.Sel[lane.Sel.Count - 1][1], p[1]);
                }
                if (lane.Sel.Count > 0) lanes.Add(lane);
            }
            if (lanes.Count < 2) { foreach (Run r in group) res.AddRange(Stations(r, spacing, pay, minSep, null)); return res; }

            List<Ev> evs = new List<Ev>();
            for (int i = 0; i < lanes.Count; i++)
                foreach (double[] p in lanes[i].Sel)
                {
                    evs.Add(new Ev { T = p[0], Lane = i, Start = true });
                    evs.Add(new Ev { T = p[1], Lane = i, Start = false });
                }
            evs.Sort((p, q) => { int c = p.T.CompareTo(q.T); if (c != 0) return c; return p.Start ? 1 : -1; });
            bool[] on = new bool[lanes.Count];
            List<double[]> portions = new List<double[]>();
            List<double[]>[] blocked = new List<double[]>[lanes.Count];
            for (int i = 0; i < lanes.Count; i++) blocked[i] = new List<double[]>();
            double prev = evs.Count > 0 ? evs[0].T : 0;
            int ei = 0;
            while (ei < evs.Count)
            {
                double t = evs[ei].T;
                if (t > prev + 1e-3) NoteSpan(lanes, on, prev, t, yanGap, portions, blocked);
                while (ei < evs.Count && Math.Abs(evs[ei].T - t) <= 1e-4)
                {
                    on[evs[ei].Lane] = evs[ei].Start;
                    ei++;
                }
                prev = t;
            }
            portions.Sort((p, q) => p[0].CompareTo(q[0]));
            List<double[]> merged = new List<double[]>();
            foreach (double[] p in portions)
            {
                if (merged.Count == 0 || p[0] > merged[merged.Count - 1][1] + 1) merged.Add(new[] { p[0], p[1] });
                else merged[merged.Count - 1][1] = Math.Max(merged[merged.Count - 1][1], p[1]);
            }
            foreach (double[] por in merged)
            {
                if (por[1] - por[0] <= 1) continue;
                List<double[]> pieces = new List<double[]> { new[] { por[0] + pay, por[1] - pay } };
                pieces = pieces.Where(p => p[1] - p[0] > 1e-6).ToList();
                foreach (Lane lane in lanes)
                    foreach (double[] z in lane.Run.Zones)
                    {
                        double z0 = lane.T0 + z[0] * lane.Scale, z1 = lane.T0 + z[1] * lane.Scale;
                        pieces = Subtract(pieces, z0, z1);
                    }
                foreach (double[] piece in pieces)
                    foreach (double t in Fill(piece[0], piece[1], spacing, minSep))
                        foreach (List<int> cluster in Clusters(lanes, onAt(lanes, t), t, yanGap))
                        {
                            if (cluster.Count < 2) continue;
                            double pmin = double.MaxValue, pmax = double.MinValue;
                            int kat = 1; double zSum = 0;
                            foreach (int li in cluster)
                            {
                                Lane lane = lanes[li];
                                double w = WidthAt(lane, t);
                                double lat = lane.Lat;
                                if (lat - w / 2 < pmin) pmin = lat - w / 2;
                                if (lat + w / 2 > pmax) pmax = lat + w / 2;
                                if (StackAt(lane, t) >= 2) kat = 2;
                                zSum += lane.Run.Z;
                            }
                            double outer = pmax - pmin;
                            if (outer < 1) continue;
                            double mid = (pmin + pmax) / 2;
                            res.Add(new St
                            {
                                X = axis.Ox + axis.Dx * t + (-axis.Dy) * mid,
                                Y = axis.Oy + axis.Dy * t + axis.Dx * mid,
                                Z = zSum / cluster.Count,
                                En = outer,
                                Kat = kat,
                                Ang = ang,
                                Span = true
                            });
                        }
            }
            for (int i = 0; i < lanes.Count; i++)
            {
                List<double[]> local = new List<double[]>();
                foreach (double[] b in blocked[i])
                {
                    double a = (b[0] - lanes[i].T0) / lanes[i].Scale, c = (b[1] - lanes[i].T0) / lanes[i].Scale;
                    if (c < a) { double tmp = a; a = c; c = tmp; }
                    local.Add(new[] { a, c });
                }
                res.AddRange(Stations(lanes[i].Run, spacing, pay, minSep, local));
            }
            return res;
        }

        class Ev { public double T; public int Lane; public bool Start; }

        static void NoteSpan(List<Lane> lanes, bool[] on, double a, double b, double yanGap, List<double[]> portions, List<double[]>[] blocked)
        {
            List<int> active = new List<int>();
            for (int i = 0; i < on.Length; i++) if (on[i]) active.Add(i);
            foreach (List<int> cluster in Clusters(lanes, active, (a + b) / 2, yanGap))
            {
                if (cluster.Count < 2) continue;
                portions.Add(new[] { a, b });
                foreach (int li in cluster) blocked[li].Add(new[] { a, b });
            }
        }

        static List<int> onAt(List<Lane> lanes, double t)
        {
            List<int> active = new List<int>();
            for (int i = 0; i < lanes.Count; i++)
                foreach (double[] p in lanes[i].Sel)
                    if (t >= p[0] - 1e-6 && t <= p[1] + 1e-6) { active.Add(i); break; }
            return active;
        }

        static List<List<int>> Clusters(List<Lane> lanes, List<int> active, double t, double yanGap)
        {
            List<List<int>> res = new List<List<int>>();
            bool[] seen = new bool[lanes.Count];
            foreach (int seed in active)
            {
                if (seen[seed]) continue;
                List<int> cluster = new List<int>();
                List<int> stack = new List<int> { seed };
                seen[seed] = true;
                while (stack.Count > 0)
                {
                    int cur = stack[stack.Count - 1];
                    stack.RemoveAt(stack.Count - 1);
                    cluster.Add(cur);
                    foreach (int oth in active)
                    {
                        if (seen[oth] || oth == cur) continue;
                        if (!Close(lanes[cur], lanes[oth], t, yanGap)) continue;
                        seen[oth] = true;
                        stack.Add(oth);
                    }
                }
                res.Add(cluster);
            }
            return res;
        }

        static bool Close(Lane a, Lane b, double t, double yanGap)
        {
            double lat = Math.Abs(a.Lat - b.Lat);
            if (lat <= LatTol) return false;
            double gap = lat - WidthAt(a, t) / 2 - WidthAt(b, t) / 2;
            return gap <= yanGap;
        }

        static double WidthAt(Lane lane, double axisT)
        {
            double local = (axisT - lane.T0) / lane.Scale;
            double w = 0;
            foreach (Cov c in lane.Run.Covs)
            {
                if (!c.Selected || local < c.A - 0.5 || local > c.B + 0.5) continue;
                if (c.VisW > w) w = c.VisW;
            }
            if (w <= 0) w = lane.HalfMax * 2;
            return w;
        }

        static int StackAt(Lane lane, double axisT)
        {
            double local = (axisT - lane.T0) / lane.Scale;
            int n = 0, sel = 0;
            foreach (Cov c in lane.Run.Covs)
            {
                if (local < c.A - 0.5 || local > c.B + 0.5) continue;
                n++;
                if (c.Selected) sel++;
            }
            return sel == 0 ? 0 : n;
        }

        static double HalfW(Run r)
        {
            double w = 0;
            foreach (Cov c in r.Covs) if (c.Selected && c.VisW > w) w = c.VisW;
            return w / 2;
        }

        static bool SideBySide(Run a, Run b, double yanGap)
        {
            double cr = Math.Abs(Cross(a.Dx, a.Dy, b.Dx, b.Dy));
            double dot = a.Dx * b.Dx + a.Dy * b.Dy;
            if (cr > Math.Sin(AngTol) || dot < 0.5) return false;
            double lat0 = Math.Abs(Cross(a.Dx, a.Dy, b.Ox - a.Ox, b.Oy - a.Oy));
            double lat1 = Math.Abs(Cross(a.Dx, a.Dy, b.Ox + b.Dx * b.L - a.Ox, b.Oy + b.Dy * b.L - a.Oy));
            double lat = (lat0 + lat1) / 2;
            if (lat <= LatTol) return false;
            if (lat - HalfW(a) - HalfW(b) > yanGap) return false;
            double sa0, sa1, sb0, sb1;
            SelSpan(a, out sa0, out sa1);
            SelSpan(b, out sb0, out sb1);
            double tb0 = (b.Ox + b.Dx * sb0 - a.Ox) * a.Dx + (b.Oy + b.Dy * sb0 - a.Oy) * a.Dy;
            double tb1 = (b.Ox + b.Dx * sb1 - a.Ox) * a.Dx + (b.Oy + b.Dy * sb1 - a.Oy) * a.Dy;
            double lo = Math.Max(sa0, Math.Min(tb0, tb1));
            double hi = Math.Min(sa1, Math.Max(tb0, tb1));
            return hi - lo > 1;
        }

        static void SelSpan(Run r, out double a, out double b)
        {
            a = double.MaxValue; b = double.MinValue;
            foreach (Cov c in r.Covs)
                if (c.Selected) { if (c.A < a) a = c.A; if (c.B > b) b = c.B; }
            if (a > b) { a = 0; b = r.L; }
        }

        static int Find(int[] parent, int i)
        {
            while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
            return i;
        }

        static void Union(int[] parent, int a, int b)
        {
            a = Find(parent, a); b = Find(parent, b);
            if (a != b) parent[a] = b;
        }

        static List<double[]> MergeSelected(Run run)
        {
            List<double[]> m = new List<double[]>();
            foreach (Cov c in run.Covs.Where(c => c.Selected && c.B - c.A > 1e-6).OrderBy(c => c.A))
            {
                if (m.Count == 0 || c.A > m[m.Count - 1][1] + 1e-4) m.Add(new[] { c.A, c.B });
                else m[m.Count - 1][1] = Math.Max(m[m.Count - 1][1], c.B);
            }
            return m;
        }

        static List<double[]> Subtract(List<double[]> src, double a, double b)
        {
            if (b < a) { double t = a; a = b; b = t; }
            List<double[]> next = new List<double[]>();
            foreach (double[] p in src)
            {
                if (b <= p[0] + 1e-9 || a >= p[1] - 1e-9) { next.Add(p); continue; }
                if (a > p[0] + 1e-9) next.Add(new[] { p[0], a });
                if (b < p[1] - 1e-9) next.Add(new[] { b, p[1] });
            }
            return next.Where(p => p[1] - p[0] > 1e-6).ToList();
        }

        static List<double> Fill(double a, double b, double spacing, double minSep)
        {
            List<double> r = new List<double>();
            double len = b - a;
            if (len <= Math.Max(0, minSep)) { r.Add((a + b) / 2); return r; }
            int gaps = (int)Math.Ceiling(len / spacing - 1e-9);
            if (gaps < 1) gaps = 1;
            double step = len / gaps;
            for (int i = 0; i <= gaps; i++) r.Add(a + step * i);
            return r;
        }

        static bool CoverAt(Run run, double t, out int kat, out double en)
        {
            kat = 0; en = 0; int sel = 0;
            foreach (Cov c in run.Covs)
            {
                if (t < c.A - 0.5 || t > c.B + 0.5) continue;
                kat++;
                if (c.Selected) sel++;
                if (c.En > en) en = c.En;
            }
            if (sel == 0) { kat = 0; return false; }
            return true;
        }
    }

    /// <summary>KONSOL / AIDKONSOL: places konsol blocks along cabletray runs (single undo).</summary>
    public class KonsolCommands
    {
        const double NearDist = 500;
        static readonly double[] DistList = { 17.5, 27.5, 37.5, 47.5, 57.5, 67.5, 77.5, 87.5, 97.5, 107.5 };

        [CommandMethod("KONSOL", CommandFlags.Modal | CommandFlags.NoBlockEditor)]
        public void Run() { Do(); }

        [CommandMethod("AIDKONSOL", CommandFlags.Modal | CommandFlags.NoBlockEditor)]
        public void RunAlias() { Do(); }

        static string F(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture).Replace('.', ','); }

        static void Do()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            Database db = doc.Database;
            try
            {
                FitConfig cfg = LoadCfg();
                KonsolSettings ks = KonsolSettings.Load();

                ObjectId sampleId = ObjectId.Null;
                PromptEntityOptions eo = new PromptEntityOptions("\nÖrnek konsol bloğunu seçin: ");
                eo.SetRejectMessage("\nBlok seçin. ");
                eo.AddAllowedClass(typeof(BlockReference), false);
                while (sampleId.IsNull)
                {
                    PromptEntityResult er = ed.GetEntity(eo);
                    if (er.Status != PromptStatus.OK) return;
                    string pickedName = "?";
                    using (Transaction tx = db.TransactionManager.StartOpenCloseTransaction())
                    {
                        BlockReference br = tx.GetObject(er.ObjectId, OpenMode.ForRead) as BlockReference;
                        if (br != null) pickedName = Fittings.EffName(tx, br);
                        tx.Commit();
                    }
                    if (!string.Equals(pickedName, "konsol", StringComparison.OrdinalIgnoreCase))
                    {
                        ed.WriteMessage("\nKonsol bloğu seçin (seçilen: " + pickedName + "). ");
                        continue;
                    }
                    sampleId = er.ObjectId;
                }

                PromptSelectionResult sr = ed.GetSelection(
                    new PromptSelectionOptions { MessageForAdding = "\nKablo tavalarını pencere ile seçin: " },
                    new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "INSERT") }));
                if (sr.Status != PromptStatus.OK || sr.Value == null) return;
                ObjectId[] selIds = sr.Value.GetObjectIds();

                int trayCount = 0;
                using (Transaction tx = db.TransactionManager.StartOpenCloseTransaction())
                {
                    foreach (ObjectId id in selIds)
                    {
                        BlockReference br = tx.GetObject(id, OpenMode.ForRead) as BlockReference;
                        if (br != null && string.Equals(Fittings.EffName(tx, br), cfg.straight_block, StringComparison.OrdinalIgnoreCase)) trayCount++;
                    }
                    tx.Commit();
                }
                if (trayCount == 0)
                {
                    ed.WriteMessage("\nSeçimde kablo tavası ('" + cfg.straight_block + "') yok.\n");
                    return;
                }

                while (true)
                {
                    string msg = "\nAralık=" + F(ks.aralik) + "  Uç/dal payı=" + F(ks.pay) + "  EN payı=" + F(ks.ek) + "  Yan yana=" + F(ks.yan) +
                                 "\n[Aralik/Pay/Ek/Yan/Devam] <Devam>: ";
                    PromptKeywordOptions ko = new PromptKeywordOptions(msg, "Aralik Pay Ek Yan Devam") { AllowNone = true };
                    PromptResult kr = ed.GetKeywords(ko);
                    if (kr.Status == PromptStatus.Cancel) return;
                    if (kr.Status == PromptStatus.None || kr.StringResult == "Devam") break;
                    if (kr.StringResult == "Aralik")
                    {
                        PromptDoubleResult r = ed.GetDouble(new PromptDoubleOptions("\nKonsol aralığı: ") { AllowNone = true, AllowNegative = false, AllowZero = false, DefaultValue = ks.aralik, UseDefaultValue = true });
                        if (r.Status == PromptStatus.Cancel) return;
                        if (r.Status == PromptStatus.OK && r.Value >= 1) ks.aralik = r.Value;
                    }
                    else if (kr.StringResult == "Pay")
                    {
                        PromptDoubleResult r = ed.GetDouble(new PromptDoubleOptions("\nUç ve dal payı (konsol bu mesafeden yakına gelmez): ") { AllowNone = true, AllowNegative = false, AllowZero = true, DefaultValue = ks.pay, UseDefaultValue = true });
                        if (r.Status == PromptStatus.Cancel) return;
                        if (r.Status == PromptStatus.OK && r.Value >= 0) ks.pay = r.Value;
                    }
                    else if (kr.StringResult == "Ek")
                    {
                        PromptDoubleResult r = ed.GetDouble(new PromptDoubleOptions("\nKonsol boyu = tava EN + bu pay (Dist1 listesine yuvarlanır): ") { AllowNone = true, AllowNegative = false, AllowZero = true, DefaultValue = ks.ek, UseDefaultValue = true });
                        if (r.Status == PromptStatus.Cancel) return;
                        if (r.Status == PromptStatus.OK && r.Value >= 0) ks.ek = r.Value;
                    }
                    else if (kr.StringResult == "Yan")
                    {
                        PromptDoubleResult r = ed.GetDouble(new PromptDoubleOptions("\nYan yana boşluk (kenardan kenara; bu kadar ve daha yakınsa tek konsol): ") { AllowNone = true, AllowNegative = false, AllowZero = true, DefaultValue = ks.yan, UseDefaultValue = true });
                        if (r.Status == PromptStatus.Cancel) return;
                        if (r.Status == PromptStatus.OK && r.Value >= 0) ks.yan = r.Value;
                    }
                }
                ks.Save();

                int made = 0, single = 0, doub = 0, skipped = 0, trayN = 0, runN = 0, miss = 0, distFail = 0, over = 0;
                bool katMissing = false;
                List<string> notes = new List<string>();
                using (doc.LockDocument())
                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    BlockReference sample = tx.GetObject(sampleId, OpenMode.ForRead) as BlockReference;
                    if (sample == null) throw new InvalidOperationException("Örnek konsol okunamadı.");
                    LayerTableRecord lay = tx.GetObject(sample.LayerId, OpenMode.ForRead) as LayerTableRecord;
                    if (lay != null && lay.IsLocked) throw new InvalidOperationException("Örnek konsolun katmanı kilitli (" + sample.Layer + ").");
                    if (lay != null && lay.IsFrozen) throw new InvalidOperationException("Örnek konsolun katmanı donuk (" + sample.Layer + ").");

                    string distName = DistName(sample);
                    double sampleDist = PropNum(sample, distName);
                    List<object> allowed = AllowedOf(sample, distName);
                    Extents3d? sampleExt = BlockAlong.DefExtents(tx, sample.BlockTableRecord);
                    double sampleAng = SampleAxisAng(sample, sampleExt);
                    ObjectId defId = sample.IsDynamicBlock ? sample.DynamicBlockTableRecord : sample.BlockTableRecord;
                    string konsolName = Fittings.EffName(tx, sample);

                    Dictionary<ObjectId, List<ObjectId>> bySpace = new Dictionary<ObjectId, List<ObjectId>>();
                    foreach (ObjectId id in selIds)
                    {
                        if (!id.IsValid || id.IsErased) continue;
                        BlockReference br = tx.GetObject(id, OpenMode.ForRead) as BlockReference;
                        if (br == null) continue;
                        List<ObjectId> list;
                        if (!bySpace.TryGetValue(br.OwnerId, out list)) bySpace[br.OwnerId] = list = new List<ObjectId>();
                        list.Add(id);
                    }

                    foreach (KeyValuePair<ObjectId, List<ObjectId>> kv in bySpace)
                    {
                        BlockTableRecord space = tx.GetObject(kv.Key, OpenMode.ForRead) as BlockTableRecord;
                        if (space == null) continue;
                        HashSet<ObjectId> picked = new HashSet<ObjectId>(kv.Value);
                        List<KonsolGeo.Seg> segs = new List<KonsolGeo.Seg>();
                        List<KonsolGeo.FitPt> fits = new List<KonsolGeo.FitPt>();
                        foreach (ObjectId id in picked)
                        {
                            BlockReference br = tx.GetObject(id, OpenMode.ForRead) as BlockReference;
                            if (br == null) continue;
                            string name = Fittings.EffName(tx, br);
                            if (string.Equals(name, cfg.straight_block, StringComparison.OrdinalIgnoreCase))
                            {
                                KonsolGeo.Seg seg;
                                if (TrySeg(tx, br, cfg, true, out seg)) { segs.Add(seg); trayN++; }
                                else miss++;
                            }
                            else if (string.Equals(name, cfg.fitting_block, StringComparison.OrdinalIgnoreCase))
                                fits.Add(FitOf(tx, br, cfg));
                        }
                        if (!segs.Any(s => s.Selected)) continue;

                        double minx = double.MaxValue, miny = double.MaxValue, maxx = double.MinValue, maxy = double.MinValue;
                        foreach (KonsolGeo.Seg s in segs)
                        {
                            minx = Math.Min(minx, Math.Min(s.X0, s.X1)); maxx = Math.Max(maxx, Math.Max(s.X0, s.X1));
                            miny = Math.Min(miny, Math.Min(s.Y0, s.Y1)); maxy = Math.Max(maxy, Math.Max(s.Y0, s.Y1));
                        }
                        minx -= NearDist; miny -= NearDist; maxx += NearDist; maxy += NearDist;
                        foreach (ObjectId id in space)
                        {
                            if (picked.Contains(id) || !id.IsValid || id.IsErased) continue;
                            BlockReference br = tx.GetObject(id, OpenMode.ForRead) as BlockReference;
                            if (br == null) continue;
                            string name = Fittings.EffName(tx, br);
                            if (string.Equals(name, cfg.straight_block, StringComparison.OrdinalIgnoreCase))
                            {
                                KonsolGeo.Seg seg;
                                if (!TrySeg(tx, br, cfg, false, out seg)) continue;
                                if (!BoxHit(seg, minx, miny, maxx, maxy)) continue;
                                segs.Add(seg);
                            }
                            else if (string.Equals(name, cfg.fitting_block, StringComparison.OrdinalIgnoreCase))
                            {
                                if (br.Position.X < minx || br.Position.X > maxx || br.Position.Y < miny || br.Position.Y > maxy) continue;
                                fits.Add(FitOf(tx, br, cfg));
                            }
                        }

                        Point3d samplePt = AnchorOf(tx, sample);
                        int sign = SignOf(sampleAng, samplePt, segs);
                        List<KonsolGeo.Run> runs = KonsolGeo.Build(segs);
                        KonsolGeo.AddBranches(runs, segs, fits, ks.pay);
                        runN += runs.Count;

                        List<Point2d> occ = new List<Point2d>();
                        foreach (ObjectId id in space)
                        {
                            BlockReference br = tx.GetObject(id, OpenMode.ForRead) as BlockReference;
                            if (br == null || !string.Equals(Fittings.EffName(tx, br), konsolName, StringComparison.OrdinalIgnoreCase)) continue;
                            Point3d a = AnchorOf(tx, br);
                            occ.Add(new Point2d(a.X, a.Y));
                        }
                        if (!space.IsWriteEnabled) space.UpgradeOpen();

                        foreach (KonsolGeo.St st in KonsolGeo.Layout(runs, ks.aralik, ks.pay, ks.atla, ks.yan))
                        {
                            double want = st.Ang + sign * Math.PI / 2.0;
                            double rot = Wrap(sample.Rotation + (want - sampleAng));
                            {
                                if (NearAny(occ, st.X, st.Y, ks.atla)) { skipped++; continue; }
                                double target = st.En + ks.ek;
                                Point3d at = new Point3d(st.X, st.Y, st.Z);
                                BlockReference b = new BlockReference(at, defId);
                                b.SetDatabaseDefaults(db);
                                space.AppendEntity(b);
                                tx.AddNewlyCreatedDBObject(b, true);
                                b.SetPropertiesFrom(sample);
                                b.Normal = sample.Normal;
                                b.ScaleFactors = sample.ScaleFactors;
                                b.Rotation = rot;
                                b.Position = at;
                                CopyDyn(sample, b, distName);
                                bool clamped;
                                bool distOk = SetDist(b, distName, target, allowed, st.Span, out clamped);
                                double distNow = PropNum(b, distName);
                                ShiftAnchor(tx, b, at, sampleDist, target);
                                double distAfter = PropNum(b, distName);
                                if (!distOk || double.IsNaN(distAfter) || (!double.IsNaN(distNow) && Math.Abs(distAfter - distNow) > 0.05))
                                {
                                    bool again;
                                    distOk = SetDist(b, distName, target, allowed, st.Span, out again);
                                    if (again) clamped = true;
                                }
                                if (!distOk) distFail++;
                                if (clamped) over++;
                                AddAttribs(tx, b);
                                if (!CopyAtts(tx, sample, b, st.Kat)) katMissing = true;
                                occ.Add(new Point2d(st.X, st.Y));
                                Point3d placed = AnchorOf(tx, b);
                                if (placed.DistanceTo(at) <= Math.Max(ks.atla, 1)) occ.Add(new Point2d(placed.X, placed.Y));
                                made++;
                                if (st.Kat >= 2) doub++; else single++;
                            }
                        }
                    }
                    if (made > 0) tx.Commit();
                }

                if (distFail > 0) notes.Add(distFail + " konsolda Dist1 yazılamadı.");
                if (over > 0) notes.Add(over + " konsolda Dist1 listenin en büyüğüne kısıldı (gerekenden kısa).");
                if (katMissing) notes.Add("KAT attribute bulunamadı.");
                string sum = made == 0
                    ? "Konsol yerleştirilmedi (" + trayN + " tava, " + runN + " hat)."
                    : "Konsol: " + made + " adet (tek kat " + single + ", çift kat " + doub + "), " + trayN + " tava, " + runN + " hat.";
                if (skipped > 0) sum += " Atlanan (yakınında konsol var): " + skipped + ".";
                if (miss > 0) sum += " Ölçüsü okunamayan tava: " + miss + ".";
                sum += " Aralık " + F(ks.aralik) + ", pay " + F(ks.pay) + ", EN+" + F(ks.ek) + ", yan yana " + F(ks.yan) + ".";
                if (notes.Count > 0) sum += " Not: " + string.Join(" ", notes.Distinct().Take(4));
                if (made > 0) sum += " Geri almak için UNDO.";
                ed.WriteMessage("\n" + sum + "\n");
                try { Metraj.Raise(sum); } catch (System.Exception) { }
                try { AppLog.Write("KONSOL [" + Path.GetFileName(doc.Name) + "] " + sum); } catch (System.Exception) { }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nKonsol hatası: " + ex.Message + "\n");
                try { Metraj.Raise("Konsol hatası: " + ex.Message); } catch (System.Exception) { }
            }
        }

        static FitConfig LoadCfg()
        {
            try { return FitConfig.Load(); }
            catch (System.Exception) { return new FitConfig(); }
        }

        static bool BoxHit(KonsolGeo.Seg s, double minx, double miny, double maxx, double maxy)
        {
            double a0 = Math.Min(s.X0, s.X1), a1 = Math.Max(s.X0, s.X1);
            double b0 = Math.Min(s.Y0, s.Y1), b1 = Math.Max(s.Y0, s.Y1);
            return a1 >= minx && a0 <= maxx && b1 >= miny && b0 <= maxy;
        }

        static bool NearAny(List<Point2d> pts, double x, double y, double tol)
        {
            double t2 = tol * tol;
            foreach (Point2d p in pts)
            {
                double dx = p.X - x, dy = p.Y - y;
                if (dx * dx + dy * dy <= t2) return true;
            }
            return false;
        }

        /// <summary>Centerline from the block's local extents transformed by BlockTransform. BOY/EN are only a check and the width used for Dist1.</summary>
        static bool TrySeg(Transaction tx, BlockReference br, FitConfig cfg, bool selected, out KonsolGeo.Seg seg)
        {
            seg = null;
            double boy = PropNum(br, cfg.length_param);
            double en = PropNum(br, cfg.width_param);
            if (double.IsNaN(boy) || boy <= 1) return false;
            if (double.IsNaN(en) || en <= 0) return false;
            Extents3d? ext = null;
            try { ext = BlockAlong.DefExtents(tx, br.BlockTableRecord); } catch (System.Exception) { }
            double x0 = 0, x1 = boy, yMid = en / 2.0;
            bool yFromExt = false;
            if (ext != null)
            {
                double minX = ext.Value.MinPoint.X, maxX = ext.Value.MaxPoint.X;
                double minY = ext.Value.MinPoint.Y, maxY = ext.Value.MaxPoint.Y;
                double spanX = maxX - minX, spanY = maxY - minY;
                if (UnitMatch(spanX, boy)) { x0 = minX; x1 = maxX; }
                if (spanY > 1e-6 && UnitMatch(spanY, en)) { yMid = (minY + maxY) / 2.0; yFromExt = true; }
            }
            Point3d p0 = new Point3d(x0, yMid, 0).TransformBy(br.BlockTransform);
            Point3d p1 = new Point3d(x1, yMid, 0).TransformBy(br.BlockTransform);
            seg = new KonsolGeo.Seg
            {
                X0 = p0.X, Y0 = p0.Y, X1 = p1.X, Y1 = p1.Y, Z = (p0.Z + p1.Z) / 2.0,
                En = en, Selected = selected,
                VisW = yFromExt && ext != null ? (ext.Value.MaxPoint.Y - ext.Value.MinPoint.Y) * Math.Abs(br.ScaleFactors.Y) : en * Math.Abs(br.ScaleFactors.Y)
            };
            if (seg.VisW < 1e-6) seg.VisW = en;
            KonsolGeo.SetDir(seg);
            return seg.Len >= 1;
        }

        static KonsolGeo.FitPt FitOf(Transaction tx, BlockReference br, FitConfig cfg)
        {
            string vis = VisOf(br, cfg == null ? "Visibility1" : cfg.visibility_param);
            string key = (vis ?? "").Trim().ToUpperInvariant();
            KonsolGeo.FitPt f = new KonsolGeo.FitPt { X = br.Position.X, Y = br.Position.Y, Half = FitHalf(br), Branch = !(key.StartsWith("YD") && !key.StartsWith("YT")) };
            try
            {
                Extents3d? e = BlockAlong.DefExtents(tx, br.BlockTableRecord);
                if (e != null)
                {
                    double cx = (e.Value.MinPoint.X + e.Value.MaxPoint.X) / 2.0;
                    double cy = (e.Value.MinPoint.Y + e.Value.MaxPoint.Y) / 2.0;
                    Point3d c = new Point3d(cx, cy, 0).TransformBy(br.BlockTransform);
                    f.X = c.X; f.Y = c.Y;
                }
            }
            catch (System.Exception) { }
            return f;
        }

        static string VisOf(BlockReference br, string name)
        {
            if (br == null || !br.IsDynamicBlock || string.IsNullOrEmpty(name)) return "";
            foreach (DynamicBlockReferenceProperty p in br.DynamicBlockReferencePropertyCollection)
                if (string.Equals(p.PropertyName, name, StringComparison.OrdinalIgnoreCase) && p.Value != null)
                    return Convert.ToString(p.Value, CultureInfo.InvariantCulture);
            return "";
        }

        static double FitHalf(BlockReference br)
        {
            if (!br.IsDynamicBlock) return 0;
            double m = 0;
            foreach (DynamicBlockReferenceProperty p in br.DynamicBlockReferencePropertyCollection)
            {
                if (p.PropertyName == "Origin") continue;
                string n = (p.PropertyName ?? "").ToUpperInvariant();
                if (n.Contains("BOY") || n.Contains("YUK") || n.Contains("VIS") || n.Contains("FLIP")) continue;
                double d;
                if (!TryD(p.Value, out d) || d < 5 || d > 160) continue;
                if (d > m) m = d;
            }
            return m / 2.0;
        }

        static int SignOf(double sampleAng, Point3d samplePt, List<KonsolGeo.Seg> segs)
        {
            double best = double.MaxValue;
            KonsolGeo.Seg near = null;
            foreach (KonsolGeo.Seg s in segs)
            {
                if (!s.Selected || s.Len < 1) continue;
                double d = KonsolGeo.SegDist(s, samplePt.X, samplePt.Y);
                if (d < best) { best = d; near = s; }
            }
            if (near == null || best > Math.Max(80, near.VisW + 40)) return 1;
            double cdx, cdy;
            KonsolGeo.Canonical(near.Dx, near.Dy, out cdx, out cdy);
            double delta = NormAng(sampleAng - Math.Atan2(cdy, cdx));
            double dp = Math.Abs(NormAng(delta - Math.PI / 2));
            double dm = Math.Abs(NormAng(delta + Math.PI / 2));
            return dp <= dm ? 1 : -1;
        }

        static double SampleAxisAng(BlockReference br, Extents3d? ext)
        {
            double lx, ly;
            AxisUnit(ext, out lx, out ly);
            Point3d o = Point3d.Origin.TransformBy(br.BlockTransform);
            Point3d a = new Point3d(lx, ly, 0).TransformBy(br.BlockTransform);
            Vector3d d = a - o;
            if (d.Length < 1e-9) return br.Rotation;
            return Math.Atan2(d.Y, d.X);
        }

        static void AxisUnit(Extents3d? ext, out double lx, out double ly)
        {
            lx = 1; ly = 0;
            if (ext == null) return;
            double spanX = ext.Value.MaxPoint.X - ext.Value.MinPoint.X;
            double spanY = ext.Value.MaxPoint.Y - ext.Value.MinPoint.Y;
            if (spanY > spanX) { lx = 0; ly = 1; }
        }

        static string DistName(BlockReference br)
        {
            if (br == null || !br.IsDynamicBlock) return "Dist1";
            foreach (DynamicBlockReferenceProperty p in br.DynamicBlockReferencePropertyCollection)
                if (!p.ReadOnly && string.Equals(p.PropertyName, "Dist1", StringComparison.OrdinalIgnoreCase)) return p.PropertyName;
            foreach (DynamicBlockReferenceProperty p in br.DynamicBlockReferencePropertyCollection)
            {
                if (p.ReadOnly || p.UnitsType != DynamicBlockReferencePropertyUnitsType.Distance) continue;
                try { object[] v = p.GetAllowedValues(); if (v != null && v.Length >= 3) return p.PropertyName; }
                catch (System.Exception) { }
            }
            return "Dist1";
        }

        static List<object> AllowedOf(BlockReference br, string name)
        {
            List<object> list = new List<object>();
            DynamicBlockReferenceProperty p = Fittings.Prop(br, name);
            if (p == null) return list;
            try { object[] v = p.GetAllowedValues(); if (v != null) foreach (object o in v) if (o != null) list.Add(o); }
            catch (System.Exception) { }
            return list;
        }

        /// <summary>Smallest allowed value that is still at least <paramref name="want"/>. Above the list max, the max is used and <paramref name="clamped"/> is set.</summary>
        static object SnapCeil(List<object> allowed, double want, out bool clamped)
        {
            clamped = false;
            bool list = allowed != null && allowed.Count >= 3;
            List<double> vals = new List<double>();
            List<object> objs = new List<object>();
            IEnumerable<object> src = list ? (IEnumerable<object>)allowed : DistList.Cast<object>();
            foreach (object o in src)
            {
                double d;
                if (!TryD(o, out d)) continue;
                vals.Add(d);
                objs.Add(list ? o : (object)d);
            }
            if (vals.Count == 0) return want;
            int ceil = -1;
            for (int i = 0; i < vals.Count; i++)
            {
                if (vals[i] + 1e-6 < want) continue;
                if (ceil < 0 || vals[i] < vals[ceil]) ceil = i;
            }
            if (ceil >= 0) return objs[ceil];
            int im = 0;
            for (int i = 1; i < vals.Count; i++) if (vals[i] > vals[im]) im = i;
            clamped = true;
            return objs[im];
        }

        static object SnapValue(List<object> allowed, double want)
        {
            bool list = allowed != null && allowed.Count >= 3;
            object best = null;
            double bestD = double.MaxValue, bestV = double.MinValue;
            bool any = false;
            IEnumerable<object> src = list ? (IEnumerable<object>)allowed : DistList.Cast<object>();
            foreach (object o in src)
            {
                double d;
                if (!TryD(o, out d)) continue;
                any = true;
                double diff = Math.Abs(d - want);
                if (diff < bestD - 1e-9 || (Math.Abs(diff - bestD) <= 1e-9 && d > bestV)) { bestD = diff; bestV = d; best = o; }
            }
            if (!any) return want;
            if (!list && bestD > 5.01) return want;
            return best;
        }

        static bool SetDist(BlockReference br, string name, double want, List<object> allowed, bool ceil, out bool clamped)
        {
            clamped = false;
            DynamicBlockReferenceProperty p = Fittings.Prop(br, name);
            if (p == null) return false;
            object snap = ceil ? SnapCeil(allowed, want, out clamped) : SnapValue(allowed, want);
            try { p.Value = snap; return true; }
            catch (System.Exception)
            {
                try
                {
                    double d;
                    if (!TryD(snap, out d)) return false;
                    p.Value = d;
                    return true;
                }
                catch (System.Exception) { return false; }
            }
        }

        static void CopyDyn(BlockReference src, BlockReference dst, string distName)
        {
            if (src == null || dst == null || !src.IsDynamicBlock || !dst.IsDynamicBlock) return;
            List<DynamicBlockReferenceProperty> srcProps = new List<DynamicBlockReferenceProperty>();
            foreach (DynamicBlockReferenceProperty p in src.DynamicBlockReferencePropertyCollection)
            {
                if (p.ReadOnly || p.PropertyName == "Origin") continue;
                if (string.Equals(p.PropertyName, distName, StringComparison.OrdinalIgnoreCase)) continue;
                srcProps.Add(p);
            }
            srcProps.Sort((a, b) => (a.UnitsType == DynamicBlockReferencePropertyUnitsType.Distance ? 1 : 0) - (b.UnitsType == DynamicBlockReferencePropertyUnitsType.Distance ? 1 : 0));
            Dictionary<string, DynamicBlockReferenceProperty> map = new Dictionary<string, DynamicBlockReferenceProperty>(StringComparer.OrdinalIgnoreCase);
            foreach (DynamicBlockReferenceProperty p in dst.DynamicBlockReferencePropertyCollection)
                if (!p.ReadOnly && !map.ContainsKey(p.PropertyName)) map[p.PropertyName] = p;
            foreach (DynamicBlockReferenceProperty sp in srcProps)
            {
                DynamicBlockReferenceProperty dp;
                if (!map.TryGetValue(sp.PropertyName, out dp) || Equals(dp.Value, sp.Value)) continue;
                try { dp.Value = sp.Value; } catch (System.Exception) { }
            }
        }

        static void ShiftAnchor(Transaction tx, BlockReference b, Point3d desired, double sampleDist, double targetDist)
        {
            try { b.RecordGraphicsModified(true); } catch (System.Exception) { }
            Extents3d? ext = null;
            try { ext = BlockAlong.DefExtents(tx, b.BlockTableRecord); } catch (System.Exception) { }
            if (ext == null) return;
            double span = Math.Max(ext.Value.MaxPoint.X - ext.Value.MinPoint.X, ext.Value.MaxPoint.Y - ext.Value.MinPoint.Y);
            double ratio = 1;
            if (targetDist > 1e-6 && !UnitMatch(span, targetDist))
            {
                if (sampleDist > 1e-6 && UnitMatch(span, sampleDist)) ratio = targetDist / sampleDist;
                else if (span > 1e-6 && span < targetDist * 3 && span > targetDist / 3.0) ratio = targetDist / span;
            }
            double cx, cy;
            LongCenter(ext.Value, ratio, out cx, out cy);
            Point3d anchor = new Point3d(cx, cy, 0).TransformBy(b.BlockTransform);
            Vector3d d = desired - anchor;
            if (d.Length < 1e-8) return;
            if (d.Length > Math.Max(200, Math.Abs(targetDist) * 4 + 50)) return;
            b.TransformBy(Matrix3d.Displacement(d));
        }

        static Point3d AnchorOf(Transaction tx, BlockReference br)
        {
            try
            {
                Extents3d? ext = BlockAlong.DefExtents(tx, br.BlockTableRecord);
                if (ext != null)
                {
                    double cx, cy;
                    LongCenter(ext.Value, 1, out cx, out cy);
                    return new Point3d(cx, cy, 0).TransformBy(br.BlockTransform);
                }
            }
            catch (System.Exception) { }
            return br.Position;
        }

        static void LongCenter(Extents3d ext, double ratio, out double cx, out double cy)
        {
            double minX = ext.MinPoint.X, maxX = ext.MaxPoint.X, minY = ext.MinPoint.Y, maxY = ext.MaxPoint.Y;
            double spanX = maxX - minX, spanY = maxY - minY;
            bool alongX = spanX >= spanY;
            double min = alongX ? minX : minY, max = alongX ? maxX : maxY;
            double span = max - min;
            double mid = (min + max) / 2.0;
            double other = alongX ? (minY + maxY) / 2.0 : (minX + maxX) / 2.0;
            double cAxis = mid;
            if (span > 1e-9 && Math.Abs(ratio - 1) > 1e-9)
            {
                bool sym = Math.Abs(Math.Abs(min) - Math.Abs(max)) <= 0.2 * span;
                double fix = Math.Abs(min) <= Math.Abs(max) ? min : max;
                cAxis = sym ? mid * ratio : fix + (mid - fix) * ratio;
            }
            if (alongX) { cx = cAxis; cy = other; }
            else { cx = other; cy = cAxis; }
        }

        static bool UnitMatch(double span, double v)
        {
            if (v <= 1e-6 || span <= 1e-6) return false;
            double r = span / v;
            return Math.Abs(r - 1) <= 0.25 || Math.Abs(r - 10) <= 1.5 || Math.Abs(r - 0.1) <= 0.025;
        }

        static void AddAttribs(Transaction tx, BlockReference br)
        {
            ObjectId defId = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
            BlockTableRecord def = tx.GetObject(defId, OpenMode.ForRead) as BlockTableRecord;
            if (def == null || !def.HasAttributeDefinitions)
            {
                Executor.AddAttributes(tx, br);
                return;
            }
            if (br.AttributeCollection != null && br.AttributeCollection.Count > 0) return;
            foreach (ObjectId id in def)
            {
                AttributeDefinition ad = tx.GetObject(id, OpenMode.ForRead) as AttributeDefinition;
                if (ad == null || ad.Constant) continue;
                AttributeReference ar = new AttributeReference();
                ar.SetAttributeFromBlock(ad, br.BlockTransform);
                br.AttributeCollection.AppendAttribute(ar);
                tx.AddNewlyCreatedDBObject(ar, true);
            }
        }

        /// <summary>Copies sample attribute text, then sets KAT to the layer count. Returns false when KAT is missing.</summary>
        static bool CopyAtts(Transaction tx, BlockReference src, BlockReference dst, int kat)
        {
            Dictionary<string, string> vals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (src.AttributeCollection != null)
                foreach (ObjectId id in src.AttributeCollection)
                {
                    AttributeReference ar = tx.GetObject(id, OpenMode.ForRead) as AttributeReference;
                    if (ar != null && !vals.ContainsKey(ar.Tag)) vals[ar.Tag] = ar.TextString ?? "";
                }
            bool katSet = false;
            if (dst.AttributeCollection == null) return false;
            foreach (ObjectId id in dst.AttributeCollection)
            {
                AttributeReference ar = tx.GetObject(id, OpenMode.ForWrite) as AttributeReference;
                if (ar == null) continue;
                if (string.Equals((ar.Tag ?? "").Trim(), "KAT", StringComparison.OrdinalIgnoreCase))
                {
                    ar.TextString = kat.ToString(CultureInfo.InvariantCulture);
                    katSet = true;
                }
                else
                {
                    string v;
                    if (vals.TryGetValue(ar.Tag, out v)) ar.TextString = v;
                }
            }
            return katSet;
        }

        static double PropNum(BlockReference br, string name)
        {
            DynamicBlockReferenceProperty p = Fittings.Prop(br, name);
            double d;
            return p != null && TryD(p.Value, out d) ? d : double.NaN;
        }

        static bool TryD(object o, out double d)
        {
            d = 0;
            if (o == null || o is bool) return false;
            if (o is double) { d = (double)o; return true; }
            if (o is float) { d = (float)o; return true; }
            if (o is int) { d = (int)o; return true; }
            if (o is short) { d = (short)o; return true; }
            if (o is long) { d = (long)o; return true; }
            if (o is decimal) { d = (double)(decimal)o; return true; }
            string s = Convert.ToString(o, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(s)) return false;
            return double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out d);
        }

        static double NormAng(double a)
        {
            while (a <= -Math.PI) a += Math.PI * 2;
            while (a > Math.PI) a -= Math.PI * 2;
            return a;
        }

        static double Wrap(double a)
        {
            double t = a % (Math.PI * 2);
            if (t < 0) t += Math.PI * 2;
            return t;
        }
    }
}
