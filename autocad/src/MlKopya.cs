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
    /// <summary>MLKOPYA / AIDMLKOPYA: clone any MLeader onto each selected insert.
    /// The arrowhead (first vertex) lands on the block; MoveMLeader shifts the landing and the text with it.</summary>
    public class MlKopyaCommands
    {
        const double TolMm = 50;
        static bool useCenter = false; // Ekleme (insertion point). Merkez = geometric extents center.

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

                using (Transaction tx = db.TransactionManager.StartOpenCloseTransaction())
                {
                    MLeader ml = tx.GetObject(er.ObjectId, OpenMode.ForRead) as MLeader;
                    Point3d tip;
                    if (ml == null || !TryArrow(ml, out tip))
                    {
                        ed.WriteMessage("\nSeçilen multileader'ın ok ucu yok.\n");
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

                int made = 0, skipped = 0, failed = 0, warned = 0;
                using (doc.LockDocument())
                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    MLeader sample = tx.GetObject(er.ObjectId, OpenMode.ForRead) as MLeader;
                    Point3d arrow;
                    if (sample == null || !TryArrow(sample, out arrow))
                    {
                        ed.WriteMessage("\nSeçilen multileader'ın ok ucu yok.\n");
                        return;
                    }
                    LayerTableRecord lay = tx.GetObject(sample.LayerId, OpenMode.ForRead) as LayerTableRecord;
                    if (lay != null && lay.IsLocked)
                    {
                        ed.WriteMessage("\nÖrnek etiketin katmanı kilitli (" + sample.Layer + ").\n");
                        return;
                    }
                    double tol = Math.Max(1.0, TolMm * ArmaturCommands.MmPublic(db));
                    double eps = Math.Max(1e-6, ArmaturCommands.MmPublic(db));
                    Dictionary<ObjectId, List<Point3d>> occupied = new Dictionary<ObjectId, List<Point3d>>();
                    foreach (ObjectId id in sr.Value.GetObjectIds())
                    {
                        BlockReference br = tx.GetObject(id, OpenMode.ForRead) as BlockReference;
                        if (br == null || br.OwnerId.IsNull) continue;
                        Point3d target = TargetPoint(br, useCenter);
                        List<Point3d> tips = Arrows(tx, br.OwnerId, occupied);
                        if (Near(tips, target, tol)) { skipped++; continue; }

                        MLeader clone = null;
                        try
                        {
                            bool rigid;
                            clone = PlaceCopy(tx, ed, sample, br.OwnerId, target - arrow, eps, out rigid);
                            if (clone == null) { failed++; continue; }
                            if (!rigid) warned++;
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
                    : "Etiket kopyalandı: " + made + " blok. " + skip + (failed > 0 ? ". Yazılamayan: " + failed : "") + (warned > 0 ? ". Uyarı: " + warned + " kopyada yazı ok ile birlikte gitmedi." : "") + ". Geri almak için U.";
                ed.WriteMessage("\n" + msg + "\n");
                try { Metraj.Raise(msg); } catch (System.Exception) { }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nEtiket kopyalama hatası: " + ex.Message + "\n");
                try { Metraj.Raise("Etiket kopyalama hatası: " + ex.Message); } catch (System.Exception) { }
            }
        }

        /// <summary>Arrow tip in WCS: GetFirstVertex of the first leader line. Not the landing (last vertex) and not the text/block position.</summary>
        static bool TryArrow(MLeader ml, out Point3d tip)
        {
            tip = Point3d.Origin;
            if (ml == null) return false;
            ArrayList leaders = null;
            try { leaders = ml.GetLeaderIndexes(); } catch (System.Exception) { return false; }
            if (leaders == null || leaders.Count == 0) return false;
            ArrayList lines = null;
            try { lines = ml.GetLeaderLineIndexes(Convert.ToInt32(leaders[0])); } catch (System.Exception) { return false; }
            if (lines == null || lines.Count == 0) return false;
            try { tip = ml.GetFirstVertex(Convert.ToInt32(lines[0])); }
            catch (System.Exception) { return false; }
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

        /// <summary>Append the clone, then MoveMLeader once. No vertex, text-location, or dogleg edits after that: those were detaching the landing from the frame.</summary>
        static MLeader PlaceCopy(Transaction tx, Editor ed, MLeader sample, ObjectId ownerId, Vector3d delta, double eps, out bool rigid)
        {
            rigid = false;
            Point3d arrow0;
            if (!TryArrow(sample, out arrow0)) return null;
            Point3d text0;
            bool hasText = TryText(sample, out text0);
            BlockTableRecord space = tx.GetObject(ownerId, OpenMode.ForWrite) as BlockTableRecord;
            if (space == null) return null;
            MLeader clone = sample.Clone() as MLeader;
            if (clone == null) return null;
            space.AppendEntity(clone);
            tx.AddNewlyCreatedDBObject(clone, true);
            try { clone.MoveMLeader(delta, MoveType.MoveAllPoints); }
            catch (System.Exception)
            {
                try { if (!clone.IsErased) clone.Erase(); } catch (System.Exception) { }
                return null;
            }
            EnsureMText(sample, clone);
            bool tipOk, contentOk;
            rigid = OffsetOk(clone, arrow0, text0, hasText, delta, eps, out tipOk, out contentOk);
            if (!rigid)
            {
                string w = "MLKOPYA uyarı: yazı oktan aynı vektörle gitmedi (ok " + (tipOk ? "tamam" : "kaydı") + ", yazı " + (hasText ? (contentOk ? "tamam" : "ayrıldı") : "yok") + ").";
                try { AppLog.Write(w); } catch (System.Exception) { }
                try { ed.WriteMessage("\n" + w); } catch (System.Exception) { }
            }
            return clone;
        }

        static void EnsureMText(MLeader sample, MLeader clone)
        {
            if (sample == null || clone == null || sample.ContentType != ContentType.MTextContent) return;
            bool missing = true;
            try { MText cur = clone.MText; missing = cur == null; } catch (System.Exception) { missing = true; }
            if (!missing) return;
            try
            {
                MText src = sample.MText;
                if (src != null) clone.MText = (MText)src.Clone();
            }
            catch (System.Exception) { }
        }

        static bool TryText(MLeader ml, out Point3d p)
        {
            p = Point3d.Origin;
            if (ml == null) return false;
            try
            {
                if (ml.ContentType == ContentType.MTextContent) { p = ml.TextLocation; return true; }
                if (ml.ContentType == ContentType.BlockContent) { p = ml.BlockPosition; return true; }
                if (ml.ContentType == ContentType.ToleranceContent) { p = ml.ToleranceLocation; return true; }
            }
            catch (System.Exception) { }
            return false;
        }

        static bool OffsetOk(MLeader ml, Point3d arrow0, Point3d text0, bool hasText, Vector3d d, double eps, out bool tipOk, out bool contentOk)
        {
            Point3d tip;
            tipOk = TryArrow(ml, out tip) && tip.DistanceTo(arrow0 + d) <= eps;
            contentOk = true;
            if (!hasText) return tipOk;
            Point3d text;
            if (!TryText(ml, out text)) { contentOk = false; return false; }
            contentOk = ((text - tip) - (text0 - arrow0)).Length <= eps;
            return tipOk && contentOk;
        }

        /// <summary>Ekleme = insertion point. Merkez = WCS center of the block reference's geometric extents.</summary>
        static Point3d TargetPoint(BlockReference br, bool center)
        {
            if (!center) return br.Position;
            try
            {
                Extents3d ex = br.GeometricExtents;
                return new Point3d(
                    (ex.MinPoint.X + ex.MaxPoint.X) / 2.0,
                    (ex.MinPoint.Y + ex.MaxPoint.Y) / 2.0,
                    (ex.MinPoint.Z + ex.MaxPoint.Z) / 2.0);
            }
            catch (System.Exception) { return br.Position; }
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
