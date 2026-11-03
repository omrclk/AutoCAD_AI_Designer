using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using GI = Autodesk.AutoCAD.GraphicsInterface;

namespace CadAiDesigner
{
    /// <summary>Graphical ghost preview using transient graphics (nothing is written to the DWG).</summary>
    internal static class Preview
    {
        static readonly List<Entity> Ghosts = new List<Entity>();

        public static void Clear()
        {
            GI.TransientManager tm = GI.TransientManager.CurrentTransientManager;
            foreach (Entity g in Ghosts)
            {
                try { tm.EraseTransient(g, new IntegerCollection()); } catch (System.Exception) { }
                try { g.Dispose(); } catch (System.Exception) { }
            }
            Ghosts.Clear();
        }

        static void Add(Entity e, short colorIndex)
        {
            e.ColorIndex = colorIndex;
            GI.TransientManager.CurrentTransientManager.AddTransient(e, GI.TransientDrawingMode.Main, 128, new IntegerCollection());
            Ghosts.Add(e);
        }

        /// <summary>Green = modified/moved result, yellow = copy, cyan = new object. Each action is previewed from the original geometry.</summary>
        public static void Show(Document doc, IList<Dictionary<string, object>> actions)
        {
            Clear();
            Database db = doc.Database;
            try
            {
                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
                    foreach (Dictionary<string, object> a in actions)
                    {
                        string type = J.Str(a, "type");
                        try
                        {
                            if (type == "move" || type == "copy" || type == "rotate" || type == "scale")
                            {
                                Matrix3d m = ActionMath.Transform(a);
                                foreach (string h in J.Handles(a, null))
                                {
                                    Entity src = Executor.Open(tx, db, h, false);
                                    Entity ghost = src.Clone() as Entity;
                                    if (ghost == null) continue;
                                    ghost.TransformBy(m);
                                    Add(ghost, type == "copy" ? (short)2 : (short)3);
                                }
                            }
                            else if (EntityFactory.IsCreateType(type))
                            {
                                Entity e = EntityFactory.Create(a, db, bt);
                                if (e != null) Add(e, 4);
                            }
                        }
                        catch (System.Exception) { /* preview is best-effort */ }
                    }
                    tx.Commit();
                }
                doc.Editor.UpdateScreen();
            }
            catch (System.Exception) { }
        }
    }
}
