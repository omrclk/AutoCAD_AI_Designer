using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace CadAiDesigner
{
    internal static class AppPaths
    {
        public static string Dir
        {
            get
            {
                string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CadAiDesigner");
                Directory.CreateDirectory(d);
                return d;
            }
        }
        public static string Logs { get { string d = Path.Combine(Dir, "logs"); Directory.CreateDirectory(d); return d; } }
        public static string Backups { get { string d = Path.Combine(Dir, "backups"); Directory.CreateDirectory(d); return d; } }
    }

    /// <summary>Append-only audit log (prompts and applied plans; never the API key or drawing geometry).</summary>
    internal static class AppLog
    {
        static readonly object Gate = new object();
        public static void Write(string line)
        {
            try
            {
                lock (Gate)
                    File.AppendAllText(Path.Combine(AppPaths.Logs, DateTime.Now.ToString("yyyyMMdd") + ".log"),
                        DateTime.Now.ToString("HH:mm:ss") + " " + line + Environment.NewLine, Encoding.UTF8);
            }
            catch (System.Exception) { }
        }
    }

    /// <summary>AI provider preset shown in the settings dialog.</summary>
    internal class ProviderPreset
    {
        public string Name, Provider, BaseUrl, Model, KeyHint;
        public bool NeedsKey;
        public override string ToString() { return Name; }

        public static readonly ProviderPreset[] All =
        {
            new ProviderPreset { Name = "Google Gemini (ücretsiz anahtar)", Provider = "openai-compatible", BaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai", Model = "gemini-2.5-flash", NeedsKey = true, KeyHint = "Ücretsiz anahtar: aistudio.google.com/apikey" },
            new ProviderPreset { Name = "Groq (ücretsiz anahtar)", Provider = "openai-compatible", BaseUrl = "https://api.groq.com/openai/v1", Model = "llama-3.3-70b-versatile", NeedsKey = true, KeyHint = "Ücretsiz anahtar: console.groq.com/keys" },
            new ProviderPreset { Name = "OpenRouter", Provider = "openai-compatible", BaseUrl = "https://openrouter.ai/api/v1", Model = "meta-llama/llama-3.3-70b-instruct:free", NeedsKey = true, KeyHint = "Anahtar: openrouter.ai/keys (\":free\" modeller ücretsiz)" },
            new ProviderPreset { Name = "OpenAI", Provider = "openai-compatible", BaseUrl = "https://api.openai.com/v1", Model = "gpt-4.1-mini", NeedsKey = true, KeyHint = "API kredisi gerekir (ChatGPT Plus kapsamaz)." },
            new ProviderPreset { Name = "DeepSeek", Provider = "openai-compatible", BaseUrl = "https://api.deepseek.com/v1", Model = "deepseek-chat", NeedsKey = true, KeyHint = "Anahtar: platform.deepseek.com (ucuz, kredi gerekir)" },
            new ProviderPreset { Name = "Anthropic Claude", Provider = "anthropic", BaseUrl = "https://api.anthropic.com/v1", Model = "claude-sonnet-4-5", NeedsKey = true, KeyHint = "Anahtar: console.anthropic.com (kredi gerekir)" },
            new ProviderPreset { Name = "Ollama (yerel, anahtarsız)", Provider = "openai-compatible", BaseUrl = "http://localhost:11434/v1", Model = "qwen2.5:7b", NeedsKey = false, KeyHint = "ollama.com'dan kurun, sonra: ollama pull qwen2.5:7b" },
            new ProviderPreset { Name = "Codex CLI (ChatGPT girişi)", Provider = "codex-cli", BaseUrl = "", Model = "", NeedsKey = false, KeyHint = "Anahtar gerekmez. Bir kez terminalde: codex login" },
            new ProviderPreset { Name = "Cursor CLI (cursor-agent)", Provider = "cursor-cli", BaseUrl = "", Model = "", NeedsKey = false, KeyHint = "Anahtar gerekmez. cursor.com/cli kurup: cursor-agent login" },
            new ProviderPreset { Name = "Özel (OpenAI uyumlu)", Provider = "openai-compatible", BaseUrl = "", Model = "", NeedsKey = true, KeyHint = "Herhangi bir OpenAI uyumlu /chat/completions adresi (LM Studio, Together, Mistral...)." },
        };

        public static ProviderPreset Find(string name)
        {
            return All.FirstOrDefault(p => p.Name == name);
        }
    }

    /// <summary>User settings. API keys are stored encrypted with Windows DPAPI (current user only), one per preset.</summary>
    internal class Settings
    {
        public string Preset = "OpenAI";
        public string Provider = "openai-compatible";
        public string BaseUrl = "https://api.openai.com/v1";
        public string Model = "gpt-4.1-mini";
        public string PythonPath = "";
        public string ApiKeyProtected = "";                       // legacy (V4.0) OpenAI key
        public Dictionary<string, string> ApiKeys = new Dictionary<string, string>();
        public bool BackupBeforeApply = true;
        /// <summary>Max selected objects sent to the AI; 0 = unlimited (default).</summary>
        public int MaxObjects = 0;
        /// <summary>Selections larger than this are sent as a compact table instead of one JSON object per entity.</summary>
        public int CompactAbove = 100;

        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CadAiDesigner.v4");
        static string FilePath { get { return Path.Combine(AppPaths.Dir, "settings.json"); } }

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    Settings s = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(FilePath, Encoding.UTF8));
                    if (s != null)
                    {
                        if (s.ApiKeys == null) s.ApiKeys = new Dictionary<string, string>();
                        if (ProviderPreset.Find(s.Preset) == null && s.Preset != null && s.Preset.StartsWith("OpenAI")) s.Preset = "OpenAI";
                        if (string.IsNullOrEmpty(s.Provider)) s.Provider = "openai-compatible";
                        return s;
                    }
                }
            }
            catch (System.Exception) { }
            return new Settings();
        }

        public void Save()
        {
            File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(this), Encoding.UTF8);
        }

        string StoredFor(string preset)
        {
            string v;
            if (ApiKeys != null && preset != null && ApiKeys.TryGetValue(preset, out v) && !string.IsNullOrEmpty(v)) return v;
            if (preset == "OpenAI" && !string.IsNullOrEmpty(ApiKeyProtected)) return ApiKeyProtected;
            return null;
        }

        public bool HasKeyFor(string preset) { return StoredFor(preset) != null; }
        public bool HasStoredKey { get { return HasKeyFor(Preset); } }

        public void SetApiKey(string preset, string key)
        {
            if (ApiKeys == null) ApiKeys = new Dictionary<string, string>();
            if (string.IsNullOrWhiteSpace(key)) { ApiKeys.Remove(preset); return; }
            ApiKeys[preset] = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), Entropy, DataProtectionScope.CurrentUser));
        }

        /// <summary>Stored key for the current preset; for OpenAI the OPENAI_API_KEY environment variable is the fallback.</summary>
        public string GetApiKey()
        {
            string p = StoredFor(Preset);
            if (p != null)
            {
                try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(p), Entropy, DataProtectionScope.CurrentUser)); }
                catch (System.Exception) { }
            }
            return Preset == "OpenAI" ? (Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "") : "";
        }

        /// <summary>Identifies the running service configuration (restart when it changes).</summary>
        public string Signature { get { return Provider + "|" + BaseUrl + "|" + Model + "|" + PythonPath; } }
    }

    internal static class CliDetector
    {
        /// <summary>Returns the full path of the first matching executable on PATH (where.exe), or null.</summary>
        public static string Find(params string[] names)
        {
            foreach (string n in names)
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo("where.exe", n)
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    using (Process p = Process.Start(psi))
                    {
                        string o = p.StandardOutput.ReadToEnd();
                        p.WaitForExit(3000);
                        if (p.ExitCode == 0)
                        {
                            string first = o.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                            if (!string.IsNullOrEmpty(first)) return first.Trim();
                        }
                    }
                }
                catch (System.Exception) { }
            }
            return null;
        }
    }

    internal class SettingsForm : Form
    {
        readonly ComboBox preset = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        readonly TextBox baseUrl = new TextBox { Dock = DockStyle.Fill };
        readonly TextBox key = new TextBox { UseSystemPasswordChar = true, Dock = DockStyle.Fill };
        readonly TextBox model = new TextBox { Dock = DockStyle.Fill };
        readonly TextBox python = new TextBox { Dock = DockStyle.Fill };
        readonly Label hint = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(380, 0) };
        readonly CheckBox backup = new CheckBox { Text = "Uygulamadan önce kayıtlı DWG'nin yedeğini al", AutoSize = true };
        readonly Settings settings;
        bool loading;

        public SettingsForm(Settings s)
        {
            settings = s;
            Text = "AutoCAD AI Designer — Ayarlar";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(540, 340);

            preset.Items.AddRange(ProviderPreset.All);
            python.Text = s.PythonPath;
            backup.Checked = s.BackupBeforeApply;

            TableLayoutPanel t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AddRow(t, "Yapay zekâ sağlayıcı", preset);
            AddRow(t, "API anahtarı", key);
            t.Controls.Add(new Label()); t.Controls.Add(hint);
            AddRow(t, "Base URL", baseUrl);
            AddRow(t, "Model", model);
            AddRow(t, "Python yolu (ops.)", python);
            t.Controls.Add(new Label()); t.Controls.Add(backup);

            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(8) };
            Button ok = new Button { Text = "Kaydet", DialogResult = DialogResult.OK };
            Button cancel = new Button { Text = "Vazgeç", DialogResult = DialogResult.Cancel };
            Button fit = new Button { Text = "Ek parça eşlemesi...", AutoSize = true };
            fit.Click += (o, e) => { try { FitConfig.OpenInEditor(); } catch (System.Exception ex) { MessageBox.Show(ex.Message); } };
            buttons.Controls.Add(cancel); buttons.Controls.Add(ok); buttons.Controls.Add(fit);
            Controls.Add(t); Controls.Add(buttons);
            AcceptButton = ok; CancelButton = cancel;
            ok.Click += (o, e) => Commit();
            preset.SelectedIndexChanged += (o, e) => OnPresetChanged();

            ProviderPreset cur = ProviderPreset.Find(s.Preset) ?? ProviderPreset.Find("OpenAI");
            loading = true;
            preset.SelectedItem = cur;
            loading = false;
            baseUrl.Text = string.IsNullOrEmpty(s.BaseUrl) ? cur.BaseUrl : s.BaseUrl;
            model.Text = s.Model ?? "";
            UpdateHint(cur);
        }

        ProviderPreset Current { get { return preset.SelectedItem as ProviderPreset; } }

        void OnPresetChanged()
        {
            ProviderPreset p = Current;
            if (p == null || loading) return;
            baseUrl.Text = p.BaseUrl;
            model.Text = p.Model;
            key.Text = "";
            UpdateHint(p);
        }

        void UpdateHint(ProviderPreset p)
        {
            bool cli = p.Provider == "codex-cli" || p.Provider == "cursor-cli";
            key.Enabled = p.NeedsKey || p.Provider == "openai-compatible" && !cli;
            baseUrl.Enabled = !cli;
            string h = p.KeyHint;
            if (p.NeedsKey) h += settings.HasKeyFor(p.Name) ? "\nKayıtlı anahtar var; boş bırakırsanız korunur." : "\nAnahtar bu Windows kullanıcısı için şifreli saklanır (DPAPI).";
            if (p.Provider == "codex-cli")
            {
                string path = CliDetector.Find("codex");
                h += path == null ? "\n⚠ codex bulunamadı (npm install -g @openai/codex)." : "\n✓ Bulundu: " + path;
            }
            else if (p.Provider == "cursor-cli")
            {
                string path = CliDetector.Find("cursor-agent", "agent");
                h += path == null ? "\n⚠ cursor-agent bulunamadı." : "\n✓ Bulundu: " + path;
            }
            if (cli) h += "\nModel boş = CLI varsayılanı.";
            hint.Text = h;
        }

        static void AddRow(TableLayoutPanel t, string label, Control c)
        {
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0) });
            t.Controls.Add(c);
        }

        void Commit()
        {
            ProviderPreset p = Current ?? ProviderPreset.Find("OpenAI");
            settings.Preset = p.Name;
            settings.Provider = p.Provider;
            settings.BaseUrl = baseUrl.Text.Trim().TrimEnd('/');
            if (key.Text.Trim().Length > 0) settings.SetApiKey(p.Name, key.Text);
            settings.Model = model.Text.Trim();
            settings.PythonPath = python.Text.Trim();
            settings.BackupBeforeApply = backup.Checked;
            settings.Save();
        }
    }
}