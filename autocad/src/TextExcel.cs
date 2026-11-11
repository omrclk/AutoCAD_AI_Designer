using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace CadAiDesigner
{
    /// <summary>TEXTEXCEL / AIDTEXTEXCEL: TEXT and MTEXT plain contents, one Excel column, reading order.</summary>
    public class TextExcelCommands
    {
        class Item
        {
            public string Text;
            public double X, Y;
            public int Index;
        }

        static readonly JavaScriptSerializer Js = new JavaScriptSerializer { MaxJsonLength = 64000000 };
        static readonly Regex FmtRun = new Regex(@"\\[AaCcFfHhQqTtWw][^;]*;", RegexOptions.Compiled);
        static readonly Regex Stack = new Regex(@"\\S([^;^/#]*)[/#^]([^;]*);", RegexOptions.Compiled);
        static readonly Regex Toggle = new Regex(@"\\[LlOoKk]", RegexOptions.Compiled);

        [CommandMethod("TEXTEXCEL", CommandFlags.Modal | CommandFlags.UsePickSet)]
        [CommandMethod("AIDTEXTEXCEL", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void TextExcel()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            try
            {
                ObjectId[] ids = Select(ed);
                if (ids == null) return;
                List<Item> items;
                using (doc.LockDocument()) items = Read(doc.Database, ids);
                if (items.Count == 0) { ed.WriteMessage("\nSe\u00e7imde TEXT veya MTEXT yok.\n"); return; }

                string mode = AskMode(ed);
                if (mode == null) return;
                string how;
                List<string> lines = Order(items, mode, out how);

                string path = AskPath(ed, doc);
                if (path == null) return;
                path = Write(path, lines);
                bool opened = OpenXlsx(path);
                string msg = lines.Count + " yaz\u0131 Excel'e aktar\u0131ld\u0131 (" + how + ").\n" + path;
                if (!opened && Environment.GetEnvironmentVariable("CADAI_NOOPEN") == null && File.Exists(path))
                    msg += "\nDosya a\u00e7\u0131lamad\u0131.";
                ed.WriteMessage("\n" + msg + "\n");
                try { AppLog.Write("TEXTEXCEL [" + Path.GetFileName(doc.Name) + "] " + lines.Count + " " + how); } catch (System.Exception) { }
                try { Metraj.Raise(msg); } catch (System.Exception) { }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nMetin aktarma hatas\u0131: " + ex.Message + "\n");
                try { Metraj.Raise("Metin aktarma hatas\u0131: " + ex.Message); } catch (System.Exception) { }
            }
        }

        /// <summary>Pickfirst TEXT/MTEXT, otherwise a window/pick filtered to TEXT and MTEXT only.</summary>
        static ObjectId[] Select(Editor ed)
        {
            PromptSelectionResult imp = ed.SelectImplied();
            if (imp.Status == PromptStatus.OK && imp.Value != null && imp.Value.Count > 0)
            {
                ObjectId[] pre = OnlyText(imp.Value.GetObjectIds());
                if (pre.Length > 0)
                {
                    ed.WriteMessage("\nSe\u00e7ili " + pre.Length + " yaz\u0131 aktar\u0131lacak.");
                    return pre;
                }
            }
            PromptSelectionResult sr = ed.GetSelection(
                new PromptSelectionOptions { MessageForAdding = "\nAktar\u0131lacak yaz\u0131lar\u0131 se\u00e7in (TEXT/MTEXT, pencere): " },
                TextFilter());
            if (sr.Status != PromptStatus.OK || sr.Value == null) return null;
            return OnlyText(sr.Value.GetObjectIds());
        }

        static SelectionFilter TextFilter()
        {
            return new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.Operator, "<OR"),
                new TypedValue((int)DxfCode.Start, "TEXT"),
                new TypedValue((int)DxfCode.Start, "MTEXT"),
                new TypedValue((int)DxfCode.Operator, "OR>")
            });
        }

        static ObjectId[] OnlyText(IEnumerable<ObjectId> ids)
        {
            List<ObjectId> kept = new List<ObjectId>();
            HashSet<ObjectId> seen = new HashSet<ObjectId>();
            foreach (ObjectId id in ids)
            {
                if (!seen.Add(id)) continue;
                string dxf = null;
                try { dxf = id.ObjectClass.DxfName; } catch (System.Exception) { }
                if (dxf == "TEXT" || dxf == "MTEXT") kept.Add(id);
            }
            return kept.ToArray();
        }

        static string AskMode(Editor ed)
        {
            PromptKeywordOptions ko = new PromptKeywordOptions("\nS\u0131ra [Otomatik/Dikey/Yatay] <Otomatik>: ", "Otomatik Dikey Yatay") { AllowNone = true };
            PromptResult kr = ed.GetKeywords(ko);
            if (kr.Status != PromptStatus.OK && kr.Status != PromptStatus.None) return null;
            string s = kr.Status == PromptStatus.OK ? (kr.StringResult ?? "") : "";
            return s.Length == 0 ? "Otomatik" : s;
        }

        static List<Item> Read(Database db, ObjectId[] ids)
        {
            List<Item> items = new List<Item>();
            using (Transaction tx = db.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    Entity e = null;
                    try { e = tx.GetObject(id, OpenMode.ForRead) as Entity; } catch (System.Exception) { continue; }
                    DBText dbt = e as DBText;
                    MText mt = e as MText;
                    // TEXT only: attributes are DBText subclasses and are not part of this export.
                    if (dbt != null && !(e is AttributeReference) && !(e is AttributeDefinition))
                    {
                        Point3d p = Anchor(dbt);
                        items.Add(new Item { Text = Normalize(dbt.TextString), X = p.X, Y = p.Y, Index = items.Count });
                    }
                    else if (mt != null)
                    {
                        Point3d p = Anchor(mt);
                        items.Add(new Item { Text = PlainMText(mt), X = p.X, Y = p.Y, Index = items.Count });
                    }
                }
                tx.Commit();
            }
            return items;
        }

        /// <summary>Left/baseline text uses the insertion point; every other justification uses the alignment point. Both are WCS.</summary>
        static Point3d Anchor(DBText t)
        {
            try
            {
                if (t.HorizontalMode != TextHorizontalMode.TextLeft || t.VerticalMode != TextVerticalMode.TextBase)
                    return t.AlignmentPoint;
            }
            catch (System.Exception) { }
            return t.Position;
        }

        static Point3d Anchor(MText m)
        {
            try { return m.Location; }
            catch (System.Exception) { }
            try
            {
                Extents3d ex = m.GeometricExtents;
                return new Point3d((ex.MinPoint.X + ex.MaxPoint.X) / 2.0, (ex.MinPoint.Y + ex.MaxPoint.Y) / 2.0, 0);
            }
            catch (System.Exception) { return Point3d.Origin; }
        }

        /// <summary>MText.Text is plain. If formatting codes are still present, strip them and keep paragraph breaks as newlines.</summary>
        static string PlainMText(MText mt)
        {
            string s = null;
            try { s = mt.Text; } catch (System.Exception) { }
            if (s == null) s = "";
            if (s.Length == 0 || HasFmt(s))
            {
                string coded = s;
                try { if (!string.IsNullOrEmpty(mt.Contents)) coded = mt.Contents; } catch (System.Exception) { }
                s = StripMText(coded);
            }
            return Normalize(s);
        }

        static bool HasFmt(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            return s.IndexOf("\\P", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("{\\", StringComparison.Ordinal) >= 0
                || s.IndexOf("\\f", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("\\H", StringComparison.Ordinal) >= 0
                || s.IndexOf("\\C", StringComparison.Ordinal) >= 0
                || s.IndexOf("\\S", StringComparison.Ordinal) >= 0;
        }

        static string StripMText(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = raw.Replace("\\P", "\n").Replace("\\p", "\n");
            s = Stack.Replace(s, "$1/$2");
            s = FmtRun.Replace(s, "");
            s = s.Replace("\\~", " ");
            s = s.Replace("\\{", "\u0001").Replace("\\}", "\u0002").Replace("\\\\", "\\");
            s = Toggle.Replace(s, "");
            s = s.Replace("{", "").Replace("}", "");
            return s.Replace('\u0001', '{').Replace('\u0002', '}');
        }

        static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r\n", "\n").Replace('\r', '\n');
            StringBuilder b = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c == '\n' || c == '\t' || c >= ' ') b.Append(c);
            }
            return b.ToString();
        }

        /// <summary>Vertical when Y spread is at least the X spread (or forced). Ties on the primary axis use the other axis.</summary>
        static List<string> Order(List<Item> items, string mode, out string how)
        {
            bool vertical;
            if (string.Equals(mode, "Dikey", StringComparison.OrdinalIgnoreCase)) vertical = true;
            else if (string.Equals(mode, "Yatay", StringComparison.OrdinalIgnoreCase)) vertical = false;
            else
            {
                double minX = items.Min(i => i.X), maxX = items.Max(i => i.X);
                double minY = items.Min(i => i.Y), maxY = items.Max(i => i.Y);
                vertical = (maxY - minY) >= (maxX - minX);
            }
            IEnumerable<Item> seq = vertical
                ? items.OrderByDescending(i => i.Y).ThenBy(i => i.X).ThenBy(i => i.Index)
                : items.OrderBy(i => i.X).ThenByDescending(i => i.Y).ThenBy(i => i.Index);
            bool auto = !string.Equals(mode, "Dikey", StringComparison.OrdinalIgnoreCase) && !string.Equals(mode, "Yatay", StringComparison.OrdinalIgnoreCase);
            how = (auto ? "otomatik: " : "") + (vertical ? "dikey, yukar\u0131dan a\u015fa\u011f\u0131ya" : "yatay, soldan sa\u011fa");
            List<string> lines = new List<string>();
            foreach (Item it in seq) lines.Add(it.Text ?? "");
            return lines;
        }

        static string AskPath(Editor ed, Document doc)
        {
            string def = DefaultPath(doc);
            int filedia = 1;
            try { filedia = Convert.ToInt32(Application.GetSystemVariable("FILEDIA")); } catch (System.Exception) { }
            if (!Console() && filedia != 0)
            {
                using (System.Windows.Forms.SaveFileDialog dlg = new System.Windows.Forms.SaveFileDialog())
                {
                    dlg.Title = "Yaz\u0131lar\u0131 Excel'e aktar";
                    dlg.Filter = "Excel (*.xlsx)|*.xlsx";
                    dlg.DefaultExt = "xlsx";
                    dlg.AddExtension = true;
                    dlg.OverwritePrompt = true;
                    dlg.FileName = Path.GetFileName(def);
                    string dir = Path.GetDirectoryName(def);
                    if (!string.IsNullOrEmpty(dir)) dlg.InitialDirectory = dir;
                    if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return null;
                    return EnsureXlsx(dlg.FileName);
                }
            }
            PromptResult pr = ed.GetString(new PromptStringOptions("\nExcel dosyas\u0131 <" + def + ">: ") { AllowSpaces = true });
            if (pr.Status != PromptStatus.OK && pr.Status != PromptStatus.None) return null;
            string typed = pr.Status == PromptStatus.OK ? (pr.StringResult ?? "").Trim().Trim('"') : "";
            return EnsureXlsx(typed.Length == 0 ? def : typed);
        }

        static bool Console()
        {
            try { return Process.GetCurrentProcess().ProcessName.ToLowerInvariant().Contains("accoreconsole"); }
            catch (System.Exception) { return false; }
        }

        static string DefaultPath(Document doc)
        {
            bool saved = Path.IsPathRooted(doc.Name) && File.Exists(doc.Name);
            string name = SafeFile(Path.GetFileNameWithoutExtension(doc.Name));
            string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CadAiDesigner", "metraj");
            string dir = saved ? Path.GetDirectoryName(doc.Name) : fallback;
            if (!Writable(dir)) { dir = fallback; try { Directory.CreateDirectory(dir); } catch (System.Exception) { } }
            return Path.Combine(dir, name + "_metin_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".xlsx");
        }

        static bool Writable(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir)) return false;
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".cadai_w");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch (System.Exception) { return false; }
        }

        static string SafeFile(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "yazi";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            name = name.Trim();
            return name.Length == 0 ? "yazi" : name;
        }

        static string EnsureXlsx(string path)
        {
            path = (path ?? "").Trim().Trim('"');
            if (path.Length == 0) return null;
            if (!path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) path += ".xlsx";
            return path;
        }

        static string Write(string path, List<string> lines)
        {
            List<object> sheet = new List<object>();
            foreach (string line in lines) sheet.Add(new object[] { line ?? "" });
            ServiceHost.EnsureStarted(Settings.Load());
            Dictionary<string, object> resp = Js.Deserialize<Dictionary<string, object>>(ServiceHost.Post("/xlsx",
                Js.Serialize(new Dictionary<string, object>
                {
                    { "path", path },
                    { "sheets", new object[] { new object[] { "Metin", sheet } } }
                })));
            if (resp == null || resp.ContainsKey("error"))
                throw new InvalidOperationException(resp == null ? "bo\u015f yan\u0131t" : Convert.ToString(resp["error"]));
            if (resp.ContainsKey("path") && resp["path"] != null)
            {
                string p = Convert.ToString(resp["path"]);
                if (!string.IsNullOrEmpty(p)) path = p;
            }
            return path;
        }

        static bool OpenXlsx(string path)
        {
            if (Environment.GetEnvironmentVariable("CADAI_NOOPEN") != null) return false;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return true;
            }
            catch (System.Exception) { return false; }
        }
    }
}
