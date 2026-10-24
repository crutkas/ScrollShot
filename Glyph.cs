using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ScrollShot;

static partial class Program
{
    // Tray glyph: viewfinder corners around a "scroll down" double chevron, drawn as vectors
    // at the exact tray size so it stays crisp at any display scaling.
    static class Glyph
    {
        public static bool LightTaskbar =>
            Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int v && v == 1;

        public static Color Accent
        {
            get
            {
                // AccentColor is stored as ABGR.
                if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null) is int abgr)
                    return Color.FromArgb(255, abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF);
                return Color.FromArgb(0, 120, 212);
            }
        }

        public static Icon MakeIcon(bool busy)
        {
            int size = GetSystemMetrics(49 /*SM_CXSMICON*/);
            if (size <= 0) size = 16;
            bool light = LightTaskbar;
            var fg = light ? Color.FromArgb(28, 28, 28) : Color.White;
            using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) Draw(g, size, fg, busy ? AccentFor(light) : fg);
            var h = bmp.GetHicon();
            var icon = (Icon)Icon.FromHandle(h).Clone();
            DestroyIcon(h);
            return icon;
        }

        // Accent tuned for contrast against the taskbar.
        static Color AccentFor(bool lightTaskbar)
        {
            var a = Accent;
            float l = (0.299f * a.R + 0.587f * a.G + 0.114f * a.B) / 255f;
            if (!lightTaskbar && l < 0.45f) return Blend(a, Color.White, 0.35f);
            if (lightTaskbar && l > 0.6f) return Blend(a, Color.Black, 0.3f);
            return a;
        }

        static Color Blend(Color a, Color b, float t) =>
            Color.FromArgb(255, (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        public static void Draw(Graphics g, float size, Color frame, Color chevrons)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var saved = g.Save();
            float s = size / 16f;
            g.ScaleTransform(s, s);

            // At 16px, 1px strokes on half-pixel coordinates keep the frame crisp.
            bool tiny = size <= 16;
            float w = tiny ? 1f : 1.3f;
            using var pen = new Pen(frame, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

            // Viewfinder corners with softly rounded bends.
            float a = tiny ? 1.5f : 1.75f, b = tiny ? 14.5f : 14.25f;
            const float len = 3.75f, r = 1.6f;
            Corner(g, pen, a, a, 1, 1, len, r);
            Corner(g, pen, b, a, -1, 1, len, r);
            Corner(g, pen, a, b, 1, -1, len, r);
            Corner(g, pen, b, b, -1, -1, len, r);

            using var cp = new Pen(chevrons, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(cp, [new PointF(5.25f, 4.9f), new PointF(8f, 7.65f), new PointF(10.75f, 4.9f)]);
            g.DrawLines(cp, [new PointF(5.25f, 8.35f), new PointF(8f, 11.1f), new PointF(10.75f, 8.35f)]);
            g.Restore(saved);
        }

        static void Corner(Graphics g, Pen pen, float x, float y, int dx, int dy, float len, float r)
        {
            using var p = new GraphicsPath();
            p.AddLine(x, y + dy * len, x, y + dy * r);
            float cx = x + dx * r, cy = y + dy * r;
            p.AddArc(cx - r, cy - r, 2 * r, 2 * r, dx > 0 ? 180 : 0, dx * dy * 90);
            p.AddLine(x + dx * r, y, x + dx * len, y);
            g.DrawPath(pen, p);
        }

        // App icon (exe, notifications): white glyph on a rounded accent tile, as a multi-size .ico.
        public static void WriteIco(string path)
        {
            int[] sizes = [16, 20, 24, 32, 48, 64, 256];
            var pngs = new List<byte[]>();
            foreach (int sz in sizes)
            {
                using var bmp = new Bitmap(sz, sz, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    float rr = sz * 0.22f;
                    using var tile = new GraphicsPath();
                    tile.AddArc(0, 0, 2 * rr, 2 * rr, 180, 90);
                    tile.AddArc(sz - 2 * rr, 0, 2 * rr, 2 * rr, 270, 90);
                    tile.AddArc(sz - 2 * rr, sz - 2 * rr, 2 * rr, 2 * rr, 0, 90);
                    tile.AddArc(0, sz - 2 * rr, 2 * rr, 2 * rr, 90, 90);
                    tile.CloseFigure();
                    using (var br = new LinearGradientBrush(new Rectangle(0, 0, sz, sz), Color.FromArgb(0, 120, 212), Color.FromArgb(0, 90, 170), 90f))
                        g.FillPath(br, tile);
                    // Glyph inset inside the tile, drawn directly (no resampling) so it stays sharp.
                    float inset = sz * 0.18f, gs = sz - 2 * inset;
                    g.TranslateTransform(inset, inset);
                    Draw(g, gs, Color.White, Color.White);
                }
                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                pngs.Add(ms.ToArray());
            }
            using var fs = File.Create(path);
            using var w = new BinaryWriter(fs);
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                w.Write(pngs[i].Length); w.Write(offset);
                offset += pngs[i].Length;
            }
            foreach (var p in pngs) w.Write(p);
        }

        // Debug aid: renders the glyph at tray sizes on light/dark backgrounds (and magnified).
        public static void Preview(string path)
        {
            int[] sizes = [16, 20, 24, 32];
            const int zoom = 8, pad = 16;
            int cell = 32 * zoom + pad;
            using var sheet = new Bitmap(pad + sizes.Length * cell, pad + 4 * (cell + 40), PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(sheet);
            g.Clear(Color.FromArgb(128, 128, 128));
            var rows = new (Color bg, Color fg, Color ch, string label)[]
            {
                (Color.FromArgb(32, 32, 32), Color.White, Color.White, "dark taskbar"),
                (Color.FromArgb(32, 32, 32), Color.White, AccentFor(false), "dark, capturing"),
                (Color.FromArgb(243, 243, 243), Color.FromArgb(28, 28, 28), Color.FromArgb(28, 28, 28), "light taskbar"),
                (Color.FromArgb(243, 243, 243), Color.FromArgb(28, 28, 28), AccentFor(true), "light, capturing"),
            };
            using var font = new Font("Segoe UI", 14);
            for (int row = 0; row < rows.Length; row++)
            {
                int y0 = pad + row * (cell + 40);
                g.DrawString(rows[row].label, font, Brushes.White, pad, y0);
                for (int i = 0; i < sizes.Length; i++)
                {
                    int sz = sizes[i];
                    using var ic = new Bitmap(sz, sz, PixelFormat.Format32bppArgb);
                    using (var ig = Graphics.FromImage(ic)) Draw(ig, sz, rows[row].fg, rows[row].ch);
                    int x0 = pad + i * cell;
                    using (var bg = new SolidBrush(rows[row].bg)) g.FillRectangle(bg, x0, y0 + 30, sz * zoom, sz * zoom);
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(ic, x0, y0 + 30, sz * zoom, sz * zoom);
                    g.PixelOffsetMode = PixelOffsetMode.Default;
                    g.DrawString($"{sz}px", font, Brushes.White, x0 + sz * zoom + 4, y0 + 30);
                }
            }
            sheet.Save(path, ImageFormat.Png);
        }

        [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    }
}
