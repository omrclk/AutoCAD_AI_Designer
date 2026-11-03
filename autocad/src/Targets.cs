using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace CadAiDesigner
{
    /// <summary>Resolves an action's "target" filter ({block, layer, entity_type, all}) to model-space handles,
    /// so the AI can act on e.g. all 'cabletray' blocks or a whole layer without a manual selection.</summary>
    internal static class Targets
    {
        const int Max = 20000;

        static bool TypeMatch(Entity e, string t)
        {
            switch ((t ?? "any").ToLowerInvariant())
            {
                case "any": case "": return true;
                case "line": return e is Line;
                case "polyline": return e is Polyline || e is Polyline2d || e is Polyline3d;
                case "block": case "insert": return e is BlockReference;
                case "text": return e is DBText || e is MText;
                case "circle": return e is Circle;
                case "arc": return e is Arc;
                case "hatch": return e is Hatch;
                case "dimension": return e is Dimension;
                case "spline": return e is Spline;
                default: return string.Equals(e.GetType().Name, t, StringComparison.OrdinalIgnoreCase);
            }
        }

        public static HashSet<string> Resolve(Document doc, List<Dictionary<string, object>> actions, DrawingContext ctx)
        {
            HashSet<string> all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> a in actions)
            {
                Dictionary<string, object> t = J.Get(a, "target") as Dictionary<string, object>;
                if (t == null) continue;
                string block = J.Str(t, "block"), layer = J.Str(t, "layer"), type = J.Str(t, "entity_type");
                bool any = J.Get(t, "all") != null && J.Bool(t, "all");
                if (block == null && layer == null && type == null && !any) throw new InvalidOperationException("Hedef filtresi boş.");
                List<object> hs = new List<object>();
                Database db = doc.Database;
                using (Transaction tx = db.TransactionManager.StartOpenCloseTransaction())
                {
                    BlockTable bt = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord ms = (BlockTableRecord)tx.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId id in ms)
                    {
                        Entity e = tx.GetObject(id, OpenMode.ForRead) as Entity;
                        if (e == null) continue;
                        if (layer != null && !string.Equals(e.Layer, layer, StringComparison.OrdinalIgnoreCase)) continue;
                        if (type != null && !TypeMatch(e, type)) continue;
                        if (block != null)
                        {
                            BlockReference br = e as BlockReference;
                            if (br == null) continue;
                            string n;
                            try { n = ((BlockTableRecord)tx.GetObject(br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord, OpenMode.ForRead)).Name; }
                            catch (System.Exception) { n = br.Name; }
                            if (!string.Equals(n, block, StringComparison.OrdinalIgnoreCase)) continue;
                        }
                        hs.Add(e.Handle.ToString().ToUpperInvariant());
                        if (hs.Count > Max) throw new InvalidOperationException("Hedef filtresi çok fazla nesne buldu (>" + Max + ").");
                    }
                    tx.Commit();
                }
                if (hs.Count == 0)
                    throw new InvalidOperationException("Hedefle eşleşen nesne yok (blok=" + (block ?? "-") + ", katman=" + (layer ?? "-") + ", tür=" + (type ?? "-") + ").");
                a["handles"] = hs;
                foreach (object h in hs) { ctx.HandleSet.Add((string)h); all.Add((string)h); }
            }
            return all;
        }
    }
}