using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace CadAiDesigner
{
    /// <summary>AIDOLCU / OLC: continuous dimension chain through the centers of selected blocks.</summary>
    public class OlcuCommands
    {
        static bool useCenter = true, total = false;
        const double MergeTol = 1.0;

        /// <summary>Center of the block's own geometry (attributes excluded) in WCS; falls back to the reference extents.</summary>
        internal static Point3d Center(Transaction tx, BlockReference br)
        {
            try
            {
                BlockTableRecord btr = (BlockTableRecord)tx.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                bool any = false; Extents3d ex = new Extents3d();
                foreach (ObjectId id in btr)
                {
                    Entity e = tx.GetObject(id, OpenMode.ForRead) as Entity;
                    if (e == null || e is AttributeDefinition || !e.Visible) continue;
                    try { Extents3d x = e.GeometricExtents; if (!any) { ex = x; any = true; } else ex.AddExtents(x); } catch (System.Exception) { }
                }
                if (any)
                {
                    Point3d c = new Point3d((ex.MinPoint.X + ex.MaxPoint.X) / 2, (ex.MinPoint.Y + ex.MaxPoint.Y) / 2, (ex.MinPoint.Z + ex.MaxPoint.Z) / 2);
                    return c.TransformBy(br.BlockTransform);
                }
            }
            catch (System.Exception) { }
            try { Extents3d x = br.GeometricExtents; return x.MinPoint + (x.MaxPoint - x.MinPoint) / 2; } catch (System.Exception) { return br.Position; }
        }

        static string F(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }

        [CommandMethod("AIDOLCU", CommandFlags.Modal | CommandFlags.UsePickSet)]
        [CommandMethod("OLC", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void Olcu()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor; Database db = doc.Database;
            SelectionFilter filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "INSERT") });

            List<ObjectId> ids = null;
            PromptSelectionResult sr = ed.SelectImplied();
            if (sr.Status == PromptStatus.OK)
            {
                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    ids = sr.Value.GetObjectIds().Where(i => tx.GetObject(i, OpenMode.ForRead) is BlockReference).ToList();
                    tx.Commit();
                }
                if (ids.Count < 2) ids = null;
            }
            while (ids == null)
            {
                bool kw = false; string word = null;
                PromptSelectionOptions so = new PromptSelectionOptions
                {
                    MessageForAdding = "\n\u00d6l\u00e7\u00fclecek bloklar\u0131 se\u00e7in [Nokta=" + (useCenter ? "Merkez" : "Ekleme") + "/Toplam=" + (total ? "Evet" : "Hayir") + "/Katman]: "
                };
                so.Keywords.Add("Nokta"); so.Keywords.Add("Toplam"); so.Keywords.Add("Katman");
                so.MessageForAdding += so.Keywords.GetDisplayString(false).Length > 0 ? "" : "";
                so.KeywordInput += (o, e) => { kw = true; word = e.Input; throw new Autodesk.AutoCAD.Runtime.Exception(ErrorStatus.OK); };
                try { sr = ed.GetSelection(so, filter); } catch (Autodesk.AutoCAD.Runtime.Exception) { if (!kw) throw; }
                if (kw)
                {
                    if (word == "Nokta")
                    {
                        PromptKeywordOptions k = new PromptKeywordOptions("\n\u00d6l\u00e7\u00fc noktas\u0131 [Merkez/Ekleme] <" + (useCenter ? "Merkez" : "Ekleme") + ">: ", "Merkez Ekleme") { AllowNone = true };
                        PromptResult r = ed.GetKeywords(k); if (r.Status == PromptStatus.OK) useCenter = r.StringResult == "Merkez";
                        continue;
                    }
                    if (word == "Toplam")
                    {
                        PromptKeywordOptions k = new PromptKeywordOptions("\nToplam \u00f6l\u00e7\u00fc eklensin mi? [Evet/Hayir] <" + (total ? "Evet" : "Hayir") + ">: ", "Evet Hayir") { AllowNone = true };
                        PromptResult r = ed.GetKeywords(k); if (r.Status == PromptStatus.OK) total = r.StringResult == "Evet";
                        continue;
                    }
                    PromptResult lr = ed.GetString(new PromptStringOptions("\nKatman ad\u0131(lar\u0131) (virg\u00fclle): ") { AllowSpaces = true });
                    if (lr.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(lr.StringResult)) return;
                    sr = ed.SelectAll(new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "INSERT"), new TypedValue((int)DxfCode.LayerName, lr.StringResult.Trim()) }));
                }
                if (sr.Status != PromptStatus.OK) return;
                ids = sr.Value.GetObjectIds().ToList();
                if (ids.Count < 2) { ed.WriteMessage("\nEn az 2 blok gerekli."); ids = null; }
            }

            using (doc.LockDocument())
            using (Transaction tx = db.TransactionManager.StartTransaction())
            {
                // 1) points (merged when nearly identical)
                List<Point3d> pts = new List<Point3d>();
                foreach (ObjectId id in ids)
                {
                    BlockReference br = tx.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (br == null) continue;
                    Point3d p = useCenter ? Center(tx, br) : br.Position;
                    p = new Point3d(p.X, p.Y, 0);
                    if (!pts.Any(q => q.DistanceTo(p) <= MergeTol)) pts.Add(p);
                }
                if (pts.Count < 2) { ed.WriteMessage("\nFarkl\u0131 konumda en az 2 blok gerekli."); return; }

                // 2) dominant direction (principal axis of the points)
                double mx = pts.Average(p => p.X), my = pts.Average(p => p.Y);
                double sxx = pts.Sum(p => (p.X - mx) * (p.X - mx)), syy = pts.Sum(p => (p.Y - my) * (p.Y - my)), sxy = pts.Sum(p => (p.X - mx) * (p.Y - my));
                double ang = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
                Vector3d u = new Vector3d(Math.Cos(ang), Math.Sin(ang), 0);
                if (u.X < -1e-9 || (Math.Abs(u.X) < 1e-9 && u.Y < 0)) { u = -u; ang = Math.Atan2(u.Y, u.X); }
                // snap to exact horizontal/vertical when within 0.5 deg
                double deg = ang * 180 / Math.PI;
                foreach (double s in new[] { -90.0, 0.0, 90.0 }) if (Math.Abs(deg - s) < 0.5) { ang = s * Math.PI / 180; u = new Vector3d(Math.Cos(ang), Math.Sin(ang), 0); }
                Vector3d n = u.GetPerpendicularVector(); // +90 deg
                Point3d m = new Point3d(mx, my, 0);
                pts = pts.OrderBy(p => (p - m).DotProduct(u)).ToList();

                // 3) offset of the dimension line
                double txt = db.Dimtxt * (db.Dimscale > 0 ? db.Dimscale : 1);
                double spread = pts.Max(p => Math.Abs((p - m).DotProduct(n)));
                double len = (pts[pts.Count - 1] - pts[0]).DotProduct(u);
                double def = spread + Math.Max(4 * txt, 0.05 * len);
                PromptPointOptions po = new PromptPointOptions("\n\u00d6l\u00e7\u00fc \u00e7izgisi konumu (taraf/mesafe) <" + F(def) + " \u00fcstte>: ") { AllowNone = true, UseBasePoint = true, BasePoint = pts[0].TransformBy(ed.CurrentUserCoordinateSystem.Inverse()) };
                PromptPointResult pr = ed.GetPoint(po);
                double off;
                if (pr.Status == PromptStatus.OK) off = (pr.Value.TransformBy(ed.CurrentUserCoordinateSystem) - m).DotProduct(n);
                else if (pr.Status == PromptStatus.None) off = def;
                else return;
                if (Math.Abs(off) < 1e-6) off = def;

                // 4) dimensions (current dimstyle, current layer)
                BlockTableRecord space = (BlockTableRecord)tx.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                Point3d line = m + n * off;
                List<double> vals = new List<double>();
                for (int i = 0; i + 1 < pts.Count; i++)
                {
                    RotatedDimension d = new RotatedDimension(ang, pts[i], pts[i + 1], line, "", db.Dimstyle);
                    d.SetDatabaseDefaults(db);
                    space.AppendEntity(d); tx.AddNewlyCreatedDBObject(d, true);
                    vals.Add((pts[i + 1] - pts[i]).DotProduct(u));
                }
                if (total && pts.Count > 2)
                {
                    double gap = Math.Max(db.Dimdli * (db.Dimscale > 0 ? db.Dimscale : 1), 2 * txt);
                    RotatedDimension d = new RotatedDimension(ang, pts[0], pts[pts.Count - 1], line + n * Math.Sign(off) * gap, "", db.Dimstyle);
                    d.SetDatabaseDefaults(db);
                    space.AppendEntity(d); tx.AddNewlyCreatedDBObject(d, true);
                }
                tx.Commit();
                ed.WriteMessage("\n" + vals.Count + " \u00f6l\u00e7\u00fc olu\u015fturuldu (" + ids.Count + " blok, " + pts.Count + " nokta, a\u00e7\u0131 " + F(ang * 180 / Math.PI) + "\u00b0" + (total && pts.Count > 2 ? ", +toplam " + F((pts[pts.Count - 1] - pts[0]).DotProduct(u)) : "") + "): " + string.Join(" | ", vals.Select(F)) + ". Geri almak i\u00e7in UNDO.\n");
            }
        }
    }
}
