using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace CadAiDesigner
{
    /// <summary>
    /// Starts/stops the local Python planner as a hidden child process. A random token and a free port are generated per
    /// AutoCAD session; the API key is handed to the child through its environment only. When AutoCAD exits, the stdin pipe
    /// closes and the service terminates itself.
    /// If the CAD_AI_TOKEN environment variable is set, an externally started service (scripts\dev-service.ps1) is used.
    /// </summary>
    internal static class ServiceHost
    {
        static readonly object Gate = new object();
        static readonly StringBuilder Output = new StringBuilder();
        static Process proc;
        static int port;
        static string token;
        static string startedKey, startedSig;

        static bool External { get { return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CAD_AI_TOKEN")); } }

        public static bool IsRunning
        {
            get { lock (Gate) { return External ? port > 0 : proc != null && !proc.HasExited; } }
        }

        public static void EnsureStarted(Settings s)
        {
            lock (Gate)
            {
                if (External)
                {
                    token = Environment.GetEnvironmentVariable("CAD_AI_TOKEN");
                    int p;
                    port = int.TryParse(Environment.GetEnvironmentVariable("CAD_AI_PORT"), out p) ? p : 8765;
                    if (!Health(port)) throw new InvalidOperationException("Harici servis (127.0.0.1:" + port + ") yanıt vermiyor.");
                    return;
                }
                string key = s.GetApiKey();
                bool alive = proc != null && !proc.HasExited && Health(port);
                if (alive && key == startedKey && s.Signature == startedSig) return;
                StopLocked();

                string script = FindScript();
                if (script == null) throw new InvalidOperationException("service\\server.py bulunamadı; kurulumu yeniden yapın.");
                string file, prefix;
                if (!PythonLocator.Find(s.PythonPath, out file, out prefix))
                    throw new InvalidOperationException("Python 3.9+ bulunamadı. python.org'dan kurun (\"Add to PATH\" işaretli) veya Ayarlar'da Python yolunu girin.");

                port = FreePort();
                token = NewToken();
                Output.Length = 0;
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = file,
                    Arguments = (prefix.Length > 0 ? prefix + " " : "") + "\"" + script + "\"",
                    WorkingDirectory = Path.GetDirectoryName(script),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.EnvironmentVariables["CAD_AI_TOKEN"] = token;
                psi.EnvironmentVariables["CAD_AI_PORT"] = port.ToString();
                psi.EnvironmentVariables["CAD_AI_WATCH_STDIN"] = "1";
                psi.EnvironmentVariables["CAD_AI_LOG_DIR"] = AppPaths.Logs;
                psi.EnvironmentVariables["CAD_AI_PROVIDER"] = s.Provider ?? "openai-compatible";
                psi.EnvironmentVariables["CAD_AI_BASE_URL"] = s.BaseUrl ?? "";
                psi.EnvironmentVariables["CAD_AI_MODEL"] = s.Model ?? "";
                psi.EnvironmentVariables.Remove("OPENAI_MODEL");
                psi.EnvironmentVariables.Remove("OPENAI_BASE_URL");
                psi.EnvironmentVariables["PYTHONUTF8"] = "1";
                psi.EnvironmentVariables.Remove("OPENAI_API_KEY");
                if (!string.IsNullOrEmpty(key)) psi.EnvironmentVariables["CAD_AI_API_KEY"] = key;
                else psi.EnvironmentVariables.Remove("CAD_AI_API_KEY");

                proc = Process.Start(psi);
                proc.OutputDataReceived += (o, e) => Append(e.Data);
                proc.ErrorDataReceived += (o, e) => Append(e.Data);
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                startedKey = key; startedSig = s.Signature;

                for (int i = 0; i < 60; i++)
                {
                    if (proc.HasExited)
                        throw new InvalidOperationException("Servis başlamadı: " + Tail());
                    if (Health(port)) return;
                    Thread.Sleep(200);
                }
                StopLocked();
                throw new InvalidOperationException("Servis zamanında başlamadı: " + Tail());
            }
        }

        public static void Stop() { lock (Gate) StopLocked(); }

        static void StopLocked()
        {
            Process p = proc;
            proc = null;
            if (p == null) return;
            try
            {
                if (!p.HasExited)
                {
                    try { p.StandardInput.Close(); } catch (System.Exception) { }
                    if (!p.WaitForExit(1500)) p.Kill();
                }
            }
            catch (System.Exception) { }
            try { p.Dispose(); } catch (System.Exception) { }
        }

        static void Append(string s)
        {
            if (s == null) return;
            lock (Output) { Output.AppendLine(s); if (Output.Length > 4000) Output.Remove(0, Output.Length - 4000); }
        }

        static string Tail()
        {
            lock (Output) { string t = Output.ToString().Trim(); return t.Length == 0 ? "(çıktı yok)" : (t.Length > 400 ? t.Substring(t.Length - 400) : t); }
        }

        public static bool Health(int p)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + p + "/health");
                req.Timeout = 1500; req.Proxy = null;
                using (HttpWebResponse r = (HttpWebResponse)req.GetResponse())
                using (StreamReader rd = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
                    return rd.ReadToEnd().Contains("\"ok\"");
            }
            catch (System.Exception) { return false; }
        }

        /// <summary>POST JSON to the service. Returns the response body for any HTTP status (the service always answers JSON).</summary>
        public static string Post(string path, string json)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + path);
            req.Method = "POST";
            req.ContentType = "application/json; charset=utf-8";
            req.Timeout = 200000; req.ReadWriteTimeout = 200000;
            req.Proxy = null; // never route loopback traffic through a corporate proxy
            req.Headers["X-CAD-AI-Token"] = token;
            byte[] data = Encoding.UTF8.GetBytes(json);
            req.ContentLength = data.Length;
            try
            {
                using (Stream st = req.GetRequestStream()) st.Write(data, 0, data.Length);
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return rd.ReadToEnd();
            }
            catch (WebException ex)
            {
                if (ex.Response != null)
                    using (StreamReader rd = new StreamReader(ex.Response.GetResponseStream(), Encoding.UTF8))
                        return rd.ReadToEnd();
                throw new InvalidOperationException("Servise bağlanılamadı: " + ex.Message);
            }
        }

        static string FindScript()
        {
            string baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string[] candidates =
            {
                Path.Combine(baseDir, "service", "server.py"),                              // installed bundle
                Path.Combine(baseDir, "..", "service", "server.py"),
                Path.Combine(baseDir, "..", "..", "..", "service", "server.py"),            // autocad\bin\Release in the source tree
            };
            foreach (string c in candidates) if (File.Exists(c)) return Path.GetFullPath(c);
            return null;
        }

        static int FreePort()
        {
            TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        static string NewToken()
        {
            byte[] b = new byte[24];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return BitConverter.ToString(b).Replace("-", "");
        }
    }

    internal static class PythonLocator
    {
        public static bool Find(string userPath, out string file, out string prefix)
        {
            List<string[]> cands = new List<string[]>();
            if (!string.IsNullOrWhiteSpace(userPath)) cands.Add(new[] { userPath.Trim(), "" });
            cands.Add(new[] { "python", "" });
            cands.Add(new[] { "py", "-3" });
            cands.Add(new[] { "python3", "" });
            foreach (string[] c in cands)
            {
                if (Probe(c[0], c[1])) { file = c[0]; prefix = c[1]; return true; }
            }
            file = null; prefix = null;
            return false;
        }

        // The Microsoft Store "python" stub exits non-zero / prints nothing, so a real run is the only reliable test.
        static bool Probe(string file, string prefix)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = file,
                    Arguments = (prefix.Length > 0 ? prefix + " " : "") + "-c \"import sys;print(sys.version_info[0]*100+sys.version_info[1])\"",
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                };
                using (Process p = Process.Start(psi))
                {
                    if (!p.WaitForExit(6000)) { try { p.Kill(); } catch (System.Exception) { } return false; }
                    int v;
                    return p.ExitCode == 0 && int.TryParse(p.StandardOutput.ReadToEnd().Trim(), out v) && v >= 309;
                }
            }
            catch (System.Exception) { return false; }
        }
    }
}
