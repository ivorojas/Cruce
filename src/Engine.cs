using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Cruce
{
    public enum Mode { Local, Remote, Controlled }

    /// <summary>Reliable event types.</summary>
    public static class Ev
    {
        public const byte Enter = 1, Leave = 2, Button = 3, Wheel = 4, Key = 5, Takeover = 6, LogLine = 7;
        public const byte DragStart = 8, DragDrop = 9, DragCancel = 10, DragProbe = 11, DragProbeReply = 12, DragPull = 13;
        public const byte AppMsg = 14;   // small message from a local app (Api) to the same app on the other PC
    }

    /// <summary>
    /// Captures this PC's mouse and keyboard with low-level hooks, decides when the pointer
    /// crosses to the other PC, forwards input while it is there, and applies input coming
    /// from the other PC when this one is being controlled.
    /// </summary>
    public sealed unsafe class Engine : ILinkHandler, IDisposable
    {
        public static readonly IntPtr Tag = new IntPtr(0x43525543); // marks our own injected input
        const int CornerGuard = 6;
        const long WarmUs = 1500000, SilentReturnUs = 3000000;

        readonly object gate = new object();
        readonly Config cfg;
        volatile Link link;
        volatile Mode mode = Mode.Local;
        volatile PeerInfo peer;
        volatile Mon[] localMons;

        Edge localEdge, remoteEdge;
        bool edgesValid;

        // controller side
        double rx, ry;
        bool haveRemotePos;
        POINT park;
        double localDpi = 96;
        int localButtons, remoteButtons;
        readonly bool[] localKeys = new bool[256], remoteKeys = new bool[256], remoteExt = new bool[256];
        readonly ushort[] remoteScan = new ushort[256];
        bool swallowF12;

        // controlled side
        readonly bool[] injKeys = new bool[256], injExt = new bool[256];
        readonly ushort[] injScan = new ushort[256];
        int injButtons, injX, injY;
        double takeoverAccum;
        long takeoverAt;

        long activeUs, lastTickUs;
        long lastMonCheck, lastSummary, lastCrossings, takeovers, enteredAt, lastHookCheck, lastSlowLog;
        volatile int lastHookTick = Environment.TickCount;
        const uint WM_REHOOK = 0x8001;
        Thread hookThread;
        uint hookThreadId;
        IntPtr mouseHook, kbHook;
        LowLevelProc mouseProc, kbProc;

        public volatile bool Paused;
        public event Action<string> Notify;
        public Func<bool> IsElevated;
        public long Crossings;
        public volatile int TcpPort;

        [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO lii);

        public Engine(Config cfg)
        {
            this.cfg = cfg;
            localMons = Geo.Enumerate();
        }

        public Mode Mode { get { return mode; } }
        public PeerInfo Peer { get { return peer; } }
        public Mon[] LocalMons { get { return localMons; } }
        public Link Link { get { return link; } }
        public bool HooksOk { get { return mouseHook != IntPtr.Zero && kbHook != IntPtr.Zero; } }

        /// <summary>Where the pointer is on the other PC while we control it (for the live map).</summary>
        public void GetRemotePos(out double x, out double y) { x = rx; y = ry; }

        public void AttachLink(Link l)
        {
            lock (gate) { DropSessionLocked(); link = l; peer = null; edgesValid = false; }
        }

        public void DetachLink()
        {
            lock (gate) { DropSessionLocked(); link = null; peer = null; edgesValid = false; }
        }

        public void InvalidateEdges() { lock (gate) edgesValid = false; }

        // ------------------------------------------------------------------ hooks

        public void Start()
        {
            var ready = new ManualResetEvent(false);
            hookThread = new Thread(() =>
            {
                Native.BoostThread();
                hookThreadId = Native.GetCurrentThreadId();
                mouseProc = MouseProc;
                kbProc = KbProc;
                IntPtr mod = Native.GetModuleHandle(null);
                mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, mod, 0);
                kbHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, kbProc, mod, 0);
                ready.Set();
                MSG msg;
                while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                {
                    if (msg.message == WM_REHOOK)
                    {
                        Native.UnhookWindowsHookEx(mouseHook); Native.UnhookWindowsHookEx(kbHook);
                        mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, mod, 0);
                        kbHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, kbProc, mod, 0);
                        lastHookTick = Environment.TickCount;
                        Log.Info("hooks reinstalados (ok={0})", HooksOk);
                        continue;
                    }
                    Native.TranslateMessage(ref msg);
                    Native.DispatchMessage(ref msg);
                }
                Native.UnhookWindowsHookEx(mouseHook);
                Native.UnhookWindowsHookEx(kbHook);
            });
            hookThread.IsBackground = true;
            hookThread.Priority = ThreadPriority.Highest;
            hookThread.Name = "cruce-hooks";
            hookThread.Start();
            ready.WaitOne(3000);
            if (!HooksOk) Log.Info("hook install failed");
        }

        IntPtr MouseProc(int nCode, IntPtr w, IntPtr l)
        {
            lastHookTick = Environment.TickCount;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { return MouseProcInner(nCode, w, l); }
            finally { SlowCheck(t0, "mouse"); }
        }

        void SlowCheck(long t0, string what)
        {
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (ms > 10) Interlocked.Increment(ref Diag.SlowHooks);
            if (ms > 10 && Link.NowUs() - lastSlowLog > 2000000) { lastSlowLog = Link.NowUs(); Log.Info("LENTO: el hook de {0} tardó {1:0.0} ms", what, ms); }
        }

        IntPtr MouseProcInner(int nCode, IntPtr w, IntPtr l)
        {
            if (nCode >= 0)
            {
                var m = (MSLLHOOKSTRUCT*)l;
                if ((m->flags & Native.LLMHF_INJECTED) == 0 || (cfg.TestAcceptInjected && m->dwExtraInfo != Tag))
                {
                    if (mode == Mode.Remote && w.ToInt32() == Native.WM_MOUSEMOVE)
                    {
                        int delay = unchecked(Environment.TickCount - (int)m->time);
                        Diag.HookDelay(delay);
                        if (delay > 15) Flight.Add(Flight.K_HOOK, delay, 0);
                    }
                    bool block = false;
                    try { block = OnMouse(w.ToInt32(), m->pt.X, m->pt.Y, m->mouseData); }
                    catch (Exception ex) { Log.Error(ex, "mouse hook"); }
                    if (block) return new IntPtr(1);
                }
            }
            return Native.CallNextHookEx(IntPtr.Zero, nCode, w, l);
        }

        IntPtr KbProc(int nCode, IntPtr w, IntPtr l)
        {
            lastHookTick = Environment.TickCount;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { return KbProcInner(nCode, w, l); }
            finally { SlowCheck(t0, "teclado"); }
        }

        IntPtr KbProcInner(int nCode, IntPtr w, IntPtr l)
        {
            if (nCode >= 0)
            {
                var k = (KBDLLHOOKSTRUCT*)l;
                bool injected = (k->flags & Native.LLKHF_INJECTED) != 0;
                if (!injected || (cfg.TestAcceptInjected && k->dwExtraInfo != Tag && k->dwExtraInfo != TestOtherApp))
                {
                    bool block = false;
                    try { block = OnKey((int)(k->vkCode & 0xFF), (int)k->scanCode, (k->flags & Native.LLKHF_EXTENDED) != 0, (k->flags & Native.LLKHF_UP) != 0); }
                    catch (Exception ex) { Log.Error(ex, "kb hook"); }
                    if (block) return new IntPtr(1);
                }
                else if (k->dwExtraInfo != Tag && mode == Mode.Remote)
                {
                    // Keys typed by another program on this PC (e.g. Dictalo pasting) while you drive the
                    // other PC belong over there, where you are.
                    bool block = false;
                    try { block = OnOtherAppKey((int)(k->vkCode & 0xFF), (int)k->scanCode, (k->flags & Native.LLKHF_EXTENDED) != 0, (k->flags & Native.LLKHF_UP) != 0); }
                    catch (Exception ex) { Log.Error(ex, "kb hook (otra app)"); }
                    if (block) return new IntPtr(1);
                }
            }
            return Native.CallNextHookEx(IntPtr.Zero, nCode, w, l);
        }

        static int ButtonOf(int msg, uint data, out bool down)
        {
            down = false;
            switch (msg)
            {
                case 0x201: down = true; return 0;
                case 0x202: return 0;
                case 0x204: down = true; return 1;
                case 0x205: return 1;
                case 0x207: down = true; return 2;
                case 0x208: return 2;
                case 0x20B: down = true; return (data >> 16) == 1 ? 3 : 4;
                case 0x20C: return (data >> 16) == 1 ? 3 : 4;
            }
            return -1;
        }

        static void SetBit(ref int mask, int bit, bool on)
        {
            if (on) mask |= 1 << bit; else mask &= ~(1 << bit);
        }

        bool OnMouse(int msg, int x, int y, uint data)
        {
            bool down;
            int btn = ButtonOf(msg, data, out down);
            lock (gate)
            {
                switch (mode)
                {
                    case Mode.Local:
                        if (btn >= 0)
                        {
                            SetBit(ref localButtons, btn, down);
                            if (btn == 0 && down) { dragSourceRoot = ShellDrag.RootAt(x, y); readyFiles = null; dragNoteLogged = false; }
                            if (btn == 0 && !down)
                            {
                                readyFiles = null;
                                if (inDragId != 0) { FinishIncomingDragLocked(x, y); return true; }
                            }
                            return false;
                        }
                        if (msg == Native.WM_MOUSEMOVE && peer != null && link != null && !Paused) return TryCrossLocked(x, y);
                        return false;

                    case Mode.Remote:
                        if (msg == Native.WM_MOUSEMOVE) { RemoteMoveLocked(x, y); return true; }
                        if (btn >= 0)
                        {
                            int bit = 1 << btn;
                            if (btn == 0 && !down && outDragId != 0) { localButtons &= ~1; DropOutgoingLocked(); return true; }
                            if (down) { remoteButtons |= bit; SendButtonLocked(btn, true); return true; }
                            if ((remoteButtons & bit) != 0) { remoteButtons &= ~bit; SendButtonLocked(btn, false); return true; }
                            localButtons &= ~bit; // released a button that was pressed here before crossing
                            return false;
                        }
                        if (msg == Native.WM_MOUSEWHEEL || msg == Native.WM_MOUSEHWHEEL)
                        {
                            var p = new WBuf(4);
                            p.U8(msg == Native.WM_MOUSEHWHEEL ? 1 : 0);
                            p.U16((short)(data >> 16));
                            Send(Ev.Wheel, p);
                        }
                        return true;

                    default: // Controlled: real input on this PC means someone is using it here
                        if (btn >= 0) SetBit(ref localButtons, btn, down);
                        if (msg == Native.WM_MOUSEMOVE)
                        {
                            POINT c;
                            Native.GetCursorPos(out c);
                            long now = Link.NowUs();
                            if (now - takeoverAt > 300000) takeoverAccum = 0;
                            takeoverAt = now;
                            takeoverAccum += Math.Abs(x - c.X) + Math.Abs(y - c.Y);
                            if (takeoverAccum >= 6) TakeoverLocked();
                        }
                        else TakeoverLocked();
                        return false;
                }
            }
        }

        bool OnKey(int vk, int scan, bool ext, bool up)
        {
            lock (gate)
            {
                if (vk == 0x7B) // Ctrl+Alt+F12 jumps between PCs
                {
                    if (!up && CtrlAltLocked()) { swallowF12 = true; HotkeyLocked(); return true; }
                    if (up && swallowF12) { swallowF12 = false; return true; }
                }
                switch (mode)
                {
                    case Mode.Local:
                        localKeys[vk] = !up;
                        if (vk == 0x1B && !up && inDragId != 0) CancelIncomingLocked();
                        return false;

                    case Mode.Remote:
                        if (localKeys[vk]) { if (up) localKeys[vk] = false; return false; } // held since before crossing
                        if (IsLocalKey(vk)) return false; // keys that never cross (e.g. Dictalo's F9): stay on this PC
                        if ((scan & 0x200) != 0) return true; // AltGr's synthetic LCtrl; the other PC makes its own
                        remoteKeys[vk] = !up;
                        remoteScan[vk] = (ushort)scan;
                        remoteExt[vk] = ext;
                        SendKeyLocked(vk, scan, ext, up);
                        return true;

                    default:
                        localKeys[vk] = !up; /* this PC's own keyboard just works; it does not end the session */
                        return false;
                }
            }
        }

        public static readonly IntPtr TestOtherApp = new IntPtr(0x44494354); // tests: simulate "another program"
        bool otherCtrl, otherV;
        HashSet<int> localKeySet;

        volatile bool localKeysActive = true;
        long lastLocalAppCheck;

        /// <summary>Api: relays a small app message over the reliable channel. Returns false if not linked.</summary>
        public bool SendAppMsg(string app, string json)
        {
            var l = link;
            if (l == null || peer == null) return false;
            var w = new WBuf(64 + json.Length * 3);
            w.Str(app); w.Str(json);
            return l.QueueReliable(Ev.AppMsg, w.ToArray());
        }

        bool IsLocalKey(int vk)
        {
            if (!localKeysActive || Api.DictadoConnected) return false; // Dictado handles cross-PC itself once it uses the API
            var s = localKeySet;
            if (s == null) { s = cfg.LocalKeyCodes(); localKeySet = s; }
            return s.Contains(vk);
        }

        /// <summary>Local keys apply only while their program (Dictalo) runs on this PC. Checked off the input path.</summary>
        void RefreshLocalKeys(long now)
        {
            if (now - lastLocalAppCheck < 3000000) return;
            lastLocalAppCheck = now;
            string app = cfg.LocalKeysApp;
            if (string.IsNullOrEmpty(app)) { localKeysActive = true; return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var ps = System.Diagnostics.Process.GetProcessesByName(app);
                    bool on = ps.Length > 0;
                    foreach (var p in ps) p.Dispose();
                    if (on != localKeysActive) Log.Info("teclas que no cruzan ({0}): {1}", cfg.LocalKeys, on ? app + " está abierto, se quedan en esta PC" : app + " no está abierto, cruzan normal");
                    localKeysActive = on;
                }
                catch { }
            });
        }

        /// <summary>Another program's synthetic key while we drive the other PC.</summary>
        bool OnOtherAppKey(int vk, int scan, bool ext, bool up)
        {
            lock (gate)
            {
                if (mode != Mode.Remote) return false;
                bool isCtrl = vk == 0x11 || vk == 0xA2 || vk == 0xA3;
                if (isCtrl) otherCtrl = !up;
                if (vk == 0x56 && (otherCtrl || otherV))
                {
                    // Ctrl+V from a program here (Dictalo): send the clipboard text and paste it over there.
                    if (!up && !otherV) { otherV = true; RemotePasteLocked(); }
                    if (up) otherV = false;
                    return true;
                }
                SendKeyLocked(vk, scan, ext, up); // Ctrl too, so other shortcuts (Ctrl+C…) arrive whole
                return true;
            }
        }

        void RemotePasteLocked()
        {
            var ui = Ui;
            if (ui == null) return;
            ui.BeginInvoke(new Action(() =>
            {
                string text = null;
                for (int i = 0; i < 6 && text == null; i++)
                {
                    try { text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : ""; }
                    catch { Thread.Sleep(30); }
                }
                var c = Clip != null ? Clip() : null;
                if (string.IsNullOrEmpty(text) || c == null) { Log.Info("pegado remoto: no había texto en el portapapeles"); return; }
                c.SendPaste(text);
                Log.Info("pegado remoto: otra app pegó {0} caracteres mientras manejabas la otra PC; los mando para pegarlos allá", text.Length);
            }));
        }

        bool CtrlAltLocked()
        {
            bool ctrl = localKeys[0xA2] || localKeys[0xA3] || remoteKeys[0xA2] || remoteKeys[0xA3];
            bool alt = localKeys[0xA4] || localKeys[0xA5] || remoteKeys[0xA4] || remoteKeys[0xA5];
            return ctrl && alt;
        }

        // ------------------------------------------------------------------ crossing

        void EnsureEdgesLocked()
        {
            if (edgesValid) return;
            var p = peer;
            Side s = cfg.Side;
            localEdge = Edge.Of(localMons, s);
            remoteEdge = p != null ? Edge.Of(p.Mons, Geo.Opposite(s)) : null;
            edgesValid = true;
        }

        bool TryCrossLocked(int x, int y)
        {
            EnsureEdgesLocked();
            var le = localEdge;
            var re = remoteEdge;
            var p = peer;
            if (le == null || re == null || p == null) return false;
            Side s = le.Side;
            POINT c;
            Native.GetCursorPos(out c);
            var mons = localMons;
            int mi = Geo.IndexOf(mons, c.X, c.Y);
            if (mi < 0 || !le.Touches(mons[mi])) return false;

            // Pointer approaching the edge: wake the Wi-Fi link up before we need it.
            double dist = -le.Overflow(Geo.Cross(s, c.X, c.Y));
            if (dist < 250) link.Warm(Link.NowUs() + WarmUs);

            double cross = Geo.Cross(s, x, y);
            if (!le.Beyond(cross)) return false;
            bool carrying = false;
            if (localButtons != 0)
            {
                // Only a left-button drag of files from Explorer/desktop may cross; anything else stays here.
                if (localButtons != 1) return false;
                if (readyFiles == null) { StartDragResolveLocked(); return false; }
                carrying = true;
            }
            var m = mons[mi];
            bool h = Geo.Horiz(s);
            double along = Geo.Along(s, x, y);
            along = Math.Max(h ? m.T : m.L, Math.Min(along, (h ? m.B : m.R) - 1));
            if (le.NearCorner(along, CornerGuard)) return false;

            double over = Math.Min(le.Overflow(cross), 12);
            double ra = Edge.Map(along, le, re);
            double nx, ny;
            Geo.Compose(s, re.InsideAt(over - 1), ra, out nx, out ny);
            Geo.Clamp(p.Mons, ref nx, ref ny);

            double ex, ey;
            Geo.Compose(s, le.InsideAt(0), along, out ex, out ey);
            park = new POINT((int)ex, (int)ey);
            localDpi = m.Dpi > 0 ? m.Dpi : 96;
            EnterRemoteLocked(nx, ny);
            if (carrying) BeginOutgoingDragLocked();
            return true;
        }

        void EnterRemoteLocked(double nx, double ny)
        {
            var l = link;
            if (l == null) return;
            if (inDragId != 0) CancelIncomingLocked();
            rx = nx; ry = ny; haveRemotePos = true;
            mode = Mode.Remote;
            remoteButtons = 0;
            Array.Clear(remoteKeys, 0, remoteKeys.Length);
            Native.SetCursorPos(park.X, park.Y);
            CursorHider.Hide();
            l.Active = true;
            var w = new WBuf(8);
            w.I32((int)Math.Floor(rx)); w.I32((int)Math.Floor(ry));
            l.QueueReliable(Ev.Enter, w.ToArray());
            l.SetMove((int)Math.Floor(rx), (int)Math.Floor(ry));
            Crossings++;
            enteredAt = Link.NowUs();
            var pp = peer;
            Log.Info("CRUCE a {0} en {1:0},{2:0} (salida {3},{4})", pp != null ? pp.Name : "?", rx, ry, park.X, park.Y);
            Flight.Add(Flight.K_MODE, (int)Mode.Remote, 0);
        }

        void RemoteMoveLocked(int x, int y)
        {
            POINT c;
            Native.GetCursorPos(out c);
            int dx = x - c.X, dy = y - c.Y;
            if (c.X != park.X || c.Y != park.Y) Native.SetCursorPos(park.X, park.Y);
            var p = peer;
            var l = link;
            if ((dx == 0 && dy == 0) || p == null || l == null) return;

            int ri = Geo.IndexOf(p.Mons, rx, ry);
            double rdpi = ri >= 0 && p.Mons[ri].Dpi > 0 ? p.Mons[ri].Dpi : 96;
            double k = cfg.Speed * rdpi / localDpi;   // same physical feel on both screens
            double nx = rx + dx * k, ny = ry + dy * k;

            EnsureEdgesLocked();
            var re = remoteEdge;
            var le = localEdge;
            if (re != null && le != null && ri >= 0 && re.Touches(p.Mons[ri]))
            {
                Side s = le.Side;
                double cr = Geo.Cross(s, nx, ny);
                double al = Geo.Along(s, nx, ny);
                if (re.Beyond(cr) && !re.NearCorner(al, CornerGuard))
                {
                    bool carryingIn = remoteButtons == 1 && probeDragId != 0;
                    if (remoteButtons == 1 && !carryingIn) ProbeRemoteDragLocked(); // dragging files over there? ask
                    else if (remoteButtons == 0 || carryingIn)
                    {
                        double over = Math.Min(re.Overflow(cr) / k, 12);
                        double la = Edge.Map(al, re, le);
                        double lx, ly;
                        Geo.Compose(s, le.InsideAt(over - 1), la, out lx, out ly);
                        Geo.Clamp(localMons, ref lx, ref ly);
                        uint pid = probeDragId;
                        string plabel = probeLabel;
                        ReturnLocalLocked(new POINT((int)lx, (int)ly), true, carryingIn ? "trayendo archivos" : "borde");
                        if (carryingIn) { inDragId = pid; DropUi.ShowGhost(plabel); }
                        return;
                    }
                }
            }
            Geo.Clamp(p.Mons, ref nx, ref ny);
            rx = nx; ry = ny;
            l.SetMove((int)Math.Floor(rx), (int)Math.Floor(ry));
        }

        void ReturnLocalLocked(POINT pt, bool sendLeave, string reason)
        {
            CancelOutgoingLocked("volviste a esta PC antes de soltar");
            probeDragId = 0;
            var l = link;
            if (l != null)
            {
                for (int vk = 0; vk < 256; vk++)
                    if (remoteKeys[vk]) { remoteKeys[vk] = false; SendKeyLocked(vk, remoteScan[vk], remoteExt[vk], true); }
                for (int b = 0; b < 5; b++)
                    if ((remoteButtons & (1 << b)) != 0) SendButtonLocked(b, false);
                l.ResetMove();
                if (sendLeave) l.QueueReliable(Ev.Leave, null);
                l.Active = false;
                l.Warm(Link.NowUs() + WarmUs);
            }
            remoteButtons = 0;
            mode = Mode.Local;
            Log.Info("VUELTA a esta PC en {0},{1} ({2}); estuvo {3:0.0} s en la otra", pt.X, pt.Y, reason, (Link.NowUs() - enteredAt) / 1e6);
            Flight.Add(Flight.K_MODE, (int)Mode.Local, 0);
            if (reason.Contains("no responde")) Flight.Incident("congelado", reason);
            Native.SetCursorPos(pt.X, pt.Y);
            CursorHider.Show();
        }

        // ------------------------------------------------------------------ drag & drop of files between PCs
        //
        // Origin side: while a left-button Explorer drag reaches the edge, the dragged items (= the selection
        // of the source window) are read, the local drag is cancelled with Esc (so Windows drops nothing
        // here and never moves a file), and the pointer crosses carrying them.
        // Destination side: where the button is released, the folder under the cursor is resolved
        // (desktop, Explorer window or a folder icon), and the origin streams the files straight into it.

        public System.Windows.Threading.Dispatcher Ui;
        public Func<ClipSync> Clip;
        IntPtr dragSourceRoot, injDragSourceRoot;
        string[] readyFiles;
        bool dragResolving;
        uint outDragId, inDragId, probeDragId, answeredProbeId;
        long probeSentAt, answeredAt;
        string probeLabel = "", answeredLabel = "";
        readonly Dictionary<uint, string[]> outgoing = new Dictionary<uint, string[]>();
        static readonly Random rnd = new Random();

        static uint NewId() { lock (rnd) return (uint)rnd.Next(1, int.MaxValue); }
        static string Label(string[] files) { return files.Length == 1 ? System.IO.Path.GetFileName(files[0].TrimEnd('\\')) : files.Length + " elementos"; }

        bool dragNoteLogged;

        void StartDragResolveLocked()
        {
            if (dragResolving || Ui == null) return;
            if (!ShellDrag.DragActive())
            {
                if (!dragNoteLogged) { dragNoteLogged = true; Log.Info("arrastre: botón apretado en el borde, pero no es un arrastre de archivos del Explorador (ventana origen '{0}'): no cruzo", ShellDrag.ClassOf(dragSourceRoot)); }
                return;
            }
            dragResolving = true;
            var src = dragSourceRoot;
            Ui.BeginInvoke(new Action(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var files = ShellDrag.Selection(src);
                Log.Info("arrastre: detectado en '{0}', {1} elemento(s) seleccionados (leídos en {2} ms)", ShellDrag.ClassOf(src), files.Length, sw.ElapsedMilliseconds);
                lock (gate)
                {
                    dragResolving = false;
                    if (files.Length > 0 && localButtons == 1 && mode == Mode.Local) readyFiles = files;
                }
            }));
        }

        /// <summary>Esc ends an Explorer drag without dropping anything anywhere.</summary>
        static void CancelLocalDrag()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                for (int i = 0; i < 3; i++)
                {
                    Inject.Key(0x1B, 0x01, false, false);
                    Inject.Key(0x1B, 0x01, false, true);
                    Thread.Sleep(80);
                    if (!ShellDrag.DragActive()) break;
                }
            });
        }

        void BeginOutgoingDragLocked()
        {
            var files = readyFiles;
            readyFiles = null;
            if (files == null) return;
            uint id = NewId();
            outgoing[id] = files;
            outDragId = id;
            CancelLocalDrag();
            var w = new WBuf(64); w.U32(id); w.U16(files.Length); w.Str(Label(files));
            Send(Ev.DragStart, w);
            Log.Info("ARRASTRE: llevando {0} a la otra PC", Label(files));
        }

        void DropOutgoingLocked()
        {
            uint id = outDragId;
            outDragId = 0;
            var w = new WBuf(16); w.U32(id); w.I32((int)Math.Floor(rx)); w.I32((int)Math.Floor(ry));
            Send(Ev.DragDrop, w);
        }

        void CancelOutgoingLocked(string why)
        {
            if (outDragId == 0) return;
            var w = new WBuf(4); w.U32(outDragId);
            Send(Ev.DragCancel, w);
            outgoing.Remove(outDragId);
            outDragId = 0;
            Log.Info("ARRASTRE cancelado: {0}", why);
        }

        void ProbeRemoteDragLocked()
        {
            long now = Link.NowUs();
            if (now - probeSentAt < 700000) return;
            probeSentAt = now;
            Send(Ev.DragProbe, new WBuf(1));
        }

        void CancelIncomingLocked()
        {
            if (inDragId == 0) return;
            var w = new WBuf(4); w.U32(inDragId);
            Send(Ev.DragCancel, w);
            inDragId = 0;
            DropUi.HideGhost();
            Log.Info("ARRASTRE cancelado (Esc o cruce)");
        }

        void FinishIncomingDragLocked(int x, int y)
        {
            uint id = inDragId;
            inDragId = 0;
            DropUi.HideGhost();
            ResolveAndPull(id, x, y);
        }

        /// <summary>Destination side: work out the folder under (x, y), expect the files there, ask the origin for them.</summary>
        void ResolveAndPull(uint id, int x, int y)
        {
            var ui = Ui;
            if (ui == null) return;
            ui.BeginInvoke(new Action(() =>
            {
                var t = ShellDrag.Resolve(x, y);
                var c = Clip != null ? Clip() : null;
                if (c != null) c.ExpectDrop(id, new ClipSync.DropSpot { Folder = t.Folder, Kind = t.Kind, X = x, Y = y });
                DropUi.Progress(id, x, y, "Preparando la copia…", t.Kind == "descargas" ? "Ahí no hay una carpeta: va a Descargas" : t.Folder, 0, 0);
                var w = new WBuf(4); w.U32(id);
                Send(Ev.DragPull, w);
                Log.Info("ARRASTRE: soltado en {0} [{1}]", t.Folder, t.Kind);
            }));
        }

        void AnswerProbe()
        {
            bool ok;
            IntPtr src;
            lock (gate) { ok = mode == Mode.Controlled && (injButtons & 1) != 0; src = injDragSourceRoot; }
            if (!ok || Ui == null || !ShellDrag.DragActive()) { ReplyProbe(0, ""); return; }
            if (answeredProbeId != 0 && Link.NowUs() - answeredAt < 3000000) { ReplyProbe(answeredProbeId, answeredLabel); return; }
            Ui.BeginInvoke(new Action(() =>
            {
                var files = ShellDrag.Selection(src);
                if (files.Length == 0) { ReplyProbe(0, ""); return; }
                uint id = NewId();
                lock (gate) outgoing[id] = files;
                answeredProbeId = id; answeredAt = Link.NowUs(); answeredLabel = Label(files);
                CancelLocalDrag();
                ReplyProbe(id, answeredLabel);
                Log.Info("ARRASTRE: la otra PC se lleva {0}", answeredLabel);
            }));
        }

        void ReplyProbe(uint id, string label)
        {
            var w = new WBuf(32); w.U32(id); w.Str(label);
            Send(Ev.DragProbeReply, w);
        }

        void HotkeyLocked()
        {
            if (mode == Mode.Remote) { ReturnLocalLocked(park, true, "atajo Ctrl+Alt+F12"); return; }
            var p = peer;
            if (mode != Mode.Local || p == null || link == null || Paused || p.Mons.Length == 0) return;
            POINT c;
            Native.GetCursorPos(out c);
            park = c;
            int mi = Geo.IndexOf(localMons, c.X, c.Y);
            localDpi = mi >= 0 && localMons[mi].Dpi > 0 ? localMons[mi].Dpi : 96;
            double nx, ny;
            if (haveRemotePos) { nx = rx; ny = ry; }
            else
            {
                var pm = p.Mons[Geo.Primary(p.Mons)];
                nx = (pm.L + pm.R) / 2.0; ny = (pm.T + pm.B) / 2.0;
            }
            Geo.Clamp(p.Mons, ref nx, ref ny);
            EnterRemoteLocked(nx, ny);
        }

        void TakeoverLocked()
        {
            if (mode != Mode.Controlled) return;
            mode = Mode.Local;
            ReleaseInjectedLocked();
            var l = link;
            if (l != null) { l.Active = false; l.QueueReliable(Ev.Takeover, null); }
            Interlocked.Increment(ref takeovers);
            Log.Info("RECUPERASTE el control de esta PC (input físico mientras la controlaban)");
        }

        /// <summary>Leaves any cross-PC state without talking to the other side (link lost or replaced).</summary>
        void DropSessionLocked()
        {
            if (mode == Mode.Remote)
            {
                mode = Mode.Local;
                remoteButtons = 0;
                Array.Clear(remoteKeys, 0, remoteKeys.Length);
                Native.SetCursorPos(park.X, park.Y);
                CursorHider.Show();
            }
            else if (mode == Mode.Controlled)
            {
                ReleaseInjectedLocked();
                mode = Mode.Local;
            }
            var l = link;
            if (l != null) l.Active = false;
        }

        void ReleaseInjectedLocked()
        {
            var keys = new List<int[]>();
            for (int vk = 0; vk < 256; vk++)
                if (injKeys[vk]) { injKeys[vk] = false; keys.Add(new[] { vk, (int)injScan[vk], injExt[vk] ? 1 : 0 }); }
            int buttons = injButtons, x = injX, y = injY;
            injButtons = 0;
            if (keys.Count == 0 && buttons == 0) return;
            // Injected off-thread: this may run inside a hook callback.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                foreach (var k in keys) Inject.Key(k[0], k[1], k[2] == 1, true);
                for (int b = 0; b < 5; b++) if ((buttons & (1 << b)) != 0) Inject.Button(b, false, x, y);
            });
        }

        void Send(byte type, WBuf payload)
        {
            var l = link;
            if (l != null) l.QueueReliable(type, payload.ToArray());
        }

        void SendKeyLocked(int vk, int scan, bool ext, bool up)
        {
            var w = new WBuf(5);
            w.U16(vk); w.U16(scan); w.U8((up ? 1 : 0) | (ext ? 2 : 0));
            Send(Ev.Key, w);
        }

        void SendButtonLocked(int btn, bool down)
        {
            var w = new WBuf(10);
            w.U8(btn); w.U8(down ? 1 : 0);
            w.I32((int)Math.Floor(rx)); w.I32((int)Math.Floor(ry));
            Send(Ev.Button, w);
        }

        void ClampLocal(ref int x, ref int y)
        {
            double dx = x, dy = y;
            Geo.Clamp(localMons, ref dx, ref dy);
            x = (int)Math.Floor(dx); y = (int)Math.Floor(dy);
        }

        // ------------------------------------------------------------------ link callbacks

        public HelloInfo GetHello()
        {
            var h = new HelloInfo();
            h.Name = cfg.Name;
            h.Mons = localMons;
            h.PeerSide = cfg.Side;
            h.SideStamp = cfg.SideStamp;
            h.Elevated = IsElevated != null && IsElevated();
            h.TcpPort = TcpPort;
            h.NetLabel = Diag.NetLabel();
            return h;
        }

        public void OnPeerUp(PeerInfo p)
        {
            lock (gate) { DropSessionLocked(); peer = p; edgesValid = false; haveRemotePos = false; }
            SyncSide(p, true);
            var n = Notify;
            if (n != null) n("Conectado con " + p.Name);
        }

        public void OnPeerInfo(PeerInfo p)
        {
            lock (gate) { peer = p; edgesValid = false; }
            SyncSide(p, false);
        }

        public void OnPeerDown()
        {
            PeerInfo old;
            if (mode != Mode.Local) Flight.Incident("desconexion", "se perdió la conexión mientras usabas la otra PC");
            lock (gate) { old = peer; DropSessionLocked(); peer = null; edgesValid = false; }
            var n = Notify;
            if (n != null && old != null) n("Se desconectó " + old.Name);
        }

        /// <summary>The layout is set on either PC; the most recent choice wins on both.</summary>
        void SyncSide(PeerInfo p, bool firstContact)
        {
            bool announce = false;
            lock (cfg)
            {
                if (p.SideStamp > cfg.SideStamp)
                {
                    cfg.Side = Geo.Opposite(p.SideOfMe);
                    cfg.SideStamp = p.SideStamp;
                    cfg.Save();
                }
                else if (firstContact && cfg.SideStamp == 0 && p.SideStamp == 0)
                {
                    var l = link;
                    if (l != null && l.Session > p.Session) { cfg.SideStamp = 1; cfg.Save(); announce = true; }
                }
            }
            InvalidateEdges();
            var lk = link;
            if (announce && lk != null) lk.AnnounceNow();
        }

        public void OnReliable(byte type, byte[] d)
        {
            var r = new RBuf(d, 0, d.Length);
            switch (type)
            {
                case Ev.Enter:
                    {
                        int x = r.I32(), y = r.I32();
                        lock (gate)
                        {
                            if (mode == Mode.Remote) ReturnLocalLocked(park, false, "conflicto: las dos cruzaron a la vez");
                            mode = Mode.Controlled;
                            takeoverAccum = 0;
                            var l = link;
                            if (l != null) l.Active = true;
                            ClampLocal(ref x, ref y);
                            injX = x; injY = y;
                        }
                        Inject.Move(x, y);
                        break;
                    }
                case Ev.Leave:
                    lock (gate)
                    {
                        if (mode == Mode.Controlled)
                        {
                            ReleaseInjectedLocked();
                            mode = Mode.Local;
                            var l = link;
                            if (l != null) l.Active = false;
                        }
                    }
                    break;
                case Ev.Button:
                    {
                        int b = r.U8();
                        bool down = r.U8() == 1;
                        int x = r.I32(), y = r.I32();
                        lock (gate)
                        {
                            if (mode != Mode.Controlled || b > 4) return;
                            ClampLocal(ref x, ref y);
                            injX = x; injY = y;
                            SetBit(ref injButtons, b, down);
                            if (b == 0 && down) { injDragSourceRoot = ShellDrag.RootAt(x, y); answeredProbeId = 0; }
                        }
                        Inject.Button(b, down, x, y);
                        break;
                    }
                case Ev.Wheel:
                    {
                        bool hz = r.U8() == 1;
                        int delta = r.I16();
                        if (mode != Mode.Controlled) return;
                        Inject.Wheel(hz, delta);
                        break;
                    }
                case Ev.Key:
                    {
                        int vk = r.U16() & 0xFF, scan = r.U16(), f = r.U8();
                        bool up = (f & 1) != 0, ext = (f & 2) != 0;
                        lock (gate)
                        {
                            if (mode != Mode.Controlled) return;
                            injKeys[vk] = !up; injScan[vk] = (ushort)scan; injExt[vk] = ext;
                        }
                        Inject.Key(vk, scan, ext, up);
                        break;
                    }
                case Ev.LogLine:
                    {
                        var pp = peer;
                        string txt = System.Text.Encoding.UTF8.GetString(d);
                        if (txt.StartsWith("CSV|"))
                        {
                            var mr = MinuteRow.Parse(txt.Substring(4));
                            if (mr != null)
                            {
                                mr.Time = DateTime.Now;
                                Metrics.Append(mr.ToCsv());
                                Log.Info("RESUMEN {0}: rtt p95 {1:0} ms, router {2} ms, señal {3}%, canal {4}, vecinos {5}+{6}, causa={7}", mr.Pc, mr.RttP95, mr.RouterP95, mr.Signal, mr.Channel, mr.SameCh, mr.Overlap, mr.Cause);
                            }
                        }
                        else Log.Info("RESUMEN {0}: {1}", pp != null ? pp.Name : "otra PC", txt);
                        break;
                    }
                case Ev.AppMsg:
                    Api.FromPeer(r.Str(), r.Str());
                    break;
                case Ev.DragStart:
                    {
                        r.U32(); r.U16();
                        DropUi.ShowGhost(r.Str());
                        break;
                    }
                case Ev.DragDrop:
                    {
                        uint id = r.U32();
                        int x = r.I32(), y = r.I32();
                        ClampLocal(ref x, ref y);
                        DropUi.HideGhost();
                        ResolveAndPull(id, x, y);
                        break;
                    }
                case Ev.DragCancel:
                    {
                        uint id = r.U32();
                        DropUi.HideGhost();
                        lock (gate) { outgoing.Remove(id); if (inDragId == id) inDragId = 0; }
                        break;
                    }
                case Ev.DragProbe:
                    AnswerProbe();
                    break;
                case Ev.DragProbeReply:
                    {
                        uint id = r.U32();
                        string label = r.Str();
                        lock (gate) { if (id != 0 && mode == Mode.Remote && remoteButtons == 1) { probeDragId = id; probeLabel = label; } }
                        break;
                    }
                case Ev.DragPull:
                    {
                        uint id = r.U32();
                        string[] files = null;
                        lock (gate) { if (outgoing.TryGetValue(id, out files)) outgoing.Remove(id); }
                        var c = Clip != null ? Clip() : null;
                        if (files != null && c != null) c.SendDrop(id, files);
                        break;
                    }
                case Ev.Takeover:
                    lock (gate) { if (mode == Mode.Remote) ReturnLocalLocked(park, false, "la otra PC tomó el control (la tocaron)"); }
                    break;
            }
        }

        public void OnMove(int x, int y)
        {
            lock (gate)
            {
                if (mode != Mode.Controlled) return;
                ClampLocal(ref x, ref y);
                injX = x; injY = y;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Inject.Move(x, y);
            Diag.InjectTime(System.Diagnostics.Stopwatch.GetTimestamp() - t0);
        }

        public void OnTick(long now)
        {
            if (now - lastMonCheck > 2000000)
            {
                lastMonCheck = now;
                var m = Geo.Enumerate();
                if (!Geo.Same(m, localMons))
                {
                    lock (gate) { localMons = m; edgesValid = false; }
                    var lk = link;
                    if (lk != null) lk.AnnounceNow();
                }
            }
            RefreshLocalKeys(now);
            if (lastTickUs != 0 && mode != Mode.Local) Interlocked.Add(ref activeUs, now - lastTickUs);
            lastTickUs = now;
            if (now - lastHookCheck > 3000000)
            {
                lastHookCheck = now;
                var lii = new LASTINPUTINFO { cbSize = 8 };
                if (GetLastInputInfo(ref lii) && (int)(lii.dwTime - (uint)lastHookTick) > 1500 && hookThreadId != 0)
                {
                    int gap = (int)(lii.dwTime - (uint)lastHookTick);
                    if (gap > 60000)
                        // Long idle, then input Cruce can't see: typing the password on the lock screen (a secure desktop). Normal.
                        Log.Info("volviste después de {0:0} min sin usar la PC (pantalla bloqueada o segura). Reengancho por las dudas.", gap / 60000.0);
                    else
                    {
                        Log.Info("HOOK MUERTO: hubo input que la captura no vio ({0} ms). En primer plano: {1}. Reenganchando.", gap, ForegroundInfo());
                        Flight.Incident("hook_muerto", "Windows dejó de pasarle el mouse/teclado a Cruce (pantalla segura, UAC o hook desenganchado)");
                    }
                    lastHookTick = Environment.TickCount;
                    Native.PostThreadMessage(hookThreadId, WM_REHOOK, IntPtr.Zero, IntPtr.Zero);
                }
            }
            if (lastSummary == 0) lastSummary = now;
            if (now - lastSummary > 60000000)
            {
                lastSummary = now;
                // Built on the thread pool: process enumeration, file I/O etc. must never delay this timer thread.
                ThreadPool.QueueUserWorkItem(_ => { try { BuildMinute(); } catch (Exception ex) { Log.Error(ex, "resumen"); } });
            }
            if (mode == Mode.Local) return;
            var l = link;
            if (l == null) return;
            long silent = Math.Min(l.SinceHeardUs(now), mode == Mode.Remote ? now - enteredAt : long.MaxValue);
            if (silent <= SilentReturnUs) return;
            lock (gate)
            {
                if (mode == Mode.Remote) ReturnLocalLocked(park, false, "la otra PC no responde hace 3 s");
                else if (mode == Mode.Controlled) { ReleaseInjectedLocked(); mode = Mode.Local; l.Active = false; }
            }
        }

        public static Func<double> TakeUiHang;

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

        /// <summary>Which program is in front and whether it runs as admin (Windows hides its input from non-admin apps).</summary>
        static string ForegroundInfo()
        {
            try
            {
                uint pid;
                GetWindowThreadProcessId(GetForegroundWindow(), out pid);
                if (pid == 0) return "nada (pantalla segura: bloqueo o UAC)";
                using (var p = System.Diagnostics.Process.GetProcessById((int)pid))
                {
                    string name = p.ProcessName;
                    bool admin = false;
                    try { var m = p.MainModule; } catch (System.ComponentModel.Win32Exception) { admin = !Autostart.IsAdmin(); }
                    return name + (admin ? " (corre como ADMIN: Cruce sin admin no ve el input sobre esa ventana)" : "");
                }
            }
            catch (Exception ex) { return "desconocido (" + ex.Message + ")"; }
        }

        void BuildMinute()
        {
            var lk2 = link;
            var row = new MinuteRow { Time = DateTime.Now, Pc = cfg.Name };
            double cpuSys, cpuApp;
            Diag.TakeCpu(out cpuSys, out cpuApp);
            int gwCount, gwFails; double gwAvg; long gwP95, gwMax;
            Diag.TakeRouter(out gwCount, out gwAvg, out gwP95, out gwMax, out gwFails);
            Diag.TakeExtra(row);
            Diag.TakePower(row);
            WifiNative.TakeMinute(row);
            long act = Interlocked.Exchange(ref activeUs, 0);
            double ui = TakeUiHang != null ? TakeUiHang() : 0;
            int incidents = Interlocked.Exchange(ref Flight.IncidentsThisMinute, 0);
            if (lk2 == null || peer == null || !lk2.TakeMinute(row)) return;
            long cr = Crossings;
            row.ActiveS = act / 1e6; row.Crossings = (int)(cr - lastCrossings); lastCrossings = cr;
            row.CpuSys = cpuSys; row.CpuApp = cpuApp;
            row.RouterAvg = gwAvg; row.RouterP95 = gwP95; row.RouterMax = gwMax; row.RouterFails = gwFails;
            var wf = Diag.Wifi;
            if (wf.OnWifi) { row.Signal = wf.Signal; row.Band = wf.Band; row.Channel = wf.Channel; row.RxMbps = wf.RxMbps; row.SameCh = wf.SameChannel; row.Overlap = wf.Overlapping; row.NeighborMax = wf.NeighborMax; }
            if (WifiNative.Available) row.LowLatency = WifiNative.LowLatency ? 1 : 0;
            row.Stalls = Interlocked.Exchange(ref Diag.Stalls, 0); row.SlowHooks = Interlocked.Exchange(ref Diag.SlowHooks, 0); row.Blocked = Interlocked.Exchange(ref Diag.InjectBlocked, 0);
            row.Incidents = incidents; row.UiHangMs = ui;
            row.Classify();
            string csv = row.ToCsv();
            Metrics.Append(csv);
            Func<double, string> v = d => d < 0 ? "–" : d.ToString("0.#");
            Log.Info("RESUMEN {0}: ida y vuelta {1}/{2}/{3} ms (prom/p95/max), tramo red p95 {4} ms, tirones {5} (máx {6} ms), pérdida {7}%, router {8}/{9} ms, internet p95 {10} ms, tráfico {11}/{12} KB/s, usando {13} s, cruces {14}, {15}, cpu {16}% [{17}], causa={18}",
                cfg.Name, v(row.RttAvg), v(row.RttP95), v(row.RttMax), v(row.OwdP95), row.Stutters, v(row.StutterMaxMs), v(row.LossPct), v(row.RouterAvg), v(row.RouterP95), v(row.InetP95),
                v(row.NetRxKBs), v(row.NetTxKBs), v(row.ActiveS), row.Crossings, Diag.WifiShort(), v(row.CpuSys), row.TopCpu, row.Cause);
            lk2.QueueReliable(Ev.LogLine, System.Text.Encoding.UTF8.GetBytes("CSV|" + csv));
        }

        public void Dispose()
        {
            lock (gate) DropSessionLocked();
            if (hookThreadId != 0) Native.PostThreadMessage(hookThreadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            if (hookThread != null) hookThread.Join(1000);
            CursorHider.Restore();
        }
    }
}
