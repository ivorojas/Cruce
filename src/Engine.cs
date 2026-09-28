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
        public const byte Enter = 1, Leave = 2, Button = 3, Wheel = 4, Key = 5, Takeover = 6;
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
        const long WarmUs = 1500000, SilentReturnUs = 1200000;

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

        long lastMonCheck;
        Thread hookThread;
        uint hookThreadId;
        IntPtr mouseHook, kbHook;
        LowLevelProc mouseProc, kbProc;

        public volatile bool Paused;
        public event Action<string> Notify;
        public Func<bool> IsElevated;
        public long Crossings;

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
            if (nCode >= 0)
            {
                var m = (MSLLHOOKSTRUCT*)l;
                if ((m->flags & Native.LLMHF_INJECTED) == 0 || (cfg.TestAcceptInjected && m->dwExtraInfo != Tag))
                {
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
            if (nCode >= 0)
            {
                var k = (KBDLLHOOKSTRUCT*)l;
                if ((k->flags & Native.LLKHF_INJECTED) == 0 || (cfg.TestAcceptInjected && k->dwExtraInfo != Tag))
                {
                    bool block = false;
                    try { block = OnKey((int)(k->vkCode & 0xFF), (int)k->scanCode, (k->flags & Native.LLKHF_EXTENDED) != 0, (k->flags & Native.LLKHF_UP) != 0); }
                    catch (Exception ex) { Log.Error(ex, "kb hook"); }
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
                        if (btn >= 0) { SetBit(ref localButtons, btn, down); return false; }
                        if (msg == Native.WM_MOUSEMOVE && peer != null && link != null && !Paused) return TryCrossLocked(x, y);
                        return false;

                    case Mode.Remote:
                        if (msg == Native.WM_MOUSEMOVE) { RemoteMoveLocked(x, y); return true; }
                        if (btn >= 0)
                        {
                            int bit = 1 << btn;
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
                        return false;

                    case Mode.Remote:
                        if (localKeys[vk]) { if (up) localKeys[vk] = false; return false; } // held since before crossing
                        if ((scan & 0x200) != 0) return true; // AltGr's synthetic LCtrl; the other PC makes its own
                        remoteKeys[vk] = !up;
                        remoteScan[vk] = (ushort)scan;
                        remoteExt[vk] = ext;
                        SendKeyLocked(vk, scan, ext, up);
                        return true;

                    default:
                        localKeys[vk] = !up;
                        TakeoverLocked();
                        return false;
                }
            }
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
            if (!le.Beyond(cross) || localButtons != 0) return false;
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
            return true;
        }

        void EnterRemoteLocked(double nx, double ny)
        {
            var l = link;
            if (l == null) return;
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
            if (re != null && le != null && remoteButtons == 0 && ri >= 0 && re.Touches(p.Mons[ri]))
            {
                Side s = le.Side;
                double cr = Geo.Cross(s, nx, ny);
                double al = Geo.Along(s, nx, ny);
                if (re.Beyond(cr) && !re.NearCorner(al, CornerGuard))
                {
                    double over = Math.Min(re.Overflow(cr) / k, 12);
                    double la = Edge.Map(al, re, le);
                    double lx, ly;
                    Geo.Compose(s, le.InsideAt(over - 1), la, out lx, out ly);
                    Geo.Clamp(localMons, ref lx, ref ly);
                    ReturnLocalLocked(new POINT((int)lx, (int)ly), true);
                    return;
                }
            }
            Geo.Clamp(p.Mons, ref nx, ref ny);
            rx = nx; ry = ny;
            l.SetMove((int)Math.Floor(rx), (int)Math.Floor(ry));
        }

        void ReturnLocalLocked(POINT pt, bool sendLeave)
        {
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
            Native.SetCursorPos(pt.X, pt.Y);
            CursorHider.Show();
        }

        void HotkeyLocked()
        {
            if (mode == Mode.Remote) { ReturnLocalLocked(park, true); return; }
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
                            if (mode == Mode.Remote) ReturnLocalLocked(park, false); // both crossed at once: yield
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
                case Ev.Takeover:
                    lock (gate) { if (mode == Mode.Remote) ReturnLocalLocked(park, false); }
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
            Inject.Move(x, y);
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
            if (mode == Mode.Local) return;
            var l = link;
            if (l == null || l.SinceHeardUs(now) <= SilentReturnUs) return;
            lock (gate)
            {
                if (mode == Mode.Remote) { ReturnLocalLocked(park, false); Log.Info("peer silent: pointer back home"); }
                else if (mode == Mode.Controlled) { ReleaseInjectedLocked(); mode = Mode.Local; l.Active = false; }
            }
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
