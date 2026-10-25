using System.Diagnostics;
using System.Drawing;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.Storage.Xps;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScrollShot;

// Full-page (scrolling) capture for any window. Scrolls the pane with posted wheel messages,
// captures frames with PrintWindow and stitches the overlaps. Never moves the mouse or takes focus.
static partial class Program
{
    internal sealed class Options
    {
        public int Delay = 3;
        public bool FromTop = true;
        public int MaxFrames = 300;
        public int MaxHeight = 40000;
        public bool Open = true, Copy = true;
        public bool Uia, ForceOpen;
        public string? Out, Window;
        public int? X, Y;
        public string Hotkey = "Ctrl+Alt+S";
    }

    [STAThread]
    static int Main(string[] args)
    {
        bool tray = args.Length == 0 || args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        if (!tray) PInvoke.AttachConsole(unchecked((uint)-1) /*ATTACH_PARENT_PROCESS*/);
        Options? o;
        try { o = Parse(args); } catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        if (o == null) return 1;
        if (tray) return Tray.Run(o);

        // The real mouse is never moved: the point is only used to pick the window/pane.
        nint hwnd;
        Point pt;
        if (o.Window != null)
        {
            hwnd = Win.FindTopWindow(o.Window);
            if (hwnd == 0) { Console.Error.WriteLine($"No visible window with title containing \"{o.Window}\"."); return 1; }
            var r = Win.WindowRect(hwnd);
            pt = new Point((r.left + r.right) / 2, (r.top + r.bottom) / 2);
        }
        else
        {
            if (o.X is int px && o.Y is int py) pt = new Point(px, py);
            else
            {
                Console.WriteLine("Hover the mouse over the area you want to scroll-capture.");
                for (int i = o.Delay; i > 0; i--) { Console.Write($"{i}.. "); Thread.Sleep(1000); }
                Console.WriteLine();
                PInvoke.GetCursorPos(out pt);
            }
            hwnd = Win.RootAt(pt);
            if (hwnd == 0) { Console.Error.WriteLine("No window at that point."); return 1; }
        }
        Uia.Init();
        return CaptureToFile(o, hwnd, pt, o.Window == null) != null ? 0 : 1;
    }

    // Scrolls the pane at pt (or the window's main pane), stitches, saves, copies, opens. Returns the file path.
    internal static string? CaptureToFile(Options o, nint hwnd, Point pt, bool usePoint)
    {
        Console.WriteLine($"Target: \"{Win.Title(hwnd)}\"");

        var pane = Uia.FindPane(hwnd, usePoint ? pt : null);
        if (pane != null) pt = pane.Point;
        IScroller scroller = pane != null && o.Uia ? new UiaScroller(pane) : new WheelScroller(hwnd, pt, pane);
        Console.WriteLine($"Scrolling via {scroller.Name}");

        Frame Grab() => Capture(hwnd);

        var sw = Stopwatch.StartNew();
        var prev = WaitStable(Grab);

        if (o.FromTop)
        {
            for (int i = 0; i < 200; i++)
            {
                bool more = scroller.ToTop();
                var f = WaitStable(Grab);
                bool same = Same(prev, f);
                prev = f;
                if (same || !more) break;
            }
        }

        int W = prev.W, H = prev.H;
        var first = prev;
        double amount = scroller.InitialAmount;
        int top = 0, bottom = H - 1, left = 0, right = W - 1;
        bool regionKnown = false;
        var scrollVotes = new int[W];
        var staticVotes = new int[W];
        var strips = new List<int[]>(); // appended full-width rows
        int totalHeight = H;
        Frame last = prev;

        Console.WriteLine($"Capturing... (Esc to stop)  [to top: {sw.Elapsed.TotalSeconds:F1}s]");
        PInvoke.GetAsyncKeyState(0x1B);
        double pxPerPct = 0; // content pixels per scroll percent: constant for a document, learned from the first good match
        for (int n = 0; n < o.MaxFrames && totalHeight < o.MaxHeight; n++)
        {
            if ((PInvoke.GetAsyncKeyState(0x1B) & 0x8001) != 0) { Console.WriteLine("Stopped (Esc)."); break; }
            double p0 = scroller.Position;
            if (!scroller.Down(amount)) break; // already at the end
            var cur = WaitStable(Grab);
            if (cur.W != W || cur.H != H) { Console.Error.WriteLine("Window size changed; stopping."); break; }

            if (!regionKnown)
            {
                if (!DiffBounds(prev, cur, out top, out bottom, out left, out right))
                {
                    if (scroller is WheelScroller) Console.Error.WriteLine("Nothing scrolled: this app may ignore background scroll messages. Use --uia to scroll it anyway (it may come to the front).");
                    break;
                }
                regionKnown = true;
            }
            else if (Same(prev, cur)) break; // reached the end

            int h = bottom - top + 1;
            double Predict() { double p1 = scroller.Position; return pxPerPct > 0 && p0 >= 0 && p1 >= 0 ? (p1 - p0) * pxPerPct : -1; }
            double predicted = Predict();
            // Apps accelerate bursts of wheel messages; if a step overshot, back up so the frames still overlap.
            for (int k = 0; k < 3 && predicted > 0.85 * h; k++)
            {
                scroller.Back(Math.Max(scroller.Clamp(0), amount * (predicted - 0.6 * h) / predicted));
                cur = WaitStable(Grab);
                predicted = Predict();
            }

            // Match on a band around the pane point first: that's the pane being scrolled.
            int cx = Math.Clamp(pt.X - cur.OX, left, right);
            int bandL = Math.Max(left, cx - 300), bandR = Math.Min(right, cx + 300);
            int dy; double score;
            if (predicted >= 1)
            {
                // The scroll position tells us the shift; the pixels just refine it.
                int e = (int)Math.Round(predicted);
                dy = FindShift(prev, cur, top, bottom, bandL, bandR, e, out score, e - 12, e + 12);
                if (dy <= 0) { dy = Math.Min(e, h); score = 0; }
            }
            else
            {
                dy = FindShift(prev, cur, top, bottom, bandL, bandR, null, out score);
                if (dy <= 0) dy = FindShift(prev, cur, top, bottom, left, right, null, out score);
                double moved = scroller.Position - p0;
                if (dy > 0 && score >= 0.9 && p0 >= 0 && moved > 0.01) pxPerPct = dy / moved;
            }
            if (dy > 0) VoteColumns(prev, cur, top, bottom, dy, scrollVotes, staticVotes);
            if (dy <= 0)
            {
                Console.Error.WriteLine($"  frame {n}: no overlap found (score {score:F2}); appending whole viewport.");
                dy = h;
                amount = scroller.Clamp(amount / 2);
            }
            else
            {
                amount = scroller.Clamp(amount * (0.6 * h) / dy);
            }

            for (int y = bottom - dy + 1; y <= bottom; y++) strips.Add(cur.Row(y).ToArray());
            totalHeight += dy;
            prev = last = cur;
            Console.WriteLine($"  frame {n + 1}: dy={dy}{(predicted >= 1 ? $" (predicted {predicted:F0})" : "")} score={score:F2} next={amount:F2} height={totalHeight}px");
        }
        Console.WriteLine($"  [frames done: {sw.Elapsed.TotalSeconds:F1}s]");
        scroller.Restore();
        Console.WriteLine($"  [restored: {sw.Elapsed.TotalSeconds:F1}s]");

        var scrollCols = new bool[W];
        for (int x = 0; x < W; x++) scrollCols[x] = scrollVotes[x] >= staticVotes[x];
        var img = Compose(first, last, strips, top, bottom, scrollCols, regionKnown);
        var outPath = o.Out ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ScrollShots", $"ScrollShot_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        Output.SavePng(outPath, img);
        Console.WriteLine($"Saved {img.W}x{img.H} in {sw.Elapsed.TotalSeconds:F1}s -> {outPath}");
        if (o.Copy) try { Output.CopyToClipboard(img, outPath); Console.WriteLine("Copied to clipboard."); } catch (Exception ex) { Console.Error.WriteLine($"Clipboard: {ex.Message}"); }
        if (o.Open) Output.Open(outPath);
        return outPath;
    }
    static Options? Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i].ToLowerInvariant();
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
            switch (a)
            {
                case "--delay": o.Delay = int.Parse(Next()); break;
                case "--no-top": o.FromTop = false; break;
                case "--max-frames": o.MaxFrames = int.Parse(Next()); break;
                case "--max-height": o.MaxHeight = int.Parse(Next()); break;
                case "--no-open": o.Open = false; break;
                case "--no-copy": o.Copy = false; break;
                case "--uia": o.Uia = true; break;
                case "--window": o.Window = Next(); break;
                case "--tray": break;
                case "--open": o.ForceOpen = true; break;
                case "--hotkey": o.Hotkey = Next(); break;
                case "--out": o.Out = Next(); break;
                case "--at": var p = Next().Split(','); o.X = int.Parse(p[0]); o.Y = int.Parse(p[1]); break;
                default:
                    Console.WriteLine("""
                        ScrollShot - full-page capture for any window.
                        Run with no arguments to sit in the tray: press the hotkey (default Ctrl+Alt+S)
                        while the mouse is over any app to capture the pane under it. Esc stops a capture.
                          --tray           tray mode (default when no arguments)
                          --hotkey KEYS    tray hotkey, e.g. "Ctrl+Shift+F12" (default Ctrl+Alt+S)
                          --open           tray: open each capture automatically (default: notification, click to open)
                        Never moves your mouse or steals focus: scrolls with posted wheel messages and captures with PrintWindow,
                        so the target window can stay behind other windows.
                          --window TITLE   capture the window whose title contains TITLE (no hovering needed)
                          --at X,Y         pick the window/pane at this screen point
                          (neither)        pick the window/pane under the mouse after a countdown
                          --delay N        countdown seconds (default 3)
                          --no-top         start from the current scroll position instead of the top
                          --out FILE       output PNG (default Pictures\ScrollShots\...)
                          --max-frames N   default 300     --max-height N   default 40000
                          --uia            scroll with UI Automation calls (works more widely, but can bring the app to the front)
                          --no-open        don't open the result
                          --no-copy        don't copy the result to the clipboard
                        """);
                    return null;
            }
        }
        return o;
    }

    // ---------- capture ----------

    static unsafe Frame Capture(nint hwnd)
    {
        var h = (HWND)hwnd;
        var wr = Win.WindowRect(hwnd);
        int ww = wr.right - wr.left, wh = wr.bottom - wr.top;
        if (ww <= 0 || wh <= 0 || PInvoke.IsIconic(h)) throw new InvalidOperationException("the window closed or was minimized");
        RECT fr;
        if (PInvoke.DwmGetWindowAttribute(h, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, &fr, (uint)sizeof(RECT)).Failed) fr = wr;
        int cl = Math.Max(0, fr.left - wr.left), ct = Math.Max(0, fr.top - wr.top);
        int cw = Math.Min(ww, fr.right - wr.left) - cl, ch = Math.Min(wh, fr.bottom - wr.top) - ct;

        var screen = PInvoke.GetDC(HWND.Null);
        var mem = PInvoke.CreateCompatibleDC(screen);
        var bi = new BITMAPINFO();
        bi.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
        bi.bmiHeader.biWidth = ww;
        bi.bmiHeader.biHeight = -wh; // top-down
        bi.bmiHeader.biPlanes = 1;
        bi.bmiHeader.biBitCount = 32;
        void* bits;
        var dib = PInvoke.CreateDIBSection(mem, &bi, DIB_USAGE.DIB_RGB_COLORS, &bits, HANDLE.Null, 0);
        var old = PInvoke.SelectObject(mem, (HGDIOBJ)dib.Value);
        try
        {
            PInvoke.PrintWindow(h, mem, (PRINT_WINDOW_FLAGS)2 /*PW_RENDERFULLCONTENT*/);
            var f = new Frame { W = cw, H = ch, OX = wr.left + cl, OY = wr.top + ct, Px = new int[cw * ch] };
            int* src = (int*)bits;
            for (int y = 0; y < ch; y++)
            {
                var row = new ReadOnlySpan<int>(src + (y + ct) * ww + cl, cw);
                row.CopyTo(f.Px.AsSpan(y * cw, cw));
            }
            for (int i = 0; i < f.Px.Length; i++) f.Px[i] |= unchecked((int)0xFF000000);
            return f;
        }
        finally
        {
            PInvoke.SelectObject(mem, old);
            PInvoke.DeleteObject((HGDIOBJ)dib.Value);
            PInvoke.DeleteDC(mem);
            PInvoke.ReleaseDC(HWND.Null, screen);
        }
    }

    static Frame WaitStable(Func<Frame> grab)
    {
        Thread.Sleep(80);
        var a = grab();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 1500)
        {
            Thread.Sleep(60);
            var b = grab();
            if (Same(a, b)) return b;
            a = b;
        }
        return a;
    }
}
