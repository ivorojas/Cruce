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

        static string etag, lastJson, lastError;

        /// <summary>
        /// Checks GitHub and installs a newer release. <paramref name="manual"/> = the button (shows every step);
        /// automatic checks run every 2 minutes and stay silent unless there is something new. They send the last
        /// ETag, so "nothing changed" is a tiny 304 answer that GitHub doesn't count against its rate limit.
        /// canInstall is asked right before swapping; notify gets the "closing to update" balloon text.
        /// </summary>
        public static void CheckAsync(bool manual, Func<bool> canInstall, Action<string> notify, Action restart)
        {
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (manual) Status = L.T("Buscando actualizaciones…");
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    string json;
                    var req = (HttpWebRequest)WebRequest.Create(Api);
                    req.UserAgent = "Cruce-updater";
                    req.Timeout = 15000;
                    if (!manual && etag != null && lastJson != null) req.Headers[HttpRequestHeader.IfNoneMatch] = etag;
                    try
                    {
                        using (var resp = (HttpWebResponse)req.GetResponse())
                        using (var rd = new StreamReader(resp.GetResponseStream()))
                        {
                            json = rd.ReadToEnd();
                            etag = resp.Headers[HttpResponseHeader.ETag];
                            lastJson = json;
                        }
                    }
                    catch (WebException wex)
                    {
                        var r304 = wex.Response as HttpWebResponse;
                        if (r304 == null || r304.StatusCode != HttpStatusCode.NotModified) throw;
                        json = lastJson; // unchanged since the last check
                    }
                    if (lastError != null) { Log.Info("actualizaciones: GitHub responde de nuevo"); lastError = null; }
                    var tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
                    var url = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+/Cruce\\.exe)\"").Groups[1].Value;
                    var latest = Parse(tag);
                    var current = Parse(AppController.Version);
                    if (latest <= current || url.Length == 0) { if (manual) Status = L.F("Estás en la última versión ({0})", AppController.Version); return; }
                    Log.Info("actualización disponible: {0} (tengo {1})", tag, AppController.Version);

                    Status = L.F("Descargando {0}…", tag);
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

                    while (!canInstall()) { Status = L.T("Actualización lista: se instala cuando vuelvas a esta PC"); Thread.Sleep(2000); }

                    Status = L.F("Instalando {0}…", tag);
                    Log.Info("instalando {0} sobre {1}", tag, exe);
                    if (notify != null) notify(L.F("Cruce se actualiza a {0}: se cierra y se vuelve a abrir solo en unos segundos", tag));
                    Thread.Sleep(2500); // time to read the balloon
                    string cmd = Path.Combine(Path.GetTempPath(), "cruce-update.cmd");
                    File.WriteAllText(cmd,
                        "@echo off\r\n" +
                        ":wait\r\n" +
                        "tasklist /FI \"PID eq " + Process.GetCurrentProcess().Id + "\" | find \"" + Process.GetCurrentProcess().Id + "\" >nul && (timeout /t 1 /nobreak >nul & goto wait)\r\n" +
                        "copy /Y \"" + tmp + "\" \"" + exe + "\" >nul\r\n" +
                        "start \"\" \"" + exe + "\" --tray --updated " + tag + "\r\n" +
                        "del \"" + tmp + "\"\r\n" +
                        "del \"%~f0\"\r\n");
                    Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + cmd + "\"") { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
                    restart();
                }
                catch (WebException ex)
                {
                    var r = ex.Response as HttpWebResponse;
                    if (manual) Status = r != null && r.StatusCode == HttpStatusCode.NotFound ? L.T("No se encontró el repositorio (¿es privado?)") : L.T("Sin conexión a GitHub");
                    if (lastError != ex.Message) { Log.Info("actualizaciones: no pude consultar GitHub ({0})", ex.Message); lastError = ex.Message; } // one line per outage
                }
                catch (Exception ex) { Status = L.F("Error al actualizar: {0}", ex.Message); Log.Error(ex, "update"); }
                finally { Interlocked.Exchange(ref busy, 0); }
            });
        }
    }
}
