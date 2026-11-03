using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace CadAiDesigner
{
    /// <summary>Creates (unappended) entities for the "create" action types. Shared by Executor and Preview.</summary>
    internal static class EntityFactory
    {
        public static bool IsCreateType(string t)
        {
            return t == "line" || t == "polyline" || t == "rectangle" || t == "circle" || t == "text" || t == "insert_block";
        }

        public static Entity Create(Dictionary<string, object> a, Database db, BlockTable bt)
        {
            switch (J.Str(a, "type"))
            {
                case "line":
                    {
                        Line l = new Line(ActionMath.P(J.Pt(J.Get(a, "start"), "start")), ActionMath.P(J.Pt(J.Get(a, "end"), "end")));
                        l.SetDatabaseDefaults(db);
                        return l;
                    }
                case "circle":
                    {
                        Circle c = new Circle(ActionMath.P(J.Pt(J.Get(a, "center"), "center")), Vector3d.ZAxis, J.Num(J.Get(a, "radius"), "radius"));
                        c.SetDatabaseDefaults(db);
                        return c;
                    }
                case "polyline":
                    {
                        List<object> pts = J.Arr(J.Get(a, "points"));
                        Polyline pl = new Polyline();
                        pl.SetDatabaseDefaults(db);
                        double z = 0;
                        for (int i = 0; i < pts.Count; i++)
                        {
                            double[] p = J.Pt(pts[i], "nokta");
                            if (i == 0) z = p[2];
                            pl.AddVertexAt(i, new Point2d(p[0], p[1]), 0, 0, 0);
                        }
                        pl.Elevation = z;
                        pl.Closed = J.Bool(a, "closed");
                        return pl;
                    }
                case "rectangle":
                    {
                        double[] c1 = J.Pt(J.Get(a, "corner1"), "corner1"), c2 = J.Pt(J.Get(a, "corner2"), "corner2");
                        Polyline pl = new Polyline();
                        pl.SetDatabaseDefaults(db);
                        pl.AddVertexAt(0, new Point2d(c1[0], c1[1]), 0, 0, 0);
                        pl.AddVertexAt(1, new Point2d(c2[0], c1[1]), 0, 0, 0);
                        pl.AddVertexAt(2, new Point2d(c2[0], c2[1]), 0, 0, 0);
                        pl.AddVertexAt(3, new Point2d(c1[0], c2[1]), 0, 0, 0);
                        pl.Elevation = c1[2];
                        pl.Closed = true;
                        return pl;
                    }
                case "text":
                    {
                        DBText t = new DBText();
                        t.SetDatabaseDefaults(db); // must come first: it resets height/style
                        double[] p = J.Pt(J.Get(a, "position"), "position");
                        t.Position = ActionMath.P(p);
                        t.Height = J.Num(J.Get(a, "height"), "height");
                        t.TextString = J.Str(a, "text");
                        t.Rotation = ActionMath.Rad(J.NumOr(a, "rotation_deg", 0));
                        return t;
                    }
                case "insert_block":
                    {
                        string name = J.Str(a, "name");
                        if (!bt.Has(name)) throw new InvalidOperationException("Blok bulunamadı: " + name);
                        BlockReference br = new BlockReference(ActionMath.P(J.Pt(J.Get(a, "position"), "position")), bt[name]);
                        br.SetDatabaseDefaults(db);
                        double s = J.NumOr(a, "scale", 1);
                        br.ScaleFactors = new Scale3d(s, s, s);
                        br.Rotation = ActionMath.Rad(J.NumOr(a, "rotation_deg", 0));
                        return br;
                    }
                default:
                    return null;
            }
        }
    }

    internal class ApplyResult
    {
        public int Created, Modified, Copied, Erased, Commands;
        public List<string> Notes = new List<string>();
        public override string ToString()
        {
            return Created + " nesne oluşturuldu, " + Modified + " nesne değiştirildi, " + Copied + " nesne kopyalandı" +
                (Erased > 0 ? ", " + Erased + " nesne silindi" : "") + (Commands > 0 ? ", " + Commands + " AutoCAD komutu çalıştı" : "") +
                (Notes.Count > 0 ? " (" + string.Join(" ", Notes) + ")" : "");
        }
    }

    /// <summary>Applies a validated plan inside ONE transaction (all-or-nothing). Must run in command context.</summary>
    internal static class Executor
    {
        /// <summary>
        /// Runs the plan in order. Consecutive native actions share one transaction; "command" actions run through
        /// Editor.Command (synchronous, document context). When commands are present everything is wrapped in an
        /// UNDO group (single U reverts) and on failure the group is undone.
        /// </summary>
        public static ApplyResult Run(Document doc, IList<Dictionary<string, object>> actions)
        {
            ApplyResult res = new ApplyResult();
            bool hasCmd = actions.Any(a => J.Str(a, "type") == "command");
            if (!hasCmd) { RunNative(doc, actions, res); return res; }

            Editor ed = doc.Editor;
            object oldEcho = null;
            try { oldEcho = Application.GetSystemVariable("CMDECHO"); } catch (System.Exception) { }
            ed.Command("_.UNDO", "_BEgin");
            bool ok = false;
            try
            {
                List<Dictionary<string, object>> batch = new List<Dictionary<string, object>>();
                foreach (Dictionary<string, object> a in actions)
                {
                    if (J.Str(a, "type") != "command") { batch.Add(a); continue; }
                    if (batch.Count > 0) { RunNative(doc, batch, res); batch.Clear(); }
                    RunCommand(doc, a);
                    res.Commands++;
                }
                if (batch.Count > 0) RunNative(doc, batch, res);
                ok = true;
            }
            finally
            {
                try { ed.Command("_.UNDO", "_End"); } catch (System.Exception) { }
                if (!ok) { try { ed.Command("_.U"); } catch (System.Exception) { } }
                if (oldEcho != null) { try { Application.SetSystemVariable("CMDECHO", oldEcho); } catch (System.Exception) { } }
            }
            return res;
        }

        static void RunCommand(Document doc, Dictionary<string, object> a)
        {
            Database db = doc.Database;
            List<object> parts = new List<object> { J.Str(a, "command").Trim() };
            List<object> args = J.Arr(J.Get(a, "args")) ?? new List<object>();
            SelectionSet ss = null;
            if (J.Get(a, "handles") != null)
            {
                List<ObjectId> ids = new List<ObjectId>();
                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    foreach (string h in J.Handles(a, null)) ids.Add(Open(tx, db, h, false).ObjectId);
                    tx.Commit();
                }
                ss = SelectionSet.FromObjectIds(ids.ToArray());
            }
            bool placed = false;
            foreach (object o in args)
            {
                string s = Convert.ToString(o, CultureInfo.InvariantCulture);
                if (s == CommandRules.Sel && ss != null) { parts.Add(ss); placed = true; }
                else if (o is string) parts.Add(s);
                else parts.Add(Convert.ToDouble(o, CultureInfo.InvariantCulture));
            }
            if (ss != null && !placed) { parts.Insert(1, ss); parts.Insert(2, ""); }
            try { doc.Editor.Command(parts.ToArray()); }
            catch (System.Exception ex) { throw new InvalidOperationException("Komut başarısız (" + J.Str(a, "command") + "): " + ex.Message); }
            // leave no command half-finished
            try { if (!string.IsNullOrEmpty(Convert.ToString(Application.GetSystemVariable("CMDNAMES")))) doc.Editor.Command(); } catch (System.Exception) { }
        }

        static void RunNative(Document doc, IList<Dictionary<string, object>> actions, ApplyResult res)
        {
            Database db = doc.Database;
            using (Transaction tx = db.TransactionManager.StartTransaction())
            {
                LayerTable lt = (LayerTable)tx.GetObject(db.LayerTableId, OpenMode.ForRead);
                BlockTable bt = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord space = (BlockTableRecord)tx.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                foreach (Dictionary<string, object> a in actions)
                {
                    string type = J.Str(a, "type");
                    switch (type)
                    {
                        case "move":
                        case "rotate":
                        case "scale":
                            {
                                Matrix3d m = ActionMath.Transform(a);
                                foreach (string h in J.Handles(a, null))
                                {
                                    Entity e = Open(tx, db, h, true);
                                    e.TransformBy(m);
                                    res.Modified++;
                                }
                                break;
                            }
                        case "copy":
                            {
                                Matrix3d m = ActionMath.Transform(a);
                                foreach (string h in J.Handles(a, null))
                                {
                                    Entity src = Open(tx, db, h, false);
                                    ObjectIdCollection ids = new ObjectIdCollection();
                                    ids.Add(src.ObjectId);
                                    IdMapping map = new IdMapping();
                                    db.DeepCloneObjects(ids, space.ObjectId, map, false);
                                    Entity clone = (Entity)tx.GetObject(map[src.ObjectId].Value, OpenMode.ForWrite);
                                    clone.TransformBy(m);
                                    res.Copied++;
                                }
                                break;
                            }
                        case "place_fittings":
                            Fittings.Run(tx, db, a, res);
                            break;
                        case "place_block_along":
                            BlockAlong.Run(tx, db, space, lt, bt, a, res);
                            break;
                        case "erase":
                            {
                                foreach (string h in J.Handles(a, null))
                                {
                                    Entity e = Open(tx, db, h, true);
                                    if (!e.IsErased) { e.Erase(); res.Erased++; }
                                }
                                break;
                            }
                        case "set_layer":
                            {
                                string layer = LayerName(tx, lt, J.Str(a, "layer"));
                                foreach (string h in J.Handles(a, null))
                                {
                                    Entity e = Open(tx, db, h, true);
                                    e.Layer = layer;
                                    res.Modified++;
                                }
                                break;
                            }
                        case "create_layer":
                            {
                                string name = J.Str(a, "name").Trim();
                                if (!lt.Has(name))
                                {
                                    lt.UpgradeOpen();
                                    LayerTableRecord ltr = new LayerTableRecord();
                                    ltr.Name = name;
                                    ltr.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, (short)J.NumOr(a, "color_index", 7));
                                    lt.Add(ltr);
                                    tx.AddNewlyCreatedDBObject(ltr, true);
                                    res.Created++;
                                }
                                break;
                            }
                        default:
                            {
                                if (!EntityFactory.IsCreateType(type))
                                    throw new InvalidOperationException("İzin verilmeyen işlem: " + type);
                                Entity e = EntityFactory.Create(a, db, bt);
                                string l = J.Str(a, "layer");
                                if (l != null) e.Layer = LayerName(tx, lt, l);
                                space.AppendEntity(e);
                                tx.AddNewlyCreatedDBObject(e, true);
                                BlockReference br = e as BlockReference;
                                if (br != null) AddAttributes(tx, br);
                                res.Created++;
                                break;
                            }
                    }
                }
                tx.Commit();
            }
        }

        internal static void AddAttributes(Transaction tx, BlockReference br)
        {
            BlockTableRecord def = (BlockTableRecord)tx.GetObject(br.BlockTableRecord, OpenMode.ForRead);
            if (!def.HasAttributeDefinitions) return;
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

        /// <summary>Returns the canonical layer name; throws when missing or frozen.</summary>
        internal static string LayerName(Transaction tx, LayerTable lt, string name)
        {
            if (name == null || !lt.Has(name)) throw new InvalidOperationException("Katman bulunamadı: " + name);
            LayerTableRecord rec = (LayerTableRecord)tx.GetObject(lt[name], OpenMode.ForRead);
            if (rec.IsFrozen) throw new InvalidOperationException("Katman dondurulmuş: " + rec.Name);
            return rec.Name;
        }

        /// <summary>Opens an entity by handle. Writing to entities on locked layers is refused.</summary>
        internal static Entity Open(Transaction tx, Database db, string handle, bool write)
        {
            long num;
            if (!long.TryParse(handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out num))
                throw new InvalidOperationException("Geçersiz handle: " + handle);
            ObjectId id;
            try { id = db.GetObjectId(false, new Handle(num), 0); }
            catch (System.Exception) { throw new InvalidOperationException("Nesne bulunamadı: " + handle); }
            if (id.IsNull || id.IsErased) throw new InvalidOperationException("Nesne silinmiş: " + handle);
            Entity ent = tx.GetObject(id, OpenMode.ForRead) as Entity;
            if (ent == null) throw new InvalidOperationException("Nesne değiştirilemez: " + handle);
            if (write)
            {
                LayerTableRecord layer = (LayerTableRecord)tx.GetObject(ent.LayerId, OpenMode.ForRead);
                if (layer.IsLocked) throw new InvalidOperationException("Nesne kilitli katmanda (" + layer.Name + "): " + handle);
                ent.UpgradeOpen();
            }
            return ent;
        }
    }
}
