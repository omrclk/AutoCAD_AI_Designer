using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
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
    /// projected. Ordinary block definitions are flattened and their Origin Z is set to 0.
    /// Dynamic blocks and anonymous *U copies are not edited. Xrefs are not opened. One transaction, one UNDO.
    /// </summary>
    public class ZSifirlaCommands
    {
        const double Tol = 1e-8;
        const double ReportZ = 1e-6;
        static StreamWriter ZLog;

        [CommandMethod("ZSIFIRLA", CommandFlags.Modal | CommandFlags.NoBlockEditor | CommandFlags.Redraw)]
        [CommandMethod("AIDZSIFIRLA", CommandFlags.Modal | CommandFlags.NoBlockEditor | CommandFlags.Redraw)]
        public void ZSifirla()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            Database db = doc.Database;
            OpenZLog();
            try
            {
            LogStep("basla", "", ObjectId.Null, "ZSIFIRLA");
            int fixedN = 0, elevN = 0, thickN = 0, projN = 0, skipN = 0, lockedN = 0, errN = 0, total = 0;
            int blocksN = 0, blockFixed = 0, blockVisited = 0;
            string firstErr = null;
            string spaceLabel = "Model";
            string leftover = null;
            List<ObjectId> processedBlocks = new List<ObjectId>();
            try
            {
                using (doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    List<KeyValuePair<ObjectId, string>> spaces = new List<KeyValuePair<ObjectId, string>>();
                    spaces.Add(new KeyValuePair<ObjectId, string>(bt[BlockTableRecord.ModelSpace], "Model"));
                    try
                    {
                        DBDictionary lays = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                        foreach (DBDictionaryEntry de in lays)
                        {
                            Layout lay = tr.GetObject(de.Value, OpenMode.ForRead) as Layout;
                            if (lay == null || lay.ModelType) continue;
                            spaces.Add(new KeyValuePair<ObjectId, string>(lay.BlockTableRecordId, lay.LayoutName));
                        }
                    }
                    catch (System.Exception ex) { NoteErr(ref errN, ref firstErr, "Pafta", ex); }
                    spaceLabel = spaces.Count > 1 ? "Model+pafta" : "Model";
                    List<ObjectId> lockedIds = LockedLayers(tr, db);
                    HashSet<ObjectId> lockedSet = new HashSet<ObjectId>(lockedIds);
                    List<ObjectId> unlocked = new List<ObjectId>();
                    Queue<ObjectId> blockQueue = new Queue<ObjectId>();
                    HashSet<ObjectId> blockSeen = new HashSet<ObjectId>();
                    foreach (KeyValuePair<ObjectId, string> sp0 in spaces) blockSeen.Add(sp0.Key);
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

                        foreach (KeyValuePair<ObjectId, string> sp in spaces)
                        {
                            BlockTableRecord area = (BlockTableRecord)tr.GetObject(sp.Key, OpenMode.ForRead);
                            LogStep("alan-basla", sp.Value, sp.Key, "SPACE");
                            foreach (ObjectId id in area)
                            {
                                if (!id.IsValid || id.IsErased) continue;
                                Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                                if (ent == null || ent.IsErased) continue;
                                total++;
                                bool onLocked = !ent.LayerId.IsNull && lockedSet.Contains(ent.LayerId);
                                if (onLocked) lockedN++;
                                int kind = ApplyOne(tr, ent, sp.Value, ref elevN, ref thickN, ref projN, ref errN, ref firstErr);
                                if (kind == 1) fixedN++;
                                else if (kind == 2) skipN++;
                                BlockReference brTop = ent as BlockReference;
                                if (brTop != null && !IsDynamicRef(brTop))
                                {
                                    FlattenAttributes(tr, brTop, lockedSet, ref fixedN, ref lockedN, ref elevN, ref thickN, ref projN, ref skipN, ref errN, ref firstErr, false);
                                    EnqueueBlockRef(tr, brTop, blockSeen, blockQueue);
                                }
                            }
                        }

                        FlattenDefinitions(tr, lockedSet, blockQueue, blockSeen, processedBlocks, ref blocksN, ref blockFixed, ref blockVisited, ref lockedN, ref elevN, ref thickN, ref projN, ref skipN, ref errN, ref firstErr);
                        foreach (KeyValuePair<ObjectId, string> sp in spaces)
                        {
                            BlockTableRecord area = (BlockTableRecord)tr.GetObject(sp.Key, OpenMode.ForRead);
                            ForceInserts(tr, area, ref fixedN, ref elevN, ref thickN, ref projN, ref errN, ref firstErr);
                        }
                        leftover = CollectLeftover(tr, db);
                        LogStep("tani", spaceLabel, ObjectId.Null, "SCAN");
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
                + "Blok içi taranan: " + blockVisited + ". "
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
            LogStep("ozet", spaceLabel, ObjectId.Null, "fixed=" + fixedN + " elev=" + elevN + " thick=" + thickN + " proj=" + projN + " skip=" + skipN + " locked=" + lockedN + " blocks=" + blocksN + " blockVisited=" + blockVisited + " blockFixed=" + blockFixed + " err=" + errN + " total=" + total);
            LogStep("bitti", spaceLabel, ObjectId.Null, "ZSIFIRLA");
            }
            finally { CloseZLog(); }
        }

        static void OpenZLog()
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CadAiDesigner");
                Directory.CreateDirectory(dir);
                ZLog = new StreamWriter(Path.Combine(dir, "zsifirla.log"), false, new UTF8Encoding(false));
                ZLog.AutoFlush = true;
            }
            catch (System.Exception) { ZLog = null; }
        }

        static void CloseZLog()
        {
            try
            {
                if (ZLog != null) { ZLog.Flush(); ZLog.Dispose(); }
            }
            catch (System.Exception) { }
            ZLog = null;
        }

        static void LogStep(string phase, string blockName, ObjectId id, string type)
        {
            try
            {
                if (ZLog == null) return;
                string handle = "";
                try { if (!id.IsNull) handle = id.Handle.ToString(); } catch (System.Exception) { }
                ZLog.WriteLine(phase + "\t" + (blockName ?? "") + "\t" + handle + "\t" + (type ?? ""));
                ZLog.Flush();
            }
            catch (System.Exception) { }
        }

        static int ApplyOne(Transaction tr, Entity ent, string where, ref int elevN, ref int thickN, ref int projN, ref int errN, ref string firstErr)
        {
            BlockReference dynBr = ent as BlockReference;
            if (dynBr != null && IsDynamicRef(dynBr))
            {
                if (Math.Abs(dynBr.Position.Z) <= Tol)
                {
                    LogStep("skip dynamic", where, ent.ObjectId, "BlockReference " + ZInfo(ent) + " " + XyTxt(ent));
                    return 0;
                }
                return ZeroDynamicInsertZ(dynBr, where, ref errN, ref firstErr);
            }
            string before = ZInfo(ent);
            string xy0 = XyTxt(ent);
            List<Point2d> xyBefore = SampleXY(ent);
            int elev0 = elevN, thick0 = thickN, proj0 = projN;
            Transaction sub = null;
            bool closed = false;
            try
            {
                sub = ent.Database.TransactionManager.StartTransaction();
                Entity live = sub.GetObject(ent.ObjectId, OpenMode.ForWrite) as Entity;
                if (live == null)
                {
                    sub.Abort();
                    closed = true;
                    LogStep("failed", where, ent.ObjectId, ent.GetType().Name + " " + before + " " + xy0 + " open");
                    errN++;
                    return -1;
                }
                bool changed = MirrorNegativeZ(live, sub, ref projN);
                Leader mirrored = live as Leader;
                if (mirrored != null)
                {
                    Vector3d ln;
                    if (TryNormal(mirrored, out ln) && IsNearNegativeZ(ln))
                    {
                        Leader neu = RebuildLeader(sub, mirrored);
                        if (neu != null)
                        {
                            live = neu;
                            changed = true;
                        }
                    }
                }
                int kind = Flatten(sub, live, ref elevN, ref thickN, ref projN);
                if (kind == 1) changed = true;
                if (kind == 2)
                {
                    sub.Commit();
                    closed = true;
                    LogStep("skipped", where, ent.ObjectId, ent.GetType().Name + " " + before + " " + xy0);
                    return 2;
                }
                Entity check = live;
                bool off, uniform;
                double z;
                bool known = TryMeasure(sub, live, out z, out off, out uniform);
                if (known && off && uniform && !(live is Viewport))
                {
                    Write(live);
                    live.TransformBy(Matrix3d.Displacement(new Vector3d(0, 0, -z)));
                    changed = true;
                    known = TryMeasure(sub, live, out z, out off, out uniform);
                }
                if (known && off)
                {
                    Leader ld = live as Leader;
                    if (ld != null && !ld.IsErased)
                    {
                        Leader neu = RebuildLeader(sub, ld);
                        if (neu != null)
                        {
                            check = neu;
                            changed = true;
                            known = TryMeasure(sub, neu, out z, out off, out uniform);
                        }
                    }
                }
                string xy1 = XyTxt(check);
                if (XyMoved(xyBefore, SampleXY(check)))
                {
                    sub.Abort();
                    closed = true;
                    elevN = elev0;
                    thickN = thick0;
                    projN = proj0;
                    LogStep("failed", where, ent.ObjectId, ent.GetType().Name + " " + before + " " + xy0 + " -> " + xy1 + " xy-shift");
                    if (firstErr == null) firstErr = ent.GetType().Name + " " + ent.Handle + " xy-shift";
                    errN++;
                    return -1;
                }
                string after = ZInfo(check);
                sub.Commit();
                closed = true;
                if (known && off)
                {
                    LogStep("failed", where, check.ObjectId, check.GetType().Name + " " + before + " " + xy0 + " -> " + after + " " + xy1 + " still off z=" + Num(z));
                    if (firstErr == null) firstErr = check.GetType().Name + " " + check.Handle + " still off Z";
                    errN++;
                    return -1;
                }
                string action = changed ? "fixed" : "unchanged";
                LogStep(action, where, check.ObjectId, check.GetType().Name + " " + before + " " + xy0 + " -> " + after + " " + xy1);
                return changed ? 1 : 0;
            }
            catch (System.Exception ex)
            {
                if (sub != null && !closed)
                {
                    try { sub.Abort(); } catch (System.Exception) { }
                    elevN = elev0;
                    thickN = thick0;
                    projN = proj0;
                }
                NoteErr(ref errN, ref firstErr, ent, ex);
                LogStep("failed", where, ent.ObjectId, ent.GetType().Name + " " + before + " " + xy0 + " " + ex.Message);
                return -1;
            }
            finally
            {
                if (sub != null) sub.Dispose();
            }
        }

        /// <summary>
        /// Normal -Z is flipped by a mirror through the horizontal plane at z/2.
        /// That sends z to 0, turns the normal to +Z, and leaves WCS XY untouched.
        /// A rotation would mirror the plan.
        /// </summary>
        static bool MirrorNegativeZ(Entity ent, Transaction tr, ref int projN)
        {
            if (ent is Viewport) return false;
            Vector3d n;
            if (!TryNormal(ent, out n) || !IsNearNegativeZ(n)) return false;
            bool off, uniform;
            double z;
            if (!TryMeasure(tr, ent, out z, out off, out uniform) || !uniform) return false;
            Write(ent);
            double c = z * 0.5;
            ent.TransformBy(Matrix3d.Mirroring(new Plane(new Point3d(0.0, 0.0, c), Vector3d.ZAxis)));
            ClearTextMirror(ent);
            projN++;
            return true;
        }

        static void ClearTextMirror(Entity ent)
        {
            DBText tx = ent as DBText;
            if (tx == null) return;
            try
            {
                if (tx.IsMirroredInX) tx.IsMirroredInX = false;
            }
            catch (System.Exception) { }
            try
            {
                if (tx.IsMirroredInY) tx.IsMirroredInY = false;
            }
            catch (System.Exception) { }
        }

        static List<Point2d> SampleXY(Entity ent)
        {
            List<Point2d> pts = new List<Point2d>();
            if (ent == null || ent.IsErased) return pts;
            try
            {
                Leader ld = ent as Leader;
                if (ld != null)
                {
                    int n = ld.NumVertices;
                    for (int i = 0; i < n && i < 64; i++)
                    {
                        Point3d p = ld.VertexAt(i);
                        pts.Add(new Point2d(p.X, p.Y));
                    }
                    return pts;
                }
                Line ln = ent as Line;
                if (ln != null)
                {
                    pts.Add(new Point2d(ln.StartPoint.X, ln.StartPoint.Y));
                    pts.Add(new Point2d(ln.EndPoint.X, ln.EndPoint.Y));
                    return pts;
                }
                Polyline pl = ent as Polyline;
                if (pl != null)
                {
                    int n = pl.NumberOfVertices;
                    for (int i = 0; i < n && i < 64; i++)
                    {
                        Point3d p = pl.GetPoint3dAt(i);
                        pts.Add(new Point2d(p.X, p.Y));
                    }
                    return pts;
                }
                Arc arc = ent as Arc;
                if (arc != null)
                {
                    pts.Add(new Point2d(arc.Center.X, arc.Center.Y));
                    pts.Add(new Point2d(arc.StartPoint.X, arc.StartPoint.Y));
                    pts.Add(new Point2d(arc.EndPoint.X, arc.EndPoint.Y));
                    return pts;
                }
                Circle c = ent as Circle;
                if (c != null)
                {
                    pts.Add(new Point2d(c.Center.X, c.Center.Y));
                    return pts;
                }
                Ellipse el = ent as Ellipse;
                if (el != null)
                {
                    pts.Add(new Point2d(el.Center.X, el.Center.Y));
                    return pts;
                }
                DBText tx = ent as DBText;
                if (tx != null)
                {
                    pts.Add(new Point2d(tx.Position.X, tx.Position.Y));
                    try
                    {
                        if (tx.HorizontalMode != TextHorizontalMode.TextLeft || tx.VerticalMode != TextVerticalMode.TextBase)
                            pts.Add(new Point2d(tx.AlignmentPoint.X, tx.AlignmentPoint.Y));
                    }
                    catch (System.Exception) { }
                    return pts;
                }
                MText mt = ent as MText;
                if (mt != null)
                {
                    pts.Add(new Point2d(mt.Location.X, mt.Location.Y));
                    return pts;
                }
                BlockReference br = ent as BlockReference;
                if (br != null)
                {
                    pts.Add(new Point2d(br.Position.X, br.Position.Y));
                    return pts;
                }
                MLeader ml = ent as MLeader;
                if (ml != null)
                {
                    try
                    {
                        ArrayList leaders = ml.GetLeaderIndexes();
                        foreach (int li in Ints(leaders))
                        {
                            ArrayList lines = ml.GetLeaderLineIndexes(li);
                            foreach (int line in Ints(lines))
                            {
                                if (ml.VerticesCount(line) <= 0) continue;
                                Point3d p = ml.GetVertex(line, 0);
                                pts.Add(new Point2d(p.X, p.Y));
                                if (pts.Count >= 8) break;
                            }
                            if (pts.Count >= 8) break;
                        }
                    }
                    catch (System.Exception) { }
                    try
                    {
                        Point3d t = ml.TextLocation;
                        pts.Add(new Point2d(t.X, t.Y));
                    }
                    catch (System.Exception) { }
                    return pts;
                }
                foreach (PropertyInfo pi in ent.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (pts.Count >= 8) break;
                    if (pi.PropertyType != typeof(Point3d) || !pi.CanRead) continue;
                    if (pi.GetIndexParameters().Length != 0) continue;
                    if (pi.Name == "ViewTarget" || pi.Name == "Target" || pi.Name.IndexOf("View", StringComparison.Ordinal) >= 0) continue;
                    try
                    {
                        Point3d p = (Point3d)pi.GetValue(ent, null);
                        pts.Add(new Point2d(p.X, p.Y));
                    }
                    catch (System.Exception) { }
                }
            }
            catch (System.Exception) { }
            return pts;
        }

        static string XyTxt(Entity ent)
        {
            List<Point2d> pts = SampleXY(ent);
            if (pts.Count == 0) return "xy=?";
            return "xy=" + Num(pts[0].X) + "," + Num(pts[0].Y);
        }

        static bool XyMoved(List<Point2d> before, List<Point2d> after)
        {
            if (before == null || after == null || before.Count == 0 || after.Count == 0) return false;
            int n = before.Count < after.Count ? before.Count : after.Count;
            for (int i = 0; i < n; i++)
            {
                if (Math.Abs(before[i].X - after[i].X) > 1e-6) return true;
                if (Math.Abs(before[i].Y - after[i].Y) > 1e-6) return true;
            }
            return false;
        }

        static string ZInfo(Entity ent)
        {
            try
            {
                Polyline pl = ent as Polyline;
                if (pl != null) return "elev=" + Num(pl.Elevation);
                Polyline2d p2 = ent as Polyline2d;
                if (p2 != null) return "elev=" + Num(p2.Elevation);
                MLeader ml = ent as MLeader;
                if (ml != null) return "mlZ=" + Num(MLeaderZ(ml));
                Hatch h = ent as Hatch;
                if (h != null) return "elev=" + Num(h.Elevation);
                Dimension dim = ent as Dimension;
                if (dim != null) return "elev=" + Num(dim.Elevation);
                DBText tx = ent as DBText;
                if (tx != null) return "z=" + Num(tx.Position.Z);
                MText mt = ent as MText;
                if (mt != null) return "z=" + Num(mt.Location.Z);
                BlockReference br = ent as BlockReference;
                if (br != null) return "z=" + Num(br.Position.Z);
                Line ln = ent as Line;
                if (ln != null) return "z=" + Num(ln.StartPoint.Z) + "," + Num(ln.EndPoint.Z);
                Leader ld = ent as Leader;
                if (ld != null) return LeaderInfo(ld);
            }
            catch (System.Exception) { }
            return "";
        }

        static string LeaderInfo(Leader ld)
        {
            string z = "?";
            string n = "?";
            try
            {
                if (ld.NumVertices > 0) z = Num(ld.VertexAt(0).Z);
            }
            catch (System.Exception) { }
            Vector3d v;
            if (TryNormal(ld, out v))
                n = Num(v.X) + "," + Num(v.Y) + "," + Num(v.Z);
            return "z0=" + z + " n=" + n;
        }

        static string Num(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

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

        /// <summary>Queue an ordinary block definition. Dynamic masters and anonymous copies are not queued.</summary>
        static void EnqueueBlockRef(Transaction tr, BlockReference br, HashSet<ObjectId> seen, Queue<ObjectId> queue)
        {
            if (br == null || IsDynamicRef(br)) return;
            try { EnqueueDefinition(tr, br.BlockTableRecord, seen, queue); } catch (System.Exception) { }
        }

        static void EnqueueDefinition(Transaction tr, ObjectId id, HashSet<ObjectId> seen, Queue<ObjectId> queue)
        {
            if (id.IsNull || !id.IsValid || !seen.Add(id)) return;
            BlockTableRecord btr = tr.GetObject(id, OpenMode.ForRead) as BlockTableRecord;
            if (btr == null || SkipDefinition(btr))
            {
                if (btr != null && IsProtectedBlock(btr))
                    LogStep("skip dynamic", SafeName(btr), id, "BLOCK");
                return;
            }
            queue.Enqueue(id);
        }

        static bool SkipDefinition(BlockTableRecord btr)
        {
            if (btr.IsLayout || btr.IsFromExternalReference || btr.IsDependent) return true;
            try { if (btr.IsFromOverlayReference) return true; } catch (System.Exception) { }
            if (IsProtectedBlock(btr)) return true;
            return false;
        }

        /// <summary>Dynamic masters, anonymous *U/*D/*X/*T blocks, and bound anonymous A$C blocks.</summary>
        static bool IsProtectedBlock(BlockTableRecord btr)
        {
            try { if (btr.IsDynamicBlock) return true; } catch (System.Exception) { }
            try { if (btr.IsAnonymous) return true; } catch (System.Exception) { }
            string name = "";
            try { name = btr.Name ?? ""; } catch (System.Exception) { name = ""; }
            if (name.StartsWith("A$C", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.Length >= 2 && name[0] == '*')
            {
                char c = char.ToUpperInvariant(name[1]);
                if (c == 'U' || c == 'D' || c == 'X' || c == 'T') return true;
            }
            return false;
        }

        static bool IsDynamicRef(BlockReference br)
        {
            if (br == null) return false;
            try { if (br.IsDynamicBlock) return true; } catch (System.Exception) { }
            try
            {
                ObjectId dyn = br.DynamicBlockTableRecord;
                if (!dyn.IsNull && dyn != br.BlockTableRecord) return true;
            }
            catch (System.Exception) { }
            return false;
        }

        static bool IsDynamicPart(Entity ent)
        {
            if (ent == null) return false;
            string n = ent.GetType().Name;
            if (NameHasDynamicPart(n)) return true;
            try
            {
                string rx = ent.GetRXClass().Name;
                if (NameHasDynamicPart(rx)) return true;
            }
            catch (System.Exception) { }
            return false;
        }

        static bool NameHasDynamicPart(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.IndexOf("Parameter", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (name.IndexOf("Action", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (name.IndexOf("Grip", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>True when Z, elevation, or the normal is off the WCS XY plane. Read-only.</summary>
        static bool NeedsFlatten(Transaction tr, Entity ent)
        {
            if (ent == null || ent.IsErased) return false;
            Vector3d n;
            if (TryNormal(ent, out n) && !IsExactPositiveZ(n) && !IsNearPositiveZ(n)) return true;
            bool off, uniform;
            double z;
            if (TryMeasure(tr, ent, out z, out off, out uniform) && off) return true;
            return false;
        }

        /// <summary>Drop a dynamic insert's position Z only. Rotation, scale, and visibility stay.</summary>
        static int ZeroDynamicInsertZ(BlockReference br, string where, ref int errN, ref string firstErr)
        {
            string before = ZInfo(br);
            string xy0 = XyTxt(br);
            List<Point2d> xyBefore = SampleXY(br);
            Transaction sub = null;
            bool closed = false;
            try
            {
                sub = br.Database.TransactionManager.StartTransaction();
                BlockReference live = sub.GetObject(br.ObjectId, OpenMode.ForWrite) as BlockReference;
                if (live == null)
                {
                    sub.Abort();
                    closed = true;
                    LogStep("failed", where, br.ObjectId, "BlockReference " + before + " " + xy0 + " open");
                    errN++;
                    return -1;
                }
                Point3d p = live.Position;
                live.Position = new Point3d(p.X, p.Y, 0.0);
                if (XyMoved(xyBefore, SampleXY(live)))
                {
                    sub.Abort();
                    closed = true;
                    LogStep("failed", where, br.ObjectId, "BlockReference " + before + " " + xy0 + " -> " + XyTxt(live) + " xy-shift");
                    if (firstErr == null) firstErr = "BlockReference " + br.Handle + " xy-shift";
                    errN++;
                    return -1;
                }
                string after = ZInfo(live) + " " + XyTxt(live);
                sub.Commit();
                closed = true;
                LogStep("fixed", where, live.ObjectId, "BlockReference " + before + " " + xy0 + " -> " + after);
                return 1;
            }
            catch (System.Exception ex)
            {
                if (sub != null && !closed)
                {
                    try { sub.Abort(); } catch (System.Exception) { }
                }
                NoteErr(ref errN, ref firstErr, br, ex);
                LogStep("failed", where, br.ObjectId, "BlockReference " + before + " " + xy0 + " " + ex.Message);
                return -1;
            }
            finally
            {
                if (sub != null) sub.Dispose();
            }
        }

        static void FlattenDefinitions(Transaction tr, HashSet<ObjectId> lockedSet, Queue<ObjectId> queue, HashSet<ObjectId> seen, List<ObjectId> processed,
            ref int blocksN, ref int blockFixed, ref int blockVisited, ref int lockedN, ref int elevN, ref int thickN, ref int projN, ref int skipN, ref int errN, ref string firstErr)
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
                LogStep("tanim", SafeName(btr), bid, "BLOCK");
                int fixedHere = 0;
                try
                {
                    Point3d org = btr.Origin;
                    if (Math.Abs(org.Z) > Tol)
                    {
                        Write(btr);
                        btr.Origin = new Point3d(org.X, org.Y, 0.0);
                        blockFixed++;
                        fixedHere++;
                    }
                }
                catch (System.Exception ex) { NoteErr(ref errN, ref firstErr, "Blok origin", ex); }
                string blockWhere = "blok:" + SafeName(btr);
                foreach (ObjectId eid in btr)
                {
                    if (!eid.IsValid || eid.IsErased) continue;
                    Entity ent = null;
                    try { ent = tr.GetObject(eid, OpenMode.ForRead) as Entity; }
                    catch (System.Exception ex) { NoteErr(ref errN, ref firstErr, "Blok varlığı", ex); continue; }
                    if (ent == null || ent.IsErased) continue;
                    blockVisited++;
                    if (!ent.LayerId.IsNull && lockedSet.Contains(ent.LayerId)) lockedN++;
                    BlockReference nested = ent as BlockReference;
                    if (nested != null && IsDynamicRef(nested))
                    {
                        if (Math.Abs(nested.Position.Z) > Tol)
                        {
                            int zKind = ZeroDynamicInsertZ(nested, blockWhere, ref errN, ref firstErr);
                            if (zKind == 1) { blockFixed++; fixedHere++; }
                            else if (zKind == 2) skipN++;
                        }
                        else
                        {
                            LogStep("skip dynamic", blockWhere, eid, "BlockReference " + ZInfo(nested) + " " + XyTxt(nested));
                            skipN++;
                        }
                        continue;
                    }
                    if (IsDynamicPart(ent))
                    {
                        LogStep("skip dynamic", blockWhere, eid, ent.GetType().Name);
                        skipN++;
                        continue;
                    }
                    if (!NeedsFlatten(tr, ent))
                    {
                        LogStep("unchanged", blockWhere, eid, ent.GetType().Name + " " + ZInfo(ent) + " " + XyTxt(ent));
                        if (nested != null) EnqueueBlockRef(tr, nested, seen, queue);
                        continue;
                    }
                    int kind = ApplyOne(tr, ent, blockWhere, ref elevN, ref thickN, ref projN, ref errN, ref firstErr);
                    if (kind == 1) { blockFixed++; fixedHere++; Touch(ent); }
                    else if (kind == 2) skipN++;
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
                if (!NeedsFlatten(tr, ar)) continue;
                int kind = ApplyOne(tr, ar, "attr", ref elevN, ref thickN, ref projN, ref errN, ref firstErr);
                if (kind == 1)
                {
                    fixedN++;
                    if (markGraphics) Touch(ar);
                }
                else if (kind == 2) skipN++;
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
            if (ent is RasterImage) return FlattenImage((RasterImage)ent, ref projN);
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
            // Read WCS first. Setting Normal before that reinterprets OCS and moves the plan.
            double elev = 0;
            try { elev = pl.Elevation; } catch (System.Exception) { }
            Vector3d n = Vector3d.ZAxis;
            try { n = pl.Normal; } catch (System.Exception) { }
            int nv = 0;
            try { nv = pl.NumberOfVertices; } catch (System.Exception) { nv = 0; }
            Point2d[] xy = new Point2d[nv];
            double[] bulge = new double[nv];
            double[] sw = new double[nv];
            double[] ew = new double[nv];
            bool negateBulge = n.Z < 0 && !IsNearNegativeZ(n);
            for (int i = 0; i < nv; i++)
            {
                try
                {
                    Point3d w = pl.GetPoint3dAt(i);
                    xy[i] = new Point2d(w.X, w.Y);
                    double b = pl.GetBulgeAt(i);
                    bulge[i] = negateBulge ? -b : b;
                    sw[i] = pl.GetStartWidthAt(i);
                    ew[i] = pl.GetEndWidthAt(i);
                }
                catch (System.Exception) { }
            }
            bool hadElev = Math.Abs(elev) > 1e-9;
            bool project = !IsExactPositiveZ(n) && !IsNearPositiveZ(n);
            bool hadThick = false;
            try { hadThick = Math.Abs(pl.Thickness) > Tol; } catch (System.Exception) { }
            if (!hadElev && !project && !hadThick && !negateBulge) return 0;
            Write(pl);
            // A -Z normal was already mirrored to +Z when every vertex shared one Z.
            // Here only a tilted normal is rewritten, using the WCS XY captured above.
            if (project && !IsNearNegativeZ(n))
            {
                try { pl.Normal = Vector3d.ZAxis; } catch (System.Exception) { }
                projN++;
                for (int i = 0; i < nv; i++)
                {
                    try
                    {
                        pl.SetPointAt(i, xy[i]);
                        pl.SetBulgeAt(i, bulge[i]);
                        pl.SetStartWidthAt(i, sw[i]);
                        pl.SetEndWidthAt(i, ew[i]);
                    }
                    catch (System.Exception) { }
                }
            }
            try
            {
                if (Math.Abs(pl.Elevation) > 1e-9)
                {
                    pl.Elevation = 0.0;
                    elevN++;
                    hadElev = true;
                }
            }
            catch (System.Exception) { }
            if (hadThick)
            {
                try { pl.Thickness = 0.0; thickN++; } catch (System.Exception) { }
            }
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
            bool hadThick = Math.Abs(arc.Thickness) > Tol;
            if (IsNearNegativeZ(n))
            {
                if (Math.Abs(c.Z) <= Tol && !hadThick) return 0;
                Write(arc);
                arc.Center = new Point3d(c.X, c.Y, 0);
                if (hadThick) { arc.Thickness = 0.0; thickN++; }
                return 1;
            }
            double a0 = Math.Atan2(s.Y - c.Y, s.X - c.X);
            double a1 = Math.Atan2(e.Y - c.Y, e.X - c.X);
            // Negative normal: CCW around -Z is CW in plan. Swap so the bulge side stays.
            if (n.Z < 0) { double tmp = a0; a0 = a1; a1 = tmp; }
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
            if (IsNearNegativeZ(n))
            {
                if (Math.Abs(cen.Z) <= Tol && !hadThick) return 0;
                Write(c);
                c.Center = new Point3d(cen.X, cen.Y, 0);
                if (hadThick) { c.Thickness = 0.0; thickN++; }
                return 1;
            }
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
            if (IsNearNegativeZ(n))
            {
                if (Math.Abs(c.Z) <= Tol) return 0;
                Write(el);
                el.Set(new Point3d(c.X, c.Y, 0), n, el.MajorAxis, el.RadiusRatio, el.StartAngle, el.EndAngle);
                return 1;
            }
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
            if (IsNearNegativeZ(pt.Normal))
            {
                if (Math.Abs(pt.Position.Z) <= Tol && Math.Abs(pt.Thickness) <= Tol) return 0;
                Write(pt);
                pt.Position = Flat(pt.Position);
                if (ApplyThick(pt, ref thickN)) { }
                return 1;
            }
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
            if (IsNearNegativeZ(n))
            {
                if (Math.Abs(p.Z) <= Tol && !alignZ && !hadThick) return 0;
                Write(t);
                t.Position = Flat(p);
                if (aligned) t.AlignmentPoint = Flat(a);
                if (hadThick) { t.Thickness = 0.0; thickN++; }
                return 1;
            }
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
            if (IsNearNegativeZ(n))
            {
                if (Math.Abs(loc.Z) <= Tol && Math.Abs(dir.Z) <= Tol) return 0;
                Write(mt);
                mt.Location = Flat(loc);
                if (flatDir.Length > Tol) mt.Direction = flatDir.GetNormal();
                return 1;
            }
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
            if (IsNearNegativeZ(n))
            {
                if (Math.Abs(p.Z) <= Tol) return 0;
                Write(br);
                br.Position = Flat(p);
                return 1;
            }
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
            bool changed = false;
            double z = MLeaderZ(ml);
            if (Math.Abs(z) > Tol)
            {
                Write(ml);
                ml.TransformBy(Matrix3d.Displacement(new Vector3d(0, 0, -z)));
                changed = true;
            }
            ArrayList leaders = null;
            try { leaders = ml.GetLeaderIndexes(); } catch (System.Exception) { }
            foreach (int leaderIndex in Ints(leaders))
            {
                ArrayList lines = null;
                try { lines = ml.GetLeaderLineIndexes(leaderIndex); } catch (System.Exception) { continue; }
                foreach (int line in Ints(lines))
                {
                    int vc = 0;
                    try { vc = ml.VerticesCount(line); } catch (System.Exception) { continue; }
                    for (int k = 0; k < vc; k++)
                    {
                        Point3d p;
                        try { p = ml.GetVertex(line, k); } catch (System.Exception) { continue; }
                        if (Math.Abs(p.Z) <= Tol) continue;
                        Write(ml);
                        ml.SetVertex(line, k, Flat(p));
                        changed = true;
                    }
                    try
                    {
                        Point3d a = ml.GetFirstVertex(line);
                        if (Math.Abs(a.Z) > Tol) { Write(ml); ml.SetFirstVertex(line, Flat(a)); changed = true; }
                        Point3d b = ml.GetLastVertex(line);
                        if (Math.Abs(b.Z) > Tol) { Write(ml); ml.SetLastVertex(line, Flat(b)); changed = true; }
                    }
                    catch (System.Exception) { }
                }
            }
            try
            {
                if (ml.ContentType == ContentType.MTextContent)
                {
                    if (Math.Abs(ml.TextLocation.Z) > Tol)
                    {
                        Write(ml);
                        ml.TextLocation = Flat(ml.TextLocation);
                        changed = true;
                    }
                    try
                    {
                        MText copy = ml.MText;
                        if (copy != null && Math.Abs(copy.Location.Z) > Tol)
                        {
                            copy.Location = Flat(copy.Location);
                            Write(ml);
                            ml.MText = copy;
                            changed = true;
                        }
                    }
                    catch (System.Exception) { }
                }
                else if (ml.ContentType == ContentType.BlockContent && Math.Abs(ml.BlockPosition.Z) > Tol)
                {
                    Write(ml);
                    ml.BlockPosition = Flat(ml.BlockPosition);
                    changed = true;
                }
                else if (ml.ContentType == ContentType.ToleranceContent)
                    changed |= MovePointProp(ml, "ToleranceLocation");
                changed |= MovePointProp(ml, "ContentBasePosition");
            }
            catch (System.Exception) { }
            try
            {
                Vector3d nrm = ml.Normal;
                if (!IsExactPositiveZ(nrm) && !IsNearPositiveZ(nrm) && !IsNearNegativeZ(nrm))
                {
                    Write(ml);
                    ml.TransformBy(Matrix3d.Projection(new Plane(Point3d.Origin, Vector3d.ZAxis), Vector3d.ZAxis));
                    projN++;
                    changed = true;
                }
            }
            catch (System.Exception) { }
            if (ApplyElev(ml, ref elevN)) changed = true;
            if (ApplyThick(ml, ref thickN)) changed = true;
            return changed ? 1 : 0;
        }

        static double MLeaderZ(MLeader ml)
        {
            double z = 0;
            bool got = false;
            try
            {
                PropertyInfo p = typeof(MLeader).GetProperty("ContentBasePosition");
                if (p != null && p.PropertyType == typeof(Point3d) && p.CanRead)
                {
                    z = ((Point3d)p.GetValue(ml, null)).Z;
                    got = true;
                }
            }
            catch (System.Exception) { }
            try
            {
                if (ml.ContentType == ContentType.MTextContent)
                {
                    double tz = ml.TextLocation.Z;
                    if (!got || Math.Abs(tz) > Math.Abs(z)) z = tz;
                    got = true;
                }
                else if (ml.ContentType == ContentType.BlockContent)
                {
                    double bz = ml.BlockPosition.Z;
                    if (!got || Math.Abs(bz) > Math.Abs(z)) z = bz;
                    got = true;
                }
            }
            catch (System.Exception) { }
            try
            {
                ArrayList leaders = ml.GetLeaderIndexes();
                foreach (int li in Ints(leaders))
                {
                    ArrayList lines = ml.GetLeaderLineIndexes(li);
                    foreach (int line in Ints(lines))
                    {
                        if (ml.VerticesCount(line) <= 0) continue;
                        double vz = ml.GetVertex(line, 0).Z;
                        if (!got || Math.Abs(vz) > Math.Abs(z)) z = vz;
                        got = true;
                    }
                }
            }
            catch (System.Exception) { }
            return z;
        }

        static int FlattenLeader(Leader ld, ref int elevN, ref int thickN, ref int projN)
        {
            // Never rotate a leader. A 180° turn about the first vertex mirrors WCS XY.
            // Normal -Z is mirrored about a horizontal plane before this runs.
            // A +Z leader is only shifted in Z. A tilted one is rebuilt from (x, y, 0).
            double zMin, zMax;
            if (!LeaderRange(ld, out zMin, out zMax)) return 0;
            Vector3d n = LeaderNormal(ld);
            bool elevated = Math.Abs(zMin) > Tol || Math.Abs(zMax) > Tol;
            if (!elevated && (IsNearPositiveZ(n) || IsNearNegativeZ(n))) return 0;
            if (IsNearNegativeZ(n)) return 0;
            bool uniform = Math.Abs(zMax - zMin) <= 1e-6;
            if (uniform && elevated)
            {
                Write(ld);
                double z = ld.VertexAt(0).Z;
                if (Math.Abs(z) > Tol)
                    ld.TransformBy(Matrix3d.Displacement(new Vector3d(0, 0, -z)));
                if (ApplyElev(ld, ref elevN)) { }
                if (ApplyThick(ld, ref thickN)) { }
                return 1;
            }
            return elevated ? 1 : 0;
        }

        static Vector3d LeaderNormal(Leader ld)
        {
            Vector3d n;
            if (TryNormal(ld, out n)) return n;
            return Vector3d.ZAxis;
        }

        static bool LeaderRange(Leader ld, out double zMin, out double zMax)
        {
            zMin = 0;
            zMax = 0;
            int n = 0;
            try { n = ld.NumVertices; }
            catch (System.Exception) { return false; }
            if (n <= 0) return false;
            try { zMin = zMax = ld.VertexAt(0).Z; }
            catch (System.Exception) { return false; }
            for (int i = 1; i < n; i++)
            {
                double z;
                try { z = ld.VertexAt(i).Z; }
                catch (System.Exception) { continue; }
                if (z < zMin) zMin = z;
                if (z > zMax) zMax = z;
            }
            return true;
        }

        static bool LeaderFlat(Leader ld)
        {
            double a, b;
            if (!LeaderRange(ld, out a, out b)) return false;
            return Math.Abs(a) <= ReportZ && Math.Abs(b) <= ReportZ;
        }

        /// <summary>
        /// Replace a leader whose plane will not accept a Z move. Vertices are projected to Z=0.
        /// Returns the new leader, or null when the copy is still elevated (the new one is erased).
        /// </summary>
        static Leader RebuildLeader(Transaction tr, Leader old)
        {
            if (old == null || old.IsErased) return null;
            int nv = 0;
            try { nv = old.NumVertices; }
            catch (System.Exception) { return null; }
            if (nv < 2) return null;
            BlockTableRecord owner = tr.GetObject(old.OwnerId, OpenMode.ForWrite) as BlockTableRecord;
            if (owner == null) return null;
            Leader neu = new Leader();
            bool added = false;
            try
            {
                neu.SetDatabaseDefaults(old.Database);
                for (int i = 0; i < nv; i++)
                    neu.AppendVertex(Flat(old.VertexAt(i)));
                owner.AppendEntity(neu);
                tr.AddNewlyCreatedDBObject(neu, true);
                added = true;
                CopyLeaderProps(old, neu);
                try { neu.EvaluateLeader(); }
                catch (System.Exception) { }
                if (!LeaderFlat(neu))
                {
                    neu.Erase();
                    return null;
                }
                old.Erase();
                return neu;
            }
            catch (System.Exception)
            {
                try
                {
                    if (added) { if (!neu.IsErased) neu.Erase(); }
                    else neu.Dispose();
                }
                catch (System.Exception) { }
                return null;
            }
        }

        static void CopyLeaderProps(Leader src, Leader dst)
        {
            try { dst.LayerId = src.LayerId; } catch (System.Exception) { }
            try { dst.Color = src.Color; } catch (System.Exception) { }
            try { dst.LinetypeId = src.LinetypeId; } catch (System.Exception) { }
            try { dst.LinetypeScale = src.LinetypeScale; } catch (System.Exception) { }
            try { dst.LineWeight = src.LineWeight; } catch (System.Exception) { }
            try { dst.Transparency = src.Transparency; } catch (System.Exception) { }
            try { dst.HasArrowHead = src.HasArrowHead; } catch (System.Exception) { }
            try { dst.IsSplined = src.IsSplined; } catch (System.Exception) { }
            string[] names = { "DimensionStyle", "DimStyle", "AnnotationOffset", "Dimscale", "Dimasz", "Dimgap", "Dimtxt", "Dimtad", "Dimsah", "Dimclrd", "Dimlwd", "Dimldrblk", "Dimtih", "Dimtoh" };
            for (int i = 0; i < names.Length; i++) CopyProp(src, dst, names[i]);
            try
            {
                ResultBuffer xb = src.XData;
                if (xb != null) dst.XData = xb;
            }
            catch (System.Exception) { }
            try
            {
                if (src.Annotation.IsNull) return;
                PropertyInfo p = typeof(Leader).GetProperty("Annotation", BindingFlags.Instance | BindingFlags.Public);
                if (p != null && p.CanWrite && p.PropertyType == typeof(ObjectId))
                    p.SetValue(dst, src.Annotation, null);
            }
            catch (System.Exception) { }
        }

        static int FlattenDim(Dimension dim, ref int elevN, ref int thickN, ref int projN)
        {
            Vector3d n = dim.Normal;
            bool project = !IsExactPositiveZ(n);
            double elev = dim.Elevation;
            bool hadElev = Math.Abs(elev) > Tol;
            if (IsNearNegativeZ(n))
            {
                if (!hadElev) return 0;
                Write(dim);
                dim.Elevation = 0.0;
                elevN++;
                return 1;
            }
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
            if (IsNearNegativeZ(n))
            {
                if (!hadElev) return 0;
                Write(h);
                h.Elevation = 0.0;
                elevN++;
                return 1;
            }
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
                if (br == null || IsDynamicRef(br)) continue;
                if (!NeedsFlatten(tr, br)) continue;
                int kind = ApplyOne(tr, br, "insert", ref elevN, ref thickN, ref projN, ref errN, ref firstErr);
                if (kind == 1) { fixedN++; Touch(br); }
            }
        }

        static string CollectLeftover(Transaction tr, Database db)
        {
            int count = 0;
            List<string> samples = new List<string>();
            BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            HashSet<ObjectId> stack = new HashSet<ObjectId>();
            ScanDeep(tr, ms, "Model", Matrix3d.Identity, stack, ref count, samples);
            try
            {
                DBDictionary lays = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry de in lays)
                {
                    Layout lay = tr.GetObject(de.Value, OpenMode.ForRead) as Layout;
                    if (lay == null || lay.ModelType) continue;
                    BlockTableRecord ps = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForRead);
                    stack.Clear();
                    ScanDeep(tr, ps, lay.LayoutName, Matrix3d.Identity, stack, ref count, samples);
                }
            }
            catch (System.Exception) { }
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
            return sb.ToString();
        }

        static void ScanDeep(Transaction tr, BlockTableRecord owner, string path, Matrix3d toWorld, HashSet<ObjectId> stack, ref int count, List<string> samples)
        {
            if (owner == null || !stack.Add(owner.ObjectId)) return;
            try
            {
                foreach (ObjectId id in owner)
                {
                    if (!id.IsValid || id.IsErased) continue;
                    Entity ent = null;
                    try { ent = tr.GetObject(id, OpenMode.ForRead) as Entity; }
                    catch (System.Exception) { continue; }
                    if (ent == null || ent.IsErased) continue;
                    BlockReference br = ent as BlockReference;
                    if (br != null)
                    {
                        BlockTableRecord def = null;
                        try { def = tr.GetObject(br.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord; }
                        catch (System.Exception) { def = null; }
                        if (def != null && !SkipDefinition(def))
                        {
                            string child = path + " > " + SafeName(def);
                            try { ScanDeep(tr, def, child, toWorld * br.BlockTransform, stack, ref count, samples); }
                            catch (System.Exception) { }
                        }
                        double pz = 0;
                        try { pz = br.Position.Z; } catch (System.Exception) { }
                        if (Math.Abs(pz) > ReportZ)
                            AddSample(ref count, samples, path + " > INSERT " + (def != null ? SafeName(def) : "") + ", katman " + br.Layer + ", Position Z " + Fmt(pz));
                        continue;
                    }
                    double z0, z1;
                    if (!EntityZ(ent, out z0, out z1)) continue;
                    if (Math.Abs(z0) <= ReportZ && Math.Abs(z1) <= ReportZ) continue;
                    string type = ent.GetType().Name;
                    try { string dxf = ent.GetRXClass().DxfName; if (!string.IsNullOrEmpty(dxf)) type = dxf; } catch (System.Exception) { }
                    string layer = "";
                    try { layer = ent.Layer; } catch (System.Exception) { }
                    string line = path + " > " + type + ", katman " + layer + ", Z " + Fmt(z0) + " .. " + Fmt(z1);
                    AddSample(ref count, samples, line);
                }
            }
            finally { stack.Remove(owner.ObjectId); }
        }

        static void AddSample(ref int count, List<string> samples, string line)
        {
            count++;
            if (samples.Count < 5) samples.Add(line);
        }

        static string SafeName(BlockTableRecord btr)
        {
            try { if (!string.IsNullOrEmpty(btr.Name)) return btr.Name; }
            catch (System.Exception) { }
            try { return btr.ObjectId.Handle.ToString(); }
            catch (System.Exception) { return "?"; }
        }

        static bool EntityZ(Entity ent, out double z0, out double z1)
        {
            z0 = 0;
            z1 = 0;
            try
            {
                Line ln = ent as Line;
                if (ln != null) { z0 = Math.Min(ln.StartPoint.Z, ln.EndPoint.Z); z1 = Math.Max(ln.StartPoint.Z, ln.EndPoint.Z); return true; }
                Polyline pl = ent as Polyline;
                if (pl != null) { z0 = z1 = pl.Elevation; return true; }
                Polyline2d p2 = ent as Polyline2d;
                if (p2 != null) { z0 = z1 = p2.Elevation; return true; }
                Circle c = ent as Circle;
                if (c != null) { z0 = z1 = c.Center.Z; return true; }
                Ellipse el = ent as Ellipse;
                if (el != null) { z0 = z1 = el.Center.Z; return true; }
                DBPoint pt = ent as DBPoint;
                if (pt != null) { z0 = z1 = pt.Position.Z; return true; }
                DBText tx = ent as DBText;
                if (tx != null) { z0 = z1 = tx.Position.Z; return true; }
                MText mt = ent as MText;
                if (mt != null) { z0 = z1 = mt.Location.Z; return true; }
                Hatch h = ent as Hatch;
                if (h != null) { z0 = z1 = h.Elevation; return true; }
                Dimension dim = ent as Dimension;
                if (dim != null) { z0 = z1 = dim.Elevation; return true; }
                MLeader ml = ent as MLeader;
                if (ml != null) { z0 = z1 = MLeaderZ(ml); return true; }
                Leader ld = ent as Leader;
                if (ld != null) return LeaderRange(ld, out z0, out z1);
                Arc arc = ent as Arc;
                if (arc != null)
                {
                    z0 = Math.Min(arc.Center.Z, Math.Min(arc.StartPoint.Z, arc.EndPoint.Z));
                    z1 = Math.Max(arc.Center.Z, Math.Max(arc.StartPoint.Z, arc.EndPoint.Z));
                    return true;
                }
                RasterImage img = ent as RasterImage;
                if (img != null) { z0 = z1 = img.Orientation.Origin.Z; return true; }
            }
            catch (System.Exception) { return false; }
            return false;
        }

        static int FlattenImage(RasterImage img, ref int projN)
        {
            CoordinateSystem3d cs = img.Orientation;
            Point3d o = cs.Origin;
            Vector3d x = cs.Xaxis, y = cs.Yaxis;
            if (Math.Abs(o.Z) <= Tol && Math.Abs(x.Z) <= Tol && Math.Abs(y.Z) <= Tol) return 0;
            Vector3d xf = new Vector3d(x.X, x.Y, 0);
            Vector3d yf = new Vector3d(y.X, y.Y, 0);
            if (xf.Length <= Tol) xf = Vector3d.XAxis;
            else xf = xf.GetNormal() * Math.Max(x.Length, Tol);
            if (yf.Length <= Tol) yf = Vector3d.YAxis;
            else yf = yf.GetNormal() * Math.Max(y.Length, Tol);
            Write(img);
            img.Orientation = new CoordinateSystem3d(new Point3d(o.X, o.Y, 0), xf, yf);
            projN++;
            return 1;
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
            {
                try { ent.TransformBy(Matrix3d.Projection(new Plane(Point3d.Origin, Vector3d.ZAxis), Vector3d.ZAxis)); }
                catch (System.Exception) { ent.TransformBy(ProjectToXY); }
            }
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

        static void CopyProp(object src, object dst, string name)
        {
            try
            {
                PropertyInfo p = src.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                if (p == null || !p.CanRead || !p.CanWrite) return;
                if (p.GetIndexParameters().Length != 0) return;
                p.SetValue(dst, p.GetValue(src, null), null);
            }
            catch (System.Exception) { }
        }

        static bool TryMeasure(Transaction tr, Entity ent, out double z, out bool off, out bool uniform)
        {
            z = 0;
            off = false;
            uniform = true;
            if (ent == null || ent.IsErased) return false;
            try
            {
                List<double> zs = new List<double>();
                double a, b;
                if (EntityZ(ent, out a, out b))
                {
                    zs.Add(a);
                    zs.Add(b);
                }
                else
                    CollectExtraZ(tr, ent, zs);
                if (zs.Count == 0) return false;
                double min = zs[0], max = zs[0];
                for (int i = 1; i < zs.Count; i++)
                {
                    if (zs[i] < min) min = zs[i];
                    if (zs[i] > max) max = zs[i];
                }
                uniform = Math.Abs(max - min) <= 1e-6;
                off = Math.Abs(min) > ReportZ || Math.Abs(max) > ReportZ;
                z = Math.Abs(min) >= Math.Abs(max) ? min : max;
                return true;
            }
            catch (System.Exception) { return false; }
        }

        static void CollectExtraZ(Transaction tr, Entity ent, List<double> zs)
        {
            BlockReference br = ent as BlockReference;
            if (br != null)
            {
                try { zs.Add(br.Position.Z); } catch (System.Exception) { }
                return;
            }
            Face face = ent as Face;
            if (face != null)
            {
                for (short i = 0; i < 4; i++)
                    try { zs.Add(face.GetVertexAt(i).Z); } catch (System.Exception) { }
                return;
            }
            Solid solid = ent as Solid;
            if (solid != null)
            {
                for (short i = 0; i < 4; i++)
                    try { zs.Add(solid.GetPointAt(i).Z); } catch (System.Exception) { }
                return;
            }
            Trace trace = ent as Trace;
            if (trace != null)
            {
                for (short i = 0; i < 4; i++)
                    try { zs.Add(trace.GetPointAt(i).Z); } catch (System.Exception) { }
                return;
            }
            Spline sp = ent as Spline;
            if (sp != null)
            {
                try
                {
                    int n = sp.NumControlPoints;
                    for (int i = 0; i < n; i++) zs.Add(sp.GetControlPointAt(i).Z);
                }
                catch (System.Exception) { }
                return;
            }
            Polyline3d p3 = ent as Polyline3d;
            if (p3 != null && tr != null)
            {
                foreach (ObjectId vid in p3)
                {
                    try
                    {
                        PolylineVertex3d v = tr.GetObject(vid, OpenMode.ForRead) as PolylineVertex3d;
                        if (v != null) zs.Add(v.Position.Z);
                    }
                    catch (System.Exception) { }
                }
                return;
            }
            double elev;
            if (TryReadDouble(ent, "Elevation", out elev)) zs.Add(elev);
            foreach (PropertyInfo pi in ent.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (pi.PropertyType != typeof(Point3d) || !pi.CanRead) continue;
                if (pi.GetIndexParameters().Length != 0) continue;
                if (pi.Name == "ViewTarget" || pi.Name == "Target" || pi.Name.IndexOf("View", StringComparison.Ordinal) >= 0) continue;
                try { zs.Add(((Point3d)pi.GetValue(ent, null)).Z); }
                catch (System.Exception) { }
            }
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

        /// <summary>True for a normal within about 0.006 degrees of -Z.</summary>
        static bool IsNearNegativeZ(Vector3d n)
        {
            double len = n.Length;
            if (len <= Tol) return false;
            double x = n.X / len, y = n.Y / len, z = n.Z / len;
            return Math.Abs(x) <= 1e-4 && Math.Abs(y) <= 1e-4 && z < 0;
        }

        /// <summary>True for a normal within about 0.006 degrees of +Z.</summary>
        static bool IsNearPositiveZ(Vector3d n)
        {
            double len = n.Length;
            if (len <= Tol) return false;
            double x = n.X / len, y = n.Y / len, z = n.Z / len;
            return Math.Abs(x) <= 1e-4 && Math.Abs(y) <= 1e-4 && z > 0;
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
