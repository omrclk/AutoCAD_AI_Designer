using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AcColor = Autodesk.AutoCAD.Colors.Color;
using AcColorMethod = Autodesk.AutoCAD.Colors.ColorMethod;

namespace CadAiDesigner
{
    /// <summary>
    /// 96×96 PNG preview of a block definition, drawn from its geometry (not only PreviewIcon).
    /// Dynamic references should pass BlockReference.BlockTableRecord (the anonymous *U definition)
    /// so the current visibility and stretches are what gets drawn; the base definition is the fallback.
    /// Results are cached per database + definition handle.
    /// </summary>
    internal static class BlockPreview
    {
        const int Px = 96;
        const int Margin = 4;
        const int MaxSeg = 8000;
        const int MaxDepth = 8;
        static readonly int Black = Color.Black.ToArgb();
        static readonly Dictionary<string, string> Cache = new Dictionary<string, string>();

        /// <summary>Resim ekle [Evet/Hayir] &lt;Evet&gt;. Null when the user cancels.</summary>
        public static bool? Ask(Editor ed)
        {
            PromptResult ir = ed.GetKeywords(new PromptKeywordOptions("\nResim ekle [Evet/Hayir] <Evet>: ", "Evet Hayir") { AllowNone = true });
            if (ir.Status != PromptStatus.OK && ir.Status != PromptStatus.None) return null;
            return !(ir.Status == PromptStatus.OK && ir.StringResult == "Hayir");
        }

        /// <summary>Render one definition. Returns the picture key (definition handle) stored in <paramref name="images"/>, or null.</summary>
        public static string Capture(Transaction tx, ObjectId btrId, Dictionary<string, string> images)
        {
            if (tx == null || images == null || btrId.IsNull) return null;
            string cacheKey, pub;
            try
            {
                cacheKey = CacheKey(btrId);
                pub = btrId.Handle.ToString();
            }
            catch (System.Exception) { return null; }
            string b64;
            if (!Cache.TryGetValue(cacheKey, out b64))
            {
                byte[] png = null;
                try { png = Render(tx, btrId); }
                catch (System.Exception) { png = null; }
                b64 = (png == null || png.Length < 8) ? "" : Convert.ToBase64String(png);
                Cache[cacheKey] = b64;
            }
            if (string.IsNullOrEmpty(b64)) return null;
            images[pub] = b64;
            return pub;
        }

        /// <summary>Anonymous (*U) definition first, then the dynamic base definition, then PreviewIcon inside each render.</summary>
        public static string Capture(Transaction tx, BlockReference br, Dictionary<string, string> images)
        {
            if (br == null) return null;
            try
            {
                string k = Capture(tx, br.BlockTableRecord, images);
                if (k != null) return k;
                if (br.IsDynamicBlock)
                {
                    ObjectId baseId = br.DynamicBlockTableRecord;
                    if (!baseId.IsNull && baseId != br.BlockTableRecord) return Capture(tx, baseId, images);
                }
            }
            catch (System.Exception) { }
            return null;
        }

        static string CacheKey(ObjectId id)
        {
            string dbk = "";
            try
            {
                Database db = id.Database;
                if (db != null)
                {
                    try { dbk = db.Filename ?? ""; }
                    catch (System.Exception) { dbk = ""; }
                    if (string.IsNullOrEmpty(dbk))
                    {
                        try { dbk = db.FingerprintGuid.ToString(); }
                        catch (System.Exception) { dbk = "db"; }
                    }
                }
            }
            catch (System.Exception) { }
            return dbk + "|" + id.Handle.ToString();
        }

        static byte[] Render(Transaction tx, ObjectId btrId)
        {
            List<Seg> segs = new List<Seg>();
            try { Walk(tx, btrId, Matrix3d.Identity, 0, new HashSet<ObjectId>(), Black, segs, new Dictionary<ObjectId, int>()); }
            catch (System.Exception) { segs.Clear(); }
            if (segs.Count > 0)
            {
                try { return Raster(segs); }
                catch (System.Exception) { }
            }
            try
            {
                BlockTableRecord btr = tx.GetObject(btrId, OpenMode.ForRead) as BlockTableRecord;
                if (btr != null) return FromIcon(btr);
            }
            catch (System.Exception) { }
            return null;
        }

        static void Walk(Transaction tx, ObjectId btrId, Matrix3d xf, int depth, HashSet<ObjectId> stack, int byBlock, List<Seg> segs, Dictionary<ObjectId, int> layers)
        {
            if (depth > MaxDepth || segs.Count >= MaxSeg || btrId.IsNull) return;
            if (!stack.Add(btrId)) return;
            try
            {
                BlockTableRecord btr = tx.GetObject(btrId, OpenMode.ForRead) as BlockTableRecord;
                if (btr == null || btr.IsLayout) return;
                foreach (ObjectId id in btr)
                {
                    if (segs.Count >= MaxSeg) break;
                    Entity e;
                    try { e = tx.GetObject(id, OpenMode.ForRead) as Entity; }
                    catch (System.Exception) { continue; }
                    if (e == null || e is AttributeDefinition) continue;
                    try { if (!e.Visible) continue; }
                    catch (System.Exception) { }
                    int argb = ColorOf(tx, e, byBlock, layers);
                    BlockReference nested = e as BlockReference;
                    if (nested != null)
                    {
                        try { Walk(tx, nested.BlockTableRecord, xf * nested.BlockTransform, depth + 1, stack, argb, segs, layers); }
                        catch (System.Exception) { }
                        continue;
                    }
                    string tn = e.GetType().Name;
                    if (tn == "RasterImage" || tn == "Wipeout" || tn == "Ole2Frame" || tn == "Viewport" || tn == "Table" || tn == "ProxyEntity" || tn == "Region" || tn == "Solid3d" || tn == "Surface")
                        continue;
                    if (e is Dimension) continue;
                    try { Draw(e, xf, argb, segs, true); }
                    catch (System.Exception) { }
                }
            }
            finally { stack.Remove(btrId); }
        }

        static bool Draw(Entity e, Matrix3d xf, int argb, List<Seg> segs, bool allowExplode)
        {
            if (segs.Count >= MaxSeg) return false;
            Line ln = e as Line;
            if (ln != null)
            {
                AddWorld(ln.StartPoint, ln.EndPoint, xf, argb, segs);
                return true;
            }
            if (e is DBText || e is MText)
                return TextBox(e, xf, argb, segs);
            Hatch hatch = e as Hatch;
            if (hatch != null)
                return HatchLoops(hatch, xf, argb, segs);
            Solid solid = e as Solid;
            if (solid != null)
                return SolidBox(solid, xf, argb, segs);
            Face face = e as Face;
            if (face != null)
                return FaceBox(face, xf, argb, segs);
            Curve curve = e as Curve;
            if (curve != null && Sample(curve, xf, argb, segs))
                return true;
            if (allowExplode)
                return ExplodeOnce(e, xf, argb, segs);
            return false;
        }

        static bool TextBox(Entity e, Matrix3d xf, int argb, List<Seg> segs)
        {
            try
            {
                Extents3d ex = e.GeometricExtents;
                Point3d a = ex.MinPoint, b = ex.MaxPoint;
                Point3d c = new Point3d(b.X, a.Y, a.Z), d = new Point3d(a.X, b.Y, b.Z);
                AddWorld(a, c, xf, argb, segs);
                AddWorld(c, b, xf, argb, segs);
                AddWorld(b, d, xf, argb, segs);
                AddWorld(d, a, xf, argb, segs);
                return true;
            }
            catch (System.Exception) { return false; }
        }

        static bool HatchLoops(Hatch h, Matrix3d xf, int argb, List<Seg> segs)
        {
            int n;
            try { n = h.NumberOfLoops; }
            catch (System.Exception) { return false; }
            if (n < 0) return false;
            if (n > 64) n = 64;
            int before = segs.Count;
            for (int i = 0; i < n; i++)
            {
                try
                {
                    HatchLoop loop = h.GetLoopAt(i);
                    if (loop == null) continue;
                    if (loop.IsPolyline)
                    {
                        BulgeVertexCollection bv = loop.Polyline;
                        if (bv == null || bv.Count < 2) continue;
                        int count = bv.Count;
                        for (int k = 0; k < count && segs.Count < MaxSeg; k++)
                        {
                            BulgeVertex v = bv[k];
                            Point2d a = v.Vertex;
                            Point2d b = bv[(k + 1) % count].Vertex;
                            Bulge2d(a, b, v.Bulge, xf, argb, segs);
                        }
                    }
                    else
                    {
                        Curve2dCollection curves = loop.Curves;
                        if (curves == null) continue;
                        for (int ci = 0; ci < curves.Count && segs.Count < MaxSeg; ci++)
                        {
                            Curve2d c2 = curves[ci];
                            if (c2 == null) continue;
                            AddWorld(new Point3d(c2.StartPoint.X, c2.StartPoint.Y, 0), new Point3d(c2.EndPoint.X, c2.EndPoint.Y, 0), xf, argb, segs);
                        }
                    }
                }
                catch (System.Exception) { }
            }
            return segs.Count > before;
        }

        /// <summary>Tessellate a bulge arc in the XY plane (positive bulge is counter-clockwise, AutoCAD convention).</summary>
        static void Bulge2d(Point2d a, Point2d b, double bulge, Matrix3d xf, int argb, List<Seg> segs)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double chord = Math.Sqrt(dx * dx + dy * dy);
            if (chord < 1e-9) return;
            if (Math.Abs(bulge) < 1e-8)
            {
                AddWorld(new Point3d(a.X, a.Y, 0), new Point3d(b.X, b.Y, 0), xf, argb, segs);
                return;
            }
            double theta = 4.0 * Math.Atan(bulge);
            double sinHalf = Math.Sin(theta / 2.0);
            if (Math.Abs(sinHalf) < 1e-9)
            {
                AddWorld(new Point3d(a.X, a.Y, 0), new Point3d(b.X, b.Y, 0), xf, argb, segs);
                return;
            }
            double radius = chord / (2.0 * sinHalf);
            double mx = (a.X + b.X) / 2.0, my = (a.Y + b.Y) / 2.0;
            double nx = -dy / chord, ny = dx / chord;
            double cx = mx + nx * radius * Math.Cos(theta / 2.0);
            double cy = my + ny * radius * Math.Cos(theta / 2.0);
            double rAbs = Math.Abs(radius);
            double sa = Math.Atan2(a.Y - cy, a.X - cx);
            int steps = (int)Math.Ceiling(Math.Abs(theta) / (Math.PI / 16.0));
            if (steps < 2) steps = 2;
            if (steps > 48) steps = 48;
            Point3d prev = new Point3d(a.X, a.Y, 0);
            for (int i = 1; i <= steps; i++)
            {
                Point3d p = i == steps
                    ? new Point3d(b.X, b.Y, 0)
                    : new Point3d(cx + rAbs * Math.Cos(sa + theta * i / steps), cy + rAbs * Math.Sin(sa + theta * i / steps), 0);
                AddWorld(prev, p, xf, argb, segs);
                prev = p;
            }
        }

        static bool SolidBox(Solid s, Matrix3d xf, int argb, List<Seg> segs)
        {
            try
            {
                Point3d p0 = s.GetPointAt(0), p1 = s.GetPointAt(1), p2 = s.GetPointAt(2), p3 = s.GetPointAt(3);
                AddWorld(p0, p1, xf, argb, segs);
                AddWorld(p1, p3, xf, argb, segs);
                AddWorld(p3, p2, xf, argb, segs);
                AddWorld(p2, p0, xf, argb, segs);
                return true;
            }
            catch (System.Exception) { return false; }
        }

        static bool FaceBox(Face f, Matrix3d xf, int argb, List<Seg> segs)
        {
            try
            {
                Point3d p0 = f.GetVertexAt(0), p1 = f.GetVertexAt(1), p2 = f.GetVertexAt(2), p3 = f.GetVertexAt(3);
                AddWorld(p0, p1, xf, argb, segs);
                AddWorld(p1, p2, xf, argb, segs);
                AddWorld(p2, p3, xf, argb, segs);
                AddWorld(p3, p0, xf, argb, segs);
                return true;
            }
            catch (System.Exception) { return false; }
        }

        static bool Sample(Curve c, Matrix3d xf, int argb, List<Seg> segs)
        {
            double a, b;
            try { a = c.StartParam; b = c.EndParam; }
            catch (System.Exception) { return false; }
            if (double.IsNaN(a) || double.IsNaN(b) || double.IsInfinity(a) || double.IsInfinity(b)) return false;
            if (b < a) { double t = a; a = b; b = t; }
            Point3d p0;
            if (!TryAt(c, a, out p0)) return false;
            double span = b - a;
            int before = segs.Count;
            if (span < 1e-12)
            {
                try { AddWorld(p0, c.EndPoint, xf, argb, segs); }
                catch (System.Exception) { }
                return segs.Count > before;
            }
            int n = (int)Math.Ceiling(span * 8.0);
            if (c is Circle || c is Ellipse) n = Math.Max(n, 36);
            if (n < 4) n = 4;
            if (n > 64) n = 64;
            Point3d prev = p0;
            for (int i = 1; i <= n && segs.Count < MaxSeg; i++)
            {
                double t = i == n ? b : a + span * i / n;
                Point3d p;
                if (!TryAt(c, t, out p))
                {
                    try { p = i == n ? c.EndPoint : prev; }
                    catch (System.Exception) { continue; }
                }
                AddWorld(prev, p, xf, argb, segs);
                prev = p;
            }
            return segs.Count > before;
        }

        static bool TryAt(Curve c, double t, out Point3d p)
        {
            p = Point3d.Origin;
            try { p = c.GetPointAtParameter(t); return true; }
            catch (System.Exception) { return false; }
        }

        static bool ExplodeOnce(Entity e, Matrix3d xf, int argb, List<Seg> segs)
        {
            DBObjectCollection bag = new DBObjectCollection();
            try { e.Explode(bag); }
            catch (System.Exception) { return false; }
            int before = segs.Count;
            try
            {
                foreach (DBObject o in bag)
                {
                    Entity ee = o as Entity;
                    if (ee == null || ee is AttributeDefinition || ee is BlockReference) continue;
                    try { Draw(ee, xf, argb, segs, false); }
                    catch (System.Exception) { }
                }
            }
            finally
            {
                foreach (DBObject o in bag)
                {
                    try { o.Dispose(); } catch (System.Exception) { }
                }
            }
            return segs.Count > before;
        }

        static void AddWorld(Point3d a, Point3d b, Matrix3d xf, int argb, List<Seg> segs)
        {
            if (segs.Count >= MaxSeg) return;
            Point3d p, q;
            try { p = a.TransformBy(xf); q = b.TransformBy(xf); }
            catch (System.Exception) { return; }
            if (double.IsNaN(p.X) || double.IsNaN(p.Y) || double.IsNaN(q.X) || double.IsNaN(q.Y)) return;
            if (double.IsInfinity(p.X) || double.IsInfinity(q.X) || double.IsInfinity(p.Y) || double.IsInfinity(q.Y)) return;
            double dx = p.X - q.X, dy = p.Y - q.Y;
            if (dx * dx + dy * dy < 1e-12) return;
            segs.Add(new Seg { X1 = p.X, Y1 = p.Y, X2 = q.X, Y2 = q.Y, Argb = argb });
        }

        static int ColorOf(Transaction tx, Entity e, int byBlock, Dictionary<ObjectId, int> layers)
        {
            try
            {
                AcColor c = e.Color;
                if (c == null) return byBlock;
                if (c.ColorMethod == AcColorMethod.ByBlock || e.ColorIndex == 0) return byBlock;
                if (c.ColorMethod == AcColorMethod.ByLayer || e.ColorIndex == 256)
                {
                    int cached;
                    if (layers != null && layers.TryGetValue(e.LayerId, out cached)) return cached;
                    int argb = byBlock;
                    try
                    {
                        LayerTableRecord ltr = tx.GetObject(e.LayerId, OpenMode.ForRead) as LayerTableRecord;
                        if (ltr != null) argb = FromAc(ltr.Color, byBlock);
                    }
                    catch (System.Exception) { }
                    if (layers != null && !e.LayerId.IsNull) layers[e.LayerId] = argb;
                    return argb;
                }
                return FromAc(c, byBlock);
            }
            catch (System.Exception) { return byBlock; }
        }

        static int FromAc(AcColor c, int fallback)
        {
            if (c == null) return fallback;
            try
            {
                if (c.ColorMethod == AcColorMethod.ByColor)
                    return Opaque(c.ColorValue);
                short ci = c.ColorIndex;
                if (ci <= 0 || ci == 7 || ci >= 256) return Black;
                return Opaque(AcColor.FromColorIndex(AcColorMethod.ByAci, ci).ColorValue);
            }
            catch (System.Exception) { return fallback; }
        }

        static int Opaque(Color c)
        {
            if (c.R >= 245 && c.G >= 245 && c.B >= 245) return Black;
            return (c.ToArgb() & 0x00FFFFFF) | unchecked((int)0xFF000000);
        }

        static byte[] Raster(List<Seg> segs)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (Seg s in segs)
            {
                if (s.X1 < minX) minX = s.X1; if (s.X2 < minX) minX = s.X2;
                if (s.Y1 < minY) minY = s.Y1; if (s.Y2 < minY) minY = s.Y2;
                if (s.X1 > maxX) maxX = s.X1; if (s.X2 > maxX) maxX = s.X2;
                if (s.Y1 > maxY) maxY = s.Y1; if (s.Y2 > maxY) maxY = s.Y2;
            }
            double w = maxX - minX, h = maxY - minY;
            if (w < 1e-9) w = 1e-9;
            if (h < 1e-9) h = 1e-9;
            double avail = Px - 2.0 * Margin;
            double scale = avail / Math.Max(w, h);
            double ox = Margin + (avail - w * scale) / 2.0 - minX * scale;
            double oy = Margin + (avail - h * scale) / 2.0 + maxY * scale;
            using (Bitmap bmp = new Bitmap(Px, Px, PixelFormat.Format24bppRgb))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                Dictionary<int, Pen> pens = new Dictionary<int, Pen>();
                try
                {
                    foreach (Seg s in segs)
                    {
                        Pen pen;
                        if (!pens.TryGetValue(s.Argb, out pen))
                        {
                            pen = new Pen(Color.FromArgb(s.Argb), 1.5f);
                            pens[s.Argb] = pen;
                        }
                        g.DrawLine(pen,
                            (float)(ox + s.X1 * scale), (float)(oy - s.Y1 * scale),
                            (float)(ox + s.X2 * scale), (float)(oy - s.Y2 * scale));
                    }
                }
                finally
                {
                    foreach (Pen pen in pens.Values) pen.Dispose();
                }
                using (MemoryStream ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    return ms.ToArray();
                }
            }
        }

        static byte[] FromIcon(BlockTableRecord btr)
        {
            // PreviewIcon is often null. Do not dispose the bitmap: AutoCAD may keep the instance.
            Bitmap icon = null;
            try { icon = btr.PreviewIcon; }
            catch (System.Exception) { return null; }
            if (icon == null) return null;
            try
            {
                using (Bitmap bmp = new Bitmap(Px, Px, PixelFormat.Format24bppRgb))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.White);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    float avail = Px - 2f * Margin;
                    float s = Math.Min(avail / Math.Max(1, icon.Width), avail / Math.Max(1, icon.Height));
                    float w = icon.Width * s, h = icon.Height * s;
                    g.DrawImage(icon, (Px - w) / 2f, (Px - h) / 2f, w, h);
                    using (MemoryStream ms = new MemoryStream())
                    {
                        bmp.Save(ms, ImageFormat.Png);
                        return ms.ToArray();
                    }
                }
            }
            catch (System.Exception) { return null; }
        }

        struct Seg { public double X1, Y1, X2, Y2; public int Argb; }
    }
}
