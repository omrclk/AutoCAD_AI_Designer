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
    /// ZSIFIRLA / AIDZSIFIRLA: put every entity in the current space on WCS Z=0
    /// (Model, or the active layout's paper space) so FILLET / CHAMFER / JOIN
    /// stop failing with "non-coplanar". No selection. Block definitions are not opened.
    /// One command and one transaction, so a single UNDO reverts the whole run.
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

            int fixedN = 0, elevN = 0, thickN = 0, skipN = 0, lockedN = 0, errN = 0, total = 0;
            string spaceLabel = "Model";
            try
            {
                using (doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTableRecord space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
                    spaceLabel = SpaceLabel(space);
                    HashSet<ObjectId> locked = LockedLayers(tr, db);

                    foreach (ObjectId id in space)
                    {
                        if (!id.IsValid || id.IsErased) continue;
                        Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent == null || ent.IsErased) continue;
                        total++;
                        if (!ent.LayerId.IsNull && locked.Contains(ent.LayerId)) { lockedN++; continue; }
                        try
                        {
                            int kind = Flatten(tr, ent, ref elevN, ref thickN);
                            if (kind == 1) fixedN++;
                            else if (kind == 2) skipN++;
                        }
                        catch (Autodesk.AutoCAD.Runtime.Exception ex)
                        {
                            if (ex.ErrorStatus == ErrorStatus.OnLockedLayer) lockedN++;
                            else errN++;
                        }
                        catch (System.Exception) { errN++; }
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
                + "Atlanan (3B/düzlemsel olmayan normal): " + skipN + ". "
                + "Kilitli katman: " + lockedN + ". "
                + "Taranan: " + total + ".";
            if (errN > 0) msg += " Hata (değiştirilmedi): " + errN + ".";
            ed.WriteMessage(msg);
            if (elevErr != null)
                ed.WriteMessage("\nELEVATION sistem değişkeni ayarlanamadı: " + elevErr);
            else if (Math.Abs(prevElev) > Tol)
                ed.WriteMessage("\nELEVATION sistem değişkeni " + Fmt(prevElev) + " değerinden 0 yapıldı.");
            else
                ed.WriteMessage("\nELEVATION sistem değişkeni 0.");
            if (!IsWorldUcs())
                ed.WriteMessage("\nUCS Dünya değil; orijin Z = " + Fmt(UcsOriginZ(ed)) + " (UCS değiştirilmedi).");
            ed.WriteMessage("\n");
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

        static HashSet<ObjectId> LockedLayers(Transaction tr, Database db)
        {
            HashSet<ObjectId> locked = new HashSet<ObjectId>();
            LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            foreach (ObjectId id in lt)
            {
                LayerTableRecord ltr = tr.GetObject(id, OpenMode.ForRead) as LayerTableRecord;
                if (ltr != null && ltr.IsLocked) locked.Add(id);
            }
            return locked;
        }

        /// <summary>1 = modified, 2 = skipped as 3D / non-planar normal, 0 = already flat.</summary>
        static int Flatten(Transaction tr, Entity ent, ref int elevN, ref int thickN)
        {
            if (ent is Line) return FlattenLine((Line)ent, ref elevN, ref thickN);
            if (ent is Polyline) return FlattenLw((Polyline)ent, ref elevN, ref thickN);
            if (ent is Polyline2d) return FlattenP2(tr, (Polyline2d)ent, ref elevN, ref thickN);
            if (ent is Polyline3d) return FlattenP3(tr, (Polyline3d)ent, ref elevN, ref thickN);
            if (ent is Circle) return FlattenCircle((Circle)ent, ref elevN, ref thickN); // Arc derives from Circle
            if (ent is Ellipse) return FlattenEllipse((Ellipse)ent, ref elevN, ref thickN);
            if (ent is Spline) return FlattenSpline((Spline)ent, ref elevN, ref thickN);
            if (ent is DBPoint) return FlattenDbPoint((DBPoint)ent, ref elevN, ref thickN);
            if (ent is DBText) return FlattenText((DBText)ent, ref elevN, ref thickN); // AttributeDefinition derives from DBText
            if (ent is MText) return FlattenMText((MText)ent, ref elevN, ref thickN);
            if (ent is MLeader) return FlattenMLeader((MLeader)ent, ref elevN, ref thickN);
            if (ent is Leader) return FlattenLeader((Leader)ent, ref elevN, ref thickN);
            if (ent is Dimension) return FlattenDim((Dimension)ent, ref elevN, ref thickN);
            if (ent is Hatch) return FlattenHatch((Hatch)ent, ref elevN, ref thickN);
            if (ent is BlockReference) return FlattenBlock((BlockReference)ent, ref elevN, ref thickN);
            if (ent is Viewport) return FlattenViewport((Viewport)ent, ref elevN, ref thickN);
            if (ent is Ray) return FlattenRay((Ray)ent, ref elevN, ref thickN);
            if (ent is Xline) return FlattenXline((Xline)ent, ref elevN, ref thickN);
            if (ent is Mline) return FlattenMline((Mline)ent, ref elevN, ref thickN);
            return FlattenFallback(ent, ref elevN, ref thickN);
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
            if (Finish(ln, false, ref elevN, ref thickN)) changed = true;
            return changed ? 1 : 0;
        }

        static int FlattenLw(Polyline pl, ref int elevN, ref int thickN)
        {
            if (!IsWorldZ(pl.Normal)) return 2;
            bool hadElev = HasNonZero(pl, "Elevation");
            bool changed = Finish(pl, hadElev, ref elevN, ref thickN);
            return changed ? 1 : 0;
        }

        static int FlattenP2(Transaction tr, Polyline2d pl, ref int elevN, ref int thickN)
        {
            if (!IsWorldZ(pl.Normal)) return 2;
            bool hadElev = HasNonZero(pl, "Elevation");
            // Elevation first: vertex Z is absolute afterwards, so the two writes do not stack.
            bool changed = Finish(pl, hadElev, ref elevN, ref thickN);
            foreach (ObjectId vid in pl)
            {
                PolylineVertex2d v = tr.GetObject(vid, OpenMode.ForRead) as PolylineVertex2d;
                if (v == null) continue;
                Point3d p = v.Position;
                if (Math.Abs(p.Z) <= Tol) continue;
                Write(pl);
                Write(v);
                v.Position = Flat(p);
                changed = true;
            }
            return changed ? 1 : 0;
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
                Write(v);
                v.Position = Flat(p);
                changed = true;
            }
            if (Finish(pl, false, ref elevN, ref thickN)) changed = true;
            return changed ? 1 : 0;
        }

        static int FlattenCircle(Circle c, ref int elevN, ref int thickN)
        {
            if (!IsWorldZ(c.Normal)) return 2;
            bool changed = MovePoint(c, c.Center, delegate (Point3d p) { c.Center = p; });
            if (Finish(c, false, ref elevN, ref thickN)) changed = true;
            return changed ? 1 : 0;
        }

        static int FlattenEllipse(Ellipse el, ref int elevN, ref int thickN)
        {
            if (!IsWorldZ(el.Normal)) return 2;
            bool changed = MovePoint(el, el.Center, delegate (Point3d p) { el.Center = p; });
            if (Finish(el, false, ref elevN, ref thickN)) changed = true;
            return changed ? 1 : 0;
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
                // Fit tangents with a Z component pull the curve off the XY plane.
                // PurgeFitData makes the control points editable; their XY stays, Z goes to 0.
                try
                {
                    Write(spl);
                    spl.PurgeFitData();
                    if (FlattenControls(spl)) changed = true;
                }
                catch (System.Exception) { }
            }
            if (Finish(spl, false, ref elevN, ref thickN)) changed = true;
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

        static int FlattenDbPoint(DBPoint pt, ref int elevN, ref int thickN)
        {
            // Position is WCS. Normal only directs thickness; thickness is cleared below.
            bool changed = MovePoint(pt, pt.Position, delegate (Point3d p) { pt.Position = p; });
            if (Finish(pt, HasNonZero(pt, "Elevation"), ref elevN, ref thickN)) changed = true;
            return changed ? 1 : 0;
        }

        static int FlattenText(DBText t, ref int elevN, ref int thickN)
        {
            if (!IsWorldZ(t.Normal)) return 2;
            bool changed = MovePoint(t, t.Position, delegate (Point3d p) { t.Position = p; });
            bool aligned = t.HorizontalMode != TextHorizontalMode.TextLeft || t.VerticalMode != TextVerticalMode.TextBase;
            if (aligned)
            {
                try
                {
                    Point3d a = t.AlignmentPoint;
                    if (Math.Abs(a.Z) > Tol)
                    {
                        Write(t);
                        t.AlignmentPoint = Flat(a);
                        changed = true;
                    }
                }
                catch (System.Exception) { }
            }
            if (Finish(t, HasNonZero(t, "Elevation"), ref elevN, ref thickN)) changed = true;
            return changed ? 1 : 0;
        }

        static int FlattenMText(MText mt, ref int elevN, ref int thickN)
        {
            if (!IsWorldZ(mt.Normal)) return 2;
            bool changed = MovePoint(mt, mt.Location, delegate (Point3d p) { mt.Location = p; });
            if (Finish(mt, HasNonZero(mt, "Elevation"), ref elevN, ref thickN)) changed = true;
            return changed ? 1 : 0;
        }

        static int FlattenBlock(BlockReference br, ref int elevN, ref int thickN)
        {
            // Insertion only. The block definition (and anything nested in it) is left untouched.
            if (!IsWorldZ(br.Normal)) return 2;
            bool changed = MovePoint(br, br.Position, delegate (Point3d p) { br.Position = p; });
            if (Finish(br, HasNonZero(br, "Elevation"), ref elevN, ref thickN)) changed = true;
            return changed ? 1 : 0;
        }

        static int FlattenMLeader(MLeader ml, ref int elevN, ref int thickN)
        {
            if (!IsWorldZ(ml.Normal)) return 2;
            bool hadElev = HasNonZero(ml, "Elevation");
            bool changed = false;
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
                        Point3d p;
                        try { p = ml.GetVertex(line, k); } catch (System.Exception) { continue; }
                        if (Math.Abs(p.Z) <= Tol) continue;
                        Write(ml);
                        ml.SetVertex(line, k, Flat(p));
                        changed = true;
                    }
                }
            }
            try
            {
                if (ml.ContentType == ContentType.MTextContent)
                    changed |= MovePoint(ml, ml.TextLocation, delegate (Point3d p) { ml.TextLocation = p; });
                else if (ml.ContentType == ContentType.BlockContent)
                    changed |= MovePoint(ml, ml.BlockPosition, delegate (Point3d p) { ml.BlockPosition = p; });
                else if (ml.ContentType == ContentType.ToleranceContent)
                    changed |= MovePointProp(ml, "ToleranceLocation");
            }
            catch (System.Exception) { }
            if (Finish(ml, hadElev, ref elevN, ref thickN)) changed = true;
            return changed ? 1 : 0;
        }

        static int FlattenLeader(Leader ld, ref int elevN, ref int thickN)
        {
            Vector3d leaderNormal;
            if (TryNormal(ld, out leaderNormal) && !IsWorldZ(leaderNormal)) return 2;
            bool changed = false;
            int nv = ld.NumVertices;
            for (int i = 0; i < nv; i++)
            {
                Point3d p = ld.VertexAt(i);
                if (Math.Abs(p.Z) <= Tol) continue;
                Write(ld);
                ld.SetVertexAt(i, Flat(p));
                changed = true;
            }
            if (Finish(ld, HasNonZero(ld, "Elevation"), ref elevN, ref thickN)) changed = true;
            return changed ? 1 : 0;
        }

        static int FlattenDim(Dimension dim, ref int elevN, ref int thickN)
        {
            if (!IsWorldZ(dim.Normal)) return 2;
            bool hadElev = HasNonZero(dim, "Elevation");
            bool changed = Finish(dim, hadElev, ref elevN, ref thickN);
            bool skipText = false;
            try { skipText = dim.UsingDefaultTextPosition; } catch (System.Exception) { }
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
                if (Math.Abs(p.Z) <= Tol) continue;
                try
                {
                    Write(dim);
                    pi.SetValue(dim, Flat(p), null);
                    changed = true;
                }
                catch (System.Exception) { }
            }
            return changed ? 1 : 0;
        }

        static int FlattenHatch(Hatch h, ref int elevN, ref int thickN)
        {
            if (!IsWorldZ(h.Normal)) return 2;
            bool changed = Finish(h, HasNonZero(h, "Elevation"), ref elevN, ref thickN);
            return changed ? 1 : 0;
        }

        static int FlattenViewport(Viewport vp, ref int elevN, ref int thickN)
        {
            // Paper-space center only. ViewTarget is the model view and must stay.
            bool changed = false;
            try
            {
                Point3d c = vp.CenterPoint;
                if (Math.Abs(c.Z) > Tol)
                {
                    Write(vp);
                    vp.CenterPoint = Flat(c);
                    changed = true;
                }
            }
            catch (System.Exception) { }
            if (Finish(vp, HasNonZero(vp, "Elevation"), ref elevN, ref thickN)) changed = true;
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
            if (Finish(ent, HasNonZero(ent, "Elevation"), ref elevN, ref thickN)) changed = true;
            return changed ? 1 : 0;
        }

        static int FlattenMline(Mline ml, ref int elevN, ref int thickN)
        {
            // MoveVertexAt projects onto the existing plane, so it cannot change elevation.
            // A Z-normal mline is a single Z; translate the whole entity.
            if (!IsWorldZ(ml.Normal)) return 2;
            int n = ml.NumberOfVertices;
            bool changed = false;
            if (n > 0)
            {
                double z0 = ml.VertexAt(0).Z;
                for (int i = 1; i < n; i++)
                    if (Math.Abs(ml.VertexAt(i).Z - z0) > Tol) return 2;
                if (Math.Abs(z0) > Tol)
                {
                    Write(ml);
                    ml.TransformBy(Matrix3d.Displacement(new Vector3d(0, 0, -z0)));
                    changed = true;
                }
            }
            if (Finish(ml, HasNonZero(ml, "Elevation"), ref elevN, ref thickN)) changed = true;
            return changed ? 1 : 0;
        }

        /// <summary>
        /// Entities without a dedicated flattener: refuse a tilted normal, otherwise translate
        /// when GeometricExtents has a single Z. Varying Z is left alone and counted as 3D.
        /// </summary>
        static int FlattenFallback(Entity ent, ref int elevN, ref int thickN)
        {
            Vector3d n;
            if (TryNormal(ent, out n) && !IsWorldZ(n)) return 2;
            double elev;
            bool hasElev = TryReadDouble(ent, "Elevation", out elev);
            bool hadElev = hasElev && Math.Abs(elev) > Tol;
            bool changed = false;
            bool extentsOk = false;
            double z = 0, zSpan = 0;
            try
            {
                Extents3d ex = ent.GeometricExtents;
                z = ex.MinPoint.Z;
                zSpan = Math.Abs(ex.MaxPoint.Z - ex.MinPoint.Z);
                extentsOk = true;
            }
            catch (System.Exception) { }
            if (extentsOk && zSpan > Tol) return 2;
            // When elevation already equals the flat Z, setting Elevation is the move.
            // Translating as well would shift the entity twice.
            bool elevExplainsZ = hadElev && extentsOk && Math.Abs(elev - z) <= 1e-6;
            if (extentsOk && !elevExplainsZ && Math.Abs(z) > Tol)
            {
                Write(ent);
                ent.TransformBy(Matrix3d.Displacement(new Vector3d(0, 0, -z)));
                changed = true;
            }
            if (Finish(ent, hadElev, ref elevN, ref thickN)) changed = true;
            return changed ? 1 : 0;
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

        /// <summary>hadElev counts a kot that a prior edit already brought to 0.</summary>
        static bool Finish(Entity ent, bool hadElev, ref int elevN, ref int thickN)
        {
            bool changed = false;
            if (hadElev || HasNonZero(ent, "Elevation"))
            {
                ZeroDoubleProp(ent, "Elevation");
                if (!HasNonZero(ent, "Elevation"))
                {
                    elevN++;
                    changed = true;
                }
            }
            if (ZeroDoubleProp(ent, "Thickness"))
            {
                thickN++;
                changed = true;
            }
            return changed;
        }

        static bool HasNonZero(Entity ent, string name)
        {
            try
            {
                PropertyInfo p = Prop(ent, name);
                if (p == null) return false;
                double v = (double)p.GetValue(ent, null);
                return Math.Abs(v) > Tol;
            }
            catch (System.Exception) { return false; }
        }

        static bool ZeroDoubleProp(Entity ent, string name)
        {
            try
            {
                PropertyInfo p = Prop(ent, name);
                if (p == null) return false;
                double v = (double)p.GetValue(ent, null);
                if (Math.Abs(v) <= Tol) return false;
                Write(ent);
                p.SetValue(ent, 0.0, null);
                return true;
            }
            catch (System.Exception) { return false; }
        }

        static PropertyInfo Prop(Entity ent, string name)
        {
            PropertyInfo p = ent.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (p == null || p.PropertyType != typeof(double) || !p.CanRead || !p.CanWrite) return null;
            if (p.GetIndexParameters().Length != 0) return null;
            return p;
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

        static bool IsWorldZ(Vector3d n)
        {
            double len = n.Length;
            if (len <= Tol) return false;
            double x = Math.Abs(n.X / len), y = Math.Abs(n.Y / len), z = Math.Abs(n.Z / len);
            return x <= 1e-8 && y <= 1e-8 && Math.Abs(z - 1.0) <= 1e-8;
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
