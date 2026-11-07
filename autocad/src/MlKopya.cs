using System;
using System.Collections;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace CadAiDesigner
{
    /// <summary>MLKOPYA / AIDMLKOPYA: clone one block-content MLeader onto each selected insert.
    /// The arrow tip lands on the block insertion point (or bbox center); the label keeps the sample's offset from that tip.</summary>
    public class MlKopyaCommands
    {
        const double TolMm = 50;
        static bool useCenter;

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
                PromptEntityOptions eo = new PromptEntityOptions("\nÖrnek etiket (blok içerikli multileader) seçin: ");
                eo.SetRejectMessage("\nMultileader seçin. ");
                eo.AddAllowedClass(typeof(MLeader), true);
                PromptEntityResult er = ed.GetEntity(eo);
                if (er.Status != PromptStatus.OK) return;

                using (Transaction tx = db.TransactionManager.StartOpenCloseTransaction())
                {
                    MLeader ml = tx.GetObject(er.ObjectId, OpenMode.ForRead) as MLeader;
                    Point3d tip;
                    if (ml == null || !AttExport.IsBlockContent(ml) || !TryArrow(ml, out tip))
                    {
                        ed.WriteMessage("\nBlok içerikli ve oku olan bir multileader seçin.\n");
                        return;
                    }
                    tx.Commit();
                }

                string def = useCenter ? "Merkez" : "Ekleme";
                PromptKeywordOptions ko = new PromptKeywordOptions("\nOk ucu [Ekleme/Merkez] <" + def + ">: ", "Ekleme Merkez") { AllowNone = true };
                PromptResult kr = ed.GetKeywords(ko);
                if (kr.Status != PromptStatus.OK && kr.Status != PromptStatus.None) return;
                if (kr.Status == PromptStatus.OK) useCenter = string.Equals(kr.StringResult, "Merkez", StringComparison.OrdinalIgnoreCase);

                PromptSelectionResult sr = ed.GetSelection(
                    new PromptSelectionOptions { MessageForAdding = "\nEtiketlenecek blokları pencere ile seçin: " },
                    new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "INSERT") }));
                if (sr.Status != PromptStatus.OK || sr.Value == null || sr.Value.Count == 0) return;

                int made = 0, skipped = 0, failed = 0;
                using (doc.LockDocument())
                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    MLeader sample = tx.GetObject(er.ObjectId, OpenMode.ForRead) as MLeader;
                    Point3d arrow;
                    if (sample == null || !AttExport.IsBlockContent(sample) || !TryArrow(sample, out arrow))
                    {
                        ed.WriteMessage("\nÖrnek etiket okunamadı.\n");
                        return;
                    }
                    LayerTableRecord lay = tx.GetObject(sample.LayerId, OpenMode.ForRead) as LayerTableRecord;
                    if (lay != null && lay.IsLocked)
                    {
                        ed.WriteMessage("\nÖrnek etiketin katmanı kilitli (" + sample.Layer + ").\n");
                        return;
                    }
                    double tol = Math.Max(1.0, TolMm * ArmaturCommands.MmPublic(db));
                    Dictionary<ObjectId, List<Point3d>> occupied = new Dictionary<ObjectId, List<Point3d>>();
                    foreach (ObjectId id in sr.Value.GetObjectIds())
                    {
                        BlockReference br = tx.GetObject(id, OpenMode.ForRead) as BlockReference;
                        if (br == null || br.OwnerId.IsNull) continue;
                        Point3d target = useCenter ? OlcuCommands.Center(tx, br) : br.Position;
                        List<Point3d> tips = Arrows(tx, br.OwnerId, occupied);
                        if (Near(tips, target, tol)) { skipped++; continue; }

                        MLeader clone = null;
                        try
                        {
                            BlockTableRecord space = tx.GetObject(br.OwnerId, OpenMode.ForWrite) as BlockTableRecord;
                            clone = sample.Clone() as MLeader;
                            if (clone == null || space == null)
                            {
                                try { if (clone != null) clone.Dispose(); } catch (System.Exception) { }
                                failed++;
                                continue;
                            }
                            space.AppendEntity(clone);
                            tx.AddNewlyCreatedDBObject(clone, true);
                            Translate(clone, target - arrow);
                            try { CopyAttrs(tx, sample, clone); } catch (System.Exception) { }
                            tips.Add(target);
                            foreach (Point3d p in AllArrows(clone)) tips.Add(p);
                            made++;
                        }
                        catch (System.Exception)
                        {
                            try
                            {
                                if (clone == null) { }
                                else if (clone.ObjectId.IsNull) clone.Dispose();
                                else if (!clone.IsErased) clone.Erase();
                            }
                            catch (System.Exception) { }
                            failed++;
                        }
                    }
                    if (made > 0) tx.Commit();
                }
                string skip = "Atlanan (50 mm içinde ok var): " + skipped;
                string msg = made == 0
                    ? "Kopyalanan etiket yok. " + skip + (failed > 0 ? ". Yazılamayan: " + failed : "") + "."
                    : "Etiket kopyalandı: " + made + " blok. " + skip + (failed > 0 ? ". Yazılamayan: " + failed : "") + ". Geri almak için U.";
                ed.WriteMessage("\n" + msg + "\n");
                try { Metraj.Raise(msg); } catch (System.Exception) { }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nEtiket kopyalama hatası: " + ex.Message + "\n");
                try { Metraj.Raise("Etiket kopyalama hatası: " + ex.Message); } catch (System.Exception) { }
            }
        }

        /// <summary>Arrow tip: first vertex of the first leader line.</summary>
        static bool TryArrow(MLeader ml, out Point3d tip)
        {
            tip = Point3d.Origin;
            List<Point3d> all = AllArrows(ml);
            if (all.Count == 0) return false;
            tip = all[0];
            return true;
        }

        static List<Point3d> AllArrows(MLeader ml)
        {
            List<Point3d> pts = new List<Point3d>();
            if (ml == null) return pts;
            ArrayList leaders = null;
            try { leaders = ml.GetLeaderIndexes(); } catch (System.Exception) { return pts; }
            if (leaders == null) return pts;
            foreach (object lo in leaders)
            {
                ArrayList lines = null;
                try { lines = ml.GetLeaderLineIndexes(Convert.ToInt32(lo)); } catch (System.Exception) { continue; }
                if (lines == null) continue;
                foreach (object lio in lines)
                {
                    try { pts.Add(ml.GetFirstVertex(Convert.ToInt32(lio))); }
                    catch (System.Exception) { }
                }
            }
            return pts;
        }

        /// <summary>Translates leader vertices and the block label by the same vector, so the label keeps its offset from the arrow.</summary>
        static void Translate(MLeader ml, Vector3d d)
        {
            Point3d blockPos = Point3d.Origin;
            bool hasBlock = false;
            try
            {
                if (ml.ContentType == ContentType.BlockContent) { blockPos = ml.BlockPosition; hasBlock = true; }
            }
            catch (System.Exception) { }
            ArrayList leaders = null;
            try { leaders = ml.GetLeaderIndexes(); } catch (System.Exception) { }
            if (leaders != null)
                foreach (object lo in leaders)
                {
                    ArrayList lines = null;
                    try { lines = ml.GetLeaderLineIndexes(Convert.ToInt32(lo)); } catch (System.Exception) { continue; }
                    if (lines == null) continue;
                    foreach (object lio in lines)
                    {
                        int line = Convert.ToInt32(lio);
                        int n = ml.VerticesCount(line);
                        for (int i = 0; i < n; i++)
                        {
                            Point3d p = ml.GetVertex(line, i) + d;
                            try { ml.SetVertex(line, i, p); }
                            catch (System.Exception)
                            {
                                if (i == 0) ml.SetFirstVertex(line, p);
                                else if (i == n - 1) ml.SetLastVertex(line, p);
                                else throw;
                            }
                        }
                    }
                }
            if (hasBlock) ml.BlockPosition = blockPos + d;
        }

        static void CopyAttrs(Transaction tx, MLeader src, MLeader dst)
        {
            LeaderInfo info = AttExport.ReadLeader(tx, src);
            if (info == null || !AttExport.IsBlockContent(dst)) return;
            BlockTableRecord def = tx.GetObject(dst.BlockContentId, OpenMode.ForRead) as BlockTableRecord;
            if (def == null) return;
            Dictionary<string, AttributeDefinition> byTag = new Dictionary<string, AttributeDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId id in def)
            {
                AttributeDefinition ad = tx.GetObject(id, OpenMode.ForRead) as AttributeDefinition;
                if (ad == null || ad.Constant || byTag.ContainsKey(ad.Tag)) continue;
                byTag[ad.Tag] = ad;
            }
            foreach (KeyValuePair<string, string> kv in info.Values)
            {
                if (info.Const.Contains(kv.Key)) continue;
                AttributeDefinition ad;
                if (!byTag.TryGetValue(kv.Key, out ad)) continue;
                AttExport.SetLeaderAttr(dst, ad, kv.Value ?? "");
            }
        }

        static List<Point3d> Arrows(Transaction tx, ObjectId spaceId, Dictionary<ObjectId, List<Point3d>> cache)
        {
            List<Point3d> list;
            if (cache.TryGetValue(spaceId, out list)) return list;
            list = new List<Point3d>();
            BlockTableRecord btr = tx.GetObject(spaceId, OpenMode.ForWrite) as BlockTableRecord;
            if (btr != null)
                foreach (ObjectId id in btr)
                {
                    if (id.ObjectClass.DxfName != "MULTILEADER") continue;
                    MLeader ml = tx.GetObject(id, OpenMode.ForRead) as MLeader;
                    if (ml == null) continue;
                    list.AddRange(AllArrows(ml));
                }
            cache[spaceId] = list;
            return list;
        }

        static bool Near(List<Point3d> tips, Point3d p, double tol)
        {
            foreach (Point3d t in tips)
                if (new Point2d(t.X, t.Y).GetDistanceTo(new Point2d(p.X, p.Y)) <= tol) return true;
            return false;
        }
    }
}
