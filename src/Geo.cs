using System;
using System.Collections.Generic;
using System.Linq;

namespace Cruce
{
    /// <summary>A monitor rectangle in physical pixels; Right/Bottom are exclusive.</summary>
    public struct Mon
    {
        public int L, T, R, B, Dpi;
        public int W { get { return R - L; } }
        public int H { get { return B - T; } }
        public bool Contains(double x, double y) { return x >= L && x < R && y >= T && y < B; }
    }

    /// <summary>Where the other PC sits relative to this one.</summary>
    public enum Side : byte { Left = 0, Right = 1, Top = 2, Bottom = 3 }

    public static class Geo
    {
        public static Side Opposite(Side s)
        {
            switch (s)
            {
                case Side.Left: return Side.Right;
                case Side.Right: return Side.Left;
                case Side.Top: return Side.Bottom;
                default: return Side.Top;
            }
        }

        /// <summary>True when crossing happens along the X axis.</summary>
        public static bool Horiz(Side s) { return s == Side.Left || s == Side.Right; }
        public static double Cross(Side s, double x, double y) { return Horiz(s) ? x : y; }
        public static double Along(Side s, double x, double y) { return Horiz(s) ? y : x; }

        public static void Compose(Side s, double cross, double along, out double x, out double y)
        {
            if (Horiz(s)) { x = cross; y = along; } else { x = along; y = cross; }
        }

        public static int IndexOf(Mon[] ms, double x, double y)
        {
            for (int i = 0; i < ms.Length; i++) if (ms[i].Contains(x, y)) return i;
            return -1;
        }

        /// <summary>Moves (x,y) to the nearest point that lies on some monitor. Returns that monitor's index.</summary>
        public static int Clamp(Mon[] ms, ref double x, ref double y)
        {
            int i = IndexOf(ms, x, y);
            if (i >= 0) return i;
            double best = double.MaxValue, bx = x, by = y;
            int bi = -1;
            for (int k = 0; k < ms.Length; k++)
            {
                var m = ms[k];
                double cx = Math.Max(m.L, Math.Min(x, m.R - 0.001));
                double cy = Math.Max(m.T, Math.Min(y, m.B - 0.001));
                double d = (cx - x) * (cx - x) + (cy - y) * (cy - y);
                if (d < best) { best = d; bx = cx; by = cy; bi = k; }
            }
            if (bi >= 0) { x = bx; y = by; }
            return bi;
        }

        public static Mon[] Enumerate()
        {
            var list = new List<Mon>();
            MonitorEnumProc cb = (IntPtr h, IntPtr hdc, ref RECT rc, IntPtr d) =>
            {
                var info = new MONITORINFO();
                info.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(MONITORINFO));
                RECT r = rc;
                if (Native.GetMonitorInfo(h, ref info)) r = info.rcMonitor;
                uint dx = 96, dy = 96;
                try { if (Native.GetDpiForMonitor(h, 0, out dx, out dy) != 0) dx = 96; } catch { dx = 96; }
                var m = new Mon();
                m.L = r.Left; m.T = r.Top; m.R = r.Right; m.B = r.Bottom; m.Dpi = (int)dx;
                list.Add(m);
                return true;
            };
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
            GC.KeepAlive(cb);
            return list.OrderBy(m => m.L).ThenBy(m => m.T).ToArray();
        }

        public static bool Same(Mon[] a, Mon[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i].L != b[i].L || a[i].T != b[i].T || a[i].R != b[i].R || a[i].B != b[i].B || a[i].Dpi != b[i].Dpi) return false;
            return true;
        }

        /// <summary>Index of the primary monitor (the one containing 0,0), or 0.</summary>
        public static int Primary(Mon[] ms)
        {
            int i = IndexOf(ms, 0, 0);
            return i < 0 ? 0 : i;
        }
    }

    /// <summary>The outer edge of a set of monitors on one side, e.g. the right-most edge of a desktop.</summary>
    public sealed class Edge
    {
        public readonly Side Side;
        public readonly int Boundary;   // Left/Top: first pixel inside. Right/Bottom: first pixel outside.
        public readonly int A0, A1;     // span along the edge [A0, A1)
        readonly Mon[] touch;

        Edge(Side s, int boundary, int a0, int a1, Mon[] t) { Side = s; Boundary = boundary; A0 = a0; A1 = a1; touch = t; }

        static int EdgeOf(Mon m, Side s)
        {
            switch (s)
            {
                case Side.Left: return m.L;
                case Side.Right: return m.R;
                case Side.Top: return m.T;
                default: return m.B;
            }
        }

        public static Edge Of(Mon[] ms, Side s)
        {
            if (ms == null || ms.Length == 0) return null;
            int b = (s == Side.Left || s == Side.Top) ? ms.Min(m => EdgeOf(m, s)) : ms.Max(m => EdgeOf(m, s));
            var t = ms.Where(m => EdgeOf(m, s) == b).ToArray();
            bool h = Geo.Horiz(s);
            int a0 = t.Min(m => h ? m.T : m.L);
            int a1 = t.Max(m => h ? m.B : m.R);
            return new Edge(s, b, a0, a1, t);
        }

        public bool Touches(Mon m) { return EdgeOf(m, Side) == Boundary; }

        public bool Beyond(double c)
        {
            return (Side == Side.Right || Side == Side.Bottom) ? c >= Boundary : c < Boundary;
        }

        /// <summary>How far past the edge c is (positive = beyond).</summary>
        public double Overflow(double c)
        {
            return (Side == Side.Right || Side == Side.Bottom) ? c - (Boundary - 1) : Boundary - c;
        }

        /// <summary>Cross coordinate "depth" pixels inside the edge.</summary>
        public double InsideAt(double depth)
        {
            depth = Math.Max(0, depth);
            return (Side == Side.Right || Side == Side.Bottom) ? Boundary - 1 - depth : Boundary + depth;
        }

        public bool AlongTouching(double along)
        {
            bool h = Geo.Horiz(Side);
            foreach (var m in touch)
            {
                int a0 = h ? m.T : m.L, a1 = h ? m.B : m.R;
                if (along >= a0 && along < a1) return true;
            }
            return false;
        }

        public bool NearCorner(double along, int guard)
        {
            return along < A0 + guard || along >= A1 - guard;
        }

        /// <summary>Proportionally maps a position along one edge onto another edge.</summary>
        public static double Map(double along, Edge from, Edge to)
        {
            double t = (along - from.A0) / Math.Max(1.0, from.A1 - from.A0);
            t = Math.Max(0, Math.Min(1, t));
            return to.A0 + t * (to.A1 - to.A0 - 0.001);
        }
    }
}
