using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace CadAiDesigner
{
    /// <summary>"place_block_along": inserts a block on every straight segment of the target polylines/lines and
    /// stretches it to the segment length (dynamic length parameter if present, otherwise X scale).</summary>
    internal static class BlockAlong
    {
        internal struct Seg { public Point3d S, E; public bool Arc; public double Len { get { return S.DistanceTo(E); } } }

        static readonly string[] LengthNames = { "Length", "Uzunluk", "Boy", "L", "Distance", "Distance1", "Mesafe", "Len", "Lenght" };
        static readonly string[] WidthNames = { "Width", "Genislik", "Genişlik", "En", "W", "Distance2", "Tava Genişliği", "Genislik1" };

        public static List<Seg> Segments(Transaction tx, Entity ent)
        {
            List<Seg> r = new List<Seg>();
            Polyline pl = ent as Polyline;
            if (pl != null)
            {
                int n = pl.Closed ? pl.NumberOfVertices : pl.NumberOfVertices - 1;
                for (int i = 0; i < n; i++)
                {
                    SegmentType st = pl.GetSegmentType(i);
                    if (st == SegmentType.Line) { LineSegment3d ls = pl.GetLineSegmentAt(i); r.Add(new Seg { S = ls.StartPoint, E = ls.EndPoint }); }
                    else if (st == SegmentType.Arc) { CircularArc3d a = pl.GetArcSegmentAt(i); r.Add(new Seg { S = a.StartPoint, E = a.EndPoint, Arc = true }); }
                }
            }
            else if (ent is Line)
            {
                Line l = (Line)ent; r.Add(new Seg { S = l.StartPoint, E = l.EndPoint });
            }
            else if (ent is Polyline2d || ent is Polyline3d)
            {
                Curve c = (Curve)ent;
                int end = (int)Math.Round(c.EndParam);
                for (int i = 0; i < end; i++)
                {
                    Point3d s = c.GetPointAtParameter(i), e = c.GetPointAtParameter(i + 1);
                    bool arc = false;
                    try { arc = Math.Abs(c.GetDistanceAtParameter(i + 1) - c.GetDistanceAtParameter(i) - s.DistanceTo(e)) > 1e-6 * Math.Max(1, s.DistanceTo(e)); } catch (System.Exception) { }
                    r.Add(new Seg { S = s, E = e, Arc = arc });
                }
            }
            else throw new InvalidOperationException("Hedef polyline/çizgi değil: " + ent.GetType().Name + " " + ent.Handle);
            return r.Where(s => s.Len > 1e-9).ToList();
        }

        /// <summary>X/Y size of a block definition (union of entity extents), relative to its base point.</summary>
        public static Extents3d? DefExtents(Transaction tx, ObjectId btrId)
        {
            BlockTableRecord btr = (BlockTableRecord)tx.GetObject(btrId, OpenMode.ForRead);
            Extents3d? ext = null;
            int n = 0;
            foreach (ObjectId id in btr)
            {
                if (++n > 5000) break;
                Entity e = tx.GetObject(id, OpenMode.ForRead) as Entity;
                if (e == null || e is AttributeDefinition) continue;
                try
                {
                    Extents3d x = e.GeometricExtents;
                    if (ext == null) ext = x; else { Extents3d u = ext.Value; u.AddExtents(x); ext = u; }
                }
                catch (System.Exception) { }
            }
            return ext;
        }

        static DynamicBlockReferenceProperty FindProp(BlockReference br, string explicitName, string[] names, bool fallbackSingleDistance, string exclude)
        {
            if (!br.IsDynamicBlock) return null;
            List<DynamicBlockReferenceProperty> props = br.DynamicBlockReferencePropertyCollection.Cast<DynamicBlockReferenceProperty>()
                .Where(p => !p.ReadOnly && p.PropertyName != exclude).ToList();
            if (!string.IsNullOrEmpty(explicitName))
            {
                DynamicBlockReferenceProperty p = props.FirstOrDefault(x => string.Equals(x.PropertyName, explicitName, StringComparison.OrdinalIgnoreCase));
                if (p == null) throw new InvalidOperationException("Dinamik özellik bulunamadı: " + explicitName + " (mevcut: " + string.Join(", ", props.Select(x => x.PropertyName)) + ")");
                return p;
            }
            foreach (string n in names)
            {
                DynamicBlockReferenceProperty p = props.FirstOrDefault(x => string.Equals(x.PropertyName, n, StringComparison.OrdinalIgnoreCase));
                if (p != null) return p;
            }
            if (fallbackSingleDistance)
            {
                List<DynamicBlockReferenceProperty> d = props.Where(x => x.UnitsType == DynamicBlockReferencePropertyUnitsType.Distance).ToList();
                if (d.Count >= 1) return d[0];
            }
            return null;
        }

        static void SetDistance(DynamicBlockReferenceProperty p, double v)
        {
            try { p.Value = v; }
            catch (System.Exception ex) { throw new InvalidOperationException("'" + p.PropertyName + "' = " + v.ToString("0.###", CultureInfo.InvariantCulture) + " ayarlanamadı (değer kümesi/aralığı kısıtlı olabilir): " + ex.Message); }
        }

        /// <summary>Kept for call sites; intentionally does nothing. Editing the hatch inside a dynamic block's
        /// anonymous copy breaks later grip stretching. Correct hatches come from setting the length ONCE on a
        /// fresh insert of the dynamic definition (see Recreate).</summary>
        internal static void RefreshHatches(Transaction tx, BlockReference br) { }

        /// <summary>Replaces a dynamic block reference by a fresh insert of its dynamic definition with the same
        /// position, rotation, scale, layer, attributes and dynamic properties; the property named
        /// <paramref name="lengthParam"/> is set last (to <paramref name="length"/> when given). The source is erased.
        /// A fresh insert evaluates the stretch/hatch exactly like a grip edit; a re-lengthened copy does not.</summary>
        internal static BlockReference Recreate(Transaction tx, BlockTableRecord space, BlockReference src, string lengthParam, double? length, List<string> notes, bool eraseSource = true)
        {
            Database db = src.Database;
            ObjectId def = src.IsDynamicBlock ? src.DynamicBlockTableRecord : src.BlockTableRecord;
            BlockReference t = new BlockReference(src.Position, def);
            t.SetDatabaseDefaults(db);
            t.SetPropertiesFrom(src);
            t.Rotation = src.Rotation; t.ScaleFactors = src.ScaleFactors; t.Normal = src.Normal;
            space.AppendEntity(t);
            tx.AddNewlyCreatedDBObject(t, true);
            Executor.AddAttributes(tx, t);
            Dictionary<string, string> vals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId aid in src.AttributeCollection) { AttributeReference ar = tx.GetObject(aid, OpenMode.ForRead) as AttributeReference; if (ar != null) vals[ar.Tag] = ar.TextString; }
            foreach (ObjectId aid in t.AttributeCollection) { AttributeReference ar = (AttributeReference)tx.GetObject(aid, OpenMode.ForWrite); string v; if (vals.TryGetValue(ar.Tag, out v)) ar.TextString = v; }
            object len = null;
            if (src.IsDynamicBlock)
            {
                Dictionary<string, DynamicBlockReferenceProperty> tp = new Dictionary<string, DynamicBlockReferenceProperty>();
                foreach (DynamicBlockReferenceProperty p in t.DynamicBlockReferencePropertyCollection) if (!tp.ContainsKey(p.PropertyName)) tp[p.PropertyName] = p;
                foreach (DynamicBlockReferenceProperty sp in src.DynamicBlockReferencePropertyCollection)
                {
                    if (sp.ReadOnly || sp.PropertyName == "Origin") continue;
                    if (string.Equals(sp.PropertyName, lengthParam, StringComparison.OrdinalIgnoreCase)) { len = sp.Value; continue; }
                    DynamicBlockReferenceProperty p;
                    if (!tp.TryGetValue(sp.PropertyName, out p) || Equals(p.Value, sp.Value)) continue;
                    try { p.Value = sp.Value; } catch (System.Exception ex) { if (notes != null) notes.Add(sp.PropertyName + " kopyalanamadı (" + ex.Message + ")."); }
                }
                DynamicBlockReferenceProperty lp = tp.Values.FirstOrDefault(x => string.Equals(x.PropertyName, lengthParam, StringComparison.OrdinalIgnoreCase));
                if (lp != null)
                {
                    object v = length.HasValue ? (object)length.Value : len;
                    if (v != null && !Equals(lp.Value, v)) { try { lp.Value = v; } catch (System.Exception ex) { if (notes != null) notes.Add(lengthParam + " ayarlanamadı (" + ex.Message + ")."); } }
                }
            }
            if (eraseSource) { if (!src.IsWriteEnabled) src.UpgradeOpen(); src.Erase(); }
            return t;
        }

        public static void Run(Transaction tx, Database db, BlockTableRecord space, LayerTable lt, BlockTable bt, Dictionary<string, object> a, ApplyResult res)
        {
            string name = J.Str(a, "name");
            if (name == null || !bt.Has(name)) throw new InvalidOperationException("Blok bulunamadı: " + name);
            ObjectId btrId = bt[name];
            bool center = J.Str(a, "alignment") == "center";
            string lengthParam = J.Str(a, "length_param"), widthParam = J.Str(a, "width_param");
            object wObj = J.Get(a, "width");
            double width = wObj == null ? 0 : J.Num(wObj, "width");
            bool useTargetLayer = J.Get(a, "use_target_layer") == null || J.Bool(a, "use_target_layer");
            bool keepSource = J.Get(a, "keep_source") == null || J.Bool(a, "keep_source");
            string layer = J.Str(a, "layer");
            if (layer != null) layer = Executor.LayerName(tx, lt, layer);
            Extents3d? def = DefExtents(tx, btrId);
            double defLen = def == null ? 0 : def.Value.MaxPoint.X - def.Value.MinPoint.X;
            double defWid = def == null ? 0 : def.Value.MaxPoint.Y - def.Value.MinPoint.Y;
            int arcs = 0;

            foreach (string h in J.Handles(a, null))
            {
                Entity target = Executor.Open(tx, db, h, !keepSource);
                foreach (Seg s in Segments(tx, target))
                {
                    if (s.Arc) arcs++;
                    Vector3d dir = s.E - s.S;
                    double len = s.Len;
                    double ang = Math.Atan2(dir.Y, dir.X);
                    Point3d pos = center ? s.S + dir * 0.5 : s.S;
                    BlockReference br = new BlockReference(pos, btrId);
                    br.SetDatabaseDefaults(db);
                    br.Rotation = ang;
                    br.Layer = layer ?? (useTargetLayer ? target.Layer : br.Layer);
                    space.AppendEntity(br);
                    tx.AddNewlyCreatedDBObject(br, true);
                    Executor.AddAttributes(tx, br);

                    DynamicBlockReferenceProperty lp = FindProp(br, lengthParam, LengthNames, true, null);
                    double sx = 1, sy = 1;
                    if (lp != null) SetDistance(lp, len);
                    else
                    {
                        if (defLen <= 1e-9) throw new InvalidOperationException("'" + name + "' bloğunun X uzunluğu ölçülemedi; uzunluk parametresi de yok.");
                        sx = len / defLen;
                    }
                    if (width > 0)
                    {
                        DynamicBlockReferenceProperty wp = FindProp(br, widthParam, WidthNames, false, lp == null ? null : lp.PropertyName);
                        if (wp != null) SetDistance(wp, width);
                        else if (defWid > 1e-9) sy = width / defWid;
                    }
                    if (sx != 1 || sy != 1) br.ScaleFactors = new Scale3d(sx, sy, 1);
                    RefreshHatches(tx, br);
                    res.Created++;
                }
                if (!keepSource) { target.Erase(); res.Erased++; }
            }
            if (arcs > 0) res.Notes.Add(arcs + " yay segmenti kiriş (düz) olarak yerleştirildi.");
        }

        /// <summary>Preview text: number of inserts and their lengths.</summary>
        public static string Summary(Document doc, Dictionary<string, object> a)
        {
            try
            {
                Database db = doc.Database;
                List<double> lens = new List<double>();
                int arcs = 0;
                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    foreach (string h in J.Handles(a, null))
                        foreach (Seg s in Segments(tx, Executor.Open(tx, db, h, false))) { lens.Add(s.Len); if (s.Arc) arcs++; }
                    tx.Commit();
                }
                string L(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }
                string list = string.Join(", ", lens.Take(15).Select(L)) + (lens.Count > 15 ? ", ..." : "");
                return lens.Count + " blok eklenecek, uzunluklar: " + list + " (toplam " + L(lens.Sum()) + ")" +
                       (arcs > 0 ? "  ⚠ " + arcs + " yay segmenti düz kiriş olarak yerleştirilecek." : "");
            }
            catch (System.Exception ex) { return "(özet hesaplanamadı: " + ex.Message + ")"; }
        }

        /// <summary>Block definitions for the AI: size and dynamic properties (read from an existing reference if any).</summary>
        public static List<Dictionary<string, object>> Definitions(Transaction tx, Database db)
        {
            List<Dictionary<string, object>> r = new List<Dictionary<string, object>>();
            BlockTable bt = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId bid in bt)
            {
                if (r.Count >= 80) break;
                BlockTableRecord b = (BlockTableRecord)tx.GetObject(bid, OpenMode.ForRead);
                if (b.IsErased || b.IsAnonymous || b.IsLayout || b.IsFromExternalReference || b.IsDependent || b.Name.StartsWith("*")) continue;
                Dictionary<string, object> d = new Dictionary<string, object> { { "name", b.Name }, { "dynamic", b.IsDynamicBlock } };
                try
                {
                    Extents3d? e = DefExtents(tx, bid);
                    if (e != null)
                    {
                        d["x_min"] = Math.Round(e.Value.MinPoint.X, 4); d["x_length"] = Math.Round(e.Value.MaxPoint.X - e.Value.MinPoint.X, 4);
                        d["y_min"] = Math.Round(e.Value.MinPoint.Y, 4); d["y_width"] = Math.Round(e.Value.MaxPoint.Y - e.Value.MinPoint.Y, 4);
                    }
                }
                catch (System.Exception) { }
                if (b.IsDynamicBlock)
                {
                    try
                    {
                        BlockReference sample = null;
                        List<ObjectId> refs = b.GetBlockReferenceIds(true, false).Cast<ObjectId>().ToList();
                        foreach (ObjectId an in b.GetAnonymousBlockIds())
                            refs.AddRange(((BlockTableRecord)tx.GetObject(an, OpenMode.ForRead)).GetBlockReferenceIds(true, false).Cast<ObjectId>());
                        if (refs.Count > 0) sample = tx.GetObject(refs[0], OpenMode.ForRead) as BlockReference;
                        if (sample != null)
                            d["dynamic_properties"] = sample.DynamicBlockReferencePropertyCollection.Cast<DynamicBlockReferenceProperty>()
                                .Where(p => p.PropertyName != "Origin")
                                .Select(p => (object)new Dictionary<string, object>
                                {
                                    { "name", p.PropertyName }, { "units", p.UnitsType.ToString() }, { "read_only", p.ReadOnly },
                                    { "value", Convert.ToString(p.Value, CultureInfo.InvariantCulture) }
                                }).Take(30).ToList();
                        else d["dynamic_properties"] = "çizimde örnek referans yok; uzunluk parametresi otomatik aranacak";
                    }
                    catch (System.Exception) { }
                }
                r.Add(d);
            }
            return r;
        }
    }
}