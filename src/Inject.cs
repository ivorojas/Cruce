using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Cruce
{
    /// <summary>Synthesizes input on this PC (the side being controlled).</summary>
    public static class Inject
    {
        const uint MOVE = 0x1, ABS = 0x8000, VDESK = 0x4000, NOCOALESCE = 0x2000, WHEEL = 0x800, HWHEEL = 0x1000;
        const uint K_EXT = 0x1, K_UP = 0x2, K_SCAN = 0x8;
        static readonly uint[] Down = { 0x2, 0x8, 0x20, 0x80, 0x80 };
        static readonly uint[] Up = { 0x4, 0x10, 0x40, 0x100, 0x100 };
        static readonly int Size = Marshal.SizeOf(typeof(INPUT));
        static long lastBlockedLog;

        static void Send(INPUT[] i)
        {
            if (Native.SendInput((uint)i.Length, i, Size) == 0)
            {
                int err = Marshal.GetLastWin32Error();
                long now = Link.NowUs();
                if (now - lastBlockedLog > 5000000)
                {
                    lastBlockedLog = now;
                    Log.Info("INYECCIÓN BLOQUEADA por Windows (error {0}): hay una ventana de administrador o de seguridad en primer plano y Cruce {1}.", err, Autostart.IsAdmin() ? "tiene admin (pantalla segura: UAC o bloqueo)" : "NO tiene admin");
                }
            }
        }

        /// <summary>Pixel -> 0..65535 virtual-desktop coordinate that Windows maps back to exactly that pixel.</summary>
        public static void Norm(int x, int y, out int nx, out int ny)
        {
            int vx = Native.GetSystemMetrics(76), vy = Native.GetSystemMetrics(77);
            int vw = Math.Max(1, Native.GetSystemMetrics(78)), vh = Math.Max(1, Native.GetSystemMetrics(79));
            nx = (int)(((long)(x - vx) * 65536 + vw - 1) / vw);
            ny = (int)(((long)(y - vy) * 65536 + vh - 1) / vh);
        }

        static void Mouse(int x, int y, uint flags, uint data)
        {
            var i = new INPUT[1];
            i[0].type = 0;
            int nx, ny;
            Norm(x, y, out nx, out ny);
            i[0].u.mi.dx = nx;
            i[0].u.mi.dy = ny;
            i[0].u.mi.dwFlags = flags;
            i[0].u.mi.mouseData = data;
            i[0].u.mi.dwExtraInfo = Engine.Tag;
            Send(i);
        }

        public static void Move(int x, int y) { Mouse(x, y, MOVE | ABS | VDESK | NOCOALESCE, 0); }

        public static void Button(int b, bool down, int x, int y)
        {
            if (b < 0 || b > 4) return;
            uint data = b == 3 ? 1u : b == 4 ? 2u : 0u;
            Mouse(x, y, MOVE | ABS | VDESK | (down ? Down[b] : Up[b]), data);
        }

        public static void Wheel(bool horizontal, int delta)
        {
            var i = new INPUT[1];
            i[0].type = 0;
            i[0].u.mi.dwFlags = horizontal ? HWHEEL : WHEEL;
            i[0].u.mi.mouseData = unchecked((uint)delta);
            i[0].u.mi.dwExtraInfo = Engine.Tag;
            Send(i);
        }

        /// <summary>
        /// Keys go by scan code so the controlled PC's own keyboard layout interprets them
        /// (ñ, accents, AltGr all behave as if typed locally). A few keys whose scan code is
        /// ambiguous (Pause, media/browser keys) are sent by virtual-key instead.
        /// </summary>
        public static void Key(int vk, int scan, bool ext, bool up)
        {
            var i = new INPUT[1];
            i[0].type = 1;
            bool byVk = scan == 0 || vk == 0x13 || (vk >= 0xA6 && vk <= 0xB7);
            i[0].u.ki.wVk = (ushort)(byVk ? vk : 0);
            i[0].u.ki.wScan = (ushort)(scan & 0xFF);
            i[0].u.ki.dwFlags = (byVk ? 0 : K_SCAN) | (ext ? K_EXT : 0) | (up ? K_UP : 0);
            i[0].u.ki.dwExtraInfo = Engine.Tag;
            Send(i);
        }
    }

    /// <summary>
    /// Hides the pointer on this PC while you are working on the other one, by swapping the
    /// system cursors for a blank one. A single worker applies the latest wanted state, so
    /// rapid hide/show calls can never be applied out of order.
    /// </summary>
    public static class CursorHider
    {
        static readonly uint[] Ids = { 32512, 32513, 32514, 32515, 32516, 32642, 32643, 32644, 32645, 32646, 32648, 32649, 32650, 32651 };
        static readonly AutoResetEvent wake = new AutoResetEvent(false);
        static volatile bool want;
        static bool hidden;
        static Thread worker;

        public static void Init()
        {
            Restore();
            worker = new Thread(Loop) { IsBackground = true, Name = "cruce-cursor" };
            worker.Start();
        }

        public static void Hide() { want = true; wake.Set(); }
        public static void Show() { want = false; wake.Set(); }

        static void Loop()
        {
            while (true)
            {
                wake.WaitOne();
                bool w = want;
                if (w == hidden) continue;
                try
                {
                    if (w)
                    {
                        var andMask = new byte[128];
                        var xorMask = new byte[128];
                        for (int i = 0; i < andMask.Length; i++) andMask[i] = 0xFF;
                        foreach (var id in Ids)
                        {
                            var c = Native.CreateCursor(IntPtr.Zero, 0, 0, 32, 32, andMask, xorMask);
                            if (c != IntPtr.Zero) Native.SetSystemCursor(c, id);
                        }
                    }
                    else Restore();
                    hidden = w;
                }
                catch (Exception ex) { Log.Error(ex, "cursor"); }
            }
        }

        /// <summary>Reloads the user's cursor scheme. Safe to call any time (also on crash/exit).</summary>
        public static void Restore()
        {
            Native.SystemParametersInfo(0x57 /* SPI_SETCURSORS */, 0, IntPtr.Zero, 0);
            hidden = false;
        }
    }
}
