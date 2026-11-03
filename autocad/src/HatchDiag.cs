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
    /// <summary>Diagnostics: does each tray's hatch pattern span its whole outline?</summary>
    public class HatchDiagCommands
    {
        [CommandMethod("AIDHATCHDIAG", CommandFlags.Modal)]
        public void Diag()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            using (Transaction tx = doc.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord ms = (BlockTableRecord)tx.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(doc.Database), OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    BlockReference br = tx.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (br == null || Fittings.EffName(tx, br) != "cabletray") continue;
                    ed.WriteMessage("\n" + Report(tx, br));
                }
                tx.Commit();
            }
        }

        /// <summary>Test helper: simulates a later grip edit by setting BOY on every tray (factor, separate transaction each).</summary>
        [CommandMethod("AIDHATCHSTRETCHTEST", CommandFlags.Modal)]
        public void StretchTest()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            PromptDoubleOptions po = new PromptDoubleOptions("\nBOY \u00e7arpan\u0131") { DefaultValue = 0.7, UseDefaultValue = true };
            PromptDoubleResult pr = doc.Editor.GetDouble(po); if (pr.Status != PromptStatus.OK) return;
            System.Collections.Generic.List<ObjectId> ids = Trays(doc.Database);
            foreach (ObjectId id in ids)
                using (Transaction tx = doc.Database.TransactionManager.StartTransaction())
                {
                    BlockReference br = (BlockReference)tx.GetObject(id, OpenMode.ForWrite);
                    foreach (DynamicBlockReferenceProperty p in br.DynamicBlockReferencePropertyCollection)
                        if (p.PropertyName == "BOY") p.Value = Math.Round(Convert.ToDouble(p.Value) * pr.Value);
                    tx.Commit();
                }
            doc.Editor.WriteMessage("\n" + ids.Count + " tava BOY de\u011fi\u015ftirildi.");
        }

        /// <summary>Repairs trays with a broken hatch: each cabletray is re-inserted fresh from the dynamic definition
        /// keeping position, rotation, layer, attributes, EN and BOY.</summary>
        [CommandMethod("AIDHATCHFIX", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void Fix()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            System.Collections.Generic.List<ObjectId> ids;
            PromptSelectionResult psr = ed.SelectImplied();
            if (psr.Status != PromptStatus.OK)
            {
                PromptSelectionOptions so = new PromptSelectionOptions { MessageForAdding = "\nOnar\u0131lacak tavalar\u0131 se\u00e7in [Tumu]: " };
                so.Keywords.Add("Tumu"); bool all = false;
                so.KeywordInput += (o, e) => { all = true; throw new Autodesk.AutoCAD.Runtime.Exception(ErrorStatus.OK); };
                try { psr = ed.GetSelection(so); } catch (Autodesk.AutoCAD.Runtime.Exception) { }
                if (!all && (psr == null || psr.Status != PromptStatus.OK)) return;
                ids = all ? Trays(doc.Database) : psr.Value.GetObjectIds().ToList();
            }
            else ids = psr.Value.GetObjectIds().ToList();
            int n = 0; System.Collections.Generic.List<string> notes = new System.Collections.Generic.List<string>();
            using (doc.LockDocument())
            using (Transaction tx = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    BlockReference br = tx.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (br == null || br.IsErased || !br.IsDynamicBlock || Fittings.EffName(tx, br) != "cabletray") continue;
                    BlockTableRecord space = (BlockTableRecord)tx.GetObject(br.OwnerId, OpenMode.ForWrite);
                    BlockAlong.Recreate(tx, space, br, "BOY", null, notes);
                    n++;
                }
                tx.Commit();
            }
            ed.WriteMessage("\n" + n + " tava yeniden olu\u015fturuldu (hatch onar\u0131ld\u0131).");
            foreach (string x in notes.Distinct().Take(5)) ed.WriteMessage("\n" + x);
        }

        static System.Collections.Generic.List<ObjectId> Trays(Database db)
        {
            System.Collections.Generic.List<ObjectId> r = new System.Collections.Generic.List<ObjectId>();
            using (Transaction tx = db.TransactionManager.StartTransaction())
            {
                BlockTableRecord ms = (BlockTableRecord)tx.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                foreach (ObjectId id in ms) { BlockReference br = tx.GetObject(id, OpenMode.ForRead) as BlockReference; if (br != null && Fittings.EffName(tx, br) == "cabletray") r.Add(id); }
                tx.Commit();
            }
            return r;
        }

        /// <summary>Diagnostic: builds the AI context for ALL model-space objects and prints how many were sent and the JSON size.</summary>
        /// <summary>Console test: inserts cabletray copies with given EN/BOY/YUKSEKLIK/KALINLIK on layers and prints the takeoff.</summary>
        [CommandMethod("AIDTAVAKESIFTEST", CommandFlags.Modal)]
        public void TavaKesifTest()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument; Database db = doc.Database; Editor ed = doc.Editor;
            object[][] cases = {
                new object[]{"E_CT_KIRMIZI",5.0,250.0,"60","1,5"}, new object[]{"E_CT_MAVI",5.0,100.0,"60.0","1.50"},
                new object[]{"E_CT_KIRMIZI",5.0,300.0,"60","2"}, new object[]{"0",10.0,400.0,"60","1,5"},
                new object[]{"X_DIGER",10.0,50.0,null,null}, new object[]{"E_CT_KIRMIZI",60.0,200.0,"100","1,5"} };
            HashSet<ObjectId> ids = new HashSet<ObjectId>();
            using (DocumentLock dl = doc.LockDocument())
            using (Transaction tx = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
                LayerTable lt = (LayerTable)tx.GetObject(db.LayerTableId, OpenMode.ForWrite);
                BlockTableRecord ms = (BlockTableRecord)tx.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                BlockTableRecord def = (BlockTableRecord)tx.GetObject(bt["cabletray"], OpenMode.ForRead);
                foreach (object[] c in cases)
                {
                    string lay = (string)c[0];
                    if (!lt.Has(lay)) { LayerTableRecord l = new LayerTableRecord { Name = lay }; lt.Add(l); tx.AddNewlyCreatedDBObject(l, true); }
                    BlockReference br = new BlockReference(Point3d.Origin, def.ObjectId) { Layer = lay };
                    ms.AppendEntity(br); tx.AddNewlyCreatedDBObject(br, true);
                    foreach (ObjectId aid in def) { AttributeDefinition ad = tx.GetObject(aid, OpenMode.ForRead) as AttributeDefinition; if (ad == null || ad.Constant) continue;
                        AttributeReference ar = new AttributeReference(); ar.SetAttributeFromBlock(ad, br.BlockTransform);
                        string tag = TrayKesif.Norm(ad.Tag);
                        if (tag.StartsWith("yuk")) ar.TextString = (string)c[3] ?? ""; else if (tag.StartsWith("kal")) ar.TextString = (string)c[4] ?? "";
                        br.AttributeCollection.AppendAttribute(ar); tx.AddNewlyCreatedDBObject(ar, true); }
                    foreach (DynamicBlockReferenceProperty p in br.DynamicBlockReferencePropertyCollection)
                    {
                        string n = TrayKesif.Norm(p.PropertyName);
                        try { if (n == "en") p.Value = (double)c[1]; else if (n == "boy") p.Value = (double)c[2];
                              else if (n.StartsWith("yuk") && c[3] != null) p.Value = Convert.ChangeType(c[3].ToString().Replace(",", "."), p.Value.GetType(), System.Globalization.CultureInfo.InvariantCulture);
                              else if (n.StartsWith("kal") && c[4] != null) p.Value = Convert.ChangeType(c[4].ToString().Replace(",", "."), p.Value.GetType(), System.Globalization.CultureInfo.InvariantCulture); }
                        catch (System.Exception ex) { ed.WriteMessage("\nprop " + p.PropertyName + ": " + ex.Message); }
                    }
                    ids.Add(br.ObjectId);
                }
                List<string> notes = new List<string>();
                foreach (Dictionary<string, object> r in Takeoff.Compute(db, new TakeoffRequest { Only = ids }, notes, tx))
                    ed.WriteMessage("\nROW | " + r["kategori"] + " | " + r["kalem"] + " | " + r["katman"] + " | " + r["miktar"] + " " + r["birim"] + " | " + r["adet"] + " adet");
                tx.Commit();
            }
        }

        [CommandMethod("AIDCTXDIAG", CommandFlags.Modal)]
        public void CtxDiag()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            System.Collections.Generic.List<ObjectId> ids = new System.Collections.Generic.List<ObjectId>();
            using (Transaction tx = doc.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord ms = (BlockTableRecord)tx.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(doc.Database), OpenMode.ForRead);
                foreach (ObjectId id in ms) ids.Add(id);
                tx.Commit();
            }
            DrawingContext ctx = ContextBuilder.Build(doc, ids);
            System.Collections.Generic.Dictionary<string, object> j = ctx.ToJson();
            System.Web.Script.Serialization.JavaScriptSerializer js = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            string json = js.Serialize(j);
            int rows = ctx.SelectedTable != null ? ((System.Collections.IList)ctx.SelectedTable["rows"]).Count : 0;
            doc.Editor.WriteMessage("\nCTX total=" + ctx.SelectionTotal + " sent=" + ctx.SelectionSent + " listed=" + ctx.Selected.Count + " compact_rows=" + rows + " handles=" + ctx.HandleSet.Count + " json_bytes=" + json.Length + " truncated=" + j["selection_truncated"]);
            if (j.ContainsKey("reference_points")) doc.Editor.WriteMessage("\nCTX refs=" + js.Serialize(j["reference_points"]) + (j.ContainsKey("reference_transform") ? " transform=" + js.Serialize(j["reference_transform"]) : "") + (j.ContainsKey("reference_points_hint") ? " hint=yes" : ""));
            if (rows > 0) doc.Editor.WriteMessage("\nCTX row0=" + js.Serialize(((System.Collections.IList)ctx.SelectedTable["rows"])[0]));
        }

        internal static string Report(Transaction tx, BlockReference br)
        {
            string F(double v) { return v.ToString("0.#", CultureInfo.InvariantCulture); }
            BlockTableRecord btr = (BlockTableRecord)tx.GetObject(br.BlockTableRecord, OpenMode.ForRead);
            string s = "DIAG " + br.Handle + " def=" + btr.Name;
            if (br.IsDynamicBlock) foreach (DynamicBlockReferenceProperty p in br.DynamicBlockReferencePropertyCollection) if (p.PropertyName == "BOY" || p.PropertyName == "EN") s += " " + p.PropertyName + "=" + Convert.ToString(p.Value, CultureInfo.InvariantCulture);
            foreach (ObjectId eid in btr)
            {
                Entity e = (Entity)tx.GetObject(eid, OpenMode.ForRead);
                if (e is Polyline) { Extents3d x = e.GeometricExtents; s += " PL[" + F(x.MinPoint.X) + ".." + F(x.MaxPoint.X) + "]"; }
                Hatch h = e as Hatch;
                if (h != null)
                {
                    s += " HATCH lines=" + h.NumberOfHatchLines;
                    try
                    {
                        DBObjectCollection c = new DBObjectCollection(); h.Explode(c);
                        double mn = double.MaxValue, mx = double.MinValue; int n = 0;
                        foreach (DBObject o in c) { Line l = o as Line; if (l != null) { n++; mn = Math.Min(mn, Math.Min(l.StartPoint.X, l.EndPoint.X)); mx = Math.Max(mx, Math.Max(l.StartPoint.X, l.EndPoint.X)); } o.Dispose(); }
                        s += " expl=" + n + " X[" + F(mn) + ".." + F(mx) + "]";
                    }
                    catch (System.Exception ex) { s += " explErr=" + ex.Message; }
                }
            }
            return s;
        }
    }
}
