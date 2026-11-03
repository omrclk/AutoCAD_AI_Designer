using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace CadAiDesigner
{
    internal static class ContextBuilder
    {
        static double R(double v) { return Math.Round(v, 4); }
        static double[] P(Point3d p) { return new[] { R(p.X), R(p.Y), R(p.Z) }; }
        static string Trunc(string s, int n) { return s == null ? "" : (s.Length <= n ? s : s.Substring(0, n)); }

        public static DrawingContext Build(Document doc, IList<ObjectId> ids)
        {
            Database db = doc.Database;
            DrawingContext ctx = new DrawingContext();
            ctx.DrawingName = doc.Name;
            ctx.Units = db.Insunits.ToString() + (db.Insunits == UnitsValue.Undefined ? " (birim tanımsız)" : "");
            ctx.Space = db.TileMode ? "Model" : "Paper";
            try { RefPoints.AddTo(ctx.Extra, doc); } catch (System.Exception) { }
            StringBuilder fp = new StringBuilder(doc.Name);

            using (Transaction tx = db.TransactionManager.StartTransaction())
            {
                LayerTable lt = (LayerTable)tx.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId lid in lt)
                {
                    LayerTableRecord l = (LayerTableRecord)tx.GetObject(lid, OpenMode.ForRead);
                    if (l.IsErased) continue;
                    ctx.Layers.Add(new LayerInfo { Name = l.Name, Frozen = l.IsFrozen, Locked = l.IsLocked, Off = l.IsOff });
                }
                LayerTableRecord cur = (LayerTableRecord)tx.GetObject(db.Clayer, OpenMode.ForRead);
                ctx.CurrentLayer = cur.Name;

                BlockTable bt = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId bid in bt)
                {
                    BlockTableRecord b = (BlockTableRecord)tx.GetObject(bid, OpenMode.ForRead);
                    if (b.IsErased || b.IsAnonymous || b.IsLayout || b.IsFromExternalReference || b.IsDependent || b.Name.StartsWith("*")) continue;
                    ctx.Blocks.Add(b.Name);
                }

                try { ctx.Summary = Summarize(tx, db, ctx); fp.Append("|D:").Append(string.Join(",", ctx.DuplicateHandles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))); }
                catch (System.Exception ex) { ctx.Summary = new Dictionary<string, object> { { "error", ex.Message } }; }

                try { ctx.BlockDefinitions = BlockAlong.Definitions(tx, db); } catch (System.Exception) { }

                List<ObjectId> alive = ids.Where(i => !i.IsErased).ToList();
                ctx.SelectionTotal = alive.Count;
                List<Entity> ents = new List<Entity>();
                foreach (ObjectId id in alive)
                {
                    Entity ent = tx.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent != null) ents.Add(ent);
                }
                Settings st = Settings.Load();
                IEnumerable<Entity> ordered = ents.OrderBy(e => e.Handle.ToString(), StringComparer.OrdinalIgnoreCase);
                if (st.MaxObjects > 0) ordered = ordered.Take(st.MaxObjects);
                List<Entity> sel = ordered.ToList();
                ctx.SelectionSent = sel.Count;
                bool compact = sel.Count > Math.Max(1, st.CompactAbove);
                List<object> rows = new List<object>();
                foreach (Entity ent in sel)
                {
                    string h;
                    double[] ext = null;
                    if (compact)
                    {
                        object[] row = Row(tx, ent, out ext);
                        rows.Add(row);
                        h = (string)row[0];
                    }
                    else
                    {
                        Dictionary<string, object> item = Describe(tx, ent);
                        ctx.Selected.Add(item);
                        h = (string)item["handle"];
                        object e0; if (item.TryGetValue("extents", out e0)) ext = (double[])e0;
                    }
                    ctx.HandleSet.Add(h);
                    fp.Append('|').Append(h).Append(',').Append(ent.Layer).Append(',');
                    if (ext != null) fp.Append(string.Join(",", ext.Select(x => x.ToString("R", System.Globalization.CultureInfo.InvariantCulture))));
                }
                if (compact)
                    ctx.SelectedTable = new Dictionary<string, object>
                    {
                        { "note", "One row per selected object; null = not applicable. xyz = block insertion point / text position / line start / circle,arc center / first polyline vertex. props = dynamic properties and attributes as key=value;key=value." },
                        { "columns", new[] { "handle", "type", "layer", "block_name", "x", "y", "z", "rotation_deg", "minx", "miny", "maxx", "maxy", "props" } },
                        { "rows", rows }
                    };
                tx.Commit();
            }
            using (SHA1 sha = SHA1.Create())
                ctx.Fingerprint = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(fp.ToString()))).Replace("-", "");
            return ctx;
        }

        /// <summary>One compact table row for a selected entity (see selected_compact.columns).</summary>
        static object[] Row(Transaction tx, Entity ent, out double[] ext)
        {
            string name = null, props = null; double[] xyz = null; double? rot = null;
            ext = null;
            try { Extents3d e = ent.GeometricExtents; ext = new[] { R(e.MinPoint.X), R(e.MinPoint.Y), R(e.MaxPoint.X), R(e.MaxPoint.Y) }; } catch (System.Exception) { }
            BlockReference br = ent as BlockReference;
            if (br != null)
            {
                name = EffectiveName(tx, br);
                xyz = P(br.Position); rot = R(br.Rotation * 180.0 / Math.PI);
                try
                {
                    List<string> kv = new List<string>();
                    if (Math.Abs(br.ScaleFactors.X - 1) > 1e-9 || Math.Abs(br.ScaleFactors.Y - 1) > 1e-9) kv.Add("scale=" + R(br.ScaleFactors.X) + "," + R(br.ScaleFactors.Y) + "," + R(br.ScaleFactors.Z));
                    if (br.IsDynamicBlock)
                    {
                        HashSet<string> seen = new HashSet<string>();
                        foreach (DynamicBlockReferenceProperty p in br.DynamicBlockReferencePropertyCollection)
                            if (p.PropertyName != "Origin" && seen.Add(p.PropertyName))
                                kv.Add(p.PropertyName + "=" + (p.Value is double ? R((double)p.Value).ToString(System.Globalization.CultureInfo.InvariantCulture) : Convert.ToString(p.Value, System.Globalization.CultureInfo.InvariantCulture)));
                    }
                    foreach (ObjectId aid in br.AttributeCollection)
                    {
                        AttributeReference ar = tx.GetObject(aid, OpenMode.ForRead) as AttributeReference;
                        if (ar != null) kv.Add(ar.Tag + "=" + Trunc(ar.TextString, 40).Replace(";", ","));
                    }
                    if (kv.Count > 0) props = string.Join(";", kv);
                }
                catch (System.Exception) { }
            }
            else
            {
                Dictionary<string, object> d = Describe(tx, ent);
                object o;
                foreach (string k in new[] { "position", "start", "center" }) if (xyz == null && d.TryGetValue(k, out o)) xyz = (double[])o;
                List<string> kv = new List<string>();
                if (d.TryGetValue("end", out o)) kv.Add("end=" + string.Join(",", ((double[])o).Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture))));
                if (d.TryGetValue("radius", out o)) kv.Add("radius=" + o);
                if (d.TryGetValue("length", out o)) kv.Add("length=" + o);
                if (d.TryGetValue("closed", out o)) kv.Add("closed=" + o);
                if (d.TryGetValue("vertex_count", out o)) kv.Add("vertices=" + o);
                if (d.TryGetValue("text", out o)) kv.Add("text=" + Convert.ToString(o).Replace(";", ","));
                if (xyz == null && d.TryGetValue("vertices", out o)) { List<double[]> vs = (List<double[]>)o; if (vs.Count > 0) xyz = new[] { vs[0][0], vs[0][1], 0.0 }; }
                if (kv.Count > 0) props = string.Join(";", kv);
            }
            return new object[] { ent.Handle.ToString().ToUpperInvariant(), ent.GetType().Name, ent.Layer, name,
                xyz != null ? (object)xyz[0] : null, xyz != null ? (object)xyz[1] : null, xyz != null ? (object)xyz[2] : null, rot,
                ext != null ? (object)ext[0] : null, ext != null ? (object)ext[1] : null, ext != null ? (object)ext[2] : null, ext != null ? (object)ext[3] : null, props };
        }

        const int MaxSummaryEntities = 300000;

        /// <summary>Effective block name: dynamic blocks report their definition name instead of *Uxx.</summary>
        internal static string EffectiveName(Transaction tx, BlockReference br)
        {
            try
            {
                ObjectId id = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                return ((BlockTableRecord)tx.GetObject(id, OpenMode.ForRead)).Name;
            }
            catch (System.Exception) { return br.Name; }
        }

        static void Inc(Dictionary<string, int> d, string k, int n = 1)
        {
            int v; d.TryGetValue(k, out v); d[k] = v + n;
        }

        static Dictionary<string, object> Sorted(Dictionary<string, int> d, int max)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            foreach (KeyValuePair<string, int> kv in d.OrderByDescending(x => x.Value).ThenBy(x => x.Key).Take(max)) r[kv.Key] = kv.Value;
            return r;
        }

        /// <summary>Nested block references inside one instance of a block definition (name -> count), memoized.</summary>
        static Dictionary<string, int> NestedOf(Transaction tx, ObjectId btrId, Dictionary<ObjectId, Dictionary<string, int>> memo, int depth)
        {
            Dictionary<string, int> res;
            if (memo.TryGetValue(btrId, out res)) return res;
            res = new Dictionary<string, int>();
            memo[btrId] = res; // guards against recursion
            if (depth > 8) return res;
            BlockTableRecord btr = (BlockTableRecord)tx.GetObject(btrId, OpenMode.ForRead);
            if (btr.IsFromExternalReference) return res;
            foreach (ObjectId id in btr)
            {
                if (!id.ObjectClass.IsDerivedFrom(RXObjectClassBr)) continue;
                BlockReference br = tx.GetObject(id, OpenMode.ForRead) as BlockReference;
                if (br == null) continue;
                Inc(res, EffectiveName(tx, br));
                foreach (KeyValuePair<string, int> kv in NestedOf(tx, br.BlockTableRecord, memo, depth + 1)) Inc(res, kv.Key, kv.Value);
            }
            return res;
        }

        static readonly Autodesk.AutoCAD.Runtime.RXClass RXObjectClassBr = Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(BlockReference));

        /// <summary>Whole-drawing summary, independent of the selection (counts per space, type, layer and block name).</summary>
        static Dictionary<string, object> Summarize(Transaction tx, Database db, DrawingContext ctx)
        {
            Dictionary<string, object> spaces = new Dictionary<string, object>();
            Dictionary<ObjectId, Dictionary<string, int>> memo = new Dictionary<ObjectId, Dictionary<string, int>>();
            Dictionary<string, int> allBlocks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> allNested = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int seen = 0;
            bool truncated = false;
            List<object> dupGroups = new List<object>();
            int dupExtra = 0;
            BlockTable bt = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId bid in bt)
            {
                BlockTableRecord space = (BlockTableRecord)tx.GetObject(bid, OpenMode.ForRead);
                if (!space.IsLayout || space.IsErased) continue;
                string spaceName = space.Name;
                try { spaceName = ((Layout)tx.GetObject(space.LayoutId, OpenMode.ForRead)).LayoutName; } catch (System.Exception) { }
                Dictionary<string, int> byType = new Dictionary<string, int>();
                Dictionary<string, int> byLayer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, int> blocks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, int> nested = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                int total = 0;
                Dictionary<string, List<BlockReference>> byPos = new Dictionary<string, List<BlockReference>>(StringComparer.OrdinalIgnoreCase);
                foreach (ObjectId id in space)
                {
                    if (++seen > MaxSummaryEntities) { truncated = true; break; }
                    Entity ent = tx.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    total++;
                    Inc(byType, ent.GetType().Name);
                    Inc(byLayer, ent.Layer);
                    BlockReference br = ent as BlockReference;
                    if (br != null)
                    {
                        string bn = EffectiveName(tx, br);
                        Inc(blocks, bn);
                        string pk = bn + "|" + Math.Round(br.Position.X, 3).ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
                                    Math.Round(br.Position.Y, 3).ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
                                    Math.Round(br.Position.Z, 3).ToString(System.Globalization.CultureInfo.InvariantCulture);
                        List<BlockReference> lst;
                        if (!byPos.TryGetValue(pk, out lst)) byPos[pk] = lst = new List<BlockReference>();
                        lst.Add(br);
                        try { foreach (KeyValuePair<string, int> kv in NestedOf(tx, br.BlockTableRecord, memo, 0)) Inc(nested, kv.Key, kv.Value); }
                        catch (System.Exception) { }
                    }
                }
                foreach (KeyValuePair<string, List<BlockReference>> g in byPos)
                {
                    if (g.Value.Count < 2) continue;
                    List<BlockReference> ordered = g.Value.OrderBy(b => b.Handle.Value).ToList();
                    List<string> extra = ordered.Skip(1).Select(b => b.Handle.ToString().ToUpperInvariant()).ToList();
                    dupExtra += extra.Count;
                    if (ctx.DuplicateHandles.Count < 2000) ctx.DuplicateHandles.UnionWith(extra);
                    if (dupGroups.Count < 200)
                        dupGroups.Add(new Dictionary<string, object>
                        {
                            { "space", spaceName }, { "block", EffectiveName(tx, ordered[0]) }, { "position", P(ordered[0].Position) },
                            { "count", ordered.Count }, { "keep_handle", ordered[0].Handle.ToString().ToUpperInvariant() },
                            { "extra_handles", extra.Take(100).ToList() },
                        });
                }
                if (total == 0 && !space.Name.Equals(BlockTableRecord.ModelSpace, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (KeyValuePair<string, int> kv in blocks) Inc(allBlocks, kv.Key, kv.Value);
                foreach (KeyValuePair<string, int> kv in nested) Inc(allNested, kv.Key, kv.Value);
                Dictionary<string, object> s = new Dictionary<string, object>
                {
                    { "entity_total", total },
                    { "by_type", Sorted(byType, 60) },
                    { "by_layer", Sorted(byLayer, 150) },
                    { "block_references", Sorted(blocks, 300) },
                };
                if (nested.Count > 0) s["nested_block_references"] = Sorted(nested, 300);
                spaces[spaceName] = s;
                if (truncated) break;
            }
            Dictionary<string, object> r = new Dictionary<string, object>
            {
                { "note", "Counts cover the whole drawing (not only the selection). block_references = top-level inserts per effective block name; nested_block_references = inserts inside other blocks (multiplied by instance count)." },
                { "spaces", spaces },
                { "block_references_total_by_name", Sorted(allBlocks, 300) },
            };
            if (allNested.Count > 0) r["nested_block_references_total_by_name"] = Sorted(allNested, 300);
            r["duplicate_block_extra_total"] = dupExtra;
            if (dupGroups.Count > 0) r["duplicate_blocks"] = dupGroups;
            if (truncated) r["truncated"] = true;
            return r;
        }

        static Dictionary<string, object> Describe(Transaction tx, Entity ent)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["handle"] = ent.Handle.ToString().ToUpperInvariant();
            d["type"] = ent.GetType().Name;
            d["layer"] = ent.Layer;
            try
            {
                Extents3d e = ent.GeometricExtents;
                d["extents"] = new[] { R(e.MinPoint.X), R(e.MinPoint.Y), R(e.MaxPoint.X), R(e.MaxPoint.Y) };
            }
            catch (System.Exception) { }

            Line ln = ent as Line;
            Circle ci = ent as Circle;
            Arc arc = ent as Arc;
            Polyline pl = ent as Polyline;
            BlockReference br = ent as BlockReference;
            DBText tx1 = ent as DBText;
            MText mt = ent as MText;

            if (ln != null) { d["start"] = P(ln.StartPoint); d["end"] = P(ln.EndPoint); d["length"] = R(ln.Length); }
            else if (arc != null) { d["center"] = P(arc.Center); d["radius"] = R(arc.Radius); d["start_angle_deg"] = R(arc.StartAngle * 180.0 / Math.PI); d["end_angle_deg"] = R(arc.EndAngle * 180.0 / Math.PI); }
            else if (ci != null) { d["center"] = P(ci.Center); d["radius"] = R(ci.Radius); }
            else if (pl != null)
            {
                d["closed"] = pl.Closed;
                d["vertex_count"] = pl.NumberOfVertices;
                List<double[]> vs = new List<double[]>();
                for (int i = 0; i < pl.NumberOfVertices && i < 30; i++) { Point2d p = pl.GetPoint2dAt(i); vs.Add(new[] { R(p.X), R(p.Y) }); }
                d["vertices"] = vs;
                try { d["length"] = R(pl.Length); } catch (System.Exception) { }
            }
            else if (br != null)
            {
                d["block_name"] = EffectiveName(tx, br);
                d["position"] = P(br.Position);
                d["rotation_deg"] = R(br.Rotation * 180.0 / Math.PI);
                d["scale"] = new[] { R(br.ScaleFactors.X), R(br.ScaleFactors.Y), R(br.ScaleFactors.Z) };
                try
                {
                    if (br.IsDynamicBlock)
                    {
                        Dictionary<string, object> dp = new Dictionary<string, object>();
                        foreach (DynamicBlockReferenceProperty p in br.DynamicBlockReferencePropertyCollection)
                            if (p.PropertyName != "Origin" && !dp.ContainsKey(p.PropertyName))
                                dp[p.PropertyName] = p.Value is double ? (object)R((double)p.Value) : Convert.ToString(p.Value, System.Globalization.CultureInfo.InvariantCulture);
                        d["dynamic_properties"] = dp;
                    }
                    Dictionary<string, object> at = new Dictionary<string, object>();
                    foreach (ObjectId aid in br.AttributeCollection)
                    {
                        AttributeReference ar = tx.GetObject(aid, OpenMode.ForRead) as AttributeReference;
                        if (ar != null) at[ar.Tag] = Trunc(ar.TextString, 80);
                    }
                    if (at.Count > 0) d["attributes"] = at;
                }
                catch (System.Exception) { }
            }
            else if (tx1 != null) { d["text"] = Trunc(tx1.TextString, 120); d["height"] = R(tx1.Height); d["position"] = P(tx1.Position); }
            else if (mt != null) { d["text"] = Trunc(mt.Contents, 120); d["height"] = R(mt.TextHeight); d["position"] = P(mt.Location); }
            else
            {
                Curve c = ent as Curve;
                if (c != null)
                {
                    try { d["length"] = R(c.GetDistanceAtParameter(c.EndParam) - c.GetDistanceAtParameter(c.StartParam)); }
                    catch (System.Exception) { }
                }
            }
            return d;
        }
    }
}
