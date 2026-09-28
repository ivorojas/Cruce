using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Cruce
{
    /// <summary>
    /// "Black box": the last ~30 s of fine-grained events kept in a fixed in-memory ring
    /// (adding a record is a few nanoseconds, no allocation, no I/O). Only when something bad
    /// happens (big spike, stutter, freeze, hook loss...) the surrounding seconds are written
    /// to incidentes\*.txt with millisecond detail. Nothing is written in normal operation.
    /// </summary>
    public static class Flight
    {
        public const byte K_RX = 1, K_RTT = 2, K_TX = 3, K_INJ = 4, K_HOOK = 5, K_WIFI = 6, K_WIFIEV = 7, K_PING = 8, K_STUTTER = 9, K_MODE = 10, K_NET = 11, K_UI = 12;

        struct Rec { public long T; public byte K; public int A, B; }
        const int N = 1 << 16;
        static readonly Rec[] ring = new Rec[N];
        static long idx;
        static readonly object gate = new object();
        static readonly Dictionary<string, long> lastByReason = new Dictionary<string, long>();
        public static int IncidentsThisMinute;
        public static readonly string Dir = Path.Combine(Config.Dir, "incidentes");

        public static void Add(byte k, int a, int b)
        {
            long t = Link.NowUs();
            lock (gate)
            {
                int i = (int)(idx & (N - 1));
                ring[i].T = t; ring[i].K = k; ring[i].A = a; ring[i].B = b;
                idx++;
            }
        }

        /// <summary>Snapshots the last 20 s and writes an incident file (at most one per reason per minute).</summary>
        public static void Incident(string reason, string detail)
        {
            long now = Link.NowUs();
            Rec[] snap;
            lock (gate)
            {
                long last;
                if (lastByReason.TryGetValue(reason, out last) && now - last < 60000000) return;
                lastByReason[reason] = now;
                long n = Math.Min(idx, N);
                var list = new List<Rec>((int)n);
                for (long j = idx - n; j < idx; j++)
                {
                    var r = ring[(int)(j & (N - 1))];
                    if (now - r.T <= 20000000) list.Add(r);
                }
                snap = list.ToArray();
            }
            Interlocked.Increment(ref IncidentsThisMinute);
            Log.Info("INCIDENTE [{0}] {1} (detalle guardado en incidentes)", reason, detail);
            ThreadPool.QueueUserWorkItem(_ => Write(reason, detail, now, snap));
        }

        static string Describe(Rec r)
        {
            switch (r.K)
            {
                case K_RX: return string.Format("paquete recibido: tramo de red {0:0.0} ms, {1:0.0} ms desde el anterior", r.A / 1000.0, r.B / 1000.0);
                case K_RTT: return string.Format("ida y vuelta {0:0.0} ms", r.A / 1000.0);
                case K_TX: return string.Format("movimiento enviado, antigüedad {0:0.00} ms", r.A / 1000.0);
                case K_INJ: return string.Format("aplicado en esta PC en {0} µs", r.A);
                case K_HOOK: return string.Format("Windows entregó el mouse con {0} ms de demora", r.A);
                case K_WIFI: return string.Format("wifi: {0} dBm, calidad {1}%", r.A, r.B);
                case K_WIFIEV: return string.Format("wifi evento fuente 0x{0:X} código {1}", r.A, r.B);
                case K_PING: return r.B < 0 ? string.Format("ping router {0} ms", r.A) : string.Format("ping internet {0} ms", r.B);
                case K_STUTTER: return string.Format("TIRÓN: {0:0} ms sin movimientos (el otro lado los mandó con {1:0} ms de diferencia)", r.A / 1000.0, r.B / 1000.0);
                case K_MODE: return "modo -> " + (Mode)r.A;
                case K_NET: return string.Format("tráfico de red: bajada {0} KB/s, subida {1} KB/s", r.A, r.B);
                case K_UI: return string.Format("la ventana de Cruce estuvo colgada {0} ms", r.A);
            }
            return "evento " + r.K;
        }

        static void Write(string reason, string detail, long now, Rec[] snap)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var files = new DirectoryInfo(Dir).GetFiles("*.txt").OrderBy(f => f.CreationTimeUtc).ToList();
                for (int i = 0; i + 199 < files.Count; i++) try { files[i].Delete(); } catch { }

                var sb = new StringBuilder();
                sb.AppendLine("INCIDENTE: " + reason);
                sb.AppendLine("Cuándo: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                sb.AppendLine("Detalle: " + detail);
                sb.AppendLine("Contexto: " + Diag.Snapshot());
                sb.AppendLine();
                sb.AppendLine("Últimos 20 segundos (ms relativos al incidente):");
                // Collapse the (very frequent) routine packets: keep every notable record, and routine ones 1 in 10.
                int routine = 0;
                foreach (var r in snap)
                {
                    bool notable = r.K != K_RX && r.K != K_TX && r.K != K_INJ && r.K != K_HOOK;
                    if (!notable && r.K == K_RX && r.A > 30000) notable = true;
                    if (!notable && r.K == K_HOOK && r.A > 15) notable = true;
                    if (!notable && routine++ % 10 != 0) continue;
                    sb.AppendFormat("{0,9:0.0}  {1}", (r.T - now) / 1000.0, Describe(r)).AppendLine();
                }
                string name = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + reason + ".txt";
                File.WriteAllText(Path.Combine(Dir, name), sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Error(ex, "incident write"); }
        }
    }
}
