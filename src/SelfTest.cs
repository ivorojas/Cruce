using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

namespace Cruce
{
    /// <summary>Headless checks: geometry, link reliability under heavy loss, reconnects, injection precision.</summary>
    public static class SelfTest
    {
        sealed class Rec : ILinkHandler
        {
            public string Name;
            public Mon[] Mons;
            public readonly List<string> Events = new List<string>();
            public volatile int LastX = -1, LastY = -1;
            public volatile bool Up;
            public int Moves;

            public HelloInfo GetHello() { var h = new HelloInfo(); h.Name = Name; h.Mons = Mons; h.PeerSide = Side.Right; return h; }
            public void OnPeerUp(PeerInfo p) { Up = true; }
            public void OnPeerInfo(PeerInfo p) { }
            public void OnPeerDown() { Up = false; }
            public void OnReliable(byte t, byte[] d) { lock (Events) Events.Add(t + ":" + BitConverter.ToString(d)); }
            public void OnMove(int x, int y) { LastX = x; LastY = y; Interlocked.Increment(ref Moves); }
            public void OnTick(long n) { }
            public int Count { get { lock (Events) return Events.Count; } }
        }

        static StringBuilder sb;
        static bool ok;

        static void Check(bool cond, string what)
        {
            sb.AppendLine((cond ? "  OK    " : "  FALLA ") + what);
            if (!cond) ok = false;
        }

        static bool WaitFor(Func<bool> cond, int ms)
        {
            var t = Environment.TickCount;
            while (!cond()) { if (Environment.TickCount - t > ms) return false; Thread.Sleep(5); }
            return true;
        }

        static Mon M(int l, int t, int r, int b, int dpi) { var m = new Mon(); m.L = l; m.T = t; m.R = r; m.B = b; m.Dpi = dpi; return m; }

        public static int Run(bool inject)
        {
            sb = new StringBuilder();
            ok = true;
            try { Geometry(); } catch (Exception ex) { Check(false, "geometría: " + ex.Message); }
            try { LinkTest(0.25, 47901, 47902); } catch (Exception ex) { Check(false, "enlace: " + ex); }
            try { LinkTest(0.0, 47903, 47904); } catch (Exception ex) { Check(false, "enlace: " + ex); }
            try { PresenceTest(); } catch (Exception ex) { Check(false, "presencia: " + ex.Message); }
            try { ApiTest(); } catch (Exception ex) { Check(false, "api: " + ex.Message); }
            if (inject) { try { InjectTest(); } catch (Exception ex) { Check(false, "inyección: " + ex.Message); } }
            sb.AppendLine(ok ? "RESULTADO: TODO OK" : "RESULTADO: HAY FALLAS");
            Console.Write(sb.ToString());
            return ok ? 0 : 1;
        }

        static void ApiTest()
        {
            sb.AppendLine("[API local: un programa colgado no frena a los demás]");
            Api.PipeName = "Cruce.Api.SelfTest." + System.Diagnostics.Process.GetCurrentProcess().Id;
            Api.Start(null, new Config(), null);
            int step = 0;
            Func<System.IO.Pipes.NamedPipeClientStream> open = () =>
            {
                var cl = new System.IO.Pipes.NamedPipeClientStream(".", Api.PipeName, System.IO.Pipes.PipeDirection.InOut);
                try { cl.Connect(3000); } catch (TimeoutException) { throw new TimeoutException("no pude conectarme al pipe (paso " + step + ")"); }
                step++; return cl;
            };
            var stuck = open();   // subscribes and then never reads again
            var sw1 = new System.IO.StreamWriter(stuck) { AutoFlush = true, NewLine = "\n" };
            sw1.WriteLine("{\"cmd\":\"subscribe\",\"app\":\"t\"}");
            var good = open();
            var sw2 = new System.IO.StreamWriter(good) { AutoFlush = true, NewLine = "\n" };
            var rd2 = new System.IO.StreamReader(good);
            sw2.WriteLine("{\"cmd\":\"subscribe\",\"app\":\"t\"}");
            Check(rd2.ReadLine().Contains("\"ok\":true"), "suscripción confirmada");
            Thread.Sleep(200);
            string big = new string('x', 4000);
            int n = 800, got = 0;
            var reader = new Thread(() => { try { string line; while (got < n && (line = rd2.ReadLine()) != null) if (line.Contains("\"from\"")) got++; } catch { } }) { IsBackground = true };
            reader.Start();
            var t0 = Environment.TickCount;
            for (int i = 0; i < n; i++) { Api.FromPeer("t", "{\"i\":" + i + ",\"s\":\"" + big + "\"}"); if (i % 50 == 0) Thread.Sleep(5); }
            reader.Join(15000);
            Check(got == n, string.Format("el programa que sí lee recibió {0}/{1} mensajes ({2} ms) aunque el otro no lee", got, n, Environment.TickCount - t0));
            bool dropped = false;
            // Drain what it had buffered: a dropped client then sees the end of the stream (or a broken pipe).
            var drain = new Thread(() => { try { var buf = new byte[65536]; while (stuck.Read(buf, 0, buf.Length) > 0) { } dropped = true; } catch { dropped = true; } }) { IsBackground = true };
            drain.Start();
            drain.Join(5000);
            Check(dropped, "al colgado lo desconecta en vez de esperarlo");
            var third = open();
            var sw3 = new System.IO.StreamWriter(third) { AutoFlush = true, NewLine = "\n" };
            var rd3 = new System.IO.StreamReader(third);
            sw3.WriteLine("{\"cmd\":\"nada\"}");
            Check(rd3.ReadLine().Contains("comando desconocido"), "después sigue atendiendo programas nuevos; comando desconocido responde con error");
            try { stuck.Dispose(); good.Dispose(); third.Dispose(); } catch { }
        }

        static void PresenceTest()
        {
            sb.AppendLine("[Presencia en el registro (clave de prueba, no toca la real)]");
            string real = Presence.KeyPath;
            Presence.KeyPath = @"Software\Cruce\PresenceSelfTest";
            try
            {
                long before = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
                Presence.Set(Mode.Remote, "NOTEBOOK");
                Presence.Flush();
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Presence.KeyPath))
                {
                    Check(k != null, "la clave existe");
                    Check((string)k.GetValue("Mode") == "Remote", "Mode = Remote");
                    Check((string)k.GetValue("Peer") == "NOTEBOOK", "Peer = NOTEBOOK");
                    Check(k.GetValueKind("Since") == Microsoft.Win32.RegistryValueKind.QWord && Math.Abs((long)k.GetValue("Since") - before) < 5000, "Since = ahora (Unix ms, QWORD)");
                    Check(k.GetValueKind("Pid") == Microsoft.Win32.RegistryValueKind.DWord && (int)k.GetValue("Pid") == System.Diagnostics.Process.GetCurrentProcess().Id, "Pid = este proceso (DWORD)");
                    Check((int)k.GetValue("Version") == 1, "Version = 1");
                }
                Presence.Set(Mode.Local, "");
                Presence.Flush();
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Presence.KeyPath))
                    Check((string)k.GetValue("Mode") == "Local" && (string)k.GetValue("Peer") == "", "vuelve a Local con Peer vacío");
            }
            finally
            {
                try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(Presence.KeyPath, false); } catch { }
                Presence.KeyPath = real;
            }
        }

        static void Geometry()
        {
            sb.AppendLine("[Geometría]");
            var desk = new[] { M(0, 0, 1920, 1080, 96), M(1920, 0, 3360, 900, 96) };
            var lap = new[] { M(0, 0, 1920, 1200, 144) };

            var le = Edge.Of(desk, Side.Right);
            Check(le.Boundary == 3360 && le.A0 == 0 && le.A1 == 900, "borde derecho del escritorio = x 3360, y 0..900");
            Check(le.Beyond(3381) && !le.Beyond(3359), "detecta empuje más allá del borde (3381) y no dentro (3359)");
            Check(!le.Touches(desk[0]) && le.Touches(desk[1]), "sólo el monitor derecho toca ese borde");
            var re = Edge.Of(lap, Side.Left);
            Check(re.Boundary == 0 && re.A1 == 1200, "borde izquierdo de la notebook = x 0, y 0..1200");
            double a = Edge.Map(450, le, re);
            Check(Math.Abs(a - 600) < 1, "mapeo proporcional: mitad (450) -> mitad (600), dio " + a.ToString("0.0"));
            Check(Math.Abs(re.InsideAt(0)) < 0.001 && Math.Abs(le.InsideAt(0) - 3359) < 0.001, "punto de entrada/salida exacto en el borde");

            var ll = Edge.Of(desk, Side.Left);
            Check(ll.Boundary == 0 && ll.A1 == 1080 && ll.Beyond(-32) && !ll.Beyond(0), "borde izquierdo del escritorio");

            double x = 2500, y = 1000;
            int i = Geo.Clamp(desk, ref x, ref y);
            Check(i == 1 && x == 2500 && y > 899 && y < 900, "punto en el hueco bajo el 2º monitor se ajusta al monitor");
            x = -50; y = 500;
            Geo.Clamp(desk, ref x, ref y);
            Check(x == 0 && y == 500, "punto fuera a la izquierda se ajusta al borde");

            var top = Edge.Of(lap, Side.Top);
            var bot = Edge.Of(desk, Side.Bottom);
            Check(top.Boundary == 0 && bot.Boundary == 1080 && bot.A1 == 1920, "bordes arriba/abajo");
        }

        static void LinkTest(double loss, int pa, int pb)
        {
            sb.AppendLine(string.Format("[Enlace con {0:0}% de pérdida simulada en cada sentido]", loss * 100));
            var keys = new Keys("prueba-cruce");
            var ha = new Rec { Name = "A", Mons = new[] { M(0, 0, 1920, 1080, 96) } };
            var hb = new Rec { Name = "B", Mons = new[] { M(0, 0, 2560, 1600, 144) } };
            var a = new Link(keys, pa, pb, IPAddress.Loopback, ha);
            var b = new Link(keys, pb, pa, IPAddress.Loopback, hb);
            a.DropRate = loss; b.DropRate = loss;
            a.Start(); b.Start();
            try
            {
                Check(WaitFor(() => ha.Up && hb.Up, 8000), "se descubren y conectan");
                a.Active = true; b.Active = true;

                var expected = new List<string>();
                var t0 = Link.NowUs();
                for (int i = 0; i < 3000; i++)
                {
                    a.SetMove(i, i * 2);
                    if (i % 10 == 0)
                    {
                        var payload = BitConverter.GetBytes(i);
                        a.QueueReliable(Ev.Key, payload);
                        expected.Add(Ev.Key + ":" + BitConverter.ToString(payload));
                    }
                    if (i % 3 == 0) Thread.Sleep(1);
                }
                var sendMs = (Link.NowUs() - t0) / 1000.0;
                bool all = WaitFor(() => hb.Count >= expected.Count, 5000);
                var tDone = (Link.NowUs() - t0) / 1000.0;
                Check(all, string.Format("llegaron {0}/{1} eventos confiables (teclas/clicks)", hb.Count, expected.Count));
                List<string> got;
                lock (hb.Events) got = hb.Events.ToList();
                Check(got.SequenceEqual(expected), "en el orden exacto, sin duplicados");
                Check(WaitFor(() => hb.LastX == 2999 && hb.LastY == 5998, 3000), string.Format("posición final exacta del mouse (2999,5998), llegó ({0},{1})", hb.LastX, hb.LastY));
                sb.AppendLine(string.Format("  info  {0} movimientos aplicados de 3000 generados en {1:0} ms; confiables completos a los {2:0} ms", hb.Moves, sendMs, tDone));
                Thread.Sleep(1200);
                var st = a.Stats;
                sb.AppendLine(string.Format("  info  RTT loopback prom {0:0.000} ms, p95 {1:0.000} ms; pérdida medida {2:0.0}%; QoS {3}", st.RttMs, st.RttP95Ms, st.LossPct, st.Qos ? "sí" : "no"));

                // restart of the other side: new session must be picked up and state reset
                b.Dispose();
                Thread.Sleep(100);
                var hb2 = new Rec { Name = "B2", Mons = hb.Mons };
                var b2 = new Link(keys, pb, pa, IPAddress.Loopback, hb2);
                b2.DropRate = loss;
                b2.Start();
                try
                {
                    Check(WaitFor(() => ha.Up && hb2.Up, 8000), "reconecta tras reiniciar la otra PC");
                    var exp2 = new List<string>();
                    for (int i = 0; i < 50; i++)
                    {
                        var payload = BitConverter.GetBytes(10000 + i);
                        a.QueueReliable(Ev.Key, payload);
                        exp2.Add(Ev.Key + ":" + BitConverter.ToString(payload));
                    }
                    WaitFor(() => hb2.Count >= exp2.Count, 5000);
                    List<string> got2;
                    lock (hb2.Events) got2 = hb2.Events.ToList();
                    Check(got2.SequenceEqual(exp2), string.Format("tras reconectar: {0}/{1} eventos en orden", got2.Count, exp2.Count));

                    // One side forgets the other without the other noticing (a one-sided timeout, or waking from
                    // sleep): the counters no longer match and, before 1.27, every event was silently discarded
                    // while the link looked alive. It must notice and start over on both sides by itself.
                    b2.TestForgetPeer();
                    Thread.Sleep(4000);
                    int a0 = ha.Count, b0 = hb2.Count;
                    for (int i = 0; i < 40; i++) { a.QueueReliable(Ev.Key, BitConverter.GetBytes(20000 + i)); b2.QueueReliable(Ev.Key, BitConverter.GetBytes(30000 + i)); }
                    WaitFor(() => hb2.Count - b0 >= 40 && ha.Count - a0 >= 40, 6000);
                    Check(hb2.Count - b0 == 40 && ha.Count - a0 == 40, string.Format("una PC olvidó a la otra sin avisar: el enlace se rearma solo ({0}/40 y {1}/40 eventos después)", hb2.Count - b0, ha.Count - a0));
                }
                finally { b2.Dispose(); }

                // a wrong key must never be accepted
                var hx = new Rec { Name = "X", Mons = hb.Mons };
                var x = new Link(new Keys("otra-clave"), pb, pa, IPAddress.Loopback, hx);
                x.Start();
                Thread.Sleep(2500);
                Check(!hx.Up, "una PC con otra clave no puede conectarse");
                x.Dispose();
            }
            finally { a.Dispose(); try { b.Dispose(); } catch { } }
        }

        /// <summary>A pretend "other PC" (1920x1200 at 150%) that logs everything it receives.</summary>
        sealed class FakeHandler : ILinkHandler
        {
            readonly System.IO.StreamWriter w;
            public FakeHandler(System.IO.StreamWriter w) { this.w = w; }
            void L(string s) { lock (w) { w.WriteLine(Link.NowUs() / 1000 + "  " + s); w.Flush(); } }
            public HelloInfo GetHello()
            {
                var h = new HelloInfo(); h.Name = "NOTEBOOK-FALSA"; h.Mons = new[] { M(0, 0, 1920, 1200, 144) }; h.PeerSide = Side.Right; h.Elevated = true;
                return h;
            }
            public void OnPeerUp(PeerInfo p) { L("PEER_UP " + p.Name); }
            public void OnPeerInfo(PeerInfo p) { L("PEER_INFO side=" + p.SideOfMe); }
            public void OnPeerDown() { L("PEER_DOWN"); }
            public void OnReliable(byte t, byte[] d)
            {
                var r = new RBuf(d, 0, d.Length);
                string s;
                switch (t)
                {
                    case Ev.Enter: s = "ENTER " + r.I32() + "," + r.I32(); break;
                    case Ev.Leave: s = "LEAVE"; break;
                    case Ev.Button: s = "BUTTON b=" + r.U8() + " down=" + r.U8() + " at " + r.I32() + "," + r.I32(); break;
                    case Ev.Wheel: s = "WHEEL h=" + r.U8() + " delta=" + r.I16(); break;
                    case Ev.Key: s = string.Format("KEY vk=0x{0:X2} scan=0x{1:X} flags={2}", r.U16(), r.U16(), r.U8()); break;
                    default: s = "EV " + t; break;
                }
                L(s);
            }
            public void OnMove(int x, int y) { L("MOVE " + x + "," + y); }
            public void OnTick(long n) { }
        }

        public static int FakePeer(string secret, int port, int peerPort, string logPath)
        {
            using (var w = new System.IO.StreamWriter(logPath, false))
            {
                var h = new FakeHandler(w);
                var l = new Link(new Keys(secret), port, peerPort, IPAddress.Loopback, h);
                l.Active = true;
                l.Start();
                Thread.Sleep(Timeout.Infinite);
            }
            return 0;
        }

        /// <summary>A pretend "other PC" that accepts drag-and-drop: every drop lands in <c>dest</c>.</summary>
        public static int FakeDrop(string secret, int port, int peerPort, string dest, string logPath)
        {
            var done = new ManualResetEvent(false);
            var t = new Thread(() =>
            {
                var w = new System.IO.StreamWriter(logPath, false) { AutoFlush = true };
                var disp = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                DropUi.Init(disp);
                var keys = new Keys(secret);
                ClipSync clip = null;
                PeerInfo peer = null;
                Link link = null;
                var h = new DropHandler();
                h.Log = s => { lock (w) w.WriteLine(Link.NowUs() / 1000 + "  " + s); };
                h.OnDrop = (id, x, y) =>
                {
                    clip.ExpectDrop(id, new ClipSync.DropSpot { Folder = dest, Kind = "prueba", X = x, Y = y });
                    var b = new WBuf(4); b.U32(id);
                    link.QueueReliable(Ev.DragPull, b.ToArray());
                };
                h.PeerUp = p => peer = p;
                ClipSync.TestNoInject = true;
                ClipSync.TestPasted = text => h.Log("PASTE '" + text + "'");
                // Echo app messages back through the same kind of channel they came on.
                ClipSync.TestAppMsg = (app, json) => { h.Log("APPMSG grande app=" + app + " " + json.Length + " chars"); clip.SendAppMsg(app, json); };
                h.OnAppMsg = (app, json) =>
                {
                    h.Log("APPMSG chico app=" + app + " " + json);
                    var b = new WBuf(64); b.Str(app); b.Str(json);
                    link.QueueReliable(Ev.AppMsg, b.ToArray());
                };
                clip = new ClipSync(keys, port, () => peer != null ? new IPEndPoint(peer.Ep.Address, peer.TcpPort > 0 ? peer.TcpPort : peerPort) : null, () => new Config(), disp);
                h.TcpPort = clip.ListenPort;
                link = new Link(keys, port, peerPort, IPAddress.Loopback, h);
                link.Active = true;
                link.Start();
                System.Windows.Threading.Dispatcher.Run();
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            done.WaitOne();
            return 0;
        }

        sealed class DropHandler : ILinkHandler
        {
            public Action<string> Log;
            public Action<uint, int, int> OnDrop;
            public Action<PeerInfo> PeerUp;
            public Action<string, string> OnAppMsg;
            public int TcpPort;
            public HelloInfo GetHello()
            {
                var h = new HelloInfo(); h.Name = "NOTEBOOK-FALSA"; h.Mons = new[] { M(0, 0, 1920, 1200, 144) }; h.PeerSide = Side.Right; h.Elevated = true; h.TcpPort = TcpPort;
                return h;
            }
            public void OnPeerUp(PeerInfo p) { PeerUp(p); Log("PEER_UP " + p.Name); }
            public void OnPeerInfo(PeerInfo p) { PeerUp(p); }
            public void OnPeerDown() { Log("PEER_DOWN"); }
            public void OnReliable(byte t, byte[] d)
            {
                var r = new RBuf(d, 0, d.Length);
                switch (t)
                {
                    case Ev.Enter: Log("ENTER " + r.I32() + "," + r.I32()); break;
                    case Ev.Leave: Log("LEAVE"); break;
                    case Ev.DragStart: { uint id = r.U32(); int n = r.U16(); Log("DRAG_START id=" + id + " n=" + n + " '" + r.Str() + "'"); break; }
                    case Ev.DragDrop: { uint id = r.U32(); int x = r.I32(), y = r.I32(); Log("DRAG_DROP id=" + id + " en " + x + "," + y); OnDrop(id, x, y); break; }
                    case Ev.DragCancel: Log("DRAG_CANCEL " + r.U32()); break;
                    case Ev.Button: Log("BUTTON b=" + r.U8() + " down=" + r.U8()); break;
                    case Ev.Key: { int vk = r.U16(); r.U16(); Log(string.Format("KEY 0x{0:X2} {1}", vk, (r.U8() & 1) != 0 ? "up" : "down")); break; }
                    case Ev.AppMsg: { string app = r.Str(); OnAppMsg(app, r.Str()); break; }
                    default: Log("EV " + t); break;
                }
            }
            public void OnMove(int x, int y) { }
            public void OnTick(long n) { }
        }

        static void InjectTest()
        {
            sb.AppendLine("[Precisión de inyección (mueve el cursor un instante)]");
            POINT orig;
            Native.GetCursorPos(out orig);
            var mons = Geo.Enumerate();
            int bad = 0, total = 0;
            var misses = new List<string>();
            foreach (var m in mons)
            {
                foreach (var px in new[] { m.L, m.L + 1, (m.L + m.R) / 2, m.R - 2, m.R - 1 })
                    foreach (var py in new[] { m.T, m.T + 1, (m.T + m.B) / 2, m.B - 2, m.B - 1 })
                    {
                        POINT c = new POINT();
                        for (int attempt = 0; attempt < 3; attempt++) // retries filter out someone else moving the mouse meanwhile
                        {
                            Inject.Move(px, py);
                            Thread.Sleep(3);
                            Native.GetCursorPos(out c);
                            if (c.X == px && c.Y == py) break;
                        }
                        total++;
                        if (c.X != px || c.Y != py) { bad++; if (misses.Count < 6) misses.Add(string.Format("({0},{1})->({2},{3})", px, py, c.X, c.Y)); }
                    }
            }
            Native.SetCursorPos(orig.X, orig.Y);
            Check(bad == 0, string.Format("{0}/{1} posiciones exactas al píxel {2}", total - bad, total, string.Join(" ", misses)));
        }
    }
}
