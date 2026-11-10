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
    /// The arrow tip (first vertex) lands on the block point; vertices and text/block content are shifted by the same vector.</summary>
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

        class Vert
        {
            public int Line, Index, Count;
            public Point3d P;
        }

        class Dog
        {
            public int Leader;
            public Vector3d V;
        }

        /// <summary>Append first, then shift arrow, landing and content by the same vector. TransformBy is not used: on AutoCAD 2022 it moves the leader line and leaves MText behind.</summary>
        static MLeader PlaceCopy(Transaction tx, Editor ed, MLeader sample, ObjectId ownerId, Vector3d delta, double eps, out bool rigid)
        {
            rigid = false;
            BlockTableRecord space = tx.GetObject(ownerId, OpenMode.ForWrite) as BlockTableRecord;
            if (space == null) return null;
            MLeader clone = sample.Clone() as MLeader;
            if (clone == null) return null;
            space.AppendEntity(clone);
            tx.AddNewlyCreatedDBObject(clone, true);

            Point3d arrow0;
            if (!TryArrow(clone, out arrow0)) arrow0 = Point3d.Origin;
            Point3d content0;
            bool hasContent = TryContent(clone, out content0);
            List<Vert> verts = Snapshot(clone);
            List<Dog> dogs = SnapshotDogs(clone);

            try { clone.MoveMLeader(delta, MoveType.MoveAllPoints); } catch (System.Exception) { }
            ApplyVertices(clone, verts, delta);
            ApplyContent(clone, content0, hasContent, delta);
            RestoreDogs(clone, dogs);
            if (!VerticesAt(clone, verts, delta, eps)) ApplyVertices(clone, verts, delta);
            if (hasContent && !ContentAt(clone, content0, delta, eps)) ApplyContent(clone, content0, hasContent, delta);
            if (!VerticesAt(clone, verts, delta, eps)) ApplyVertices(clone, verts, delta);
            if (hasContent && !ContentAt(clone, content0, delta, eps)) ApplyContent(clone, content0, hasContent, delta);
            RestoreDogs(clone, dogs);
            try { clone.RecordGraphicsModified(true); } catch (System.Exception) { }

            Point3d tip;
            bool tipOk = TryArrow(clone, out tip) && tip.DistanceTo(arrow0 + delta) <= eps;
            bool contentOk = true;
            Point3d content1 = Point3d.Origin;
            if (hasContent) contentOk = TryContent(clone, out content1) && content1.DistanceTo(content0 + delta) <= eps;
            rigid = tipOk && contentOk;
            if (!rigid)
            {
                string w = "MLKOPYA uyarı: kopya tam taşınmadı (ok " + (tipOk ? "tamam" : "yerinde kaldı") + ", içerik " + (hasContent ? (contentOk ? "tamam" : "yerinde kaldı") : "yok") + ").";
                try { AppLog.Write(w); } catch (System.Exception) { }
                try { ed.WriteMessage("\n" + w); } catch (System.Exception) { }
            }
            return clone;
        }

        static List<Vert> Snapshot(MLeader ml)
        {
            List<Vert> verts = new List<Vert>();
            ArrayList leaders = null;
            try { leaders = ml.GetLeaderIndexes(); } catch (System.Exception) { return verts; }
            if (leaders == null) return verts;
            foreach (object lo in leaders)
            {
                ArrayList lines = null;
                try { lines = ml.GetLeaderLineIndexes(Convert.ToInt32(lo)); } catch (System.Exception) { continue; }
                if (lines == null) continue;
                foreach (object lio in lines)
                {
                    int line = Convert.ToInt32(lio);
                    int n = 0;
                    try { n = ml.VerticesCount(line); } catch (System.Exception) { continue; }
                    for (int i = 0; i < n; i++)
                    {
                        try { verts.Add(new Vert { Line = line, Index = i, Count = n, P = ml.GetVertex(line, i) }); }
                        catch (System.Exception) { }
                    }
                }
            }
            return verts;
        }

        static void ApplyVertices(MLeader ml, List<Vert> verts, Vector3d d)
        {
            foreach (Vert v in verts)
            {
                Point3d p = v.P + d;
                try
                {
                    if (v.Count <= 1 || v.Index == 0) ml.SetFirstVertex(v.Line, p);
                    else if (v.Index == v.Count - 1) ml.SetLastVertex(v.Line, p);
                    else ml.SetVertex(v.Line, v.Index, p);
                }
                catch (System.Exception)
                {
                    try { ml.SetVertex(v.Line, v.Index, p); } catch (System.Exception) { }
                }
            }
        }

        static bool VerticesAt(MLeader ml, List<Vert> verts, Vector3d d, double eps)
        {
            foreach (Vert v in verts)
            {
                try
                {
                    if (ml.GetVertex(v.Line, v.Index).DistanceTo(v.P + d) > eps) return false;
                }
                catch (System.Exception) { return false; }
            }
            return true;
        }

        static bool TryContent(MLeader ml, out Point3d p)
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
            try
            {
                MText t = ml.MText;
                if (t != null) { p = t.Location; return true; }
            }
            catch (System.Exception) { }
            return false;
        }

        static void ApplyContent(MLeader ml, Point3d origin, bool has, Vector3d d)
        {
            if (!has) return;
            Point3d dest = origin + d;
            try
            {
                if (ml.ContentType == ContentType.MTextContent)
                {
                    MText t = ml.MText;
                    if (t != null)
                    {
                        t.Location = dest;
                        ml.MText = t;
                    }
                    ml.TextLocation = dest;
                }
                else if (ml.ContentType == ContentType.BlockContent)
                    ml.BlockPosition = dest;
                else if (ml.ContentType == ContentType.ToleranceContent)
                    ml.ToleranceLocation = dest;
            }
            catch (System.Exception) { }
        }

        static bool ContentAt(MLeader ml, Point3d origin, Vector3d d, double eps)
        {
            Point3d p;
            return TryContent(ml, out p) && p.DistanceTo(origin + d) <= eps;
        }

        static List<Dog> SnapshotDogs(MLeader ml)
        {
            List<Dog> dogs = new List<Dog>();
            ArrayList leaders = null;
            try { leaders = ml.GetLeaderIndexes(); } catch (System.Exception) { return dogs; }
            if (leaders == null) return dogs;
            foreach (object lo in leaders)
            {
                int leader = Convert.ToInt32(lo);
                try { dogs.Add(new Dog { Leader = leader, V = ml.GetDogleg(leader) }); }
                catch (System.Exception) { }
            }
            return dogs;
        }

        static void RestoreDogs(MLeader ml, List<Dog> dogs)
        {
            foreach (Dog dog in dogs)
            {
                try { ml.SetDogleg(dog.Leader, dog.V); } catch (System.Exception) { }
            }
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
