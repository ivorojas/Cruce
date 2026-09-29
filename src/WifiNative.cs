using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Cruce
{
    /// <summary>
    /// Talks to the Wi-Fi driver through the Windows Native Wifi API (wlanapi.dll):
    ///  - link state every couple of seconds (RSSI dBm, quality, rates, channel, retry/failure counters),
    ///  - every driver notification (scans, roaming, disconnects) as it happens,
    ///  - neighbour access points (channel + RSSI) for congestion analysis,
    ///  - "low latency mode": media-streaming mode on and background scans off while Cruce runs
    ///    (the same trick game-streaming apps use; Windows restores both when the handle closes).
    /// Every call is defensive: any failure just means "no data".
    /// </summary>
    public static class WifiNative
    {
        [DllImport("wlanapi.dll")] static extern int WlanOpenHandle(uint ver, IntPtr res, out uint negotiated, out IntPtr h);
        [DllImport("wlanapi.dll")] static extern int WlanCloseHandle(IntPtr h, IntPtr res);
        [DllImport("wlanapi.dll")] static extern int WlanEnumInterfaces(IntPtr h, IntPtr res, out IntPtr list);
        [DllImport("wlanapi.dll")] static extern int WlanQueryInterface(IntPtr h, ref Guid g, int op, IntPtr res, out int size, out IntPtr data, IntPtr type);
        [DllImport("wlanapi.dll")] static extern int WlanSetInterface(IntPtr h, ref Guid g, int op, int size, IntPtr data, IntPtr res);
        [DllImport("wlanapi.dll")] static extern int WlanGetNetworkBssList(IntPtr h, ref Guid g, IntPtr ssid, int bssType, bool secure, IntPtr res, out IntPtr list);
        [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr p);
        [DllImport("wlanapi.dll")] static extern int WlanScan(IntPtr h, ref Guid g, IntPtr ssid, IntPtr ie, IntPtr res);
        [DllImport("wlanapi.dll")] static extern int WlanConnect(IntPtr h, ref Guid g, IntPtr parameters, IntPtr res);
        delegate void NotifyCb(IntPtr data, IntPtr ctx);
        [DllImport("wlanapi.dll")] static extern int WlanRegisterNotification(IntPtr h, uint src, bool ignoreDup, NotifyCb cb, IntPtr ctx, IntPtr res, out uint prev);

        const int OP_BG_SCAN = 2, OP_STREAMING = 3, OP_CONNECTION = 7, OP_CHANNEL = 8, OP_STATS = 0x10000101, OP_RSSI = 0x10000102;

        static IntPtr handle;
        static Guid iface;
        static bool ok;
        static NotifyCb cbKeep;
        static readonly object gate = new object();

        // live state
        public static volatile bool Connected;
        public static int Quality = -1, Rssi = 0, Channel = -1, RxKbps = -1, TxKbps = -1;
        public static string Bssid = "";
        public static bool LowLatency;
        // per-minute counters
        static long txFrames, retries, failures, lastTx, lastRetry, lastFail;
        public static int Scans, Roams, Disconnects, Events;
        static readonly List<int> rssiSamples = new List<int>();

        public static bool Available { get { return ok; } }

        public static void Start()
        {
            try
            {
                uint neg;
                if (WlanOpenHandle(2, IntPtr.Zero, out neg, out handle) != 0) { Log.Info("wifi nativo: no disponible (servicio WLAN apagado o sin placa)"); return; }
                IntPtr list;
                if (WlanEnumInterfaces(handle, IntPtr.Zero, out list) != 0) return;
                int n = Marshal.ReadInt32(list, 0);
                if (n == 0) { WlanFreeMemory(list); Log.Info("wifi nativo: esta PC no tiene placa WiFi"); return; }
                var gb = new byte[16];
                Marshal.Copy(list + 8, gb, 0, 16);
                iface = new Guid(gb);
                string desc = Marshal.PtrToStringUni(list + 8 + 16);
                WlanFreeMemory(list);
                ok = true;
                Log.Info("wifi nativo: placa '{0}'", desc);

                cbKeep = OnNotify;
                uint prev;
                int r = WlanRegisterNotification(handle, 0xFFFF, true, cbKeep, IntPtr.Zero, IntPtr.Zero, out prev);
                if (r != 0) Log.Info("wifi nativo: sin notificaciones (err {0})", r);

                SetLowLatency(true);
                new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "cruce-wlan" }.Start();
            }
            catch (Exception ex) { ok = false; Log.Info("wifi nativo: error {0}", ex.Message); }
        }

        static bool SetBool(int op, bool value)
        {
            IntPtr p = Marshal.AllocHGlobal(4);
            try { Marshal.WriteInt32(p, value ? 1 : 0); return WlanSetInterface(handle, ref iface, op, 4, p, IntPtr.Zero) == 0; }
            finally { Marshal.FreeHGlobal(p); }
        }

        public static void SetLowLatency(bool on)
        {
            if (!ok) return;
            bool a = SetBool(OP_STREAMING, on);
            bool b = SetBool(OP_BG_SCAN, !on);
            LowLatency = on && (a || b);
            Log.Info("wifi modo baja latencia {0}: streaming={1} escaneo_en_segundo_plano_desactivado={2}", on ? "pedido" : "quitado", a ? "ok" : "rechazado", b ? "ok" : "rechazado");
        }

        static bool QueryInt(int op, out int v)
        {
            v = 0;
            int size; IntPtr data;
            if (WlanQueryInterface(handle, ref iface, op, IntPtr.Zero, out size, out data, IntPtr.Zero) != 0) return false;
            try { if (size < 4) return false; v = Marshal.ReadInt32(data); return true; }
            finally { WlanFreeMemory(data); }
        }

        static void Loop()
        {
            int errLogged = 0;
            while (true)
            {
                try { Poll(); }
                catch (Exception ex) { if (errLogged++ < 3) Log.Info("wifi nativo poll: {0}", ex.Message); }
                Thread.Sleep(2000);
            }
        }

        static void Poll()
        {
            int size; IntPtr data;
            if (WlanQueryInterface(handle, ref iface, OP_CONNECTION, IntPtr.Zero, out size, out data, IntPtr.Zero) == 0)
            {
                try
                {
                    int state = Marshal.ReadInt32(data, 0);
                    Connected = state == 1; // wlan_interface_state_connected
                    if (size >= 588)
                    {
                        var mac = new byte[6];
                        Marshal.Copy(data + 560, mac, 0, 6);
                        Bssid = BitConverter.ToString(mac).Replace('-', ':').ToLowerInvariant();
                        Quality = Marshal.ReadInt32(data, 576);
                        RxKbps = Marshal.ReadInt32(data, 580);
                        TxKbps = Marshal.ReadInt32(data, 584);
                    }
                }
                finally { WlanFreeMemory(data); }
            }
            else Connected = false;
            int v;
            if (QueryInt(OP_CHANNEL, out v)) Channel = v;
            CheckBand();
            if (QueryInt(OP_RSSI, out v)) { Rssi = v; lock (gate) rssiSamples.Add(v); Flight.Add(Flight.K_WIFI, v, Quality); }

            // Driver frame counters (not every driver supports them)
            if (WlanQueryInterface(handle, ref iface, OP_STATS, IntPtr.Zero, out size, out data, IntPtr.Zero) == 0)
            {
                try
                {
                    int phys = Marshal.ReadInt32(data, 216);
                    if (phys > 0 && size >= 224 + 32)
                    {
                        long tx = Marshal.ReadInt64(data, 224), fail = Marshal.ReadInt64(data, 224 + 16), retry = Marshal.ReadInt64(data, 224 + 24);
                        lock (gate)
                        {
                            if (lastTx > 0 && tx >= lastTx) { txFrames += tx - lastTx; retries += Math.Max(0, retry - lastRetry); failures += Math.Max(0, fail - lastFail); }
                            lastTx = tx; lastRetry = retry; lastFail = fail;
                        }
                    }
                }
                finally { WlanFreeMemory(data); }
            }
        }

        // ------------------------------------------------------------ keep the laptop on 5 GHz
        //
        // With one network name for both bands, the router picks the band at every (re)connection and
        // sometimes drops the laptop on congested 2.4 GHz. The band can't be pinned in this laptop's
        // driver, so Cruce does it: if it's on 2.4 GHz and the same network is visible on 5 GHz with a
        // usable signal, it reconnects to that 5 GHz access point (1-2 s blip). Only while not crossing,
        // at most 3 tries per hour, 10 min apart; every step is logged.

        public static Func<Mode> CurrentMode;
        public static bool Prefer5GHz = true;
        public static event Action<string> Notify;
        static DateTime on24Since = DateTime.MinValue, lastAttempt = DateTime.MinValue, hourStart = DateTime.MinValue;
        static int attemptsThisHour;

        static void CheckBand()
        {
            if (!Prefer5GHz || !Connected || Channel <= 0) { on24Since = DateTime.MinValue; return; }
            if (Channel > 14) { on24Since = DateTime.MinValue; return; }
            var now = DateTime.Now;
            if (on24Since == DateTime.MinValue) { on24Since = now; Log.Info("wifi: la notebook está en 2,4 GHz (canal {0}); la paso a 5 GHz", Channel); }
            if (CurrentMode != null && CurrentMode() != Mode.Local) return;          // never mid-crossing
            if ((now - lastAttempt).TotalMinutes < 10) return;
            if ((now - hourStart).TotalMinutes >= 60) { hourStart = now; attemptsThisHour = 0; }
            if (attemptsThisHour >= 3) return;
            attemptsThisHour++;
            lastAttempt = now;
            BandStatus = "Buscando la red de 5 GHz…";
            try { MoveTo5GHz(); } catch (Exception ex) { Log.Info("wifi: no pude pasar a 5 GHz ({0})", ex.Message); }
            if (BandStatus == "Buscando la red de 5 GHz…") BandStatus = "";
            bandStatusAt = DateTime.Now;
        }

        /// <summary>Short user-facing state of the 5 GHz keeper (shown in the window); clears itself after a while.</summary>
        public static volatile string BandStatus = "";
        static DateTime bandStatusAt;
        public static string CurrentBandStatus()
        {
            if (BandStatus != "" && BandStatus != "Buscando la red de 5 GHz…" && (DateTime.Now - bandStatusAt).TotalSeconds > 15) BandStatus = "";
            return L.T(BandStatus);
        }

        static void MoveTo5GHz()
        {
            // Current profile name and SSID (WLAN_CONNECTION_ATTRIBUTES)
            int size; IntPtr data;
            if (WlanQueryInterface(handle, ref iface, OP_CONNECTION, IntPtr.Zero, out size, out data, IntPtr.Zero) != 0) return;
            string profile; byte[] ssid;
            try
            {
                profile = Marshal.PtrToStringUni(data + 8);
                int len = Math.Min(32, Marshal.ReadInt32(data, 520));
                ssid = new byte[len];
                Marshal.Copy(data + 524, ssid, 0, len);
            }
            finally { WlanFreeMemory(data); }
            if (string.IsNullOrEmpty(profile) || ssid.Length == 0) return;

            // Fresh scan (background scanning is off in low-latency mode), then look for our SSID on 5 GHz.
            WlanScan(handle, ref iface, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            System.Threading.Thread.Sleep(4000);
            IntPtr list;
            if (WlanGetNetworkBssList(handle, ref iface, IntPtr.Zero, 3, false, IntPtr.Zero, out list) != 0) return;
            byte[] best = null; int bestRssi = -999, bestCh = 0, seen24 = 0;
            try
            {
                int n = Marshal.ReadInt32(list, 4);
                for (int i = 0; i < n; i++)
                {
                    IntPtr e = list + 8 + i * 360;
                    int l = Marshal.ReadInt32(e, 0);
                    if (l != ssid.Length) continue;
                    var s = new byte[l];
                    Marshal.Copy(e + 4, s, 0, l);
                    if (!s.SequenceEqual(ssid)) continue;
                    int rssi = Marshal.ReadInt32(e, 56), mhz = Marshal.ReadInt32(e, 92) / 1000;
                    if (mhz < 5000) { seen24++; continue; }
                    if (rssi > bestRssi) { bestRssi = rssi; bestCh = FreqToChannel(mhz); best = new byte[6]; Marshal.Copy(e + 40, best, 0, 6); }
                }
            }
            finally { WlanFreeMemory(list); }
            string net = System.Text.Encoding.UTF8.GetString(ssid);
            if (best == null) { Log.Info("wifi: no veo la red '{0}' en 5 GHz desde acá (solo {1} antena/s de 2,4 GHz); me quedo en 2,4", net, seen24); BandStatus = "No encuentro la red de 5 GHz: sigo en 2,4"; return; }
            if (bestRssi < -78) { Log.Info("wifi: la red '{0}' en 5 GHz llega muy débil ({1} dBm); me quedo en 2,4", net, bestRssi); BandStatus = "La red de 5 GHz llega muy débil: sigo en 2,4"; return; }
            BandStatus = "Pasando el WiFi a 5 GHz…";

            // WlanConnect to the same profile, restricted to that 5 GHz access point.
            int ps = IntPtr.Size;
            IntPtr prof = Marshal.StringToHGlobalUni(profile), bssids = Marshal.AllocHGlobal(20), prm = Marshal.AllocHGlobal(4 * ps + 8);
            try
            {
                for (int i = 0; i < 20; i++) Marshal.WriteByte(bssids, i, 0);
                Marshal.WriteByte(bssids, 0, 0x80);        // NDIS_OBJECT_TYPE_DEFAULT
                Marshal.WriteByte(bssids, 1, 1);           // DOT11_BSSID_LIST_REVISION_1
                Marshal.WriteInt16(bssids, 2, 20);         // size
                Marshal.WriteInt32(bssids, 4, 1);          // uNumOfEntries
                Marshal.WriteInt32(bssids, 8, 1);          // uTotalNumOfEntries
                Marshal.Copy(best, 0, bssids + 12, 6);
                for (int i = 0; i < 4 * ps + 8; i++) Marshal.WriteByte(prm, i, 0);
                Marshal.WriteInt32(prm, 0, 0);             // wlan_connection_mode_profile
                Marshal.WriteIntPtr(prm, ps, prof);
                Marshal.WriteIntPtr(prm, 2 * ps, IntPtr.Zero);
                Marshal.WriteIntPtr(prm, 3 * ps, bssids);
                Marshal.WriteInt32(prm, 4 * ps, 1);        // dot11_BSS_type_infrastructure
                Marshal.WriteInt32(prm, 4 * ps + 4, 0);
                string mac = BitConverter.ToString(best).Replace('-', ':').ToLowerInvariant();
                Log.Info("wifi: paso la notebook a 5 GHz: red '{0}', antena {1}, canal {2}, {3} dBm", net, mac, bestCh, bestRssi);
                int r = WlanConnect(handle, ref iface, prm, IntPtr.Zero);
                if (r != 0) { Log.Info("wifi: Windows rechazó el cambio a 5 GHz (error {0})", r); BandStatus = L.F("Windows no dejó pasar a 5 GHz (error {0})", r); return; }
            }
            finally { Marshal.FreeHGlobal(prof); Marshal.FreeHGlobal(bssids); Marshal.FreeHGlobal(prm); }

            System.Threading.Thread.Sleep(8000);
            int ch;
            if (QueryInt(OP_CHANNEL, out ch)) Channel = ch;
            if (Channel > 14)
            {
                Log.Info("wifi: listo, la notebook quedó en 5 GHz (canal {0})", Channel);
                BandStatus = "WiFi en 5 GHz ✓";
                on24Since = DateTime.MinValue;
                var n2 = Notify; if (n2 != null) n2(L.T("Pasé la notebook al WiFi de 5 GHz (va mucho mejor que 2,4)"));
            }
            else { Log.Info("wifi: pedí 5 GHz pero sigue en canal {0}; reintento más tarde", Channel); BandStatus = "Sigue en 2,4 GHz: reintento en 10 min"; }
        }

        /// <summary>Per-minute Wi-Fi driver stats; resets counters.</summary>
        public static void TakeMinute(MinuteRow r)
        {
            if (!ok) return;
            lock (gate)
            {
                if (rssiSamples.Count > 0) { r.RssiAvg = rssiSamples.Average(); r.RssiMin = rssiSamples.Min(); }
                r.WifiRetryPct = txFrames > 0 ? 100.0 * retries / (txFrames + retries) : -1;
                r.WifiFailures = txFrames > 0 ? (int)failures : -1;
                txFrames = retries = failures = 0;
                rssiSamples.Clear();
            }
            r.WifiScans = Interlocked.Exchange(ref Scans, 0);
            r.WifiRoams = Interlocked.Exchange(ref Roams, 0);
            r.WifiDisconnects = Interlocked.Exchange(ref Disconnects, 0);
            r.WifiEvents = Interlocked.Exchange(ref Events, 0);
        }

        // ------------------------------------------------------------ notifications

        static readonly Dictionary<long, string> Names = new Dictionary<long, string>
        {
            { 0x8L << 32 | 7, "escaneo terminado" }, { 0x8L << 32 | 8, "escaneo falló" },
            { 0x8L << 32 | 9, "conexión iniciando" }, { 0x8L << 32 | 10, "conexión completa" }, { 0x8L << 32 | 11, "intento de conexión falló" },
            { 0x8L << 32 | 20, "desconectando" }, { 0x8L << 32 | 21, "DESCONECTADO" },
            { 0x8L << 32 | 3, "escaneo en segundo plano ACTIVADO" }, { 0x8L << 32 | 4, "escaneo en segundo plano desactivado" },
            { 0x8L << 32 | 6, "cambio de ahorro de energía del WiFi" }, { 0x8L << 32 | 24, "pantalla encendida/apagada (afecta al WiFi)" },
            { 0x8L << 32 | 18, "red no disponible" }, { 0x8L << 32 | 19, "red disponible" },
            { 0x10L << 32 | 1, "asociando" }, { 0x10L << 32 | 2, "asociado" }, { 0x10L << 32 | 4, "conectado (radio)" },
            { 0x10L << 32 | 5, "ROAMING empieza (cambio de punto de acceso)" }, { 0x10L << 32 | 6, "roaming termina" },
            { 0x10L << 32 | 7, "cambio de estado de la radio" }, { 0x10L << 32 | 9, "desasociando" }, { 0x10L << 32 | 10, "DESCONECTADO (radio)" },
            { 0x10L << 32 | 15, "ENLACE DEGRADADO" }, { 0x10L << 32 | 16, "enlace mejoró" },
        };
        static long lastSignalLog;

        static void OnNotify(IntPtr p, IntPtr ctx)
        {
            try
            {
                uint src = (uint)Marshal.ReadInt32(p, 0);
                uint code = (uint)Marshal.ReadInt32(p, 4);
                Interlocked.Increment(ref Events);
                long key = (long)src << 32 | code;
                if (src == 0x8 && (code == 7 || code == 8 || code == 26)) Interlocked.Increment(ref Scans);
                if (src == 0x10 && code == 5) Interlocked.Increment(ref Roams);
                if ((src == 0x8 && code == 21) || (src == 0x10 && code == 10)) Interlocked.Increment(ref Disconnects);
                Flight.Add(Flight.K_WIFIEV, (int)src, (int)code);
                if ((src == 0x10 && code == 8) || (src == 0x8 && code == 26)) // signal change / scan list refresh: frequent, log at most every 30 s
                {
                    long now = Link.NowUs();
                    if (now - lastSignalLog < 30000000) return;
                    lastSignalLog = now;
                    Log.Info("wifi: cambio de calidad de señal (calidad {0}%, rssi {1} dBm)", Quality, Rssi);
                    return;
                }
                string name;
                if (Names.TryGetValue(key, out name)) Log.Info("wifi evento: {0}", name);
                else if (src == 0x8 || src == 0x10) Log.Info("wifi evento: fuente 0x{0:X} código {1}", src, code);
                if (src == 0x8 && code == 10) SetLowLatency(true); // re-apply after reconnecting
            }
            catch { }
        }

        // ------------------------------------------------------------ neighbours

        public static int FreqToChannel(int mhz)
        {
            if (mhz == 2484) return 14;
            if (mhz >= 2412 && mhz <= 2472) return (mhz - 2407) / 5;
            if (mhz >= 5000 && mhz < 5925) return (mhz - 5000) / 5;
            if (mhz >= 5925 && mhz <= 7125) return (mhz - 5950) / 5;
            return -1;
        }

        /// <summary>Neighbour APs as {rssi dBm, channel}; null if the native call is unusable.</summary>
        public static List<int[]> Neighbours()
        {
            if (!ok) return null;
            IntPtr list;
            if (WlanGetNetworkBssList(handle, ref iface, IntPtr.Zero, 3 /* any */, false, IntPtr.Zero, out list) != 0) return null;
            try
            {
                int n = Marshal.ReadInt32(list, 4);
                const int entry = 360, first = 8;
                var res = new List<int[]>();
                for (int i = 0; i < n; i++)
                {
                    IntPtr e = list + first + i * entry;
                    var mac = new byte[6];
                    Marshal.Copy(e + 40, mac, 0, 6);
                    string b = BitConverter.ToString(mac).Replace('-', ':').ToLowerInvariant();
                    int rssi = Marshal.ReadInt32(e, 56);
                    int khz = Marshal.ReadInt32(e, 92);
                    int ch = FreqToChannel(khz / 1000);
                    if (rssi > 0 || rssi < -120 || ch < 0) return null; // layout mismatch: don't trust it
                    if (b == Bssid) continue;
                    res.Add(new[] { rssi, ch });
                }
                return res;
            }
            finally { WlanFreeMemory(list); }
        }

        public static void Stop()
        {
            try { if (ok) { SetLowLatency(false); WlanCloseHandle(handle, IntPtr.Zero); } } catch { }
        }
    }
}
