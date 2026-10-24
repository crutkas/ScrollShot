using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScrollShot;

static partial class Program
{
    // Tray glyph: viewfinder corners around a "scroll down" double chevron. Rendered with a tiny
    // signed-distance rasterizer (4x4 supersampling) at the exact tray size, so it stays crisp.
    internal static unsafe class Glyph
    {
        public static bool LightTaskbar =>
            Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int v && v == 1;

        static uint Accent
        {
            get
            {
                // AccentColor is stored as ABGR.
                if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null) is int abgr)
                    return Rgb((uint)abgr & 0xFF, ((uint)abgr >> 8) & 0xFF, ((uint)abgr >> 16) & 0xFF);
                return Rgb(0, 120, 212);
            }
        }

        static uint Rgb(uint r, uint g, uint b) => (r << 16) | (g << 8) | b;
        static uint Blend(uint a, uint b, float t)
        {
            uint C(int sh) => (uint)(((a >> sh) & 0xFF) + ((((b >> sh) & 0xFF) - (float)((a >> sh) & 0xFF)) * t)) & 0xFF;
            return (C(16) << 16) | (C(8) << 8) | C(0);
        }

        // Accent tuned for contrast against the taskbar.
        static uint AccentFor(bool lightTaskbar)
        {
            uint a = Accent;
            float l = (0.299f * ((a >> 16) & 0xFF) + 0.587f * ((a >> 8) & 0xFF) + 0.114f * (a & 0xFF)) / 255f;
            if (!lightTaskbar && l < 0.45f) return Blend(a, 0xFFFFFF, 0.35f);
            if (lightTaskbar && l > 0.6f) return Blend(a, 0, 0.3f);
            return a;
        }

        public static HICON MakeIcon(bool busy)
        {
            int size = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSMICON);
            if (size <= 0) size = 16;
            bool light = LightTaskbar;
            uint fg = light ? Rgb(28, 28, 28) : 0xFFFFFF;
            var px = Render(size, fg, busy ? AccentFor(light) : fg);

            var bi = new BITMAPINFO();
            bi.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
            bi.bmiHeader.biWidth = size;
            bi.bmiHeader.biHeight = -size;
            bi.bmiHeader.biPlanes = 1;
            bi.bmiHeader.biBitCount = 32;
            void* bits;
            var color = PInvoke.CreateDIBSection(HDC.Null, &bi, DIB_USAGE.DIB_RGB_COLORS, &bits, default, 0);
            px.AsSpan().CopyTo(new Span<uint>(bits, size * size));
            var mask = PInvoke.CreateBitmap(size, size, 1, 1, null);
            var info = new ICONINFO { fIcon = true, hbmColor = color, hbmMask = mask };
            var icon = PInvoke.CreateIconIndirect(&info);
            PInvoke.DeleteObject((HGDIOBJ)color.Value);
            PInvoke.DeleteObject((HGDIOBJ)mask.Value);
            return icon;
        }

        // Returns straight-alpha BGRA pixels.
        static uint[] Render(int size, uint frame, uint chevrons)
        {
            float s = size / 16f;
            bool tiny = size <= 16;
            float w = (tiny ? 1f : 1.3f) * s / 2; // half stroke width, in pixels
            float a = (tiny ? 1.5f : 1.75f) * s, b = (tiny ? 14.5f : 14.25f) * s, len = 3.75f * s, r = 1.6f * s;

            var frameShapes = new List<Func<float, float, float>>();
            foreach (var (x, y, dx, dy) in new[] { (a, a, 1, 1), (b, a, -1, 1), (a, b, 1, -1), (b, b, -1, -1) })
            {
                float cx = x + dx * r, cy = y + dy * r;
                frameShapes.Add((px, py) => Seg(px, py, x, y + dy * len, x, cy));
                frameShapes.Add((px, py) => Arc(px, py, cx, cy, r, -dx, -dy));
                frameShapes.Add((px, py) => Seg(px, py, cx, y, x + dx * len, y));
            }
            var chevShapes = new List<Func<float, float, float>>();
            foreach (float top in new[] { 4.9f, 8.35f })
            {
                float y0 = top * s, y1 = (top + 2.75f) * s, x0 = 5.25f * s, xm = 8f * s, x1 = 10.75f * s;
                chevShapes.Add((px, py) => Seg(px, py, x0, y0, xm, y1));
                chevShapes.Add((px, py) => Seg(px, py, xm, y1, x1, y0));
            }

            var outPx = new uint[size * size];
            const int ss = 4;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int hf = 0, hc = 0;
                    for (int sy = 0; sy < ss; sy++)
                        for (int sx = 0; sx < ss; sx++)
                        {
                            float px = x + (sx + 0.5f) / ss, py = y + (sy + 0.5f) / ss;
                            if (frameShapes.Any(f => f(px, py) <= w)) hf++;
                            else if (chevShapes.Any(f => f(px, py) <= w)) hc++;
                        }
                    int cov = hf + hc;
                    if (cov == 0) continue;
                    uint col = hf >= hc ? frame : chevrons;
                    uint alpha = (uint)(cov * 255 / (ss * ss));
                    outPx[y * size + x] = (alpha << 24) | col;
                }
            return outPx;
        }

        static float Seg(float px, float py, float ax, float ay, float bx, float by)
        {
            float vx = bx - ax, vy = by - ay, wx = px - ax, wy = py - ay;
            float t = Math.Clamp((wx * vx + wy * vy) / Math.Max(1e-6f, vx * vx + vy * vy), 0, 1);
            float dx = wx - t * vx, dy = wy - t * vy;
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        // Quarter arc around (cx, cy) in the quadrant pointing toward (qx, qy).
        static float Arc(float px, float py, float cx, float cy, float r, int qx, int qy)
        {
            float dx = px - cx, dy = py - cy;
            if (dx * qx >= 0 && dy * qy >= 0) return MathF.Abs(MathF.Sqrt(dx * dx + dy * dy) - r);
            return MathF.Min(Dist(px, py, cx + qx * r, cy), Dist(px, py, cx, cy + qy * r));
        }

        static float Dist(float ax, float ay, float bx, float by) => MathF.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
    }
}
