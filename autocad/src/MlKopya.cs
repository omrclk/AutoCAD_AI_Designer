using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using Autodesk.AutoCAD.Runtime;

namespace CadAiDesigner
{
    /// <summary>MLKOPYA / AIDMLKOPYA: base-point copy of one multileader, the same way COPY clones it.
    /// DeepCloneObjects then a single TransformBy. The leader, landing and text are not edited afterwards.</summary>
    public class MlKopyaCommands
    {
        [CommandMethod("MLKOPYA", CommandFlags.Modal)]
        public void Run() { Do(); }

        [CommandMethod("AIDMLKOPYA", CommandFlags.Modal)]
        public void RunAlias() { Do(); }

        static void Do()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            Database db = doc.Database;
            try
            {
                PromptEntityOptions eo = new PromptEntityOptions("\nÖrnek multileader seçin: ");
                eo.SetRejectMessage("\nMultileader seçin. ");
                eo.AddAllowedClass(typeof(MLeader), false);
                PromptEntityResult er = ed.GetEntity(eo);
                if (er.Status != PromptStatus.OK) return;

                PromptPointOptions bo = new PromptPointOptions("\nMultileader üzerinde baz noktasını tıkla: ");
                PromptPointResult br = ed.GetPoint(bo);
                if (br.Status != PromptStatus.OK) return;
                Point3d basis = br.Value.TransformBy(ed.CurrentUserCoordinateSystem);

                List<Point3d> places = new List<Point3d>();
                while (true)
                {
                    Point3d p;
                    if (!NextPoint(ed, er.ObjectId, basis, out p)) break;
                    places.Add(p);
                }
                if (places.Count == 0)
                {
                    ed.WriteMessage("\nKopya yok.\n");
                    return;
                }

                int made = 0, failed = 0;
                using (doc.LockDocument())
                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    MLeader sample = tx.GetObject(er.ObjectId, OpenMode.ForRead) as MLeader;
                    if (sample == null)
                    {
                        ed.WriteMessage("\nÖrnek multileader okunamadı.\n");
                        return;
                    }
                    LayerTableRecord lay = tx.GetObject(sample.LayerId, OpenMode.ForRead) as LayerTableRecord;
                    if (lay != null && lay.IsLocked)
                    {
                        ed.WriteMessage("\nÖrnek etiketin katmanı kilitli (" + sample.Layer + ").\n");
                        return;
                    }
                    ObjectId spaceId = sample.OwnerId.IsNull ? db.CurrentSpaceId : sample.OwnerId;
                    BlockTableRecord space = tx.GetObject(spaceId, OpenMode.ForWrite) as BlockTableRecord;
                    if (space == null)
                    {
                        ed.WriteMessage("\nHedef alan açılamadı.\n");
                        return;
                    }
                    foreach (Point3d p in places)
                    {
                        if (PlaceOne(tx, db, sample, space, p - basis)) made++;
                        else failed++;
                    }
                    if (made > 0) tx.Commit();
                }
                string msg = made == 0
                    ? "Kopyalanan etiket yok." + (failed > 0 ? " Yazılamayan: " + failed + "." : "")
                    : "Etiket kopyalandı: " + made + "." + (failed > 0 ? " Yazılamayan: " + failed + "." : "") + " Geri almak için U.";
                ed.WriteMessage("\n" + msg + "\n");
                try { Metraj.Raise(msg); } catch (System.Exception) { }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nEtiket kopyalama hatası: " + ex.Message + "\n");
                try { Metraj.Raise("Etiket kopyalama hatası: " + ex.Message); } catch (System.Exception) { }
            }
        }

        /// <summary>COPY's path: deep-clone into the same space, then one displacement. Nothing else is changed.</summary>
        static bool PlaceOne(Transaction tx, Database db, MLeader sample, BlockTableRecord space, Vector3d delta)
        {
            try
            {
                ObjectIdCollection ids = new ObjectIdCollection();
                ids.Add(sample.ObjectId);
                IdMapping map = new IdMapping();
                db.DeepCloneObjects(ids, space.ObjectId, map, false);
                if (!map.Contains(sample.ObjectId)) return false;
                IdPair pair = map[sample.ObjectId];
                if (!pair.IsCloned || pair.Value.IsNull) return false;
                Entity clone = tx.GetObject(pair.Value, OpenMode.ForWrite) as Entity;
                if (clone == null) return false;
                clone.TransformBy(Matrix3d.Displacement(delta));
                return true;
            }
            catch (System.Exception)
            {
                try
                {
                    Entity clone = sample.Clone() as Entity;
                    if (clone == null) return false;
                    clone.TransformBy(Matrix3d.Displacement(delta));
                    space.AppendEntity(clone);
                    tx.AddNewlyCreatedDBObject(clone, true);
                    return true;
                }
                catch (System.Exception) { return false; }
            }
        }

        static bool NextPoint(Editor ed, ObjectId sampleId, Point3d basis, out Point3d p)
        {
            p = Point3d.Origin;
            Entity ghost = null;
            try
            {
                using (Transaction tx = ed.Document.Database.TransactionManager.StartOpenCloseTransaction())
                {
                    MLeader sample = tx.GetObject(sampleId, OpenMode.ForRead) as MLeader;
                    if (sample != null) ghost = sample.Clone() as Entity;
                    tx.Commit();
                }
            }
            catch (System.Exception) { ghost = null; }
            if (ghost != null)
            {
                PreviewJig jig = new PreviewJig(ghost, basis);
                try
                {
                    PromptResult dr = ed.Drag(jig);
                    if (dr != null && dr.Status == PromptStatus.OK) { p = jig.Point; return true; }
                    return false;
                }
                catch (System.Exception) { }
                finally { try { ghost.Dispose(); } catch (System.Exception) { } }
            }
            PromptPointOptions o = new PromptPointOptions("\nBlokta yerleştirme noktasını tıkla [Bitir]: ");
            o.AllowNone = true;
            o.UseBasePoint = true;
            o.BasePoint = basis;
            o.Keywords.Add("Bitir");
            PromptPointResult r = ed.GetPoint(o);
            if (r.Status != PromptStatus.OK) return false;
            p = r.Value.TransformBy(ed.CurrentUserCoordinateSystem);
            return true;
        }

        /// <summary>Non-database clone following the cursor. AcquirePoint is WCS inside a jig.</summary>
        class PreviewJig : DrawJig
        {
            readonly Entity _ghost;
            readonly Point3d _base;
            Point3d _last;
            public Point3d Point;

            public PreviewJig(Entity ghost, Point3d basePt)
            {
                _ghost = ghost;
                _base = basePt;
                _last = basePt;
                Point = basePt;
            }

            protected override SamplerStatus Sampler(JigPrompts prompts)
            {
                JigPromptPointOptions opt = new JigPromptPointOptions("\nBlokta yerleştirme noktasını tıkla [Bitir]: ");
                opt.BasePoint = _base;
                opt.UseBasePoint = true;
                opt.AppendKeywordsToMessage = false;
                opt.UserInputControls = UserInputControls.Accept3dCoordinates | UserInputControls.NullResponseAccepted | UserInputControls.GovernedByOrthoMode;
                opt.Keywords.Add("Bitir");
                PromptPointResult r = prompts.AcquirePoint(opt);
                if (r.Status == PromptStatus.Keyword || r.Status == PromptStatus.None || r.Status == PromptStatus.Cancel)
                    return SamplerStatus.Cancel;
                if (r.Status != PromptStatus.OK) return SamplerStatus.Cancel;
                if (r.Value.DistanceTo(_last) < 1e-8) return SamplerStatus.NoChange;
                try { _ghost.TransformBy(Matrix3d.Displacement(r.Value - _last)); }
                catch (System.Exception) { return SamplerStatus.Cancel; }
                _last = r.Value;
                Point = r.Value;
                return SamplerStatus.OK;
            }

            protected override bool WorldDraw(WorldDraw draw)
            {
                return draw.Geometry.Draw(_ghost);
            }
        }
    }
}
