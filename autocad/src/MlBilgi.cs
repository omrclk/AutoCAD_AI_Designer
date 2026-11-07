using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace CadAiDesigner
{
    /// <summary>Palette of block-content attributes for the current pickfirst set (MLeader and BlockReference).</summary>
    public class MlBilgiCommands
    {
        internal static PaletteSet Palette;
        static MlBilgiPanel Panel;

        [CommandMethod("MLBILGI", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void Open() { Show(); }

        [CommandMethod("AIDMLBILGI", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void OpenAlias() { Show(); }

        internal static bool IsOpen { get { return Palette != null && Palette.Visible && Panel != null && !Panel.IsDisposed; } }

        public static void Show()
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            try
            {
                if (Palette == null)
                {
                    Palette = new PaletteSet("Etiket Bilgisi", new Guid("7C2E9A41-6B18-4D55-8F30-1A6E4C8B2D90"));
                    Palette.Style = PaletteSetStyles.ShowCloseButton | PaletteSetStyles.ShowAutoHideButton | PaletteSetStyles.ShowPropertiesMenu;
                    Palette.DockEnabled = DockSides.Left | DockSides.Right;
                    Palette.KeepFocus = true;
                    Palette.MinimumSize = new Size(300, 360);
                    Palette.Size = new Size(340, 520);
                    Panel = new MlBilgiPanel();
                    Palette.Add("Etiket Bilgisi", Panel);
                    Palette.StateChanged += OnState;
                }
                Palette.Visible = true;
                if (Panel != null && !Panel.IsDisposed) Panel.Reload();
            }
            catch (System.Exception ex)
            {
                try { AppLog.Write("MLBILGI " + ex); } catch (System.Exception) { }
                if (doc != null) doc.Editor.WriteMessage("\nEtiket Bilgisi açılamadı: " + ex.Message + "\n");
            }
        }

        static void OnState(object sender, PaletteSetStateEventArgs e)
        {
            try { if (e.NewState == StateEventIndex.Show && IsOpen) Panel.Reload(); } catch (System.Exception) { }
        }
    }

    public class MlBilgiPanel : UserControl
    {
        const string Various = "*Çeşitli*";

        readonly Label head = new Label();
        readonly Label sub = new Label();
        readonly Label hint = new Label();
        readonly Label status = new Label();
        readonly DataGridView grid = new DataGridView();
        readonly Button apply = new Button();
        readonly List<Snap> snaps = new List<Snap>();
        Document doc;
        bool busy;

        public MlBilgiPanel()
        {
            Dock = DockStyle.Fill;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new System.Drawing.Font("Segoe UI", 9);
            head.Dock = DockStyle.Fill; head.AutoSize = false; head.Height = 36; head.Font = new System.Drawing.Font("Segoe UI", 9.5f, System.Drawing.FontStyle.Bold); head.AutoEllipsis = true;
            sub.Dock = DockStyle.Fill; sub.AutoSize = false; sub.Height = 22; sub.ForeColor = SystemColors.GrayText; sub.AutoEllipsis = true;
            hint.Dock = DockStyle.Fill; hint.AutoSize = false; hint.Height = 48; hint.ForeColor = SystemColors.GrayText; hint.Font = new System.Drawing.Font("Segoe UI", 8);
            hint.Text = "Aynı blok çoklu seçimde farklı değer " + Various + " görünür. Değiştirilen değer seçili tüm nesnelere yazılır; olduğu gibi bırakılırsa korunur. Sabit attribute düzenlenemez.";
            status.Dock = DockStyle.Fill; status.AutoSize = false; status.Height = 36; status.AutoEllipsis = true;
            apply.Text = "Uygula"; apply.AutoSize = true; apply.Padding = new Padding(8, 2, 8, 2); apply.Enabled = false;
            apply.Click += (s, e) => Apply();

            grid.Dock = DockStyle.Fill;
            grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false; grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false; grid.MultiSelect = false; grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
            grid.EditMode = DataGridViewEditMode.EditOnEnter; grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.BackgroundColor = SystemColors.Window; grid.BorderStyle = BorderStyle.None; grid.Font = new System.Drawing.Font("Segoe UI", 9);
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "tag", HeaderText = "Etiket", ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable, FillWeight = 40 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "val", HeaderText = "Değer", SortMode = DataGridViewColumnSortMode.NotSortable, FillWeight = 60 });
            grid.CellEndEdit += OnCellEndEdit;

            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(8) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            FlowLayoutPanel bar = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = System.Windows.Forms.FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0) };
            bar.Controls.Add(apply);
            status.Margin = new Padding(8, 6, 0, 0);
            bar.Controls.Add(status);
            layout.Controls.Add(head, 0, 0);
            layout.Controls.Add(sub, 0, 1);
            layout.Controls.Add(grid, 0, 2);
            layout.Controls.Add(hint, 0, 3);
            layout.Controls.Add(bar, 0, 4);
            Controls.Add(layout);

            try
            {
                AcadApp.DocumentManager.DocumentActivated += OnActivated;
                AcadApp.DocumentManager.DocumentCreated += OnCreated;
                AcadApp.DocumentManager.DocumentToBeDestroyed += OnDestroyed;
                foreach (Document d in AcadApp.DocumentManager) Hook(d);
            }
            catch (System.Exception) { }
            ShowIdle("Multileader (blok içerik) veya attribute bloğu seçin.");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    AcadApp.DocumentManager.DocumentActivated -= OnActivated;
                    AcadApp.DocumentManager.DocumentCreated -= OnCreated;
                    AcadApp.DocumentManager.DocumentToBeDestroyed -= OnDestroyed;
                    foreach (Document d in AcadApp.DocumentManager) d.ImpliedSelectionChanged -= OnImplied;
                }
                catch (System.Exception) { }
            }
            base.Dispose(disposing);
        }

        void Hook(Document d)
        {
            if (d == null) return;
            d.ImpliedSelectionChanged -= OnImplied;
            d.ImpliedSelectionChanged += OnImplied;
        }

        void OnCreated(object sender, DocumentCollectionEventArgs e) { try { Hook(e.Document); } catch (System.Exception) { } }

        void OnDestroyed(object sender, DocumentCollectionEventArgs e)
        {
            try { if (e.Document != null) e.Document.ImpliedSelectionChanged -= OnImplied; } catch (System.Exception) { }
        }

        void OnActivated(object sender, DocumentCollectionEventArgs e)
        {
            try { Hook(e.Document); } catch (System.Exception) { }
            if (MlBilgiCommands.IsOpen) UI(() => Reload(true));
        }

        void OnImplied(object sender, EventArgs e)
        {
            Document d = sender as Document;
            if (d == null || d != AcadApp.DocumentManager.MdiActiveDocument) return;
            try
            {
                string cmds = Convert.ToString(AcadApp.GetSystemVariable("CMDNAMES"));
                if (!string.IsNullOrEmpty(cmds)) return;
            }
            catch (System.Exception) { }
            RequestReload();
        }

        void RequestReload()
        {
            if (!MlBilgiCommands.IsOpen) return;
            UI(() => Reload(false));
        }

        void UI(Action a)
        {
            if (IsDisposed || Disposing) return;
            Action safe = () => { try { if (!IsDisposed) a(); } catch (System.Exception ex) { try { AppLog.Write("MLBILGI " + ex); } catch (System.Exception) { } } };
            try { if (InvokeRequired) { if (IsHandleCreated) BeginInvoke(safe); } else safe(); } catch (System.Exception) { }
        }

        /// <summary>Reads the pickfirst set and fills the grid. Safe to call while MLBILGI itself is the active command.</summary>
        internal void Reload() { Reload(true); }

        void Reload(bool force)
        {
            if (busy || IsDisposed) return;
            Document d = AcadApp.DocumentManager.MdiActiveDocument;
            if (d == null) { ShowIdle("Açık çizim yok."); return; }
            ObjectId[] ids;
            try
            {
                PromptSelectionResult r = d.Editor.SelectImplied();
                ids = r.Status == PromptStatus.OK && r.Value != null ? r.Value.GetObjectIds() : new ObjectId[0];
            }
            catch (System.Exception)
            {
                ShowIdle("Seçim okunamadı.");
                return;
            }
            List<Snap> found = new List<Snap>();
            try
            {
                using (d.LockDocument())
                using (Transaction tx = d.Database.TransactionManager.StartOpenCloseTransaction())
                {
                    foreach (ObjectId id in ids)
                    {
                        if (id.IsNull || id.IsErased) continue;
                        Entity ent = null;
                        try { ent = tx.GetObject(id, OpenMode.ForRead) as Entity; } catch (System.Exception) { continue; }
                        Snap s = Read(tx, ent);
                        if (s != null) found.Add(s);
                    }
                    tx.Commit();
                }
            }
            catch (System.Exception ex)
            {
                ShowIdle("Okunamadı: " + ex.Message);
                return;
            }
            if (!force && SameContent(found)) return;
            Bind(d, found);
        }

        bool SameContent(List<Snap> neu)
        {
            if (neu.Count != snaps.Count || doc == null) return false;
            Dictionary<ObjectId, Snap> by = new Dictionary<ObjectId, Snap>();
            foreach (Snap s in snaps)
            {
                if (by.ContainsKey(s.Id)) return false;
                by[s.Id] = s;
            }
            foreach (Snap n in neu)
            {
                Snap o;
                if (!by.TryGetValue(n.Id, out o) || o.Kind != n.Kind) return false;
                if (!string.Equals(o.Block, n.Block, StringComparison.OrdinalIgnoreCase)) return false;
                if (o.Fields.Count != n.Fields.Count) return false;
                for (int i = 0; i < o.Fields.Count; i++)
                {
                    if (!string.Equals(o.Fields[i].Tag, n.Fields[i].Tag, StringComparison.OrdinalIgnoreCase)) return false;
                    if ((o.Fields[i].Value ?? "") != (n.Fields[i].Value ?? "") || o.Fields[i].Constant != n.Fields[i].Constant) return false;
                }
            }
            return true;
        }

        static Snap Read(Transaction tx, Entity ent)
        {
            if (ent == null) return null;
            MLeader ml = ent as MLeader;
            if (ml != null) return FromLeader(ml, AttExport.ReadLeader(tx, ml));
            BlockReference br = ent as BlockReference;
            if (br != null) return FromInsert(tx, br);
            return null;
        }

        static Snap FromLeader(MLeader ml, LeaderInfo li)
        {
            if (li == null || li.Values.Count == 0) return null;
            Snap s = new Snap { Id = ml.ObjectId, Kind = "MLEADER", Block = li.Name };
            foreach (KeyValuePair<string, string> kv in li.Values)
                s.Fields.Add(new Field { Tag = kv.Key, Value = kv.Value ?? "", Constant = li.Const.Contains(kv.Key) });
            return s;
        }

        static Snap FromInsert(Transaction tx, BlockReference br)
        {
            string name = AttExport.EffName(tx, br);
            Snap s = new Snap { Id = br.ObjectId, Kind = "BLOK", Block = name };
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ObjectId defId = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
            try
            {
                BlockTableRecord btr = tx.GetObject(defId, OpenMode.ForRead) as BlockTableRecord;
                if (btr != null && btr.HasAttributeDefinitions)
                    foreach (ObjectId id in btr)
                    {
                        AttributeDefinition ad = tx.GetObject(id, OpenMode.ForRead) as AttributeDefinition;
                        if (ad == null || !seen.Add(ad.Tag)) continue;
                        s.Fields.Add(new Field { Tag = ad.Tag, Value = AttExport.DefText(ad), Constant = ad.Constant });
                    }
            }
            catch (System.Exception) { }
            foreach (ObjectId aid in br.AttributeCollection)
            {
                AttributeReference ar = tx.GetObject(aid, OpenMode.ForRead) as AttributeReference;
                if (ar == null) continue;
                Field f = s.Fields.FirstOrDefault(x => string.Equals(x.Tag, ar.Tag, StringComparison.OrdinalIgnoreCase));
                string val = AttExport.AttText(ar);
                bool cnst = false; try { cnst = ar.IsConstant; } catch (System.Exception) { }
                if (f == null) s.Fields.Add(new Field { Tag = ar.Tag, Value = val, Constant = cnst });
                else if (!f.Constant) f.Value = val;
            }
            if (s.Fields.Count == 0) return null;
            return s;
        }

        void Bind(Document d, List<Snap> found)
        {
            snaps.Clear();
            doc = d;
            grid.Rows.Clear();
            apply.Enabled = false;
            status.Text = "";
            if (found.Count == 0) { ShowIdle("Multileader (blok içerik) veya attribute bloğu seçin."); return; }
            List<string> names = found.Select(s => s.Block ?? "").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (names.Count != 1)
            {
                string shown = string.Join(", ", names.Take(4));
                if (names.Count > 4) shown += "...";
                ShowIdle("Farklı bloklar seçili. Aynı bloğu seçin: " + shown);
                return;
            }
            snaps.AddRange(found);
            head.Text = "Blok: " + names[0];
            int ml = found.Count(s => s.Kind == "MLEADER");
            int bl = found.Count - ml;
            if (ml > 0 && bl > 0) sub.Text = found.Count + " nesne (" + ml + " multileader, " + bl + " blok)";
            else if (ml > 0) sub.Text = ml == 1 ? "1 multileader" : ml + " multileader";
            else sub.Text = bl == 1 ? "1 blok" : bl + " blok";

            List<string> order = new List<string>();
            foreach (Snap s in found) foreach (Field f in s.Fields) if (!order.Contains(f.Tag, StringComparer.OrdinalIgnoreCase)) order.Add(f.Tag);
            bool anyEdit = false;
            foreach (string tag in order)
            {
                List<Field> hits = new List<Field>();
                int present = 0;
                foreach (Snap s in found)
                {
                    Field f = s.Fields.FirstOrDefault(x => string.Equals(x.Tag, tag, StringComparison.OrdinalIgnoreCase));
                    if (f == null) continue;
                    present++;
                    hits.Add(f);
                }
                bool various = present != found.Count || hits.Select(h => h.Value ?? "").Distinct().Count() > 1;
                bool constant = hits.Count > 0 && hits.All(h => h.Constant);
                string shownVal = various ? Various : (hits.Count > 0 ? hits[0].Value ?? "" : "");
                EditRow er = new EditRow { Tag = hits.Count > 0 ? hits[0].Tag : tag, Shown = shownVal, Various = various, Constant = constant };
                int i = grid.Rows.Add(er.Tag, shownVal);
                DataGridViewRow row = grid.Rows[i];
                row.Tag = er;
                if (constant)
                {
                    row.Cells[1].ReadOnly = true;
                    row.Cells[1].Style.ForeColor = SystemColors.GrayText;
                    row.Cells[0].ToolTipText = "Sabit attribute";
                }
                else
                {
                    anyEdit = true;
                    if (various) row.Cells[1].Style.ForeColor = SystemColors.GrayText;
                }
            }
            apply.Enabled = anyEdit;
        }

        void ShowIdle(string message)
        {
            snaps.Clear();
            doc = null;
            grid.Rows.Clear();
            apply.Enabled = false;
            head.Text = message;
            sub.Text = "";
        }

        void OnCellEndEdit(object sender, DataGridViewCellEventArgs e)
        {
            if (e.ColumnIndex != 1 || e.RowIndex < 0) return;
            DataGridViewRow row = grid.Rows[e.RowIndex];
            EditRow er = row.Tag as EditRow;
            if (er == null || er.Constant) return;
            string now = Convert.ToString(row.Cells[1].Value) ?? "";
            row.Cells[1].Style.ForeColor = (er.Various && now == Various) ? SystemColors.GrayText : SystemColors.WindowText;
        }

        void Apply()
        {
            if (busy) return;
            string msg;
            bool wrote;
            busy = true;
            try { msg = ApplyCore(out wrote); }
            finally { busy = false; }
            if (wrote) Reload();
            status.Text = msg ?? "";
        }

        string ApplyCore(out bool wrote)
        {
            wrote = false;
            Document d = AcadApp.DocumentManager.MdiActiveDocument;
            if (d == null) return "Açık çizim yok.";
            if (doc == null || d != doc || snaps.Count == 0) return "Seçim değişti; yeniden seçin.";
            try { if (!d.Editor.IsQuiescent) return "Çalışan bir komut var; Esc ile bitirip tekrar deneyin."; }
            catch (System.Exception) { }
            try { grid.EndEdit(); } catch (System.Exception) { }
            Dictionary<string, string> edits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DataGridViewRow row in grid.Rows)
            {
                EditRow er = row.Tag as EditRow;
                if (er == null || er.Constant) continue;
                string now = Convert.ToString(row.Cells[1].Value) ?? "";
                if (er.Various) { if (now == Various) continue; }
                else if (now == er.Shown) continue;
                edits[er.Tag] = now;
            }
            if (edits.Count == 0) return "Değişiklik yok.";
            int done = 0, lockedN = 0, missing = 0, ents = 0;
            try
            {
                using (d.LockDocument())
                using (Transaction tx = d.Database.TransactionManager.StartTransaction())
                {
                    foreach (Snap s in snaps)
                    {
                        if (s.Id.IsNull || s.Id.IsErased) { missing += edits.Count; continue; }
                        Entity ent = tx.GetObject(s.Id, OpenMode.ForRead) as Entity;
                        if (ent == null) { missing += edits.Count; continue; }
                        LayerTableRecord ltr = tx.GetObject(ent.LayerId, OpenMode.ForRead) as LayerTableRecord;
                        if (ltr != null && ltr.IsLocked) { lockedN++; continue; }
                        BlockReference br = ent as BlockReference;
                        MLeader ml = ent as MLeader;
                        int n = 0;
                        if (br != null) n = WriteInsert(tx, br, edits);
                        else if (ml != null && AttExport.IsBlockContent(ml)) n = WriteLeader(tx, ml, edits);
                        else { missing += edits.Count; continue; }
                        done += n;
                        if (n > 0) ents++;
                        missing += Unmatched(tx, br, ml, edits);
                    }
                    if (done > 0) { tx.Commit(); wrote = true; }
                }
                if (wrote) try { d.Editor.Regen(); } catch (System.Exception) { }
            }
            catch (System.Exception ex)
            {
                string fail = "Yazılamadı, çizim değişmedi: " + ex.Message;
                try { d.Editor.WriteMessage("\n" + fail + "\n"); } catch (System.Exception) { }
                return fail;
            }
            if (done == 0 && lockedN == 0 && missing == 0) return "Değişiklik yok.";
            string msg = done > 0
                ? "Yazıldı: " + done + " değer / " + ents + " nesne. Geri almak için U."
                : "Yazılmadı.";
            if (lockedN > 0) msg += " Kilitli katman: " + lockedN + " nesne atlandı.";
            if (missing > 0) msg += " Uygulanamayan etiket: " + missing + ".";
            try { d.Editor.WriteMessage("\nEtiket bilgisi: " + msg + "\n"); } catch (System.Exception) { }
            try { Metraj.Raise(msg); } catch (System.Exception) { }
            return msg;
        }

        static int WriteInsert(Transaction tx, BlockReference br, Dictionary<string, string> edits)
        {
            int n = 0;
            foreach (ObjectId aid in br.AttributeCollection)
            {
                AttributeReference ar = tx.GetObject(aid, OpenMode.ForRead) as AttributeReference;
                string v;
                if (ar == null || !edits.TryGetValue(ar.Tag, out v)) continue;
                bool cnst = false; try { cnst = ar.IsConstant; } catch (System.Exception) { }
                if (cnst) continue;
                if (!ar.IsWriteEnabled) ar.UpgradeOpen();
                AttExport.SetAttText(ar, v);
                n++;
            }
            return n;
        }

        static int WriteLeader(Transaction tx, MLeader ml, Dictionary<string, string> edits)
        {
            BlockTableRecord def = tx.GetObject(ml.BlockContentId, OpenMode.ForRead) as BlockTableRecord;
            if (def == null) return 0;
            int n = 0;
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId did in def)
            {
                AttributeDefinition ad = tx.GetObject(did, OpenMode.ForRead) as AttributeDefinition;
                if (ad == null || ad.Constant || !seen.Add(ad.Tag)) continue;
                string v;
                if (!edits.TryGetValue(ad.Tag, out v)) continue;
                AttExport.SetLeaderAttr(ml, ad, v);
                n++;
            }
            return n;
        }

        /// <summary>Edit tags that this entity could not accept (so the user sees they were not all written).</summary>
        static int Unmatched(Transaction tx, BlockReference br, MLeader ml, Dictionary<string, string> edits)
        {
            HashSet<string> have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (br != null)
                foreach (ObjectId aid in br.AttributeCollection)
                {
                    AttributeReference ar = tx.GetObject(aid, OpenMode.ForRead) as AttributeReference;
                    if (ar != null) have.Add(ar.Tag);
                }
            else if (ml != null && AttExport.IsBlockContent(ml))
            {
                BlockTableRecord def = tx.GetObject(ml.BlockContentId, OpenMode.ForRead) as BlockTableRecord;
                if (def != null)
                    foreach (ObjectId did in def)
                    {
                        AttributeDefinition ad = tx.GetObject(did, OpenMode.ForRead) as AttributeDefinition;
                        if (ad != null && !ad.Constant) have.Add(ad.Tag);
                    }
            }
            int n = 0;
            foreach (string t in edits.Keys) if (!have.Contains(t)) n++;
            return n;
        }

        class Snap
        {
            public ObjectId Id;
            public string Kind;
            public string Block;
            public readonly List<Field> Fields = new List<Field>();
        }

        class Field
        {
            public string Tag;
            public string Value;
            public bool Constant;
        }

        class EditRow
        {
            public string Tag;
            public string Shown;
            public bool Various;
            public bool Constant;
        }
    }
}
