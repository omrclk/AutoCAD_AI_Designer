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
    /// <summary>One reference point pair: the same physical point on the source plan and on the target plan (WCS).</summary>
    internal class RefPair
    {
        public string Name = "";
        public double[] Source = new double[3], Target = new double[3];
        public Point3d S { get { return new Point3d(Source[0], Source[1], Source[2]); } }
        public Point3d T { get { return new Point3d(Target[0], Target[1], Target[2]); } }
    }

    /// <summary>Reference point pairs per drawing (kept for the session) and the plan-to-plan transform they define.</summary>
    internal static class RefPoints
    {
        static readonly Dictionary<string, List<RefPair>> store = new Dictionary<string, List<RefPair>>(StringComparer.OrdinalIgnoreCase);
        public static event Action Changed;
        /// <summary>Mode the AI asked for ("Kopyala"/"Tasi"); used as the default of AIDPLANKOPYA once.</summary>
        public static string PendingMode;

        static string Key(Document d) { return d == null ? "" : d.Name; }

        public static List<RefPair> For(Document d)
        {
            List<RefPair> l;
            return store.TryGetValue(Key(d), out l) ? new List<RefPair>(l) : new List<RefPair>();
        }

        public static void Set(Document d, List<RefPair> pairs)
        {
            for (int i = 0; i < pairs.Count; i++) pairs[i].Name = "R" + (i + 1);
            store[Key(d)] = new List<RefPair>(pairs);
            Action h = Changed; if (h != null) h();
        }

        static string F(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

        /// <summary>Translation (1 pair) or rotation about the first source point + translation (+ scale ratio) from 2 pairs.</summary>
        public static bool Compute(List<RefPair> p, out double rotDeg, out double scale, out double[] offset)
        {
            rotDeg = 0; scale = 1; offset = null;
            if (p == null || p.Count == 0) return false;
            Vector3d off = p[0].T - p[0].S;
            offset = new[] { Math.Round(off.X, 4), Math.Round(off.Y, 4), Math.Round(off.Z, 4) };
            if (p.Count >= 2)
            {
                Vector3d vs = p[1].S - p[0].S, vt = p[1].T - p[0].T;
                double ls = Math.Sqrt(vs.X * vs.X + vs.Y * vs.Y), lt = Math.Sqrt(vt.X * vt.X + vt.Y * vt.Y);
                if (ls < 1e-9 || lt < 1e-9) return false;
                double a = Math.Atan2(vt.Y, vt.X) - Math.Atan2(vs.Y, vs.X);
                while (a > Math.PI) a -= 2 * Math.PI;
                while (a <= -Math.PI) a += 2 * Math.PI;
                rotDeg = Math.Round(a * 180.0 / Math.PI, 6);
                scale = Math.Round(lt / ls, 6);
            }
            return true;
        }

        public static Matrix3d Matrix(List<RefPair> p, bool applyScale)
        {
            double rot, sc; double[] off;
            Compute(p, out rot, out sc, out off);
            Matrix3d m = Matrix3d.Identity;
            if (p.Count >= 2)
            {
                if (applyScale && Math.Abs(sc - 1) > 1e-9) m = Matrix3d.Scaling(sc, p[0].S) * m;
                if (Math.Abs(rot) > 1e-9) m = Matrix3d.Rotation(rot * Math.PI / 180.0, Vector3d.ZAxis, p[0].S) * m;
            }
            return Matrix3d.Displacement(p[0].T - p[0].S) * m;
        }

        public static string Summary(Document d)
        {
            List<RefPair> p = For(d);
            if (p.Count == 0) return "Referans noktas\u0131 yok";
            double rot, sc; double[] off;
            Compute(p, out rot, out sc, out off);
            string s = p.Count + " \u00e7ift: " + string.Join("; ", p.Select(x => x.Name + " (" + F(x.Source[0]) + "," + F(x.Source[1]) + ") \u2192 (" + F(x.Target[0]) + "," + F(x.Target[1]) + ")"));
            s += "\nOffset dX=" + F(off[0]) + " dY=" + F(off[1]) + " dZ=" + F(off[2]);
            if (p.Count >= 2) s += ", d\u00f6nme=" + F(rot) + "\u00b0, \u00f6l\u00e7ek=" + F(sc);
            return s;
        }

        public static void AddTo(Dictionary<string, object> json, Document d)
        {
            List<RefPair> p = For(d);
            if (p.Count == 0)
            {
                json["reference_points"] = new object[0];
                json["reference_points_hint"] = "No reference points set. To align/copy objects between two plans tell the user (Turkish) to press the 'Referans noktalar\u0131' button in the panel (or type RN) and pick one point on the source plan and the SAME physical point on the target plan with the mouse. Never ask for typed coordinates.";
                return;
            }
            json["reference_points"] = p.Select(x => (object)new Dictionary<string, object> { { "name", x.Name }, { "source", x.Source }, { "target", x.Target } }).ToList();
            double rot, sc; double[] off;
            if (Compute(p, out rot, out sc, out off))
            {
                Dictionary<string, object> t = new Dictionary<string, object> { { "pairs", p.Count }, { "offset", off } };
                if (p.Count >= 2) { t["rotation_deg"] = rot; t["scale"] = sc; t["rule"] = "target = Translate(offset) * Rotate(rotation_deg about source[0]) * Scale(scale about source[0]) applied to a source point"; }
                else t["rule"] = "target = source + offset (no rotation/scale known)";
                json["reference_transform"] = t;
            }
        }
    }

    public class RefCommands
    {
        static bool Pick(Editor ed, string msg, out Point3d wcs)
        {
            wcs = Point3d.Origin;
            PromptPointResult r = ed.GetPoint(new PromptPointOptions(msg) { AllowNone = false });
            if (r.Status != PromptStatus.OK) return false;
            wcs = r.Value.TransformBy(ed.CurrentUserCoordinateSystem);
            return true;
        }

        [CommandMethod("AIDREFNOKTA", CommandFlags.Modal)]
        [CommandMethod("RN", CommandFlags.Modal)]
        public void Ref()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            List<RefPair> pairs = RefPoints.For(doc);
            if (pairs.Count > 0)
            {
                ed.WriteMessage("\n" + RefPoints.Summary(doc));
                PromptKeywordOptions ko = new PromptKeywordOptions("\n[Ekle/Yeniden/Bitir] <Bitir>: ", "Ekle Yeniden Bitir") { AllowNone = true };
                if (pairs.Count >= 2) ko = new PromptKeywordOptions("\n[Yeniden/Bitir] <Bitir>: ", "Yeniden Bitir") { AllowNone = true };
                PromptResult kr = ed.GetKeywords(ko);
                if (kr.Status != PromptStatus.OK || kr.StringResult == "Bitir") return;
                if (kr.StringResult == "Yeniden") pairs.Clear();
            }
            while (pairs.Count < 2)
            {
                int n = pairs.Count + 1;
                Point3d s, t;
                if (!Pick(ed, "\nKaynak plan referans noktas\u0131 " + n + " (osnap kullan\u0131labilir): ", out s)) break;
                if (!Pick(ed, "\nHedef plan referans noktas\u0131 " + n + " (ayn\u0131 fiziksel nokta): ", out t)) break;
                if (n == 2 && (s - pairs[0].S).Length < 1e-6) { ed.WriteMessage("\nikinci kaynak nokta birincisiyle ayn\u0131; atland\u0131."); break; }
                RefPair p = new RefPair { Source = new[] { Math.Round(s.X, 4), Math.Round(s.Y, 4), Math.Round(s.Z, 4) }, Target = new[] { Math.Round(t.X, 4), Math.Round(t.Y, 4), Math.Round(t.Z, 4) } };
                pairs.Add(p);
                RefPoints.Set(doc, pairs);
                if (n == 1)
                {
                    PromptKeywordOptions ko = new PromptKeywordOptions("\n\u0130kinci \u00e7ift (d\u00f6nme/\u00f6l\u00e7ek i\u00e7in) eklensin mi? [Evet/Hayir] <Hayir>: ", "Evet Hayir") { AllowNone = true };
                    PromptResult kr = ed.GetKeywords(ko);
                    if (kr.Status != PromptStatus.OK || kr.StringResult != "Evet") break;
                }
            }
            if (pairs.Count > 0) { RefPoints.Set(doc, pairs); ed.WriteMessage("\nKaydedildi: " + RefPoints.Summary(doc) + "\n"); }
        }

        [CommandMethod("AIDPLANKOPYA", CommandFlags.Modal | CommandFlags.UsePickSet)]
        [CommandMethod("PKOPYA", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void PlanCopy()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor; Database db = doc.Database;
            string pending = RefPoints.PendingMode; RefPoints.PendingMode = null;
            List<RefPair> pairs = RefPoints.For(doc);
            if (pairs.Count == 0) { ed.WriteMessage("\n\u00d6nce referans noktalar\u0131n\u0131 se\u00e7in: AIDREFNOKTA (RN) veya panelde 'Referans noktalar\u0131' d\u00fc\u011fmesi.\n"); return; }

            List<ObjectId> ids;
            PromptSelectionResult sr = ed.SelectImplied();
            if (sr.Status == PromptStatus.OK) ids = sr.Value.GetObjectIds().ToList();
            else
            {
                bool byLayer = false;
                PromptSelectionOptions so = new PromptSelectionOptions { MessageForAdding = "\nKaynak plandan kopyalanacak nesneleri se\u00e7in veya [Katman]: " };
                so.Keywords.Add("Katman");
                so.KeywordInput += (o, e) => { byLayer = true; throw new Autodesk.AutoCAD.Runtime.Exception(ErrorStatus.OK); };
                try { sr = ed.GetSelection(so); } catch (Autodesk.AutoCAD.Runtime.Exception) { if (!byLayer) throw; }
                if (byLayer)
                {
                    PromptResult lr = ed.GetString(new PromptStringOptions("\nKatman ad\u0131(lar\u0131) (virg\u00fclle; * = t\u00fcm\u00fc): ") { AllowSpaces = true });
                    if (lr.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(lr.StringResult)) return;
                    string ls = lr.StringResult.Trim();
                    List<TypedValue> f = new List<TypedValue>();
                    if (ls != "*") f.Add(new TypedValue((int)DxfCode.LayerName, ls));
                    sr = f.Count > 0 ? ed.SelectAll(new SelectionFilter(f.ToArray())) : ed.SelectAll();
                }
                if (sr.Status != PromptStatus.OK) return;
                ids = sr.Value.GetObjectIds().ToList();
            }
            if (ids.Count == 0) return;

            double rot, sc; double[] off;
            if (!RefPoints.Compute(pairs, out rot, out sc, out off)) { ed.WriteMessage("\nReferans noktalar\u0131 ge\u00e7ersiz (iki kaynak nokta ayn\u0131?).\n"); return; }
            bool applyScale = false;
            if (pairs.Count >= 2 && Math.Abs(sc - 1) > 0.001)
            {
                PromptKeywordOptions so2 = new PromptKeywordOptions("\n\u00d6l\u00e7ek fark\u0131 " + sc.ToString("0.#####", CultureInfo.InvariantCulture) + " (iki plan farkl\u0131 \u00f6l\u00e7ekli). Uygulans\u0131n m\u0131? [Evet/Hayir] <Hayir>: ", "Evet Hayir") { AllowNone = true };
                PromptResult r2 = ed.GetKeywords(so2);
                if (r2.Status != PromptStatus.OK) return;
                applyScale = r2.StringResult == "Evet";
            }
            string def = pending == "Tasi" ? "Tasi" : "Kopyala";
            ed.WriteMessage("\n" + ids.Count + " nesne, " + RefPoints.Summary(doc));
            PromptKeywordOptions mo = new PromptKeywordOptions("\n[Kopyala/Tasi] <" + def + ">: ", "Kopyala Tasi") { AllowNone = true };
            PromptResult mr = ed.GetKeywords(mo);
            if (mr.Status != PromptStatus.OK && mr.Status != PromptStatus.None) return;
            bool move = mr.Status == PromptStatus.OK ? mr.StringResult == "Tasi" : def == "Tasi";

            Matrix3d m = RefPoints.Matrix(pairs, applyScale);
            int done = 0, skipped = 0;
            List<ObjectId> result = new List<ObjectId>();
            using (doc.LockDocument())
            using (Transaction tx = db.TransactionManager.StartTransaction())
            {
                foreach (IGrouping<ObjectId, ObjectId> g in ids.Where(i => !i.IsErased).GroupBy(i =>
                {
                    Entity e0 = tx.GetObject(i, OpenMode.ForRead) as Entity; return e0 == null ? ObjectId.Null : e0.OwnerId;
                }))
                {
                    if (g.Key.IsNull) { skipped += g.Count(); continue; }
                    if (move)
                    {
                        foreach (ObjectId id in g)
                        {
                            try { Entity e = (Entity)tx.GetObject(id, OpenMode.ForWrite); e.TransformBy(m); result.Add(id); done++; }
                            catch (Autodesk.AutoCAD.Runtime.Exception) { skipped++; }
                        }
                    }
                    else
                    {
                        ObjectIdCollection col = new ObjectIdCollection(g.ToArray());
                        IdMapping map = new IdMapping();
                        db.DeepCloneObjects(col, g.Key, map, false);
                        foreach (ObjectId src in g)
                        {
                            if (!map.Contains(src)) { skipped++; continue; }
                            ObjectId nid = map[src].Value;
                            try { Entity e = (Entity)tx.GetObject(nid, OpenMode.ForWrite); e.TransformBy(m); result.Add(nid); done++; }
                            catch (Autodesk.AutoCAD.Runtime.Exception)
                            {
                                skipped++;
                                try { DBObject o = tx.GetObject(nid, OpenMode.ForWrite); o.Erase(); } catch (Autodesk.AutoCAD.Runtime.Exception) { }
                            }
                        }
                    }
                }
                tx.Commit();
            }
            ed.WriteMessage("\n" + done + " nesne " + (move ? "ta\u015f\u0131nd\u0131" : "kopyaland\u0131") + (skipped > 0 ? ", " + skipped + " nesne atland\u0131 (kilitli katman vb.)" : "") + ". Geri almak i\u00e7in UNDO.\n");
            try { if (result.Count > 0) ed.SetImpliedSelection(result.ToArray()); } catch (System.Exception) { }
        }

        /// <summary>Test helper: prints where the first block reference named like the argument ended up (position, rotation, attributes).</summary>
        [CommandMethod("AIDREFDIAG", CommandFlags.Modal)]
        public void Diag()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            using (Transaction tx = doc.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord ms = (BlockTableRecord)tx.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(doc.Database), OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    BlockReference br = tx.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (br == null) continue;
                    string s = "REF " + br.Handle + " " + br.Layer + " pos=" + br.Position.X.ToString("0.###", CultureInfo.InvariantCulture) + "," + br.Position.Y.ToString("0.###", CultureInfo.InvariantCulture) + " rot=" + (br.Rotation * 180 / Math.PI).ToString("0.###", CultureInfo.InvariantCulture);
                    foreach (ObjectId aid in br.AttributeCollection) { AttributeReference ar = tx.GetObject(aid, OpenMode.ForRead) as AttributeReference; if (ar != null) s += " att[" + ar.Tag + "@" + ar.Position.X.ToString("0.#", CultureInfo.InvariantCulture) + "," + ar.Position.Y.ToString("0.#", CultureInfo.InvariantCulture) + "]"; }
                    doc.Editor.WriteMessage("\n" + s);
                }
                tx.Commit();
            }
        }
    }
}
