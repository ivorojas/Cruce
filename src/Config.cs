using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Cruce
{
    public sealed class Config
    {
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cruce");
        static readonly string FilePath = Path.Combine(Dir, "config.ini");

        public string Name = Environment.MachineName;
        public string Secret = "";
        public Side Side = Side.Right;
        public long SideStamp;
        public string PeerIp = "";
        public int Port = 47810;
        public double Speed = 1.0;
        public bool Clipboard = true;
        public bool Files = true;
        public int MoveIntervalUs = 1000;
        public string LocalKeys = "F9";    // keys that never cross: they stay on this PC (e.g. Dictalo's F9)...
        public string LocalKeysApp = "DictadoApp"; // ...but only while this program runs here ("" = always)
        public bool AudioSend;             // this PC's sound plays on the other PC (and is muted here)
        public string Language = "en";     // UI language: "en" (default) or "es"
        public int PeerPort;               // 0 = same as Port (only differs in local testing)
        public bool TestAcceptInjected;    // testing only: treat synthetic input as real

        public static Config Load()
        {
            var c = new Config();
            try
            {
                if (!File.Exists(FilePath)) return c;
                var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) kv[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
                string v;
                if (kv.TryGetValue("Name", out v) && v.Length > 0) c.Name = v;
                if (kv.TryGetValue("Secret", out v)) c.Secret = Unprotect(v);
                if (kv.TryGetValue("Side", out v)) { Side s; if (Enum.TryParse(v, out s)) c.Side = s; }
                if (kv.TryGetValue("SideStamp", out v)) long.TryParse(v, out c.SideStamp);
                if (kv.TryGetValue("PeerIp", out v)) c.PeerIp = v;
                if (kv.TryGetValue("Port", out v)) int.TryParse(v, out c.Port);
                if (kv.TryGetValue("Speed", out v)) double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out c.Speed);
                if (kv.TryGetValue("Clipboard", out v)) c.Clipboard = v == "1";
                if (kv.TryGetValue("Files", out v)) c.Files = v == "1";
                if (kv.TryGetValue("MoveIntervalUs", out v)) int.TryParse(v, out c.MoveIntervalUs);
                if (kv.TryGetValue("PeerPort", out v)) int.TryParse(v, out c.PeerPort);
                if (kv.TryGetValue("LocalKeys", out v)) c.LocalKeys = v;
                if (kv.TryGetValue("LocalKeysApp", out v)) c.LocalKeysApp = v;
            if (kv.TryGetValue("AudioSend", out v)) c.AudioSend = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
            if (kv.TryGetValue("Language", out v)) c.Language = v.Trim().ToLowerInvariant() == "es" ? "es" : "en";
                if (kv.TryGetValue("TestAcceptInjected", out v)) c.TestAcceptInjected = v == "1";
                c.Speed = Math.Max(0.25, Math.Min(4, c.Speed));
                if (c.Port <= 0 || c.Port > 65535) c.Port = 47810;
                c.MoveIntervalUs = Math.Max(0, Math.Min(20000, c.MoveIntervalUs));
            }
            catch (Exception ex) { Log.Error(ex, "config load"); }
            return c;
        }

        /// <summary>LocalKeys as virtual-key codes. Accepts F1..F24 or hex codes like 0x78, separated by commas.</summary>
        public HashSet<int> LocalKeyCodes()
        {
            var set = new HashSet<int>();
            foreach (var raw in (LocalKeys ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var k = raw.Trim().ToUpperInvariant();
                int n;
                if (k.Length >= 2 && k[0] == 'F' && int.TryParse(k.Substring(1), out n) && n >= 1 && n <= 24) set.Add(0x70 + n - 1);
                else if (k.StartsWith("0X") && int.TryParse(k.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n)) set.Add(n & 0xFF);
            }
            return set;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var sb = new StringBuilder();
                sb.AppendLine("# Cruce - configuración (la clave está cifrada para tu usuario de Windows)");
                sb.AppendLine("Name=" + Name);
                sb.AppendLine("Secret=" + Protect(Secret));
                sb.AppendLine("Side=" + Side);
                sb.AppendLine("SideStamp=" + SideStamp);
                sb.AppendLine("PeerIp=" + PeerIp);
                sb.AppendLine("Port=" + Port);
                sb.AppendLine("Speed=" + Speed.ToString("0.00", CultureInfo.InvariantCulture));
                sb.AppendLine("Clipboard=" + (Clipboard ? "1" : "0"));
                sb.AppendLine("Files=" + (Files ? "1" : "0"));
                sb.AppendLine("MoveIntervalUs=" + MoveIntervalUs);
                sb.AppendLine("LocalKeys=" + LocalKeys);
                sb.AppendLine("LocalKeysApp=" + LocalKeysApp);
            sb.AppendLine("Language=" + Language);
            sb.AppendLine("AudioSend=" + (AudioSend ? "1" : "0"));
                if (PeerPort != 0) sb.AppendLine("PeerPort=" + PeerPort);
                if (TestAcceptInjected) sb.AppendLine("TestAcceptInjected=1");
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null); else File.Move(tmp, FilePath);
            }
            catch (Exception ex) { Log.Error(ex, "config save"); }
        }

        static string Protect(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var b = ProtectedData.Protect(Encoding.UTF8.GetBytes(s), null, DataProtectionScope.CurrentUser);
            return "dpapi:" + Convert.ToBase64String(b);
        }

        static string Unprotect(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (!s.StartsWith("dpapi:")) return s;
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s.Substring(6)), null, DataProtectionScope.CurrentUser)); }
            catch { return ""; }
        }
    }

    public static class Log
    {
        static readonly BlockingCollection<string> q = new BlockingCollection<string>(2000);
        static string path;

        public static string PathName { get { return path; } }

        public static void Init() { Init("cruce.log"); }

        public static void Init(string fileName)
        {
            try
            {
                Directory.CreateDirectory(Config.Dir);
                path = Path.Combine(Config.Dir, fileName);
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > MaxSize) Rotate();
            }
            catch { }
            var t = new Thread(Pump) { IsBackground = true, Name = "cruce-log" };
            t.Start();
        }

        public static void Info(string fmt, params object[] args)
        {
            string s;
            try { s = args.Length == 0 ? fmt : string.Format(fmt, args); } catch { s = fmt; }
            q.TryAdd(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + s);
        }

        public static void Error(Exception ex, string ctx) { Info("ERROR [{0}] {1}", ctx, ex); }

        static StreamWriter Open(string p)
        {
            var fs = new FileStream(p, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            return new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
        }

        // History: it used to rotate only at startup, past 1 MB, keeping one old file, so a PC that ran for days kept
        // ~3 days at most (and a long session grew without limit). Now 4 MB per file, checked while running, and the
        // last 4 files are kept (cruce.log + .1 .. .3, roughly two weeks).
        const long MaxSize = 4L << 20;
        const int Keep = 3;

        static void Rotate()
        {
            try { File.Delete(path + "." + Keep); } catch { }
            for (int i = Keep - 1; i >= 1; i--)
                try { if (File.Exists(path + "." + i)) File.Move(path + "." + i, path + "." + (i + 1)); } catch { }
            try { File.Move(path, path + ".1"); } catch { }
        }

        static void Pump()
        {
            StreamWriter w = null;
            int sinceCheck = 0;
            foreach (var s in q.GetConsumingEnumerable())
            {
                if (w != null && ++sinceCheck >= 500)
                {
                    sinceCheck = 0;
                    try { if (w.BaseStream.Length > MaxSize) { w.Dispose(); w = null; Rotate(); } } catch { }
                }
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        if (w == null)
                        {
                            try { w = Open(path); }
                            catch { path = Path.Combine(Path.GetTempPath(), "cruce.log"); w = Open(path); } // fallback location
                        }
                        w.WriteLine(s);
                        break;
                    }
                    catch { try { if (w != null) w.Dispose(); } catch { } w = null; }
                }
            }
        }
    }
}
