using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;

namespace Cruce
{
    /// <summary>
    /// Checks GitHub releases for a newer Cruce.exe, downloads it and swaps it in place
    /// (a tiny helper script waits for this process to exit, replaces the file and restarts).
    /// </summary>
    public static class Updater
    {
        const string Api = "https://api.github.com/repos/ivorojas/Cruce/releases/latest";
        public static volatile string Status = "";
        static int busy;

        static Version Parse(string tag)
        {
            var m = Regex.Match(tag ?? "", @"(\d+)\.(\d+)(?:\.(\d+))?");
            if (!m.Success) return new Version(0, 0);
            return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
        }

        /// <summary>Checks and, if <paramref name="install"/>, installs. canInstall is asked right before swapping.</summary>
        public static void CheckAsync(bool install, Func<bool> canInstall, Action restart)
        {
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Status = "Buscando actualizaciones…";
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    string json;
                    using (var wc = new WebClient())
                    {
                        wc.Headers[HttpRequestHeader.UserAgent] = "Cruce-updater";
                        json = wc.DownloadString(Api);
                    }
                    var tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
                    var url = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+/Cruce\\.exe)\"").Groups[1].Value;
                    var latest = Parse(tag);
                    var current = Parse(AppController.Version);
                    if (latest <= current || url.Length == 0) { Status = "Estás en la última versión (" + AppController.Version + ")"; return; }
                    Log.Info("actualización disponible: {0} (tengo {1})", tag, AppController.Version);
                    if (!install) { Status = "Hay una versión nueva: " + tag; return; }

                    Status = "Descargando " + tag + "…";
                    string exe = Process.GetCurrentProcess().MainModule.FileName;
                    string tmp = Path.Combine(Path.GetTempPath(), "Cruce-" + tag + ".exe");
                    using (var wc = new WebClient())
                    {
                        wc.Headers[HttpRequestHeader.UserAgent] = "Cruce-updater";
                        wc.DownloadFile(url, tmp);
                    }
                    var head = new byte[2];
                    using (var f = File.OpenRead(tmp)) f.Read(head, 0, 2);
                    if (new FileInfo(tmp).Length < 50000 || head[0] != 'M' || head[1] != 'Z') throw new InvalidDataException("descarga inválida");

                    while (!canInstall()) { Status = "Actualización lista: se instala cuando vuelvas a esta PC"; Thread.Sleep(2000); }

                    Status = "Instalando " + tag + "…";
                    Log.Info("instalando {0} sobre {1}", tag, exe);
                    string cmd = Path.Combine(Path.GetTempPath(), "cruce-update.cmd");
                    File.WriteAllText(cmd,
                        "@echo off\r\n" +
                        ":wait\r\n" +
                        "tasklist /FI \"PID eq " + Process.GetCurrentProcess().Id + "\" | find \"" + Process.GetCurrentProcess().Id + "\" >nul && (timeout /t 1 /nobreak >nul & goto wait)\r\n" +
                        "copy /Y \"" + tmp + "\" \"" + exe + "\" >nul\r\n" +
                        "start \"\" \"" + exe + "\" --tray\r\n" +
                        "del \"" + tmp + "\"\r\n" +
                        "del \"%~f0\"\r\n");
                    Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + cmd + "\"") { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
                    restart();
                }
                catch (WebException ex)
                {
                    var r = ex.Response as HttpWebResponse;
                    Status = r != null && r.StatusCode == HttpStatusCode.NotFound ? "No se encontró el repositorio (¿es privado?)" : "Sin conexión a GitHub";
                    Log.Info("update check failed: {0}", ex.Message);
                }
                catch (Exception ex) { Status = "Error al actualizar: " + ex.Message; Log.Error(ex, "update"); }
                finally { Interlocked.Exchange(ref busy, 0); }
            });
        }
    }
}
