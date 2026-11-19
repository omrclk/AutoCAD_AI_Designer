using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using ObjectId = Autodesk.AutoCAD.DatabaseServices.ObjectId;
using Autodesk.AutoCAD.EditorInput;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace CadAiDesigner
{
    public class DesignerPanel : UserControl
    {
        readonly TextBox chat = new TextBox();
        readonly TextBox prompt = new TextBox();
        readonly Label status = new Label();
        readonly Button btnSelect = new Button(), btnAsk = new Button(), btnApply = new Button(),
                        btnCancel = new Button(), btnNew = new Button(), btnSettings = new Button(), btnMetraj = new Button(), btnFit = new Button(), btnAtt = new Button(), btnTava = new Button(), btnPk = new Button(), btnRef = new Button(), btnOlcu = new Button(), btnAttImp = new Button(), btnMl = new Button(), btnMlKop = new Button(), btnArm = new Button(), btnAyd = new Button(), btnOda = new Button(), btnOSinir = new Button();
        readonly JavaScriptSerializer js = new JavaScriptSerializer { MaxJsonLength = 8000000, RecursionLimit = 40 };
        readonly ToolTip tips = new ToolTip();
        string session = Guid.NewGuid().ToString("N");
        bool busy, warnedSel;

        public DesignerPanel()
        {
            Dock = DockStyle.Fill;
            AutoScaleMode = AutoScaleMode.Dpi;
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(8) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); // never grow wider than the palette
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            status.Dock = DockStyle.Fill; status.AutoSize = false; status.Height = 40; status.AutoEllipsis = true; status.Padding = new Padding(0, 0, 0, 6);
            chat.Multiline = true; chat.WordWrap = true; chat.ReadOnly = true; chat.ScrollBars = ScrollBars.Vertical; chat.HideSelection = false; chat.Dock = DockStyle.Fill;
            chat.Font = new Font("Segoe UI", 9); chat.BackColor = SystemColors.Window;
            prompt.Multiline = true; prompt.WordWrap = true; prompt.AcceptsReturn = true; prompt.ScrollBars = ScrollBars.Vertical; prompt.Dock = DockStyle.Fill; prompt.Font = new Font("Segoe UI", 9.5f);
            prompt.KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.Enter) { e.Handled = true; e.SuppressKeyPress = true; AskAsync().ContinueWith(t => { }); return; }
                if ((e.KeyCode == Keys.Up || e.KeyCode == Keys.Down) && history.Count > 0 && (e.Control || prompt.TextLength == 0 || histPos >= 0))
                {
                    if (!e.Control && prompt.TextLength > 0 && histPos >= 0 && prompt.Text != history[histPos]) return; // user edited a recalled entry: normal caret keys
                    e.Handled = true; e.SuppressKeyPress = true;
                    if (histPos < 0) histPos = history.Count;
                    histPos += e.KeyCode == Keys.Up ? -1 : 1;
                    if (histPos < 0) histPos = 0;
                    if (histPos >= history.Count) { histPos = -1; prompt.Clear(); return; }
                    prompt.Text = history[histPos]; prompt.SelectionStart = prompt.TextLength;
                }
            };

            Setup(btnSelect, "Nesne seç", (s, e) => SelectObjects());
            Setup(btnAsk, "Plan hazırla", async (s, e) => await AskAsync());
            Setup(btnApply, "Onayla / Uygula", (s, e) => ApplyClicked());
            Setup(btnCancel, "İptal", (s, e) => { ClearPlan(); Log("Plan iptal edildi."); });
            Setup(btnNew, "Yeni sohbet", (s, e) => NewChat());
            Setup(btnSettings, "Ayarlar", (s, e) => OpenSettings());
            Setup(btnFit, "Ek parçalar", (s, e) => { Document d = AcadApp.DocumentManager.MdiActiveDocument; if (d != null) { Log("Ek parçalar: tavaları seçin veya Enter (tümü), sonra komut satırında onaylayın."); d.SendStringToExecute("AIDBAGLANTI ", true, false, false); } });
            Setup(btnTava, "Tava Yerleştir", (s, e) => { Document d = AcadApp.DocumentManager.MdiActiveDocument; if (d != null) { Log("Tava Yerleştir: çizgileri seçin veya Tumu, Enter = son ayar. Eksen: tava ekseni. Renk: örnek çizginin katman ve rengi (ByLayer ayrı); Tumu alt seçeneği renk tablosundaki EN ile yerleştirir. RenkTablosu: 1=100 gibi."); d.SendStringToExecute("AIDTAVA ", true, false, false); } });
            tips.SetToolTip(btnTava, "Renk: örnek çizgiyle aynı katman ve renk. Tumu: katmandaki her renk, RenkTablosu EN'i. Eksen veya çift çizgi son ayardan.");
            Setup(btnPk, "Plandan Keşif", (s, e) => { Document d = AcadApp.DocumentManager.MdiActiveDocument; if (d != null) { Log("Plandan keşif: plan çizgilerini seçin veya Tumu, sonra Enter (çizim değişmez)."); d.SendStringToExecute("AIDPLANKESIF ", true, false, false); } });
            Setup(btnRef, "Referans noktaları", (s, e) => { Document d = AcadApp.DocumentManager.MdiActiveDocument; if (d != null) { Log("Referans noktaları: önce kaynak plandan bir nokta, sonra hedef plandaki AYNI noktayı fareyle (osnap) seçin. İkinci çift dönme/ölçek için isteğe bağlıdır."); d.SendStringToExecute("AIDREFNOKTA ", true, false, false); } });
            Setup(btnOlcu, "Blok ölçüsü", (s, e) => { Document d = AcadApp.DocumentManager.MdiActiveDocument; if (d != null) { Log("Blok ölçüsü: blokları seçin (pencere), Enter; sonra ölçü çizgisinin tarafını tıklayın (Enter = varsayılan). Ardışık ölçüler blok merkezlerinden yapılır."); d.SendStringToExecute("AIDOLCU ", true, false, false); } });
            Setup(btnAtt, "Attribute Excel", (s, e) => { Document d = AcadApp.DocumentManager.MdiActiveDocument; if (d != null) { Log("Attribute Excel: komut satırındaki soruları yanıtlayın (seçim varsa seçili bloklar aktarılır)."); d.SendStringToExecute("AIDATTEXPORT ", true, false, false); } });
            Setup(btnAttImp, "Attribute İçe Aktar", (s, e) => { Document d = AcadApp.DocumentManager.MdiActiveDocument; if (d != null) { Log("Attribute İçe Aktar: düzenlenmiş Excel dosyasını seçin; önizlemeden sonra komut satırında Evet/Hayır ile onaylayın (Handle sütununu değiştirmeyin)."); d.SendStringToExecute("AIDATTIMPORT ", true, false, false); } });
            Setup(btnMl, "Etiket Bilgisi", (s, e) => { Document d = AcadApp.DocumentManager.MdiActiveDocument; if (d != null) { Log("Etiket Bilgisi: seçili multileader (blok içerik) veya attribute bloğu. Farklı değer *Çeşitli* görünür; Uygula yazılan değeri seçili tüm nesnelere yazar."); d.SendStringToExecute("MLBILGI ", true, false, false); } });
            Setup(btnMlKop, "Bloklara etiket kopyala", (s, e) => { Document d = AcadApp.DocumentManager.MdiActiveDocument; if (d != null) { Log("Bloklara etiket kopyala: multileader'ı seçin, baz noktasını tıklayın, örnek blokta karşılık gelen noktayı tıklayın (Enter = ekleme noktası), sonra blokları pencere ile seçin. Tekli ile tek tek tıklanır. 50 mm içinde oku olan blok atlanır. Tek UNDO."); d.SendStringToExecute("MLKOPYA ", true, false, false); } });
            Setup(btnOSinir, "Oda sınırı çiz", (s, e) => { Document d = AcadApp.DocumentManager.MdiActiveDocument; if (d != null) { Log("Oda sınırı: planı pencere ile seçin; OSAY ile aynı oda tespiti, kapalı polyline A_ODA_SINIR katmanında."); d.SendStringToExecute("AIDODASINIR ", true, false, false); } });
            Setup(btnOda, "Oda blok sayımı", (s, e) => { Document d = AcadApp.DocumentManager.MdiActiveDocument; if (d != null) { Log("Oda blok sayımı: kapalı oda polyline'ları (veya iç nokta), isteğe bağlı bina sınırı ve blokları seçin. Excel: satır=blok, sütun=oda, toplamlar; mükerrer tek, bina dışı sayılmaz."); d.SendStringToExecute("AIDODASAYIM ", true, false, false); } });
            Setup(btnAyd, "Aydınlatma hesabı", (s, e) => { Document d = AcadApp.DocumentManager.MdiActiveDocument; if (d != null) { Log("Aydınlatma hesabı: oda/koridor polyline'ını (veya iç nokta) ve armatürleri seçin; H, Φ, MF, yansımalar sorulur. Eort lümen yöntemi, Uo yaklaşık."); d.SendStringToExecute("AIDAYDHESAP ", true, false, false); } });
            Setup(btnArm, "Armatür yerleşimi", (s, e) => { Document d = AcadApp.DocumentManager.MdiActiveDocument; if (d != null) { Log("Armatür yerleşimi: armatür bloğunu tıklayın, sonra açık (koridor: tek sıra) / kapalı (oda: grid) polyline'ları seçin, x mesafesini girin veya iki nokta tıklayın. Uç/duvar = x, aralık = 2x."); d.SendStringToExecute("AIDARMATUR ", true, false, false); } });
            Setup(btnMetraj, "Metraj", (s, e) => { Document d = AcadApp.DocumentManager.MdiActiveDocument; if (d != null) { Log("Metraj: AutoCAD komut satırındaki soruları yanıtlayın (Enter = varsayılan)."); d.SendStringToExecute("AIDMETRAJ ", true, false, false); } });
            btnApply.Enabled = false; btnCancel.Enabled = false;

            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0) };
            buttons.Controls.AddRange(new Control[] { btnSelect, btnAsk, btnApply, btnCancel, btnNew, btnSettings, btnMetraj, btnTava, btnPk, btnRef, btnOlcu, btnFit, btnAtt, btnAttImp, btnMl, btnMlKop, btnArm, btnAyd, btnOda, btnOSinir });
            Label tip = new Label
            {
                Dock = DockStyle.Fill, AutoSize = false, Height = 48, AutoEllipsis = true, ForeColor = SystemColors.GrayText, Padding = new Padding(0, 6, 0, 0),
                Text = "Ctrl+Enter: gönder. Önizleme AutoCAD'de renkli hayalet olarak görünür (yeşil: taşınan/değişen, sarı: kopya, camgöbeği: yeni). Uygulama tek UNDO ile geri alınır. Ölçüler çizim birimindedir."
            };
            layout.Controls.Add(status, 0, 0);
            layout.Controls.Add(chat, 0, 1);
            layout.Controls.Add(prompt, 0, 2);
            layout.Controls.Add(buttons, 0, 3);
            layout.Controls.Add(tip, 0, 4);
            Controls.Add(layout);
            Resize += (s, e) => { buttons.MaximumSize = new Size(Math.Max(60, layout.ClientSize.Width - 16), 0); layout.PerformLayout(); };

            SelectionState.Changed += OnSelectionChanged;
            Commands.ApplyFinished += OnApplyFinished;
            Metraj.Finished += OnMetraj;
            RefPoints.Changed += OnRefChanged;
            AcadApp.DocumentManager.DocumentActivated += OnDocumentActivated;
            AcadApp.DocumentManager.DocumentCreated += OnDocumentCreated;
            foreach (Document d in AcadApp.DocumentManager) Hook(d);
            CaptureImplied(AcadApp.DocumentManager.MdiActiveDocument);
            RefreshStatus();
            Log("Hazır. Çizimde nesne seçip 'Nesne seç'e basın (seçim olmadan yalnızca yeni nesne çizilebilir), sonra isteğinizi yazın.");
        }

        static void Setup(Button b, string text, EventHandler click)
        {
            b.Text = text; b.AutoSize = true; b.Padding = new Padding(4, 0, 4, 0); b.Click += click;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SelectionState.Changed -= OnSelectionChanged;
                Commands.ApplyFinished -= OnApplyFinished;
                Metraj.Finished -= OnMetraj;
                RefPoints.Changed -= OnRefChanged;
                try { AcadApp.DocumentManager.DocumentActivated -= OnDocumentActivated; } catch (System.Exception) { }
                try
                {
                    AcadApp.DocumentManager.DocumentCreated -= OnDocumentCreated;
                    foreach (Document d in AcadApp.DocumentManager) d.ImpliedSelectionChanged -= OnImpliedSelectionChanged;
                }
                catch (System.Exception) { }
                try { tips.Dispose(); } catch (System.Exception) { }
            }
            base.Dispose(disposing);
        }

        void OnMetraj(string s) { UI(() => Log(s)); }
        void OnRefChanged() { UI(() => { Document d = AcadApp.DocumentManager.MdiActiveDocument; Log("Referans noktaları kaydedildi: " + RefPoints.Summary(d)); RefreshStatus(); }); }

        /// <summary>Runs UI work on the panel thread; never lets an exception escape into AutoCAD's event loop.</summary>
        internal void UI(Action a)
        {
            if (IsDisposed || Disposing) return;
            Action safe = () => { try { if (!IsDisposed) a(); } catch (System.Exception ex) { try { AppLog.Write("PANEL ERROR " + ex); } catch (System.Exception) { } } };
            try { if (InvokeRequired) { if (IsHandleCreated) BeginInvoke(safe); } else safe(); } catch (System.Exception) { }
        }

        string presetCache; DateTime presetAt;
        string Preset()
        {
            if (presetCache == null || (DateTime.Now - presetAt).TotalSeconds > 5)
            {
                try { presetCache = Convert.ToString(Settings.Load().Preset); } catch (System.Exception) { presetCache = presetCache ?? "?"; }
                presetAt = DateTime.Now;
            }
            return presetCache;
        }

        void Log(string s)
        {
            chat.AppendText(s + Environment.NewLine + Environment.NewLine);
            chat.SelectionStart = chat.TextLength; chat.ScrollToCaret();
        }

        /// <summary>True when the panel has lost its controls (blank palette) and must be rebuilt.</summary>
        internal bool Broken { get { return IsDisposed || Controls.Count == 0; } }

        /// <summary>Inserts break opportunities into very long unbroken tokens (paths, handles) so the TextBox can wrap them.</summary>
        static string BreakLong(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            System.Text.StringBuilder b = new System.Text.StringBuilder(s.Length + 16);
            int run = 0;
            foreach (char c in s)
            {
                b.Append(c);
                run = char.IsWhiteSpace(c) ? 0 : run + 1;
                if (run >= 40 && (c == '\\' || c == '/' || c == '_' || c == '.' || c == '-' || run >= 60)) { b.Append('\u200B'); run = 0; }
            }
            return b.ToString();
        }

        void RefreshStatus()
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            int n = doc == null ? 0 : SelectionState.For(doc).Count;
            if (IsDisposed) return;
            status.Text = "Seçili nesne: " + n + "   |   Servis: " + (ServiceHost.IsRunning ? "çalışıyor" : "beklemede") + "   |   AI: " + Preset() + (doc != null && RefPoints.For(doc).Count > 0 ? "   |   Ref: " + RefPoints.For(doc).Count + " çift" : "");
        }

        void ClearPlan()
        {
            PlanState.Current = null;
            Preview.Clear();
            btnApply.Enabled = false; btnCancel.Enabled = false;
        }

        void OnSelectionChanged() { UI(OnSelectionChangedCore); }
        void OnSelectionChangedCore()
        {
            if (PlanState.Current != null && !warnedSel) { warnedSel = true; Log("Not: seçim değişti; bekleyen plan, hazırlandığı nesnelerle uygulanır (gerekirse İptal)."); }
            RefreshStatus();
        }

        void OnDocumentActivated(object sender, DocumentCollectionEventArgs e) { UI(() => OnDocumentActivatedCore(e)); }
        void OnDocumentActivatedCore(DocumentCollectionEventArgs e)
        {
            Hook(e.Document);
            if (PlanState.Current == null) CaptureImplied(e.Document);
            if (PlanState.Current != null) { ClearPlan(); Log("Çizim değişti; bekleyen plan iptal edildi."); }
            RefreshStatus();
        }

        // ---- live tracking of the AutoCAD pickfirst (grip) selection --------------------------------------------
        void Hook(Document d)
        {
            if (d == null) return;
            d.ImpliedSelectionChanged -= OnImpliedSelectionChanged;
            d.ImpliedSelectionChanged += OnImpliedSelectionChanged;
        }

        void OnDocumentCreated(object sender, DocumentCollectionEventArgs e) { try { Hook(e.Document); } catch (System.Exception) { } }

        void OnImpliedSelectionChanged(object sender, EventArgs e)
        {
            Document d = sender as Document;
            if (d == null || d != AcadApp.DocumentManager.MdiActiveDocument) return;
            try
            {
                // While one of our commands (AIDSELECT/AIDAPPLY) or any other command runs, the pickfirst set is
                // consumed/cleared transiently; that must not wipe the tracked selection or a pending plan.
                string cmds = Convert.ToString(AcadApp.GetSystemVariable("CMDNAMES"));
                if (!string.IsNullOrEmpty(cmds)) return;
            }
            catch (System.Exception) { }
            // Esc/*Cancel* clears the pickfirst set from inside AutoCAD's own processing: read it later, on the UI loop.
            UI(() => { CaptureImplied(d); });
        }

        /// <summary>Reads the current pickfirst set (objects with grips) into SelectionState. Returns the count.</summary>
        static int CaptureImplied(Document d)
        {
            if (d == null) return 0;
            try
            {
                PromptSelectionResult r = d.Editor.SelectImplied();
                ObjectId[] ids = r.Status == PromptStatus.OK && r.Value != null ? r.Value.GetObjectIds() : new ObjectId[0];
                SelectionState.Set(d, ids);
                return ids.Length;
            }
            catch (System.Exception) { return -1; }
        }

        void OnApplyFinished(bool ok, string message) { UI(() => OnApplyFinishedCore(ok, message)); }
        void OnApplyFinishedCore(bool ok, string message)
        {
            ClearPlan();
            Log(message);
            RefreshStatus();
        }

        void SelectObjects()
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) { Log("Açık DWG yok."); return; }
            if (!doc.Editor.IsQuiescent) { Log("AutoCAD'de çalışan bir komut var; Esc ile bitirip tekrar deneyin."); return; }
            doc.SendStringToExecute("AIDSELECT ", true, false, false);
            AcadApp.MainWindow.Focus(); // so AutoCAD receives the selection clicks
        }

        void NewChat()
        {
            session = Guid.NewGuid().ToString("N");
            chat.Clear();
            ClearPlan();
            Log("Yeni sohbet başlatıldı.");
        }

        void OpenSettings()
        {
            Settings s = Settings.Load();
            using (SettingsForm f = new SettingsForm(s))
            {
                if (AcadApp.ShowModalDialog(f) == DialogResult.OK)
                {
                    ServiceHost.Stop(); // restarted with the new key/model on the next request
                    Log("Ayarlar kaydedildi.");
                    RefreshStatus();
                }
            }
        }

        void SetBusy(bool value)
        {
            busy = value;
            btnAsk.Enabled = !value;
            if (value) { btnApply.Enabled = false; status.Text = "Plan hazırlanıyor..."; }
            else RefreshStatus();
        }

        readonly List<string> history = new List<string>();
        int histPos = -1;

        async Task AskAsync()
        {
            if (busy) return;
            string msg = prompt.Text.Trim();
            if (msg.Length == 0) return;
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) { Log("Açık DWG yok."); return; }

            SetBusy(true);
            try
            {
                ClearPlan();
                Log("Siz: " + msg);
                if (history.Count == 0 || history[history.Count - 1] != msg) history.Add(msg);
                histPos = -1;
                prompt.Clear();
                if (SelectionState.For(doc).Count == 0) CaptureImplied(doc); // grips selected before the panel got focus
                DrawingContext ctx = ContextBuilder.Build(doc, SelectionState.For(doc));
                ctx.Revisions = Metraj.Revisions(doc);
                RefreshStatus();
                Settings s = Settings.Load();
                string json = js.Serialize(new Dictionary<string, object> { { "message", msg }, { "session", session }, { "context", ctx.ToJson() } });
                AppLog.Write("ASK [" + System.IO.Path.GetFileName(doc.Name) + ", sel=" + ctx.SelectionTotal + "] " + msg);

                string body = await Task.Run(() => { ServiceHost.EnsureStarted(s); return ServiceHost.Post("/plan", json); });
                Dictionary<string, object> resp = js.Deserialize<Dictionary<string, object>>(body);
                if (resp == null) throw new InvalidOperationException("Servisten boş yanıt geldi.");
                if (resp.ContainsKey("error")) throw new InvalidOperationException(Convert.ToString(resp["error"]));

                Log("AI: " + J.Str(resp, "reply"));
                List<Dictionary<string, object>> actions = new List<Dictionary<string, object>>();
                List<object> list = J.Arr(J.Get(resp, "actions"));
                if (list != null) foreach (object o in list) { Dictionary<string, object> d = o as Dictionary<string, object>; if (d != null) actions.Add(d); }

HashSet<string> extra = Targets.Resolve(doc, actions, ctx);
                if (actions.RemoveAll(x => J.Str(x, "type") == "plan_takeoff") > 0)
                {
                    Log("Plandan keşif başlatıldı (salt okunur): komut satırında plan çizgilerini seçin veya Tumu, sonra Enter.");
                    doc.SendStringToExecute("AIDPLANKESIF ", true, false, false);
                }
                List<Dictionary<string, object>> pcs = actions.Where(x => J.Str(x, "type") == "plan_copy").ToList();
                actions.RemoveAll(x => J.Str(x, "type") == "plan_copy");
                if (pcs.Count > 0)
                {
                    if (RefPoints.For(doc).Count == 0) Log("Referans noktası yok: 'Referans noktaları' düğmesine basıp kaynak ve hedef plandaki aynı noktayı seçin.");
                    else if (SelectionState.For(doc).Count == 0) Log("Planlar arası kopyalama için önce nesneleri seçin.");
                    else
                    {
                        try { doc.Editor.SetImpliedSelection(SelectionState.For(doc).ToArray()); } catch (System.Exception) { }
                        RefPoints.PendingMode = J.Str(pcs[0], "mode") == "move" ? "Tasi" : "Kopyala";
                        Log("Planlar arası " + (RefPoints.PendingMode == "Tasi" ? "taşıma" : "kopyalama") + " başlatıldı: komut satırında onaylayın (Enter). Geri almak için UNDO.");
                        doc.SendStringToExecute("AIDPLANKOPYA ", true, false, false);
                    }
                }
                List<Dictionary<string, object>> attx = actions.Where(x => J.Str(x, "type") == "export_attributes").ToList();
                actions.RemoveAll(x => J.Str(x, "type") == "export_attributes");
                foreach (Dictionary<string, object> xa in attx)
                {
                    Log("Attribute'lar Excel'e aktarılıyor (salt okunur)...");
                    try { Log(AttExport.Run(doc, AttExport.FromAction(xa, SelectionState.For(doc)))); }
                    catch (System.Exception ex) { Log("Attribute aktarma hatası: " + ex.Message); }
                }
                List<Dictionary<string, object>> takeoffs = actions.Where(x => J.Str(x, "type") == "takeoff").ToList();
                actions.RemoveAll(x => J.Str(x, "type") == "takeoff");
                foreach (Dictionary<string, object> ta in takeoffs)
                {
                    Log("Metraj hesaplanıyor (salt okunur)...");
                    TakeoffRequest rq = Metraj.FromAction(ta, doc, SelectionState.For(doc));
                    try { Log(Metraj.Run(doc, rq)); } // drawing must be read on the AutoCAD main thread
                    catch (System.Exception ex) { Log("Metraj hatası: " + ex.Message); }
                }

                if (actions.Count > 0)
                {
                    try { PlanValidator.Validate(actions, ctx); }
                    catch (InvalidOperationException ex) { Log("Güvenlik doğrulaması planı reddetti: " + ex.Message); return; }
                    PlanState.Current = new PendingPlan { Doc = doc, Actions = actions, Fingerprint = ctx.Fingerprint, Prompt = msg, ExtraHandles = extra };
                    warnedSel = false;
                    int i = 1;
                    foreach (Dictionary<string, object> a in actions)
                    {
                        Log("  " + (i++) + ") " + PlanDescriber.Describe(a));
                        if (J.Str(a, "type") == "place_block_along") Log("      → " + BlockAlong.Summary(doc, a));
                        if (J.Str(a, "type") == "place_fittings") Log("      → " + Fittings.Summary(doc, a));
                    }
                    Preview.Show(doc, actions);
                    btnApply.Enabled = true; btnCancel.Enabled = true;
                    Log("Bu bir ÖNERİDİR; çizime henüz hiçbir şey yazılmadı. Uygun ise 'Onayla / Uygula'.");
                }
                prompt.Clear();
            }
            catch (System.Exception ex)
            {
                ClearPlan();
                Log("Hata: " + ex.Message);
            }
            finally { SetBusy(false); }
        }

        void ApplyClicked()
        {
            PendingPlan plan = PlanState.Current;
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (plan == null || doc == null) return;
            if (plan.Doc != doc) { ClearPlan(); Log("Çizim değişti; planı yeniden hazırlayın."); return; }
            if (!doc.Editor.IsQuiescent) { Log("AutoCAD'de çalışan bir komut var; Esc ile bitirip tekrar deneyin."); return; }

            string text = plan.Actions.Count + " işlem '" + System.IO.Path.GetFileName(doc.Name) + "' çizimine uygulanacak:\n\n" +
                string.Join("\n", plan.Actions.Take(12).Select((a, i) => (i + 1) + ") " + PlanDescriber.Describe(a))) +
                (plan.Actions.Count > 12 ? "\n..." : "") +
                "\n\nTek adımda uygulanır; UNDO ile geri alınabilir. Devam edilsin mi?";
            if (MessageBox.Show(text, "AutoCAD AI Designer", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            btnApply.Enabled = false;
            doc.SendStringToExecute("AIDAPPLY ", true, false, false);
        }
    }
}
