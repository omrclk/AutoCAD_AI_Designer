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

        class Tail
        {
            public int Line;
            public Point3d P;
        }

        class Dog
        {
            public int Leader;
            public Vector3d V;
            public double Length;
            public bool HasLength;
        }

        class Landing
        {
            public bool HasGlobals;
            public bool EnableDogleg, EnableLanding;
            public double DoglegLength, LandingGap;
            public bool HasAttach;
            public TextAttachmentType Left, Right, Top, Bottom;
            public readonly List<Dog> Dogs = new List<Dog>();
        }

        /// <summary>Read the sample, append the clone, move every vertex (including the landing), then set the text location last so the frame stays at the landing instead of snapping onto the arrow.</summary>
        static MLeader PlaceCopy(Transaction tx, Editor ed, MLeader sample, ObjectId ownerId, Vector3d delta, double eps, out bool rigid)
        {
            rigid = false;
            Point3d arrow0;
            if (!TryArrow(sample, out arrow0)) return null;
            Point3d text0;
            bool hasText = TryText(sample, out text0);
            List<Vert> verts = Snapshot(sample);
            List<Tail> tails = SnapshotTails(sample);
            Landing land = SnapshotLanding(sample);

            BlockTableRecord space = tx.GetObject(ownerId, OpenMode.ForWrite) as BlockTableRecord;
            if (space == null) return null;
            MLeader clone = sample.Clone() as MLeader;
            if (clone == null) return null;
            space.AppendEntity(clone);
            tx.AddNewlyCreatedDBObject(clone, true);

            PlaceRigid(clone, verts, tails, land, text0, hasText, delta);
            if (!OffsetOk(clone, arrow0, text0, hasText, delta, eps))
                PlaceRigid(clone, verts, tails, land, text0, hasText, delta);
            try { clone.RecordGraphicsModified(true); } catch (System.Exception) { }

            bool tipOk, contentOk;
            rigid = OffsetOk(clone, arrow0, text0, hasText, delta, eps, out tipOk, out contentOk);
            if (!rigid)
            {
                string w = "MLKOPYA uyarı: yazı oktan aynı vektörle gitmedi (ok " + (tipOk ? "tamam" : "kaydı") + ", yazı " + (hasText ? (contentOk ? "tamam" : "ok ucuna yapıştı") : "yok") + ").";
                try { AppLog.Write(w); } catch (System.Exception) { }
                try { ed.WriteMessage("\n" + w); } catch (System.Exception) { }
            }
            return clone;
        }

        /// <summary>Vertices first (arrow and landing), landing settings next, text location last.</summary>
        static void PlaceRigid(MLeader ml, List<Vert> verts, List<Tail> tails, Landing land, Point3d text0, bool hasText, Vector3d d)
        {
            ApplyVertices(ml, verts, d);
            ApplyTails(ml, tails, d);
            RestoreLanding(ml, land);
            ApplyText(ml, text0, hasText, d);
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

        static List<Tail> SnapshotTails(MLeader ml)
        {
            List<Tail> tails = new List<Tail>();
            ArrayList leaders = null;
            try { leaders = ml.GetLeaderIndexes(); } catch (System.Exception) { return tails; }
            if (leaders == null) return tails;
            foreach (object lo in leaders)
            {
                ArrayList lines = null;
                try { lines = ml.GetLeaderLineIndexes(Convert.ToInt32(lo)); } catch (System.Exception) { continue; }
                if (lines == null) continue;
                foreach (object lio in lines)
                {
                    int line = Convert.ToInt32(lio);
                    try { tails.Add(new Tail { Line = line, P = ml.GetLastVertex(line) }); }
                    catch (System.Exception) { }
                }
            }
            return tails;
        }

        static void ApplyTails(MLeader ml, List<Tail> tails, Vector3d d)
        {
            foreach (Tail t in tails)
            {
                try { ml.SetLastVertex(t.Line, t.P + d); } catch (System.Exception) { }
            }
        }

        /// <summary>Content position of the database leader. MText.Location is not used: the getter returns a copy whose location is not the frame.</summary>
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

        static void ApplyText(MLeader ml, Point3d origin, bool has, Vector3d d)
        {
            if (!has) return;
            Point3d dest = origin + d;
            try
            {
                if (ml.ContentType == ContentType.MTextContent) ml.TextLocation = dest;
                else if (ml.ContentType == ContentType.BlockContent) ml.BlockPosition = dest;
                else if (ml.ContentType == ContentType.ToleranceContent) ml.ToleranceLocation = dest;
            }
            catch (System.Exception) { }
        }

        static bool OffsetOk(MLeader ml, Point3d arrow0, Point3d text0, bool hasText, Vector3d d, double eps, out bool tipOk, out bool contentOk)
        {
            Point3d tip;
            tipOk = TryArrow(ml, out tip) && tip.DistanceTo(arrow0 + d) <= eps;
            contentOk = true;
            if (!hasText) return tipOk;
            Point3d text;
            if (!TryText(ml, out text)) { contentOk = false; return false; }
            Vector3d want = text0 - arrow0;
            Vector3d got = text - tip;
            contentOk = (got - want).Length <= eps;
            return tipOk && contentOk;
        }

        static bool OffsetOk(MLeader ml, Point3d arrow0, Point3d text0, bool hasText, Vector3d d, double eps)
        {
            bool tipOk, contentOk;
            return OffsetOk(ml, arrow0, text0, hasText, d, eps, out tipOk, out contentOk);
        }

        static Landing SnapshotLanding(MLeader ml)
        {
            Landing land = new Landing();
            try
            {
                land.EnableDogleg = ml.EnableDogleg;
                land.EnableLanding = ml.EnableLanding;
                land.DoglegLength = ml.DoglegLength;
                land.LandingGap = ml.LandingGap;
                land.HasGlobals = true;
            }
            catch (System.Exception) { }
            try
            {
                land.Left = ml.GetTextAttachmentType(LeaderDirectionType.LeftLeader);
                land.Right = ml.GetTextAttachmentType(LeaderDirectionType.RightLeader);
                land.Top = ml.GetTextAttachmentType(LeaderDirectionType.TopLeader);
                land.Bottom = ml.GetTextAttachmentType(LeaderDirectionType.BottomLeader);
                land.HasAttach = true;
            }
            catch (System.Exception) { }
            ArrayList leaders = null;
            try { leaders = ml.GetLeaderIndexes(); } catch (System.Exception) { leaders = null; }
            if (leaders != null)
                foreach (object lo in leaders)
                {
                    int leader = Convert.ToInt32(lo);
                    Dog dog = new Dog { Leader = leader };
                    try { dog.V = ml.GetDogleg(leader); } catch (System.Exception) { continue; }
                    try { dog.Length = ml.GetDoglegLength(leader); dog.HasLength = true; } catch (System.Exception) { }
                    land.Dogs.Add(dog);
                }
            return land;
        }

        static void RestoreLanding(MLeader ml, Landing land)
        {
            if (land == null) return;
            if (land.HasGlobals)
            {
                try { ml.EnableDogleg = land.EnableDogleg; } catch (System.Exception) { }
                try { ml.EnableLanding = land.EnableLanding; } catch (System.Exception) { }
                try { ml.LandingGap = land.LandingGap; } catch (System.Exception) { }
                try { ml.DoglegLength = land.DoglegLength; } catch (System.Exception) { }
            }
            foreach (Dog dog in land.Dogs)
            {
                try { ml.SetDogleg(dog.Leader, dog.V); } catch (System.Exception) { }
                if (dog.HasLength) { try { ml.SetDoglegLength(dog.Leader, dog.Length); } catch (System.Exception) { } }
            }
            if (land.HasAttach)
            {
                try { ml.SetTextAttachmentType(land.Left, LeaderDirectionType.LeftLeader); } catch (System.Exception) { }
                try { ml.SetTextAttachmentType(land.Right, LeaderDirectionType.RightLeader); } catch (System.Exception) { }
                try { ml.SetTextAttachmentType(land.Top, LeaderDirectionType.TopLeader); } catch (System.Exception) { }
                try { ml.SetTextAttachmentType(land.Bottom, LeaderDirectionType.BottomLeader); } catch (System.Exception) { }
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
