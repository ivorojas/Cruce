using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace Cruce
{
    /// <summary>Draws the app mark: a pointer about to cross a dashed boundary, on a blue-violet tile.</summary>
    public static class IconArt
    {
        public static readonly Color Green = Color.FromArgb(61, 220, 151);
        public static readonly Color Amber = Color.FromArgb(255, 181, 71);
        public static readonly Color Gray = Color.FromArgb(140, 147, 166);
        public static readonly Color Red = Color.FromArgb(255, 107, 107);

        static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Bitmap Draw(int size, Color? dot)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                float s = size / 32f;
                var tile = new RectangleF(1 * s, 1 * s, 30 * s, 30 * s);
                using (var path = Round(tile, 8 * s))
                using (var br = new LinearGradientBrush(tile, Color.FromArgb(76, 125, 255), Color.FromArgb(139, 92, 246), 45f))
                    g.FillPath(br, path);

                // boundary between the two PCs
                using (var pen = new Pen(Color.FromArgb(size < 24 ? 170 : 150, 255, 255, 255), Math.Max(1.2f, 2f * s)))
                {
                    if (size >= 24) pen.DashPattern = new[] { 1.6f, 1.2f };
                    g.DrawLine(pen, 24.5f * s, 6.5f * s, 24.5f * s, 25.5f * s);
                }

                // pointer
                var pts = new[]
                {
                    new PointF(8.5f, 6.5f), new PointF(8.5f, 24f), new PointF(12.8f, 20f), new PointF(15.7f, 26.4f),
                    new PointF(18.9f, 25f), new PointF(16.1f, 18.8f), new PointF(21.8f, 18.8f)
                };
                for (int i = 0; i < pts.Length; i++) pts[i] = new PointF(pts[i].X * s, pts[i].Y * s);
                var shadow = new PointF[pts.Length];
                for (int i = 0; i < pts.Length; i++) shadow[i] = new PointF(pts[i].X + 0.7f * s, pts[i].Y + 0.9f * s);
                using (var sb = new SolidBrush(Color.FromArgb(70, 20, 16, 60))) g.FillPolygon(sb, shadow);
                using (var wb = new SolidBrush(Color.White)) g.FillPolygon(wb, pts);

                if (dot.HasValue)
                {
                    float r = 5.6f * s, cx = 25.6f * s, cy = 25.6f * s;
                    using (var ob = new SolidBrush(Color.FromArgb(24, 26, 34))) g.FillEllipse(ob, cx - r - 1.3f * s, cy - r - 1.3f * s, 2 * (r + 1.3f * s), 2 * (r + 1.3f * s));
                    using (var db = new SolidBrush(dot.Value)) g.FillEllipse(db, cx - r, cy - r, 2 * r, 2 * r);
                }
            }
            return bmp;
        }

        public static Icon MakeIcon(int size, Color? dot)
        {
            using (var b = Draw(size, dot)) return Icon.FromHandle(b.GetHicon());
        }

        public static System.Windows.Media.ImageSource Wpf(int size)
        {
            using (var b = Draw(size, null))
            using (var ms = new MemoryStream())
            {
                b.Save(ms, ImageFormat.Png);
                ms.Position = 0;
                var img = new System.Windows.Media.Imaging.BitmapImage();
                img.BeginInit();
                img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                img.StreamSource = ms;
                img.EndInit();
                img.Freeze();
                return img;
            }
        }

        /// <summary>Multi-resolution .ico (PNG-compressed entries) for the executable.</summary>
        public static void WriteIco(string path)
        {
            var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
            var pngs = new List<byte[]>();
            foreach (var sz in sizes)
                using (var b = Draw(sz, null))
                using (var ms = new MemoryStream()) { b.Save(ms, ImageFormat.Png); pngs.Add(ms.ToArray()); }
            using (var fs = File.Create(path))
            using (var w = new BinaryWriter(fs))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0); w.Write((byte)0);
                    w.Write((short)1); w.Write((short)32);
                    w.Write(pngs[i].Length); w.Write(offset);
                    offset += pngs[i].Length;
                }
                foreach (var p in pngs) w.Write(p);
            }
        }
    }
}
