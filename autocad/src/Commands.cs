using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(CadAiDesigner.Commands))]
[assembly: CommandClass(typeof(CadAiDesigner.MetrajCommands))]
[assembly: CommandClass(typeof(CadAiDesigner.FittingCommands))]
[assembly: CommandClass(typeof(CadAiDesigner.AttExportCommands))]
[assembly: CommandClass(typeof(CadAiDesigner.AttImportCommands))]
[assembly: CommandClass(typeof(CadAiDesigner.MlBilgiCommands))]
[assembly: CommandClass(typeof(CadAiDesigner.MlKopyaCommands))]
[assembly: CommandClass(typeof(CadAiDesigner.ArmaturCommands))]
[assembly: CommandClass(typeof(CadAiDesigner.AydHesapCommands))]
[assembly: CommandClass(typeof(CadAiDesigner.OdaSayimCommands))]
[assembly: CommandClass(typeof(CadAiDesigner.TavaCommands))]
[assembly: CommandClass(typeof(CadAiDesigner.HatchDiagCommands))]
[assembly: CommandClass(typeof(CadAiDesigner.RefCommands))]
[assembly: CommandClass(typeof(CadAiDesigner.OlcuCommands))]
[assembly: ExtensionApplication(typeof(CadAiDesigner.PluginApp))]
[assembly: AssemblyTitle("AutoCAD AI Designer")]
[assembly: AssemblyVersion("4.0.0.0")]
[assembly: AssemblyFileVersion("4.0.0.0")]

namespace CadAiDesigner
{
    public class PluginApp : IExtensionApplication
    {
        public void Initialize()
        {
            try
            {
                Document doc = AcadApp.DocumentManager.MdiActiveDocument;
                if (doc != null) doc.Editor.WriteMessage("\nAutoCAD AI Designer yüklendi. Paneli açmak için AIDESIGNER yazın.\n");
            }
            catch (System.Exception) { }
        }

        public void Terminate()
        {
            try { Preview.Clear(); } catch (System.Exception) { }
            try { ServiceHost.Stop(); } catch (System.Exception) { }
        }
    }

    internal static class SelectionState
    {
        static List<ObjectId> ids = new List<ObjectId>();
        public static Document Doc;
        public static event Action Changed;

        public static void Set(Document d, IEnumerable<ObjectId> value)
        {
            List<ObjectId> nv = value.ToList();
            if (d == Doc && nv.Count == ids.Count && !nv.Except(ids).Any()) return; // unchanged: keep pending plan
            Doc = d;
            ids = nv;
            Action h = Changed;
            if (h != null) h();
        }

        public static List<ObjectId> For(Document d)
        {
            return d != Doc ? new List<ObjectId>() : ids.Where(i => !i.IsErased).ToList();
        }
    }

    internal class PendingPlan
    {
        public Document Doc;
        public List<Dictionary<string, object>> Actions;
        public string Fingerprint;
        public string Prompt;
        public HashSet<string> ExtraHandles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    internal static class PlanState
    {
        public static PendingPlan Current;
    }

    public class Commands
    {
        internal static PaletteSet Palette;
        internal static DesignerPanel Panel;
        /// <summary>success, message</summary>
        internal static event Action<bool, string> ApplyFinished;

        [CommandMethod("AIDESIGNER", CommandFlags.Modal)]
        public void OpenPanel()
        {
            try
            {
                if (Palette == null)
                {
                    Palette = new PaletteSet("AutoCAD AI Designer", new Guid("B3A8D5C1-4E27-4F0B-9C55-6E1D2A7F9C04"));
                    Palette.Style = PaletteSetStyles.ShowCloseButton | PaletteSetStyles.ShowAutoHideButton | PaletteSetStyles.ShowPropertiesMenu;
                    Palette.MinimumSize = new System.Drawing.Size(360, 480);
                    Palette.Size = new System.Drawing.Size(440, 680);
                    Palette.Add("AI Designer", BuildPanel());
                }
                else if (Panel == null || Panel.Broken)
                {
                    // blank palette: drop the dead page and build a fresh one
                    try { while (Palette.Count > 0) Palette.Remove(0); } catch (System.Exception) { }
                    Palette.Add("AI Designer", BuildPanel());
                }
                Palette.Visible = true;
                try { if (Panel != null && !Panel.IsDisposed) { Panel.PerformLayout(); Panel.Invalidate(true); Panel.Update(); } } catch (System.Exception) { }
            }
            catch (System.Exception ex)
            {
                AppLog.Write("PANEL OPEN ERROR " + ex);
                Document d = Application.DocumentManager.MdiActiveDocument;
                if (d != null) d.Editor.WriteMessage("\nAI Designer paneli açılamadı: " + ex.Message + "\n");
            }
        }

        /// <summary>Creates the panel; if that fails, returns a page that shows the error instead of a blank palette.</summary>
        static System.Windows.Forms.Control BuildPanel()
        {
            try { Panel = new DesignerPanel(); return Panel; }
            catch (System.Exception ex)
            {
                AppLog.Write("PANEL INIT ERROR " + ex);
                Panel = null;
                return new System.Windows.Forms.TextBox
                {
                    Multiline = true, ReadOnly = true, Dock = System.Windows.Forms.DockStyle.Fill, ScrollBars = System.Windows.Forms.ScrollBars.Vertical,
                    Text = "AI Designer paneli başlatılamadı:\r\n\r\n" + ex.Message + "\r\n\r\nAIDESIGNER komutunu tekrar çalıştırın. Ayrıntı: %LOCALAPPDATA%\\CadAiDesigner\\logs"
                };
            }
        }

        /// <summary>Internal: used by the panel's "Nesne seç" button. Uses the current pickfirst set, else prompts.</summary>
        [CommandMethod("AIDSELECT", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void SelectObjects()
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            PromptSelectionResult r = ed.SelectImplied();
            if (r.Status != PromptStatus.OK)
            {
                PromptSelectionOptions opts = new PromptSelectionOptions();
                opts.MessageForAdding = "\nAI için nesneleri seçin: ";
                r = ed.GetSelection(opts);
            }
            if (r.Status != PromptStatus.OK) return;
            ObjectId[] ids = r.Value.GetObjectIds();
            SelectionState.Set(doc, ids);
            try { ed.SetImpliedSelection(ids); } catch (System.Exception) { }
        }

        /// <summary>Internal: applies the pending plan as one real AutoCAD command, so a single UNDO reverts all of it.</summary>
        [CommandMethod("AIDAPPLY", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ApplyPending()
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            PendingPlan plan = PlanState.Current;
            if (doc == null) return;
            try
            {
                if (plan == null) throw new InvalidOperationException("Bekleyen bir AI planı yok.");
                if (plan.Doc != doc) throw new InvalidOperationException("Çizim değişti; planı yeniden hazırlayın.");

                DrawingContext now = ContextBuilder.Build(doc, SelectionState.For(doc));
                if (now.Fingerprint != plan.Fingerprint)
                    throw new InvalidOperationException("Seçili nesneler plan hazırlandıktan sonra değişmiş; planı yeniden hazırlayın.");
                now.HandleSet.UnionWith(plan.ExtraHandles); // objects resolved from block/layer/type filters
                PlanValidator.Validate(plan.Actions, now);

                List<string> notes = new List<string>();
                string backup = MakeBackup(doc, Settings.Load(), notes);
                Preview.Clear();
                ApplyResult res = Executor.Run(doc, plan.Actions);
                PlanState.Current = null;

                AppLog.Write("APPLIED [" + Path.GetFileName(doc.Name) + "] " + plan.Prompt + " => " + res);
                string msg = "Uygulandı: " + res + ". Geri almak için UNDO (Ctrl+Z)." + (backup != null ? " Yedek: " + backup : "");
                if (notes.Count > 0) msg += " (" + string.Join(" ", notes) + ")";
                doc.Editor.WriteMessage("\n" + msg + "\n");
                Raise(true, msg);
            }
            catch (System.Exception ex)
            {
                // Executor works in a single transaction that was not committed, so the drawing is unchanged.
                string msg = "Uygulanmadı, çizim değişmedi: " + ex.Message;
                AppLog.Write("APPLY FAILED: " + ex.Message);
                try { doc.Editor.WriteMessage("\n" + msg + "\n"); } catch (System.Exception) { }
                Raise(false, msg);
            }
        }

        static void Raise(bool ok, string msg)
        {
            Action<bool, string> h = ApplyFinished;
            if (h != null) h(ok, msg);
        }

        static string MakeBackup(Document doc, Settings s, List<string> notes)
        {
            if (!s.BackupBeforeApply) return null;
            string f = doc.Database.Filename;
            if (string.IsNullOrEmpty(f) || !File.Exists(f))
            {
                notes.Add("DWG henüz kaydedilmemiş; dosya yedeği alınamadı.");
                return null;
            }
            try
            {
                string dest = Path.Combine(AppPaths.Backups, Path.GetFileNameWithoutExtension(f) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".dwg");
                File.Copy(f, dest, false);
                foreach (FileInfo old in new DirectoryInfo(AppPaths.Backups).GetFiles("*.dwg").OrderByDescending(x => x.CreationTime).Skip(30))
                {
                    try { old.Delete(); } catch (System.Exception) { }
                }
                try
                {
                    if (Convert.ToInt32(AcadApp.GetSystemVariable("DBMOD")) != 0)
                        notes.Add("Yedek yalnızca son KAYDEDİLEN sürümü içerir.");
                }
                catch (System.Exception) { }
                return dest;
            }
            catch (System.Exception ex)
            {
                notes.Add("Yedek alınamadı: " + ex.Message);
                return null;
            }
        }
    }
}
