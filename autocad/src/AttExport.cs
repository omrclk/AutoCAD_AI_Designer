using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace CadAiDesigner
{
    /// <summary>Block-content attributes read from one MLeader.</summary>
    internal class LeaderInfo
    {
        public string Name;
        public double X, Y;
        public readonly List<string> Order = new List<string>();
        public readonly List<KeyValuePair<string, string>> Values = new List<KeyValuePair<string, string>>();
        public readonly HashSet<string> Const = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    internal class AttExportRequest
    {
        public HashSet<ObjectId> Only;     // selection (null = whole drawing)
        public string Block;               // effective block name filter (null = any)
        public bool AttributedOnly = true;
        public bool AllLayouts;
        public string GroupBy;
        public bool Open = true;
    }

    /// <summary>Read-only export of block attributes (any tags) to Excel via the service's xlsx writer.</summary>
    internal static class AttExport
    {
        static readonly JavaScriptSerializer Js = new JavaScriptSerializer { MaxJsonLength = 64000000, RecursionLimit = 40 };

        internal static string EffName(Transaction tx, BlockReference br)
        {
            try { return ((BlockTableRecord)tx.GetObject(br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord, OpenMode.ForRead)).Name; }
            catch (System.Exception) { return br.Name; }
        }

        internal static string AttText(AttributeReference ar)
        {
            if (ar == null) return "";
            try { if (ar.IsMTextAttribute && ar.MTextAttribute != null) return ar.MTextAttribute.Text ?? ""; } catch (System.Exception) { }
            return ar.TextString ?? "";
        }

        internal static string DefText(AttributeDefinition ad)
        {
            if (ad == null) return "";
            string v = ad.TextString;
            try { if (ad.IsMTextAttributeDefinition && ad.MTextAttributeDefinition != null) v = ad.MTextAttributeDefinition.Text; } catch (System.Exception) { }
            return v ?? "";
        }

        /// <summary>Writes an attribute value, keeping MText contents when the attribute is multiline.</summary>
        internal static void SetAttText(AttributeReference ar, string v)
        {
            bool mt = false; try { mt = ar.IsMTextAttribute; } catch (System.Exception) { }
            if (mt) { try { MText m = ar.MTextAttribute; m.Contents = v; ar.MTextAttribute = m; ar.UpdateMTextAttribute(); } catch (System.Exception) { ar.TextString = v; } }
            else ar.TextString = v;
        }

        internal static bool IsBlockContent(MLeader ml)
        {
            if (ml == null) return false;
            try { return ml.ContentType == ContentType.BlockContent && !ml.BlockContentId.IsNull; }
            catch (System.Exception) { return false; }
        }

        /// <summary>Block-content attributes of an MLeader (definition order). Null when the leader has no block content.
        /// GetBlockAttribute returns a non-database copy; SetLeaderAttr writes it back.</summary>
        internal static LeaderInfo ReadLeader(Transaction tx, MLeader ml)
        {
            if (!IsBlockContent(ml)) return null;
            BlockTableRecord btr = tx.GetObject(ml.BlockContentId, OpenMode.ForRead) as BlockTableRecord;
            if (btr == null) return null;
            LeaderInfo info = new LeaderInfo { Name = btr.Name };
            try { info.X = ml.BlockPosition.X; info.Y = ml.BlockPosition.Y; }
            catch (System.Exception)
            {
                try { Extents3d ex = ml.GeometricExtents; info.X = ex.MinPoint.X; info.Y = ex.MinPoint.Y; }
                catch (System.Exception) { }
            }
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId id in btr)
            {
                AttributeDefinition ad = tx.GetObject(id, OpenMode.ForRead) as AttributeDefinition;
                if (ad == null || !seen.Add(ad.Tag)) continue;
                info.Order.Add(ad.Tag);
                if (ad.Constant)
                {
                    info.Const.Add(ad.Tag);
                    info.Values.Add(new KeyValuePair<string, string>(ad.Tag, DefText(ad)));
                    continue;
                }
                string val = DefText(ad);
                try
                {
                    AttributeReference ar = ml.GetBlockAttribute(ad.ObjectId);
                    if (ar != null) val = AttText(ar);
                }
                catch (System.Exception) { }
                info.Values.Add(new KeyValuePair<string, string>(ad.Tag, val ?? ""));
            }
            return info;
        }

        /// <summary>Writes one block-content attribute. The leader is upgraded to write inside the caller's transaction.</summary>
        internal static void SetLeaderAttr(MLeader ml, AttributeDefinition ad, string value)
        {
            if (ml == null || ad == null || ad.Constant) return;
            AttributeReference ar = null;
            try { ar = ml.GetBlockAttribute(ad.ObjectId); }
            catch (System.Exception) { }
            if (ar == null)
            {
                ar = new AttributeReference();
                try { ar.SetAttributeFromBlock(ad, Matrix3d.Identity); }
                catch (System.Exception) { try { ar.Tag = ad.Tag; } catch (System.Exception) { } }
            }
            SetAttText(ar, value ?? "");
            if (!ml.IsWriteEnabled) ml.UpgradeOpen();
            ml.SetBlockAttribute(ad.ObjectId, ar);
        }

        /// <summary>Tag order and constant attribute values of a block definition.</summary>
        static void DefInfo(Transaction tx, ObjectId btrId, List<string> order, List<KeyValuePair<string, string>> consts)
        {
            BlockTableRecord btr = (BlockTableRecord)tx.GetObject(btrId, OpenMode.ForRead);
            if (!btr.HasAttributeDefinitions) return;
            foreach (ObjectId id in btr)
            {
                AttributeDefinition ad = tx.GetObject(id, OpenMode.ForRead) as AttributeDefinition;
                if (ad == null) continue;
                if (!order.Contains(ad.Tag)) order.Add(ad.Tag);
                if (ad.Constant) consts.Add(new KeyValuePair<string, string>(ad.Tag, DefText(ad)));
            }
        }

        static IEnumerable<KeyValuePair<string, ObjectId>> Spaces(Transaction tx, Database db, bool all)
        {
            BlockTable bt = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
            yield return new KeyValuePair<string, ObjectId>("Model", bt[BlockTableRecord.ModelSpace]);
            if (!all) yield break;
            DBDictionary lays = (DBDictionary)tx.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry e in lays)
            {
                Layout l = (Layout)tx.GetObject(e.Value, OpenMode.ForRead);
                if (l.ModelType) continue;
                yield return new KeyValuePair<string, ObjectId>(l.LayoutName, l.BlockTableRecordId);
            }
        }

        /// <summary>Block names (effective) in the drawing that carry attributes, with counts.</summary>
        public static SortedDictionary<string, int> AttributedNames(Database db, bool allLayouts)
        {
            SortedDictionary<string, int> d = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            using (Transaction tx = db.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (KeyValuePair<string, ObjectId> sp in Spaces(tx, db, allLayouts))
                    foreach (ObjectId id in (BlockTableRecord)tx.GetObject(sp.Value, OpenMode.ForRead))
                    {
                        string dxf = id.ObjectClass.DxfName;
                        if (dxf == "INSERT")
                        {
                            BlockReference br = tx.GetObject(id, OpenMode.ForRead) as BlockReference;
                            if (br == null) continue;
                            bool has = br.AttributeCollection.Count > 0;
                            if (!has) { try { has = ((BlockTableRecord)tx.GetObject(br.BlockTableRecord, OpenMode.ForRead)).HasAttributeDefinitions; } catch (System.Exception) { } }
                            if (!has) continue;
                            string n = EffName(tx, br);
                            int c; d.TryGetValue(n, out c); d[n] = c + 1;
                        }
                        else if (dxf == "MULTILEADER")
                        {
                            LeaderInfo li = ReadLeader(tx, tx.GetObject(id, OpenMode.ForRead) as MLeader);
                            if (li == null || li.Values.Count == 0) continue;
                            int c; d.TryGetValue(li.Name, out c); d[li.Name] = c + 1;
                        }
                    }
                tx.Commit();
            }
            return d;
        }

        public static Dictionary<string, object> Collect(Database db, AttExportRequest rq)
        {
            List<object> blocks = new List<object>();
            Dictionary<string, List<string>> order = new Dictionary<string, List<string>>();
            using (Transaction tx = db.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (KeyValuePair<string, ObjectId> sp in Spaces(tx, db, rq.AllLayouts || rq.Only != null))
                    foreach (ObjectId id in (BlockTableRecord)tx.GetObject(sp.Value, OpenMode.ForRead))
                    {
                        if (rq.Only != null && !rq.Only.Contains(id)) continue;
                        string dxf = id.ObjectClass.DxfName;
                        if (dxf == "INSERT")
                        {
                            BlockReference br = tx.GetObject(id, OpenMode.ForRead) as BlockReference;
                            if (br == null) continue;
                            string name = EffName(tx, br);
                            if (!string.IsNullOrEmpty(rq.Block) && !string.Equals(name, rq.Block, StringComparison.OrdinalIgnoreCase)) continue;
                            List<string> ord = new List<string>();
                            List<KeyValuePair<string, string>> consts = new List<KeyValuePair<string, string>>();
                            try { DefInfo(tx, br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord, ord, consts); } catch (System.Exception) { }
                            List<object> attrs = new List<object>();
                            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (ObjectId aid in br.AttributeCollection)
                            {
                                AttributeReference ar = tx.GetObject(aid, OpenMode.ForRead) as AttributeReference;
                                if (ar == null || !seen.Add(ar.Tag)) continue;
                                attrs.Add(new object[] { ar.Tag, AttText(ar) ?? "" });
                                if (!ord.Contains(ar.Tag)) ord.Add(ar.Tag);
                            }
                            foreach (KeyValuePair<string, string> c in consts) if (seen.Add(c.Key)) attrs.Add(new object[] { c.Key, c.Value ?? "" });
                            if (attrs.Count == 0 && rq.AttributedOnly) continue;
                            NoteOrder(order, name, ord);
                            blocks.Add(Row("BLOK", name, br.Layer, br.Handle.ToString(), br.Position.X, br.Position.Y, sp.Key, attrs, consts.Select(c => c.Key).ToList()));
                        }
                        else if (dxf == "MULTILEADER")
                        {
                            MLeader ml = tx.GetObject(id, OpenMode.ForRead) as MLeader;
                            LeaderInfo li = ReadLeader(tx, ml);
                            if (li == null) continue;
                            if (!string.IsNullOrEmpty(rq.Block) && !string.Equals(li.Name, rq.Block, StringComparison.OrdinalIgnoreCase)) continue;
                            if (li.Values.Count == 0 && rq.AttributedOnly) continue;
                            NoteOrder(order, li.Name, li.Order);
                            List<object> attrs = new List<object>();
                            foreach (KeyValuePair<string, string> kv in li.Values) attrs.Add(new object[] { kv.Key, kv.Value ?? "" });
                            blocks.Add(Row("MLEADER", li.Name, ml.Layer, ml.Handle.ToString(), li.X, li.Y, sp.Key, attrs, li.Const.ToList()));
                        }
                    }
                tx.Commit();
            }
            return new Dictionary<string, object> { { "blocks", blocks }, { "tag_order", order } };
        }

        static void NoteOrder(Dictionary<string, List<string>> order, string name, List<string> ord)
        {
            List<string> o;
            if (!order.TryGetValue(name, out o)) order[name] = o = new List<string>();
            foreach (string t in ord) if (!o.Contains(t)) o.Add(t);
        }

        static Dictionary<string, object> Row(string type, string name, string layer, string handle, double x, double y, string space, List<object> attrs, List<string> constTags)
        {
            return new Dictionary<string, object>
            {
                { "type", type }, { "block", name }, { "layer", layer }, { "handle", handle },
                { "x", Math.Round(x, 3) }, { "y", Math.Round(y, 3) }, { "space", space }, { "attrs", attrs }, { "const_tags", constTags }
            };
        }

        static string ExportPath(Document doc)
        {
            bool saved = Path.IsPathRooted(doc.Name) && File.Exists(doc.Name);
            string name = Path.GetFileNameWithoutExtension(doc.Name);
            string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CadAiDesigner", "metraj");
            string dir = saved ? Path.GetDirectoryName(doc.Name) : fallback;
            try { Directory.CreateDirectory(dir); string probe = Path.Combine(dir, ".cadai_w"); File.WriteAllText(probe, ""); File.Delete(probe); }
            catch (System.Exception) { dir = fallback; Directory.CreateDirectory(dir); }
            return Path.Combine(dir, name + "_attribute_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".xlsx");
        }

        public static string Run(Document doc, AttExportRequest rq)
        {
            Dictionary<string, object> body;
            using (doc.LockDocument()) body = Collect(doc.Database, rq);
            if (((List<object>)body["blocks"]).Count == 0) return "Aktarılacak attribute bloğu veya multileader bulunamadı" + (string.IsNullOrEmpty(rq.Block) ? "." : " ('" + rq.Block + "').");
            body["export_path"] = ExportPath(doc);
            if (!string.IsNullOrEmpty(rq.GroupBy)) body["group_by"] = rq.GroupBy;
            ServiceHost.EnsureStarted(Settings.Load());
            Dictionary<string, object> resp = Js.Deserialize<Dictionary<string, object>>(ServiceHost.Post("/attributes", Js.Serialize(body)));
            if (resp == null) throw new InvalidOperationException("Servisten boş yanıt.");
            if (resp.ContainsKey("error")) throw new InvalidOperationException(Convert.ToString(resp["error"]));
            string path = J.Str(resp, "path");
            AppLog.Write("ATTEXPORT [" + Path.GetFileName(doc.Name) + "] " + J.Str(resp, "count") + " blok");
            if (rq.Open && Environment.GetEnvironmentVariable("CADAI_NOOPEN") == null && !string.IsNullOrEmpty(path) && File.Exists(path)) { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (System.Exception) { } }
            return J.Str(resp, "summary");
        }

        public static AttExportRequest FromAction(Dictionary<string, object> a, IEnumerable<ObjectId> selection)
        {
            AttExportRequest rq = new AttExportRequest
            {
                Block = J.Str(a, "block"), GroupBy = J.Str(a, "group_by"),
                AllLayouts = J.Get(a, "all_layouts") != null && J.Bool(a, "all_layouts")
            };
            string scope = J.Str(a, "scope") ?? "auto";
            List<ObjectId> sel = (selection ?? Enumerable.Empty<ObjectId>()).ToList();
            if (scope == "selection" || (scope == "auto" && sel.Count > 0 && string.IsNullOrEmpty(rq.Block)))
            {
                if (sel.Count == 0) throw new InvalidOperationException("Seçim kapsamı istendi ama seçili nesne yok.");
                rq.Only = new HashSet<ObjectId>(sel);
            }
            if (scope == "all") rq.AttributedOnly = false;
            return rq;
        }
    }

    public class AttExportCommands
    {
        [CommandMethod("AIDATTEXPORT", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void Export()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            try
            {
                AttExportRequest rq = new AttExportRequest();
                PromptSelectionResult imp = ed.SelectImplied();
                if (imp.Status == PromptStatus.OK && imp.Value != null && imp.Value.Count > 0)
                {
                    rq.Only = new HashSet<ObjectId>(imp.Value.GetObjectIds());
                    ed.WriteMessage("\nSeçili " + rq.Only.Count + " nesne aktarılacak.");
                }
                else
                {
                    PromptKeywordOptions ko = new PromptKeywordOptions("\nKapsam [Attributeli/Tumbloklar/Blok/Secim] <Attributeli>: ", "Attributeli Tumbloklar Blok Secim") { AllowNone = true };
                    PromptResult kr = ed.GetKeywords(ko);
                    if (kr.Status != PromptStatus.OK && kr.Status != PromptStatus.None) return;
                    string scope = kr.Status == PromptStatus.OK ? kr.StringResult : "Attributeli";
                    PromptResult lr = ed.GetKeywords(new PromptKeywordOptions("\nPafta (layout) alanları da dahil mi? [Hayir/Evet] <Hayir>: ", "Hayir Evet") { AllowNone = true });
                    if (lr.Status != PromptStatus.OK && lr.Status != PromptStatus.None) return;
                    rq.AllLayouts = lr.Status == PromptStatus.OK && lr.StringResult == "Evet";
                    if (scope == "Tumbloklar") rq.AttributedOnly = false;
                    else if (scope == "Secim")
                    {
                        PromptSelectionResult sr = ed.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nAktarılacak blok veya multileader'ları seçin: " },
                            new SelectionFilter(new[]
                            {
                                new TypedValue((int)DxfCode.Operator, "<OR"),
                                new TypedValue((int)DxfCode.Start, "INSERT"),
                                new TypedValue((int)DxfCode.Start, "MULTILEADER"),
                                new TypedValue((int)DxfCode.Operator, "OR>")
                            }));
                        if (sr.Status != PromptStatus.OK) return;
                        rq.Only = new HashSet<ObjectId>(sr.Value.GetObjectIds());
                    }
                    else if (scope == "Blok")
                    {
                        List<KeyValuePair<string, int>> names = AttExport.AttributedNames(doc.Database, rq.AllLayouts).ToList();
                        if (names.Count == 0) { ed.WriteMessage("\nÇizimde attribute'lu blok veya multileader yok.\n"); return; }
                        for (int i = 0; i < names.Count; i++) ed.WriteMessage("\n  " + (i + 1) + ") " + names[i].Key + " (" + names[i].Value + ")");
                        PromptResult nr = ed.GetString(new PromptStringOptions("\nBlok adı veya numarası: ") { AllowSpaces = true });
                        if (nr.Status != PromptStatus.OK) return;
                        string s = nr.StringResult.Trim(); int k;
                        if (int.TryParse(s, out k) && k >= 1 && k <= names.Count) rq.Block = names[k - 1].Key;
                        else
                        {
                            KeyValuePair<string, int> hit = names.FirstOrDefault(x => string.Equals(x.Key, s, StringComparison.OrdinalIgnoreCase));
                            if (hit.Key == null) { ed.WriteMessage("\n'" + s + "' bulunamadı.\n"); return; }
                            rq.Block = hit.Key;
                        }
                    }
                }
                PromptResult gr = ed.GetString(new PromptStringOptions("\nÖzet için gruplanacak etiket (örn. KABLO; boş = özet yok): ") { AllowSpaces = true });
                if (gr.Status == PromptStatus.Cancel) return;
                if (gr.Status == PromptStatus.OK && gr.StringResult.Trim().Length > 0) rq.GroupBy = gr.StringResult.Trim();
                ed.WriteMessage("\nAttribute'lar aktarılıyor...");
                string sum = AttExport.Run(doc, rq);
                ed.WriteMessage("\n" + sum + "\n");
                Metraj.Raise(sum);
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nAttribute aktarma hatası: " + ex.Message + "\n");
                try { Metraj.Raise("Attribute aktarma hatası: " + ex.Message); } catch (System.Exception) { }
            }
        }
    }
}
