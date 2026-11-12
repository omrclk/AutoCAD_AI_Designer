using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace CadAiDesigner
{
    /// <summary>
    /// ZSIFIRLA / AIDZSIFIRLA: project every entity in the current space onto WCS Z=0
    /// (Model, or the active layout's paper space), the way FLATTEN does.
    /// Normals within 0.001 rad of ±Z are snapped to +Z; other planar objects are
    /// projected. Referenced block definitions (nested and dynamic *U) are flattened
    /// too, and each definition's Origin Z is set to 0. Xrefs are not opened. One transaction, one UNDO.
    /// </summary>
    public class ZSifirlaCommands
    {
        const double Tol = 1e-8;

        [CommandMethod("ZSIFIRLA", CommandFlags.Modal | CommandFlags.NoBlockEditor | CommandFlags.Redraw)]
        [CommandMethod("AIDZSIFIRLA", CommandFlags.Modal | CommandFlags.NoBlockEditor | CommandFlags.Redraw)]
        public void ZSifirla()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            Database db = doc.Database;

            int fixedN = 0, elevN = 0, thickN = 0, projN = 0, skipN = 0, lockedN = 0, errN = 0, total = 0;
            int blocksN = 0, blockFixed = 0;
            string firstErr = null;
            string spaceLabel = "Model";
            string leftover = null;
            List<ObjectId> processedBlocks = new List<ObjectId>();
            try
            {
                using (doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTableRecord space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
                    spaceLabel = SpaceLabel(space);
                    List<ObjectId> lockedIds = LockedLayers(tr, db);
                    HashSet<ObjectId> lockedSet = new HashSet<ObjectId>(lockedIds);
                    List<ObjectId> unlocked = new List<ObjectId>();
                    Queue<ObjectId> blockQueue = new Queue<ObjectId>();
                    HashSet<ObjectId> blockSeen = new HashSet<ObjectId>();
                    blockSeen.Add(space.ObjectId);
                    try
                    {
                        foreach (ObjectId lid in lockedIds)
                        {
                            try
                            {
                                LayerTableRecord ltr = (LayerTableRecord)tr.GetObject(lid, OpenMode.ForWrite);
                                if (!ltr.IsLocked) continue;
                                ltr.IsLocked = false;
                                unlocked.Add(lid);
                            }
                            catch (System.Exception ex)
                            {
                                NoteErr(ref errN, ref firstErr, "Katman", ex);
                            }
                        }

                        foreach (ObjectId id in space)
                        {
                            if (!id.IsValid || id.IsErased) continue;
                            Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                            if (ent == null || ent.IsErased) continue;
                            total++;
                            bool onLocked = !ent.LayerId.IsNull && lockedSet.Contains(ent.LayerId);
                            if (onLocked) lockedN++;
                            try
                            {
                                int kind = Flatten(tr, ent, ref elevN, ref thickN, ref projN);
                                if (kind == 1) fixedN++;
                                else if (kind == 2) skipN++;
                            }
                            catch (System.Exception ex)
                            {
                                NoteErr(ref errN, ref firstErr, ent, ex);
                            }
                            BlockReference brTop = ent as BlockReference;
                            if (brTop != null)
                            {
                                FlattenAttributes(tr, brTop, lockedSet, ref fixedN, ref lockedN, ref elevN, ref thickN, ref projN, ref skipN, ref errN, ref firstErr, false);
                                EnqueueBlockRef(tr, brTop, blockSeen, blockQueue);
                            }
                        }

                        FlattenDefinitions(tr, lockedSet, blockQueue, blockSeen, processedBlocks, ref blocksN, ref blockFixed, ref lockedN, ref elevN, ref thickN, ref projN, ref skipN, ref errN, ref firstErr);
                        ForceInserts(tr, space, ref fixedN, ref elevN, ref thickN, ref projN, ref errN, ref firstErr);
                        leftover = CollectLeftover(tr, space, spaceLabel, processedBlocks);
                    }
                    finally
                    {
                        foreach (ObjectId lid in unlocked)
                        {
                            try
                            {
                                LayerTableRecord ltr = (LayerTableRecord)tr.GetObject(lid, OpenMode.ForRead);
                                if (ltr.IsLocked) continue;
                                if (!ltr.IsWriteEnabled) ltr.UpgradeOpen();
                                ltr.IsLocked = true;
                            }
                            catch (System.Exception ex)
                            {
                                NoteErr(ref errN, ref firstErr, "Katman kilidi", ex);
                            }
                        }
                    }
                    tr.Commit();
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nZSIFIRLA başarısız, çizim değişmedi: " + ex.Message + "\n");
                return;
            }

            double prevElev = 0;
            string elevErr = null;
            try
            {
                prevElev = SysDouble("ELEVATION");
                if (Math.Abs(prevElev) > Tol)
                    Application.SetSystemVariable("ELEVATION", 0.0);
            }
            catch (System.Exception ex) { elevErr = ex.Message; }

            string msg = "\nZSIFIRLA (" + spaceLabel + "): " + fixedN + " nesne düzeltildi. "
                + "Elevation sıfırlanan: " + elevN + ". "
                + "Thickness sıfırlanan: " + thickN + ". "
                + "Normal düzlenen/izdüşürülen: " + projN + ". "
                + "Atlanan (düşey): " + skipN + ". "
                + "Kilitli katmandaki: " + lockedN + ". "
                + "Blok tanımı: " + blocksN + ". "
                + "Blok içi düzeltilen: " + blockFixed + ". "
                + "Taranan: " + total + ".";
            if (errN > 0) msg += " Hata: " + errN + ".";
            ed.WriteMessage(msg);
            if (firstErr != null) ed.WriteMessage("\nİlk hata: " + firstErr);
            if (!string.IsNullOrEmpty(leftover)) ed.WriteMessage("\n" + leftover);
            if (elevErr != null)
                ed.WriteMessage("\nELEVATION sistem değişkeni ayarlanamadı: " + elevErr);
            else if (Math.Abs(prevElev) > Tol)
                ed.WriteMessage("\nELEVATION sistem değişkeni " + Fmt(prevElev) + " değerinden 0 yapıldı.");
            else
                ed.WriteMessage("\nELEVATION sistem değişkeni 0.");
            if (!IsWorldUcs())
                ed.WriteMessage("\nUCS Dünya değil; orijin Z = " + Fmt(UcsOriginZ(ed)) + " (UCS değiştirilmedi).");
            ed.WriteMessage("\n");
            try { ed.Regen(); } catch (System.Exception) { }
        }

        static void NoteErr(ref int errN, ref string firstErr, Entity ent, System.Exception ex)
        {
            errN++;
            if (firstErr != null) return;
            string kind = ent.GetType().Name;
            try { kind = ent.GetRXClass().Name; } catch (System.Exception) { }
            firstErr = kind + " " + ent.Handle + Detail(ex);
        }

        static void NoteErr(ref int errN, ref string firstErr, string kind, System.Exception ex)
        {
            errN++;
            if (firstErr != null) return;
            firstErr = kind + Detail(ex);
        }

        static string Detail(System.Exception ex)
        {
            Autodesk.AutoCAD.Runtime.Exception rex = ex as Autodesk.AutoCAD.Runtime.Exception;
            string status = rex != null ? " (" + rex.ErrorStatus + ")" : "";
            return status + ": " + ex.Message;
        }

        static string SpaceLabel(BlockTableRecord space)
        {
            if (space != null && string.Equals(space.Name, BlockTableRecord.ModelSpace, StringComparison.OrdinalIgnoreCase))
                return "Model";
            try
            {
                string lay = LayoutManager.Current.CurrentLayout;
                if (!string.IsNullOrEmpty(lay)) return "Pafta " + lay;
            }
            catch (System.Exception) { }
            return "Kağıt alanı";
        }

        static List<ObjectId> LockedLayers(Transaction tr, Database db)
        {
            List<ObjectId> locked = new List<ObjectId>();
            LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            foreach (ObjectId id in lt)
            {
                LayerTableRecord ltr = tr.GetObject(id, OpenMode.ForRead) as LayerTableRecord;
                if (ltr != null && ltr.IsLocked) locked.Add(id);
            }
            return locked;
        }

        /// <summary>Queue the definition a reference uses, plus the dynamic master and every anonymous *U copy.</summary>
        static void EnqueueBlockRef(Transaction tr, BlockReference br, HashSet<ObjectId> seen, Queue<ObjectId> queue)
        {
            if (br == null) return;
            try { EnqueueDefinition(tr, br.BlockTableRecord, seen, queue); } catch (System.Exception) { }
            try
            {
                if (!br.IsDynamicBlock) return;
                ObjectId dyn = br.DynamicBlockTableRecord;
                EnqueueDefinition(tr, dyn, seen, queue);
                EnqueueAnonymousCopies(tr, dyn, seen, queue);
            }
            catch (System.Exception) { }
        }

        static void EnqueueAnonymousCopies(Transaction tr, ObjectId dynId, HashSet<ObjectId> seen, Queue<ObjectId> queue)
        {
            BlockTableRecord dyn = tr.GetObject(dynId, OpenMode.ForRead) as BlockTableRecord;
            if (dyn == null) return;
            bool isDyn = false;
            try { isDyn = dyn.IsDynamicBlock; } catch (System.Exception) { return; }
            if (!isDyn) return;
            ObjectIdCollection copies = dyn.GetAnonymousBlockIds();
            if (copies == null) return;
            foreach (ObjectId id in copies) EnqueueDefinition(tr, id, seen, queue);
        }

        /// <summary>Skip layouts and true xrefs. Bound and normal blocks are processed. Origin Z is cleared later.</summary>
        static void EnqueueDefinition(Transaction tr, ObjectId id, HashSet<ObjectId> seen, Queue<ObjectId> queue)
        {
            if (id.IsNull || !id.IsValid || !seen.Add(id)) return;
            BlockTableRecord btr = tr.GetObject(id, OpenMode.ForRead) as BlockTableRecord;
            if (btr == null || SkipDefinition(btr))
                return;
            queue.Enqueue(id);
        }

        static bool SkipDefinition(BlockTableRecord btr)
        {
            if (btr.IsLayout || btr.IsFromExternalReference || btr.IsDependent) return true;
            try { if (btr.IsFromOverlayReference) return true; } catch (System.Exception) { }
            return false;
        }

        static void FlattenDefinitions(Transaction tr, HashSet<ObjectId> lockedSet, Queue<ObjectId> queue, HashSet<ObjectId> seen, List<ObjectId> processed,
            ref int blocksN, ref int blockFixed, ref int lockedN, ref int elevN, ref int thickN, ref int projN, ref int skipN, ref int errN, ref string firstErr)
        {
            while (queue.Count > 0)
            {
                ObjectId bid = queue.Dequeue();
                BlockTableRecord btr = null;
                try { btr = tr.GetObject(bid, OpenMode.ForRead) as BlockTableRecord; }
                catch (System.Exception ex) { NoteErr(ref errN, ref firstErr, "Blok", ex); continue; }
                if (btr == null || SkipDefinition(btr)) continue;
                blocksN++;
                processed.Add(bid);
                Write(btr);
                int fixedHere = 0;
                try
                {
                    Point3d org = btr.Origin;
                    if (Math.Abs(org.Z) > Tol)
                    {
                        btr.Origin = new Point3d(org.X, org.Y, 0.0);
                        blockFixed++;
                        fixedHere++;
                    }
                }
                catch (System.Exception ex) { NoteErr(ref errN, ref firstErr, "Blok origin", ex); }
                foreach (ObjectId eid in btr)
                {
                    if (!eid.IsValid || eid.IsErased) continue;
                    Entity ent = null;
                    try { ent = tr.GetObject(eid, OpenMode.ForRead) as Entity; }
                    catch (System.Exception ex) { NoteErr(ref errN, ref firstErr, "Blok varlığı", ex); continue; }
                    if (ent == null || ent.IsErased) continue;
                    if (!ent.LayerId.IsNull && lockedSet.Contains(ent.LayerId)) lockedN++;
                    try
                    {
                        int kind = Flatten(tr, ent, ref elevN, ref thickN, ref projN);
                        if (kind == 1) { blockFixed++; fixedHere++; Touch(ent); }
                        else if (kind == 2) skipN++;
                    }
                    catch (System.Exception ex) { NoteErr(ref errN, ref firstErr, ent, ex); }
                    BlockReference nested = ent as BlockReference;
                    if (nested == null) continue;
                    int before = blockFixed;
                    FlattenAttributes(tr, nested, lockedSet, ref blockFixed, ref lockedN, ref elevN, ref thickN, ref projN, ref skipN, ref errN, ref firstErr, true);
                    if (blockFixed > before) fixedHere += blockFixed - before;
                    EnqueueBlockRef(tr, nested, seen, queue);
                }
                if (fixedHere > 0) TouchReferences(tr, btr);
            }
        }

        static void FlattenAttributes(Transaction tr, BlockReference br, HashSet<ObjectId> lockedSet,
            ref int fixedN, ref int lockedN, ref int elevN, ref int thickN, ref int projN, ref int skipN, ref int errN, ref string firstErr, bool markGraphics)
        {
            if (br == null || br.AttributeCollection == null) return;
            foreach (ObjectId aid in br.AttributeCollection)
            {
                if (!aid.IsValid || aid.IsErased) continue;
                AttributeReference ar = null;
                try { ar = tr.GetObject(aid, OpenMode.ForRead) as AttributeReference; }
                catch (System.Exception ex) { NoteErr(ref errN, ref firstErr, "Attribute", ex); continue; }
                if (ar == null || ar.IsErased) continue;
                if (!ar.LayerId.IsNull && lockedSet.Contains(ar.LayerId)) lockedN++;
                try
                {
                    int kind = Flatten(tr, ar, ref elevN, ref thickN, ref projN);
                    if (kind == 1)
                    {
                        fixedN++;
                        if (markGraphics) Touch(ar);
                    }
                    else if (kind == 2) skipN++;
                }
                catch (System.Exception ex) { NoteErr(ref errN, ref firstErr, ar, ex); }
            }
        }

        static void Touch(Entity ent)
        {
            try
            {
                if (ent == null) return;
                if (!ent.IsWriteEnabled) ent.UpgradeOpen();
                ent.RecordGraphicsModified(true);
            }
            catch (System.Exception) { }
        }

        static void TouchReferences(Transaction tr, BlockTableRecord btr)
        {
            try
            {
                ObjectIdCollection refs = btr.GetBlockReferenceIds(false, true);
                if (refs == null) return;
                foreach (ObjectId rid in refs)
                {
                    Entity r = tr.GetObject(rid, OpenMode.ForRead) as Entity;
                    Touch(r);
                }
            }
            catch (System.Exception) { }
        }

        /// <summary>1 = modified, 2 = skipped (vertical line/ray), 0 = already flat.</summary>
        static int Flatten(Transaction tr, Entity ent, ref int elevN, ref int thickN, ref int projN)
        {
            if (ent is Line) return FlattenLine((Line)ent, ref elevN, ref thickN);
            if (ent is Polyline) return FlattenLw((Polyline)ent, ref elevN, ref thickN, ref projN);
            if (ent is Polyline2d) return FlattenP2(tr, (Polyline2d)ent, ref elevN, ref thickN, ref projN);
            if (ent is Polyline3d) return FlattenP3(tr, (Polyline3d)ent, ref elevN, ref thickN);
            if (ent is Arc) return FlattenArc((Arc)ent, ref elevN, ref thickN, ref projN);
            if (ent is Circle) return FlattenCircle((Circle)ent, ref elevN, ref thickN, ref projN);
            if (ent is Ellipse) return FlattenEllipse((Ellipse)ent, ref elevN, ref thickN, ref projN);
            if (ent is Spline) return FlattenSpline((Spline)ent, ref elevN, ref thickN);
            if (ent is DBPoint) return FlattenDbPoint((DBPoint)ent, ref elevN, ref thickN, ref projN);
            if (ent is DBText) return FlattenText((DBText)ent, ref elevN, ref thickN, ref projN);
            if (ent is MText) return FlattenMText((MText)ent, ref elevN, ref thickN, ref projN);
            if (ent is MLeader) return FlattenMLeader((MLeader)ent, ref elevN, ref thickN, ref projN);
            if (ent is Leader) return FlattenLeader((Leader)ent, ref elevN, ref thickN, ref projN);
            if (ent is Dimension) return FlattenDim((Dimension)ent, ref elevN, ref thickN, ref projN);
            if (ent is Hatch) return FlattenHatch((Hatch)ent, ref elevN, ref thickN, ref projN);
            if (ent is BlockReference) return FlattenBlock((BlockReference)ent, ref elevN, ref thickN, ref projN);
            if (ent is Face) return FlattenFace((Face)ent);
            if (ent is Solid) return FlattenSolid((Solid)ent, ref thickN, ref projN);
            if (ent is Trace) return FlattenTrace((Trace)ent, ref thickN, ref projN);
            if (ent is Region) return FlattenRegion((Region)ent);
            if (ent is Viewport) return FlattenViewport((Viewport)ent, ref elevN, ref thickN);
            if (ent is Ray) return FlattenRay((Ray)ent, ref elevN, ref thickN);
            if (ent is Xline) return FlattenXline((Xline)ent, ref elevN, ref thickN);
            if (ent is Mline) return FlattenMline((Mline)ent, ref elevN, ref thickN, ref projN);
            return FlattenFallback(ent, ref elevN, ref thickN, ref projN);
        }

        static int FlattenLine(Line ln, ref int elevN, ref int thickN)
        {
            Point3d a = ln.StartPoint, b = ln.EndPoint;
            bool vertical = Math.Abs(a.X - b.X) <= Tol && Math.Abs(a.Y - b.Y) <= Tol && Math.Abs(a.Z - b.Z) > Tol;
            if (vertical) return 2;
            bool changed = false;
            if (Math.Abs(a.Z) > Tol || Math.Abs(b.Z) > Tol)
            {
                Write(ln);
                ln.StartPoint = Flat(a);
                ln.EndPoint = Flat(b);
                changed = true;
            }
            if (ApplyThick(ln, ref thickN)) changed = true;
            return changed ? 1 : 0;
        }

        /// <summary>
        /// Read WCS vertices first, then force Normal = +Z and Elevation = 0 and write the plan points back.
        /// A negative Z normal negates bulges so the plan curve does not flip.
        /// </summary>
        static int FlattenLw(Polyline pl, ref int elevN, ref int thickN, ref int projN)
        {
            Vector3d n = pl.Normal;
            bool project = !IsExactPositiveZ(n);
            bool negateBulge = n.Z < 0;
            int nv = pl.NumberOfVertices;
            Point2d[] xy = new Point2d[nv];
            double[] bulge = new double[nv];
            double[] sw = new double[nv];
            double[] ew = new double[nv];
            bool anyZ = Math.Abs(pl.Elevation) > Tol;
            for (int i = 0; i < nv; i++)
            {
                Point3d w = pl.GetPoint3dAt(i);
                xy[i] = new Point2d(w.X, w.Y);
                if (Math.Abs(w.Z) > Tol) anyZ = true;
                double b = pl.GetBulgeAt(i);
                bulge[i] = negateBulge ? -b : b;
                sw[i] = pl.GetStartWidthAt(i);
                ew[i] = pl.GetEndWidthAt(i);
            }
            double cw = pl.ConstantWidth;
            bool hasCw = Math.Abs(cw) > Tol;
            bool hadElev = Math.Abs(pl.Elevation) > Tol;
            bool hadThick = Math.Abs(pl.Thickness) > Tol;
            if (!project && !anyZ && !hadElev && !hadThick) return 0;

            Write(pl);
            pl.Normal = Vector3d.ZAxis;
            pl.Elevation = 0.0;
            for (int i = 0; i < nv; i++)
            {
                pl.SetPointAt(i, xy[i]);
                pl.SetBulgeAt(i, bulge[i]);
                if (!hasCw)
                {
                    pl.SetStartWidthAt(i, sw[i]);
                    pl.SetEndWidthAt(i, ew[i]);
                }
            }
            if (hasCw) pl.ConstantWidth = cw;
            pl.Normal = Vector3d.ZAxis;
            pl.Elevation = 0.0;
            if (hadThick) { pl.Thickness = 0.0; thickN++; }
            if (hadElev) elevN++;
            if (project) projN++;
            return 1;
        }

        static int FlattenP2(Transaction tr, Polyline2d pl, ref int elevN, ref int thickN, ref int projN)
        {
            Vector3d n = pl.Normal;
            bool project = !IsExactPositiveZ(n);
            bool negateBulge = n.Z < 0;
            double elev = pl.Elevation;
            Matrix3d toWcs = Matrix3d.PlaneToWorld(n);
            List<ObjectId> ids = new List<ObjectId>();
            List<Point2d> xy = new List<Point2d>();
            List<double> bulge = new List<double>();
            List<double> sw = new List<double>();
            List<double> ew = new List<double>();
            bool anyZ = Math.Abs(elev) > Tol;
            foreach (ObjectId vid in pl)
            {
                Vertex2d v = tr.GetObject(vid, OpenMode.ForRead) as Vertex2d;
                if (v == null) continue;
                // Vertex2d.Position is OCS. Elevation is the plane Z; some drawings also store Z on the vertex.
                double vz = v.Position.Z;
                if (Math.Abs(vz) > Tol) anyZ = true;
                double planeZ = Math.Abs(vz) > Tol ? vz : elev;
                Point3d wcs = new Point3d(v.Position.X, v.Position.Y, planeZ).TransformBy(toWcs);
                if (Math.Abs(wcs.Z) > Tol) anyZ = true;
                ids.Add(vid);
                xy.Add(new Point2d(wcs.X, wcs.Y));
                bulge.Add(negateBulge ? -v.Bulge : v.Bulge);
                sw.Add(v.StartWidth);
                ew.Add(v.EndWidth);
            }
            bool hadElev = Math.Abs(elev) > Tol;
            bool hadThick = Math.Abs(pl.Thickness) > Tol;
            if (!project && !anyZ && !hadElev && !hadThick) return 0;

            Write(pl);
            pl.Normal = Vector3d.ZAxis;
            pl.Elevation = 0.0;
            for (int i = 0; i < ids.Count; i++)
            {
                Vertex2d v = (Vertex2d)tr.GetObject(ids[i], OpenMode.ForWrite);
                v.Position = new Point3d(xy[i].X, xy[i].Y, 0);
                v.Bulge = bulge[i];
                v.StartWidth = sw[i];
                v.EndWidth = ew[i];
            }
            pl.Elevation = 0.0;
            if (hadThick) { pl.Thickness = 0.0; thickN++; }
            if (hadElev) elevN++;
            if (project) projN++;
            return 1;
        }

        static int FlattenP3(Transaction tr, Polyline3d pl, ref int elevN, ref int thickN)
        {
            bool changed = false;
            foreach (ObjectId vid in pl)
            {
                PolylineVertex3d v = tr.GetObject(vid, OpenMode.ForRead) as PolylineVertex3d;
                if (v == null) continue;
                Point3d p = v.Position;
                if (Math.Abs(p.Z) <= Tol) continue;
                Write(pl);
                if (!v.IsWriteEnabled) v.UpgradeOpen();
                v.Position = Flat(p);
                changed = true;
            }
            if (ApplyThick(pl, ref thickN)) changed = true;
            if (ApplyElev(pl, ref elevN)) changed = true;
            return changed ? 1 : 0;
        }

        static int FlattenArc(Arc arc, ref int elevN, ref int thickN, ref int projN)
        {
            Vector3d n = arc.Normal;
            bool project = !IsExactPositiveZ(n);
            Point3d c = arc.Center;
            Point3d s = arc.StartPoint;
            Point3d e = arc.EndPoint;
            double rad = arc.Radius;
            double a0 = Math.Atan2(s.Y - c.Y, s.X - c.X);
            double a1 = Math.Atan2(e.Y - c.Y, e.X - c.X);
            // Negative normal: CCW around -Z is CW in plan. Swap so the bulge side stays.
            if (n.Z < 0) { double tmp = a0; a0 = a1; a1 = tmp; }
            bool hadThick = Math.Abs(arc.Thickness) > Tol;
            if (!project && Math.Abs(c.Z) <= Tol && !hadThick) return 0;
            Write(arc);
            if (project) { arc.Normal = Vector3d.ZAxis; projN++; }
            arc.Center = new Point3d(c.X, c.Y, 0);
            arc.Radius = rad;
            arc.StartAngle = a0;
            arc.EndAngle = a1;
            if (hadThick) { arc.Thickness = 0.0; thickN++; }
            if (ApplyElev(arc, ref elevN)) { }
            return 1;
        }

        static int FlattenCircle(Circle c, ref int elevN, ref int thickN, ref int projN)
        {
            Vector3d n = c.Normal;
            bool project = !IsExactPositiveZ(n);
            Point3d cen = c.Center;
            double rad = c.Radius;
            bool hadThick = Math.Abs(c.Thickness) > Tol;
            if (!project && Math.Abs(cen.Z) <= Tol && !hadThick) return 0;
            Write(c);
            if (project) { c.Normal = Vector3d.ZAxis; projN++; }
            c.Center = new Point3d(cen.X, cen.Y, 0);
            c.Radius = rad;
            if (hadThick) { c.Thickness = 0.0; thickN++; }
            if (ApplyElev(c, ref elevN)) { }
            return 1;
        }

        static int FlattenEllipse(Ellipse el, ref int elevN, ref int thickN, ref int projN)
        {
            Vector3d n = el.Normal;
            bool project = !IsExactPositiveZ(n);
            Point3d c = el.Center;
            Vector3d maj = el.MajorAxis;
            double ratio = el.RadiusRatio;
            double sa = el.StartAngle, ea = el.EndAngle;
            if (n.Z < 0) { double tmp = sa; sa = ea; ea = tmp; }
            double majorR = maj.Length;
            Vector3d flat = new Vector3d(maj.X, maj.Y, 0);
            if (flat.Length <= Tol)
            {
                Vector3d minor = el.MinorAxis;
                flat = new Vector3d(minor.X, minor.Y, 0);
            }
            if (flat.Length <= Tol) flat = Vector3d.XAxis;
            if (!project && Math.Abs(c.Z) <= Tol) return 0;
            Write(el);
            if (project) projN++;
            el.Set(new Point3d(c.X, c.Y, 0), Vector3d.ZAxis, flat.GetNormal() * (majorR > Tol ? majorR : flat.Length), ratio, sa, ea);
            if (ApplyThick(el, ref thickN)) { }
            if (ApplyElev(el, ref elevN)) { }
            return 1;
        }

        static int FlattenSpline(Spline spl, ref int elevN, ref int thickN)
        {
            bool changed = false;
            if (spl.HasFitData)
            {
                int n = spl.NumFitPoints;
                for (int i = 0; i < n; i++)
                {
                    Point3d p = spl.GetFitPointAt(i);
                    if (Math.Abs(p.Z) <= Tol) continue;
                    Write(spl);
                    spl.SetFitPointAt(i, Flat(p));
                    changed = true;
                }
            }

            bool tangentOffPlane = false;
            if (spl.HasFitData)
            {
                try
                {
                    Vector3d st = spl.StartFitTangent, et = spl.EndFitTangent;
                    tangentOffPlane = Math.Abs(st.Z) > Tol || Math.Abs(et.Z) > Tol;
                }
                catch (System.Exception) { }
            }

            if (!spl.HasFitData)
            {
                if (FlattenControls(spl)) changed = true;
            }
            else if (tangentOffPlane)
            {
                Write(spl);
                spl.PurgeFitData();
                if (FlattenControls(spl)) changed = true;
            }
            if (ApplyThick(spl, ref thickN)) changed = true;
            if (ApplyElev(spl, ref elevN)) changed = true;
            return changed ? 1 : 0;
        }

        static bool FlattenControls(Spline spl)
        {
            bool changed = false;
            int n = spl.NumControlPoints;
            for (int i = 0; i < n; i++)
            {
                Point3d p = spl.GetControlPointAt(i);
                if (Math.Abs(p.Z) <= Tol) continue;
                Write(spl);
                spl.SetControlPointAt(i, Flat(p));
                changed = true;
            }
            return changed;
        }

        static int FlattenDbPoint(DBPoint pt, ref int elevN, ref int thickN, ref int projN)
        {
            bool project = !IsExactPositiveZ(pt.Normal);
            bool changed = Math.Abs(pt.Position.Z) > Tol || project;
            if (!changed && Math.Abs(pt.Thickness) <= Tol) return 0;
            Write(pt);
            if (project) { pt.Normal = Vector3d.ZAxis; projN++; }
            pt.Position = Flat(pt.Position);
            if (ApplyThick(pt, ref thickN)) changed = true;
            if (ApplyElev(pt, ref elevN)) changed = true;
            return changed ? 1 : 0;
        }

        static int FlattenText(DBText t, ref int elevN, ref int thickN, ref int projN)
        {
            Vector3d n = t.Normal;
            bool project = !IsExactPositiveZ(n);
            Point3d p = t.Position;
            double rot = t.Rotation;
            double newRot = PlanAngle(n, rot);
            bool aligned = t.HorizontalMode != TextHorizontalMode.TextLeft || t.VerticalMode != TextVerticalMode.TextBase;
            Point3d a = Point3d.Origin;
            bool alignZ = false;
            if (aligned)
            {
                a = t.AlignmentPoint;
                alignZ = Math.Abs(a.Z) > Tol;
            }
            bool hadThick = Math.Abs(t.Thickness) > Tol;
            if (!project && Math.Abs(p.Z) <= Tol && !alignZ && !hadThick && AngClose(rot, newRot)) return 0;
            Write(t);
            if (project) { t.Normal = Vector3d.ZAxis; projN++; }
            t.Position = Flat(p);
            if (aligned) t.AlignmentPoint = Flat(a);
            if (!AngClose(rot, newRot)) t.Rotation = newRot;
            if (hadThick) { t.Thickness = 0.0; thickN++; }
            if (ApplyElev(t, ref elevN)) { }
            return 1;
        }

        static int FlattenMText(MText mt, ref int elevN, ref int thickN, ref int projN)
        {
            Vector3d n = mt.Normal;
            bool project = !IsExactPositiveZ(n);
            Point3d loc = mt.Location;
            Vector3d dir = mt.Direction;
            Vector3d flatDir = new Vector3d(dir.X, dir.Y, 0);
            if (!project && Math.Abs(loc.Z) <= Tol && Math.Abs(dir.Z) <= Tol) return 0;
            Write(mt);
            if (project) { mt.Normal = Vector3d.ZAxis; projN++; }
            mt.Location = Flat(loc);
            if (flatDir.Length > Tol) mt.Direction = flatDir.GetNormal();
            if (ApplyElev(mt, ref elevN)) { }
            if (ApplyThick(mt, ref thickN)) { }
            return 1;
        }

        static int FlattenBlock(BlockReference br, ref int elevN, ref int thickN, ref int projN)
        {
            // Insertion Z in this owner (model space or a parent block). Definition Origin is handled separately.
            Vector3d n = br.Normal;
            bool project = !IsExactPositiveZ(n);
            Point3d p = br.Position;
            double rot = br.Rotation;
            double newRot = PlanAngle(n, rot);
            if (!project && Math.Abs(p.Z) <= Tol && AngClose(rot, newRot)) return 0;
            Write(br);
            if (project) { br.Normal = Vector3d.ZAxis; projN++; }
            br.Position = Flat(p);
            if (!AngClose(rot, newRot)) br.Rotation = newRot;
            if (ApplyElev(br, ref elevN)) { }
            if (ApplyThick(br, ref thickN)) { }
            return 1;
        }

        static int FlattenMLeader(MLeader ml, ref int elevN, ref int thickN, ref int projN)
        {
            Vector3d n = ml.Normal;
            bool project = !IsExactPositiveZ(n);
            bool changed = project;
            if (project)
            {
                Write(ml);
                // MLeader.Normal is read-only; vertices/text are flattened below.
                projN++;
            }
            ArrayList leaders = null;
            try { leaders = ml.GetLeaderIndexes(); } catch (System.Exception) { }
            foreach (int leaderIndex in Ints(leaders))
            {
                ArrayList lines = null;
                try { lines = ml.GetLeaderLineIndexes(leaderIndex); } catch (System.Exception) { continue; }
                foreach (int line in Ints(lines))
                {
                    int vc;
                    try { vc = ml.VerticesCount(line); } catch (System.Exception) { continue; }
                    for (int k = 0; k < vc; k++)
                    {
                        Point3d p = ml.GetVertex(line, k);
                        if (Math.Abs(p.Z) <= Tol) continue;
                        Write(ml);
                        ml.SetVertex(line, k, Flat(p));
                        changed = true;
                    }
                }
            }
            if (ml.ContentType == ContentType.MTextContent)
                changed |= MovePoint(ml, ml.TextLocation, delegate (Point3d p) { ml.TextLocation = p; });
            else if (ml.ContentType == ContentType.BlockContent)
                changed |= MovePoint(ml, ml.BlockPosition, delegate (Point3d p) { ml.BlockPosition = p; });
            else if (ml.ContentType == ContentType.ToleranceContent)
                changed |= MovePointProp(ml, "ToleranceLocation");
            if (ApplyElev(ml, ref elevN)) changed = true;
            if (ApplyThick(ml, ref thickN)) changed = true;
            return changed ? 1 : 0;
        }

        static int FlattenLeader(Leader ld, ref int elevN, ref int thickN, ref int projN)
        {
            Vector3d n;
            bool hasN = TryNormal(ld, out n);
            bool project = hasN && !IsExactPositiveZ(n);
            bool changed = project;
            if (project) { Write(ld); SetNormal(ld, Vector3d.ZAxis); projN++; }
            int nv = ld.NumVertices;
            for (int i = 0; i < nv; i++)
            {
                Point3d p = ld.VertexAt(i);
                if (Math.Abs(p.Z) <= Tol) continue;
                Write(ld);
                ld.SetVertexAt(i, Flat(p));
                changed = true;
            }
            if (ApplyElev(ld, ref elevN)) changed = true;
            if (ApplyThick(ld, ref thickN)) changed = true;
            return changed ? 1 : 0;
        }

        static int FlattenDim(Dimension dim, ref int elevN, ref int thickN, ref int projN)
        {
            Vector3d n = dim.Normal;
            bool project = !IsExactPositiveZ(n);
            double elev = dim.Elevation;
            bool hadElev = Math.Abs(elev) > Tol;
            bool skipText = false;
            try { skipText = dim.UsingDefaultTextPosition; } catch (System.Exception) { }
            Matrix3d toWcs = Matrix3d.PlaneToWorld(n);
            List<PropertyInfo> props = new List<PropertyInfo>();
            List<Point3d> pts = new List<Point3d>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (PropertyInfo pi in dim.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (pi.PropertyType != typeof(Point3d) || !pi.CanRead || !pi.CanWrite) continue;
                if (pi.GetIndexParameters().Length != 0) continue;
                if (!seen.Add(pi.Name)) continue;
                if (skipText && pi.Name == "TextPosition") continue;
                if (pi.Name.IndexOf("Block", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                Point3d p;
                try { p = (Point3d)pi.GetValue(dim, null); }
                catch (System.Exception) { continue; }
                double z = Math.Abs(p.Z) > Tol ? p.Z : elev;
                Point3d wcs = new Point3d(p.X, p.Y, z).TransformBy(toWcs);
                props.Add(pi);
                pts.Add(new Point3d(wcs.X, wcs.Y, 0));
            }
            bool moved = project || hadElev;
            for (int i = 0; i < pts.Count; i++)
            {
                Point3d cur;
                try { cur = (Point3d)props[i].GetValue(dim, null); }
                catch (System.Exception) { continue; }
                if (cur.DistanceTo(pts[i]) > Tol) moved = true;
            }
            if (!moved) return 0;
            Write(dim);
            if (project) { dim.Normal = Vector3d.ZAxis; projN++; }
            dim.Elevation = 0.0;
            if (hadElev) elevN++;
            for (int i = 0; i < props.Count; i++)
                props[i].SetValue(dim, pts[i], null);
            if (ApplyThick(dim, ref thickN)) { }
            return 1;
        }

        static int FlattenHatch(Hatch h, ref int elevN, ref int thickN, ref int projN)
        {
            Vector3d n = h.Normal;
            bool project = !IsExactPositiveZ(n);
            double elev = h.Elevation;
            bool hadElev = Math.Abs(elev) > Tol;
            if (!project && !hadElev) return 0;
            bool assoc = false;
            try { assoc = h.Associative; } catch (System.Exception) { }
            if (project && !assoc) ProjectHatchLoops(h, n, elev);
            Write(h);
            h.Normal = Vector3d.ZAxis;
            h.Elevation = 0.0;
            if (project) projN++;
            if (hadElev) elevN++;
            try { h.EvaluateHatch(true); } catch (System.Exception) { }
            if (ApplyThick(h, ref thickN)) { }
            return 1;
        }

        /// <summary>Rewrite polyline hatch loops into WCS XY before the normal is changed. Curve loops are left for the normal snap.</summary>
        static void ProjectHatchLoops(Hatch h, Vector3d normal, double elevation)
        {
            int n = h.NumberOfLoops;
            if (n <= 0) return;
            List<HatchLoopTypes> types = new List<HatchLoopTypes>();
            List<Point2dCollection> polys = new List<Point2dCollection>();
            List<DoubleCollection> bulges = new List<DoubleCollection>();
            Matrix3d toWcs = Matrix3d.PlaneToWorld(normal);
            for (int i = 0; i < n; i++)
            {
                HatchLoop loop = h.GetLoopAt(i);
                types.Add(loop.LoopType);
                if (!loop.IsPolyline || loop.Polyline == null) return;
                Point2dCollection pts = new Point2dCollection();
                DoubleCollection bs = new DoubleCollection();
                foreach (BulgeVertex bv in loop.Polyline)
                {
                    Point3d wcs = new Point3d(bv.Vertex.X, bv.Vertex.Y, elevation).TransformBy(toWcs);
                    pts.Add(new Point2d(wcs.X, wcs.Y));
                    bs.Add(normal.Z < 0 ? -bv.Bulge : bv.Bulge);
                }
                polys.Add(pts);
                bulges.Add(bs);
            }
            for (int i = n - 1; i >= 0; i--) h.RemoveLoopAt(i);
            for (int i = 0; i < n; i++) h.AppendLoop(types[i], polys[i], bulges[i]);
        }

        static int FlattenViewport(Viewport vp, ref int elevN, ref int thickN)
        {
            // Paper-space center only. ViewTarget is the model view and must stay.
            bool changed = false;
            Point3d c = vp.CenterPoint;
            if (Math.Abs(c.Z) > Tol)
            {
                Write(vp);
                vp.CenterPoint = Flat(c);
                changed = true;
            }
            if (ApplyElev(vp, ref elevN)) changed = true;
            if (ApplyThick(vp, ref thickN)) changed = true;
            return changed ? 1 : 0;
        }

        static int FlattenRay(Ray ray, ref int elevN, ref int thickN)
        {
            return FlattenInfinite(ray, ray.BasePoint, delegate (Point3d p) { ray.BasePoint = p; }, ray.UnitDir, delegate (Vector3d v) { ray.UnitDir = v; }, ref elevN, ref thickN);
        }

        static int FlattenXline(Xline xl, ref int elevN, ref int thickN)
        {
            return FlattenInfinite(xl, xl.BasePoint, delegate (Point3d p) { xl.BasePoint = p; }, xl.UnitDir, delegate (Vector3d v) { xl.UnitDir = v; }, ref elevN, ref thickN);
        }

        static int FlattenInfinite(Entity ent, Point3d basePt, Action<Point3d> setBase, Vector3d dir, Action<Vector3d> setDir, ref int elevN, ref int thickN)
        {
            double xy = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
            if (xy <= Tol) return 2;
            bool changed = false;
            if (Math.Abs(basePt.Z) > Tol)
            {
                Write(ent);
                setBase(Flat(basePt));
                changed = true;
            }
            if (Math.Abs(dir.Z) > Tol)
            {
                Write(ent);
                setDir(new Vector3d(dir.X, dir.Y, 0).GetNormal());
                changed = true;
            }
            if (ApplyElev(ent, ref elevN)) changed = true;
            if (ApplyThick(ent, ref thickN)) changed = true;
            return changed ? 1 : 0;
        }

        static int FlattenMline(Mline ml, ref int elevN, ref int thickN, ref int projN)
        {
            Vector3d n = ml.Normal;
            bool project = !IsExactPositiveZ(n);
            int nv = ml.NumberOfVertices;
            Point3d[] pts = new Point3d[nv];
            bool anyZ = false;
            for (int i = 0; i < nv; i++)
            {
                pts[i] = ml.VertexAt(i);
                if (Math.Abs(pts[i].Z) > Tol) anyZ = true;
            }
            if (!project && !anyZ) return 0;
            Write(ml);
            if (project) { ml.Normal = Vector3d.ZAxis; projN++; }
            for (int i = 0; i < nv; i++)
                ml.MoveVertexAt(i, new Point3d(pts[i].X, pts[i].Y, 0));
            if (nv > 0 && Math.Abs(ml.VertexAt(0).Z) > Tol)
                ml.TransformBy(Matrix3d.Displacement(new Vector3d(0, 0, -ml.VertexAt(0).Z)));
            if (ApplyElev(ml, ref elevN)) { }
            if (ApplyThick(ml, ref thickN)) { }
            return 1;
        }

        static void ForceInserts(Transaction tr, BlockTableRecord space, ref int fixedN, ref int elevN, ref int thickN, ref int projN, ref int errN, ref string firstErr)
        {
            foreach (ObjectId id in space)
            {
                if (!id.IsValid || id.IsErased) continue;
                BlockReference br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                if (br == null) continue;
                try
                {
                    int kind = Flatten(tr, br, ref elevN, ref thickN, ref projN);
                    if (kind == 1) { fixedN++; Touch(br); }
                }
                catch (System.Exception ex) { NoteErr(ref errN, ref firstErr, br, ex); }
            }
        }

        const double ReportZ = 1e-6;

        static string CollectLeftover(Transaction tr, BlockTableRecord space, string spaceLabel, List<ObjectId> blocks)
        {
            int count = 0;
            List<string> samples = new List<string>();
            List<string> inserts = new List<string>();
            ScanOwner(tr, space, spaceLabel, ref count, samples, inserts);
            foreach (ObjectId bid in blocks)
            {
                BlockTableRecord btr = tr.GetObject(bid, OpenMode.ForRead) as BlockTableRecord;
                if (btr == null) continue;
                string name = btr.Name;
                try { if (string.IsNullOrEmpty(name)) name = bid.Handle.ToString(); } catch (System.Exception) { }
                ScanOwner(tr, btr, name, ref count, samples, inserts);
            }
            if (count == 0) return "Kalan Z'li nesne: 0.";
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("Kalan Z'li nesne: ").Append(count).Append(".");
            int shown = 0;
            foreach (string s in samples)
            {
                if (shown >= 5) break;
                sb.Append("\n  ").Append(s);
                shown++;
            }
            foreach (string s in inserts)
            {
                if (shown >= 5) break;
                sb.Append("\n  ").Append(s);
                shown++;
            }
            return sb.ToString();
        }

        static void ScanOwner(Transaction tr, BlockTableRecord owner, string blockName, ref int count, List<string> samples, List<string> inserts)
        {
            foreach (ObjectId id in owner)
            {
                if (!id.IsValid || id.IsErased) continue;
                Entity ent = null;
                try { ent = tr.GetObject(id, OpenMode.ForRead) as Entity; }
                catch (System.Exception) { continue; }
                if (ent == null || ent.IsErased) continue;
                Extents3d ex;
                try { ex = ent.GeometricExtents; }
                catch (System.Exception) { continue; }
                if (Math.Abs(ex.MinPoint.Z) <= ReportZ && Math.Abs(ex.MaxPoint.Z) <= ReportZ) continue;
                count++;
                string type = ent.GetType().Name;
                try { string dxf = ent.GetRXClass().DxfName; if (!string.IsNullOrEmpty(dxf)) type = dxf; } catch (System.Exception) { }
                string layer = "";
                try { layer = ent.Layer; } catch (System.Exception) { }
                string line = "Blok " + blockName + ", " + type + ", katman " + layer + ", Z " + Fmt(ex.MinPoint.Z) + " .. " + Fmt(ex.MaxPoint.Z);
                BlockReference br = ent as BlockReference;
                if (br != null)
                {
                    try
                    {
                        BlockTableRecord def = tr.GetObject(br.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
                        if (def != null) line += " (referans " + def.Name + ")";
                    }
                    catch (System.Exception) { }
                    if (inserts.Count < 5) inserts.Add(line);
                }
                else if (samples.Count < 5) samples.Add(line);
            }
        }

        static int FlattenFace(Face face)
        {
            bool changed = false;
            for (short i = 0; i < 4; i++)
            {
                Point3d p = face.GetVertexAt(i);
                if (Math.Abs(p.Z) <= Tol) continue;
                Write(face);
                face.SetVertexAt(i, Flat(p));
                changed = true;
            }
            return changed ? 1 : 0;
        }

        static int FlattenSolid(Solid solid, ref int thickN, ref int projN)
        {
            return FlattenQuad(solid, solid.Normal, true,
                delegate (short i) { return solid.GetPointAt(i); },
                delegate (short i, Point3d p) { solid.SetPointAt(i, p); },
                delegate (Vector3d n) { solid.Normal = n; },
                ref thickN, ref projN);
        }

        static int FlattenTrace(Trace trace, ref int thickN, ref int projN)
        {
            return FlattenQuad(trace, trace.Normal, true,
                delegate (short i) { return trace.GetPointAt(i); },
                delegate (short i, Point3d p) { trace.SetPointAt(i, p); },
                delegate (Vector3d n) { trace.Normal = n; },
                ref thickN, ref projN);
        }

        static int FlattenQuad(Entity ent, Vector3d normal, bool ocs, Func<short, Point3d> getPt, Action<short, Point3d> setPt, Action<Vector3d> setNormal, ref int thickN, ref int projN)
        {
            bool project = !IsExactPositiveZ(normal);
            Matrix3d toWcs = Matrix3d.Identity;
            if (ocs)
            {
                try { toWcs = Matrix3d.PlaneToWorld(normal); }
                catch (System.Exception) { toWcs = Matrix3d.Identity; }
            }
            Point3d[] wcs = new Point3d[4];
            bool anyZ = false;
            for (short i = 0; i < 4; i++)
            {
                Point3d raw = getPt(i);
                Point3d w = ocs ? raw.TransformBy(toWcs) : raw;
                wcs[i] = w;
                if (Math.Abs(w.Z) > Tol) anyZ = true;
            }
            if (!project && !anyZ)
                return ApplyThick(ent, ref thickN) ? 1 : 0;
            Write(ent);
            if (project) { setNormal(Vector3d.ZAxis); projN++; }
            for (short i = 0; i < 4; i++) setPt(i, new Point3d(wcs[i].X, wcs[i].Y, 0));
            if (ApplyThick(ent, ref thickN)) { }
            return 1;
        }

        static int FlattenRegion(Region region)
        {
            Extents3d ex = region.GeometricExtents;
            double span = Math.Abs(ex.MaxPoint.Z - ex.MinPoint.Z);
            if (span <= Tol && Math.Abs(ex.MinPoint.Z) <= Tol) return 0;
            Write(region);
            if (span <= 1e-4)
                region.TransformBy(Matrix3d.Displacement(new Vector3d(0, 0, -ex.MinPoint.Z)));
            else
                region.TransformBy(ProjectToXY);
            return 1;
        }

        /// <summary>Last resort: snap a normal to +Z, zero elevation, and drop a single Z or project the entity.</summary>
        static int FlattenFallback(Entity ent, ref int elevN, ref int thickN, ref int projN)
        {
            Vector3d n;
            bool hasN = TryNormal(ent, out n);
            bool project = hasN && !IsExactPositiveZ(n);
            double elev;
            bool hasElev = TryReadDouble(ent, "Elevation", out elev);
            bool hadElev = hasElev && Math.Abs(elev) > Tol;
            bool extentsOk = false;
            double z = 0, span = 0;
            try
            {
                Extents3d ex = ent.GeometricExtents;
                z = ex.MinPoint.Z;
                span = Math.Abs(ex.MaxPoint.Z - ex.MinPoint.Z);
                extentsOk = true;
            }
            catch (System.Exception) { }

            bool flat = extentsOk && span <= Tol;
            bool needMove = (flat && Math.Abs(z) > Tol) || (extentsOk && span > Tol);
            if (!project && !hadElev && !needMove)
            {
                bool t = ApplyThick(ent, ref thickN);
                return t ? 1 : 0;
            }

            Write(ent);
            if (project && SetNormal(ent, Vector3d.ZAxis)) projN++;
            if (hadElev)
            {
                if (ZeroDoubleProp(ent, "Elevation")) elevN++;
                else if (ApplyElev(ent, ref elevN)) { }
            }
            if (flat && Math.Abs(z) > Tol && !(hadElev && Math.Abs(elev - z) <= 1e-6))
                ent.TransformBy(Matrix3d.Displacement(new Vector3d(0, 0, -z)));
            else if (extentsOk && span > Tol)
                ent.TransformBy(ProjectToXY);
            if (ApplyThick(ent, ref thickN)) { }
            return 1;
        }

        static Matrix3d ProjectToXY
        {
            get
            {
                return new Matrix3d(new double[]
                {
                    1, 0, 0, 0,
                    0, 1, 0, 0,
                    0, 0, 0, 0,
                    0, 0, 0, 1
                });
            }
        }

        static List<int> Ints(ArrayList list)
        {
            List<int> r = new List<int>();
            if (list == null) return r;
            foreach (object o in list)
            {
                if (o == null) continue;
                try { r.Add(Convert.ToInt32(o, CultureInfo.InvariantCulture)); }
                catch (System.Exception) { }
            }
            return r;
        }

        static bool MovePointProp(Entity ent, string name)
        {
            PropertyInfo p = ent.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (p == null || p.PropertyType != typeof(Point3d) || !p.CanRead || !p.CanWrite) return false;
            if (p.GetIndexParameters().Length != 0) return false;
            Point3d cur = (Point3d)p.GetValue(ent, null);
            if (Math.Abs(cur.Z) <= Tol) return false;
            Write(ent);
            p.SetValue(ent, Flat(cur), null);
            return true;
        }

        static bool TryReadDouble(Entity ent, string name, out double value)
        {
            value = 0;
            try
            {
                PropertyInfo p = Prop(ent, name);
                if (p == null) return false;
                value = (double)p.GetValue(ent, null);
                return true;
            }
            catch (System.Exception) { return false; }
        }

        static bool MovePoint(Entity ent, Point3d current, Action<Point3d> assign)
        {
            if (Math.Abs(current.Z) <= Tol) return false;
            Write(ent);
            assign(Flat(current));
            return true;
        }

        /// <summary>Sets Elevation to 0 and counts it. Failures propagate so the command can print them.</summary>
        static bool ApplyElev(Entity ent, ref int elevN)
        {
            PropertyInfo p = Prop(ent, "Elevation");
            if (p == null) return false;
            double v = (double)p.GetValue(ent, null);
            if (Math.Abs(v) <= Tol) return false;
            Write(ent);
            p.SetValue(ent, 0.0, null);
            elevN++;
            return true;
        }

        static bool ApplyThick(Entity ent, ref int thickN)
        {
            PropertyInfo p = Prop(ent, "Thickness");
            if (p == null) return false;
            double v = (double)p.GetValue(ent, null);
            if (Math.Abs(v) <= Tol) return false;
            Write(ent);
            p.SetValue(ent, 0.0, null);
            thickN++;
            return true;
        }

        static bool ZeroDoubleProp(Entity ent, string name)
        {
            PropertyInfo p = Prop(ent, name);
            if (p == null) return false;
            double v = (double)p.GetValue(ent, null);
            if (Math.Abs(v) <= Tol) return false;
            Write(ent);
            p.SetValue(ent, 0.0, null);
            return true;
        }

        static PropertyInfo Prop(Entity ent, string name)
        {
            PropertyInfo p = ent.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (p == null || p.PropertyType != typeof(double) || !p.CanRead || !p.CanWrite) return null;
            if (p.GetIndexParameters().Length != 0) return null;
            return p;
        }

        static bool SetNormal(Entity ent, Vector3d n)
        {
            PropertyInfo p = ent.GetType().GetProperty("Normal", BindingFlags.Instance | BindingFlags.Public);
            if (p == null || p.PropertyType != typeof(Vector3d) || !p.CanWrite) return false;
            if (p.GetIndexParameters().Length != 0) return false;
            Write(ent);
            p.SetValue(ent, n, null);
            return true;
        }

        static bool TryNormal(Entity ent, out Vector3d n)
        {
            n = Vector3d.ZAxis;
            try
            {
                PropertyInfo p = ent.GetType().GetProperty("Normal", BindingFlags.Instance | BindingFlags.Public);
                if (p == null || p.PropertyType != typeof(Vector3d) || !p.CanRead) return false;
                if (p.GetIndexParameters().Length != 0) return false;
                n = (Vector3d)p.GetValue(ent, null);
                return true;
            }
            catch (System.Exception) { return false; }
        }

        /// <summary>True only for a normal that is already +Z, not -Z and not a near miss.</summary>
        static bool IsExactPositiveZ(Vector3d n)
        {
            double len = n.Length;
            if (len <= Tol) return false;
            double x = n.X / len, y = n.Y / len, z = n.Z / len;
            return Math.Abs(x) <= 1e-8 && Math.Abs(y) <= 1e-8 && z > 0;
        }

        /// <summary>WCS plan angle of an OCS rotation about the given normal.</summary>
        static double PlanAngle(Vector3d normal, double ocsRotation)
        {
            Vector3d dir = new Vector3d(Math.Cos(ocsRotation), Math.Sin(ocsRotation), 0);
            try { dir = dir.TransformBy(Matrix3d.PlaneToWorld(normal)); }
            catch (System.Exception) { return ocsRotation; }
            if (dir.X * dir.X + dir.Y * dir.Y <= Tol * Tol) return ocsRotation;
            return Math.Atan2(dir.Y, dir.X);
        }

        static bool AngClose(double a, double b)
        {
            double d = a - b;
            while (d > Math.PI) d -= 2 * Math.PI;
            while (d < -Math.PI) d += 2 * Math.PI;
            return Math.Abs(d) <= 1e-9;
        }

        static Point3d Flat(Point3d p) { return new Point3d(p.X, p.Y, 0); }

        static void Write(DBObject obj)
        {
            if (!obj.IsWriteEnabled) obj.UpgradeOpen();
        }

        static double SysDouble(string name)
        {
            object v = Application.GetSystemVariable(name);
            if (v == null) return 0;
            return Convert.ToDouble(v, CultureInfo.InvariantCulture);
        }

        static bool IsWorldUcs()
        {
            try
            {
                object v = Application.GetSystemVariable("WORLDUCS");
                return Convert.ToInt32(v, CultureInfo.InvariantCulture) != 0;
            }
            catch (System.Exception) { return true; }
        }

        static double UcsOriginZ(Editor ed)
        {
            try { return ed.CurrentUserCoordinateSystem.CoordinateSystem3d.Origin.Z; }
            catch (System.Exception)
            {
                try
                {
                    object v = Application.GetSystemVariable("UCSORG");
                    if (v is Point3d) return ((Point3d)v).Z;
                }
                catch (System.Exception) { }
                return 0;
            }
        }

        static string Fmt(double v) { return v.ToString("0.###", CultureInfo.CurrentCulture); }
    }
}
