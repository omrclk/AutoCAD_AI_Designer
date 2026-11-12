using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

namespace CadAiDesigner
{
    /// <summary>Reverse of AIDATTEXPORT: diff an edited workbook (service /attributes_import) and write changed attribute values.</summary>
    internal static class AttImport
    {
        static readonly JavaScriptSerializer Js = new JavaScriptSerializer { MaxJsonLength = 64000000, RecursionLimit = 40 };

        public static string LatestExport(Document doc)
        {
            string name = Path.GetFileNameWithoutExtension(doc.Name);
            List<string> dirs = new List<string>();
            if (Path.IsPathRooted(doc.Name)) dirs.Add(Path.GetDirectoryName(doc.Name));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CadAiDesigner", "metraj"));
            foreach (string d in dirs)
            {
                try
                {
                    if (!Directory.Exists(d)) continue;
                    FileInfo f = new DirectoryInfo(d).GetFiles(name + "_attribute_*.xlsx").Where(x => !x.Name.StartsWith("~$")).OrderByDescending(x => x.LastWriteTime).FirstOrDefault();
                    if (f != null) return f.FullName;
                }
                catch (System.Exception) { }
            }
            return null;
        }

        public static Dictionary<string, object> Diff(Document doc, string path)
        {
            Dictionary<string, object> body;
            using (doc.LockDocument()) body = AttExport.Collect(doc.Database, new AttExportRequest { AllLayouts = true, Images = false });
            body["path"] = path;
            ServiceHost.EnsureStarted(Settings.Load());
            Dictionary<string, object> resp = Js.Deserialize<Dictionary<string, object>>(ServiceHost.Post("/attributes_import", Js.Serialize(body)));
            if (resp == null) throw new InvalidOperationException("Servisten bo\u015f yan\u0131t.");
            if (resp.ContainsKey("error")) throw new InvalidOperationException(Convert.ToString(resp["error"]));
            return resp;
        }

        static List<Dictionary<string, object>> Changes(Dictionary<string, object> r)
        {
            return ((r.ContainsKey("changes") ? r["changes"] as IEnumerable : null) ?? new object[0]).OfType<Dictionary<string, object>>().ToList();
        }

        static List<string> Strs(Dictionary<string, object> r, string k)
        {
            return ((r.ContainsKey(k) ? r[k] as IEnumerable : null) ?? new object[0]).Cast<object>().Select(Convert.ToString).ToList();
        }

        public static string Preview(Dictionary<string, object> r)
        {
            List<Dictionary<string, object>> ch = Changes(r);
            List<string> nf = Strs(r, "not_found"), fb = Strs(r, "fallback"), cf = Strs(r, "conflicts");
            System.Text.StringBuilder b = new System.Text.StringBuilder();
            b.Append("Attribute i\u00e7e aktarma \u00f6nizleme (" + J.Str(r, "source") + "): " + ch.Count + " de\u011fer, " + J.Str(r, "changed_blocks") + " blok de\u011fi\u015fecek; "
                + J.Str(r, "unchanged_blocks") + " blok ayn\u0131; " + nf.Count + " sat\u0131r e\u015fle\u015fmedi.");
            foreach (Dictionary<string, object> c in ch.Take(20))
                b.Append("\n  " + J.Str(c, "handle") + " " + J.Str(c, "block") + " " + J.Str(c, "tag") + ": \"" + J.Str(c, "old") + "\" -> \"" + J.Str(c, "new") + "\"");
            if (ch.Count > 20) b.Append("\n  ... (+" + (ch.Count - 20) + ")");
            if (fb.Count > 0) b.Append("\nHandle yerine blok ad\u0131 + konumla e\u015fle\u015fen: " + fb.Count + " (" + string.Join("; ", fb.Take(5)) + ")");
            if (nf.Count > 0) b.Append("\nBulunamayan: " + string.Join("; ", nf.Take(8)) + (nf.Count > 8 ? " ..." : ""));
            if (cf.Count > 0) b.Append("\n\u00c7ak\u0131\u015fma: " + string.Join("; ", cf.Take(5)));
            return b.ToString();
        }

        /// <summary>Writes the changes in one transaction (one UNDO step). Returns the result text.</summary>
        public static string Apply(Document doc, Dictionary<string, object> r)
        {
            Database db = doc.Database;
            int done = 0, locked = 0, missing = 0;
            HashSet<string> blocks = new HashSet<string>();
            List<string> lockedList = new List<string>();
            using (doc.LockDocument())
            using (Transaction tx = db.TransactionManager.StartTransaction())
            {
                foreach (IGrouping<string, Dictionary<string, object>> g in Changes(r).GroupBy(c => J.Str(c, "handle")))
                {
                    ObjectId id = ObjectId.Null;
                    try { long hv = Convert.ToInt64(g.Key, 16); db.TryGetObjectId(new Handle(hv), out id); } catch (System.Exception) { }
                    Entity ent = id.IsNull || id.IsErased ? null : tx.GetObject(id, OpenMode.ForRead) as Entity;
                    BlockReference br = ent as BlockReference;
                    MLeader ml = ent as MLeader;
                    if (br == null && (ml == null || !AttExport.IsBlockContent(ml))) { missing += g.Count(); continue; }
                    LayerTableRecord ltr = (LayerTableRecord)tx.GetObject(ent.LayerId, OpenMode.ForRead);
                    if (ltr.IsLocked) { locked += g.Count(); if (lockedList.Count < 8) lockedList.Add(g.Key + " (" + ent.Layer + ")"); continue; }
                    Dictionary<string, string> want = g.ToDictionary(c => J.Str(c, "tag").ToUpperInvariant(), c => J.Str(c, "new") ?? "");
                    if (br != null)
                    {
                        foreach (ObjectId aid in br.AttributeCollection)
                        {
                            AttributeReference ar = tx.GetObject(aid, OpenMode.ForRead) as AttributeReference;
                            string v;
                            if (ar == null || !want.TryGetValue(ar.Tag.ToUpperInvariant(), out v)) continue;
                            bool lockedAttr = false; try { lockedAttr = ar.IsConstant; } catch (System.Exception) { }
                            if (lockedAttr) continue;
                            ar.UpgradeOpen();
                            AttExport.SetAttText(ar, v);
                            want.Remove(ar.Tag.ToUpperInvariant()); done++; blocks.Add(g.Key);
                        }
                    }
                    else
                    {
                        BlockTableRecord def = tx.GetObject(ml.BlockContentId, OpenMode.ForRead) as BlockTableRecord;
                        Dictionary<string, AttributeDefinition> defs = new Dictionary<string, AttributeDefinition>();
                        if (def != null)
                            foreach (ObjectId did in def)
                            {
                                AttributeDefinition ad = tx.GetObject(did, OpenMode.ForRead) as AttributeDefinition;
                                if (ad == null || ad.Constant) continue;
                                string k = ad.Tag.ToUpperInvariant();
                                if (!defs.ContainsKey(k)) defs[k] = ad;
                            }
                        foreach (string key in want.Keys.ToList())
                        {
                            AttributeDefinition ad;
                            if (!defs.TryGetValue(key, out ad)) continue;
                            AttExport.SetLeaderAttr(ml, ad, want[key]);
                            want.Remove(key); done++; blocks.Add(g.Key);
                        }
                    }
                    missing += want.Count;
                }
                tx.Commit();
            }
            AppLog.Write("ATTIMPORT [" + Path.GetFileName(doc.Name) + "] " + done + " de\u011fer");
            string s = "Attribute i\u00e7e aktar\u0131ld\u0131: " + done + " de\u011fer / " + blocks.Count + " blok g\u00fcncellendi; " + J.Str(r, "unchanged_blocks") + " blok de\u011fi\u015fmedi; "
                + Strs(r, "not_found").Count + " sat\u0131r bulunamad\u0131.";
            if (locked > 0) s += "\nKilitli katman nedeniyle atlanan: " + locked + " (" + string.Join(", ", lockedList) + ")";
            if (missing > 0) s += "\nUygulanamayan (blok/etiket yok): " + missing;
            s += "\nGeri almak i\u00e7in: U (tek ad\u0131m).";
            return s;
        }
    }

    public class AttImportCommands
    {
        [CommandMethod("AIDATTIMPORT", CommandFlags.Modal | CommandFlags.NoBlockEditor)]
        public void Import() { Run(); }

        [CommandMethod("ATI", CommandFlags.Modal | CommandFlags.NoBlockEditor)]
        public void ImportShort() { Run(); }

        static bool Console()
        {
            try { return System.Diagnostics.Process.GetCurrentProcess().ProcessName.ToLowerInvariant().Contains("accoreconsole"); } catch (System.Exception) { return false; }
        }

        static void Run()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            try
            {
                string def = AttImport.LatestExport(doc), path = null;
                object fd = null; try { fd = Application.GetSystemVariable("FILEDIA"); } catch (System.Exception) { }
                if (!Console() && Convert.ToInt32(fd ?? 1) == 1)
                {
                    using (System.Windows.Forms.OpenFileDialog dlg = new System.Windows.Forms.OpenFileDialog { Title = "Attribute Excel dosyas\u0131 (AIDATTEXPORT)", Filter = "Excel (*.xlsx)|*.xlsx" })
                    {
                        if (def != null) { dlg.InitialDirectory = Path.GetDirectoryName(def); dlg.FileName = Path.GetFileName(def); }
                        else if (Path.IsPathRooted(doc.Name)) dlg.InitialDirectory = Path.GetDirectoryName(doc.Name);
                        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                        path = dlg.FileName;
                    }
                }
                else
                {
                    PromptResult pr = ed.GetString(new PromptStringOptions("\nExcel dosyas\u0131" + (def != null ? " <" + def + ">" : "") + ": ") { AllowSpaces = true });
                    if (pr.Status != PromptStatus.OK) return;
                    path = pr.StringResult.Trim().Trim('"');
                    if (path.Length == 0) path = def;
                }
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) { ed.WriteMessage("\nDosya bulunamad\u0131: " + path + "\n"); return; }
                ed.WriteMessage("\nOkunuyor: " + path);
                Dictionary<string, object> r = AttImport.Diff(doc, path);
                string pv = AttImport.Preview(r);
                ed.WriteMessage("\n" + pv + "\n");
                Metraj.Raise(pv);
                int n = r.ContainsKey("changes") && r["changes"] is ICollection ? ((ICollection)r["changes"]).Count : 0;
                if (n == 0) { ed.WriteMessage("\nDe\u011fi\u015fiklik yok.\n"); return; }
                PromptResult kr = ed.GetKeywords(new PromptKeywordOptions("\n" + n + " de\u011fer \u00e7izime yaz\u0131ls\u0131n m\u0131? [Evet/Hayir] <Hayir>: ", "Evet Hayir") { AllowNone = true });
                if (kr.Status != PromptStatus.OK || kr.StringResult != "Evet") { ed.WriteMessage("\n\u0130ptal edildi, \u00e7izim de\u011fi\u015fmedi.\n"); Metraj.Raise("Attribute i\u00e7e aktarma iptal edildi."); return; }
                string res = AttImport.Apply(doc, r);
                ed.WriteMessage("\n" + res + "\n");
                ed.Regen();
                Metraj.Raise(res);
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nAttribute i\u00e7e aktarma hatas\u0131: " + ex.Message + "\n");
                try { Metraj.Raise("Attribute i\u00e7e aktarma hatas\u0131: " + ex.Message); } catch (System.Exception) { }
            }
        }
    }
}
