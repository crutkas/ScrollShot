using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace ScrollShot;

// Full-page (scrolling) capture for any window: hover over the scrollable area,
// it scrolls to the top, then wheels down capturing frames and stitches the overlaps.
static partial class Program
{
    sealed class Frame
    {
        public int W, H, OX, OY;
        public int[] Px = [];
        public ReadOnlySpan<int> Row(int y) => Px.AsSpan(y * W, W);
    }

    internal sealed class Options
    {
        public int Delay = 3;
        public bool FromTop = true;
        public int MaxFrames = 300;
        public int MaxHeight = 40000;
        public bool Open = true, Copy = true;
        public bool Screen, Uia, ForceOpen;
        public string? Out, Window;
        public int? X, Y;
        public string Hotkey = "Ctrl+Alt+S";
    }

    [STAThread]
    static int Main(string[] args)
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        bool tray = args.Length == 0 || args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        if (args.Length == 2 && args[0] == "--icon-preview") { Glyph.Preview(args[1]); return 0; }
        if (args.Length == 2 && args[0] == "--write-ico") { Glyph.WriteIco(args[1]); return 0; }
        if (!tray) AttachConsole(-1);
        Options? o;
        try { o = Parse(args); } catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        if (o == null) return 1;
        if (tray) return TrayApp.Run(o);

        // The real mouse is never moved: the point is only used to pick the window/pane.
        IntPtr hwnd;
        POINT pt;
        if (o.Window != null)
        {
            hwnd = FindTopWindow(o.Window);
            if (hwnd == IntPtr.Zero) { Console.Error.WriteLine($"No visible window with title containing \"{o.Window}\"."); return 1; }
            GetWindowRect(hwnd, out var r);
            pt = new POINT { X = (r.Left + r.Right) / 2, Y = (r.Top + r.Bottom) / 2 };
        }
        else
        {
            if (o.X is int px && o.Y is int py) pt = new POINT { X = px, Y = py };
            else
            {
                Console.WriteLine("Hover the mouse over the area you want to scroll-capture.");
                for (int i = o.Delay; i > 0; i--) { Console.Write($"{i}.. "); Thread.Sleep(1000); }
                Console.WriteLine();
                GetCursorPos(out pt);
            }
            hwnd = GetAncestor(WindowFromPoint(pt), 2 /*GA_ROOT*/);
            if (hwnd == IntPtr.Zero) { Console.Error.WriteLine("No window at that point."); return 1; }
        }
        return CaptureToFile(o, hwnd, pt, o.Window == null) != null ? 0 : 1;
    }

    // Scrolls the pane at pt (or the window's main pane), stitches, saves, copies, opens. Returns the file path.
    internal static string? CaptureToFile(Options o, IntPtr hwnd, POINT pt, bool usePoint)
    {
        Console.WriteLine($"Target: \"{Title(hwnd)}\"");
        if (o.Screen) { ShowWindow(GetConsoleWindow(), 6 /*SW_MINIMIZE*/); SetForegroundWindow(hwnd); Thread.Sleep(300); }

        var pane = Pane.Find(hwnd, usePoint ? pt : null);
        if (pane != null) pt = pane.Point;
        IScroller scroller;
        if (pane != null && o.Uia) scroller = new UiaScroller(pane);
        else
        {
            scroller = new WheelScroller(hwnd, pt, pane);

        }
        Console.WriteLine($"Scrolling via {scroller.Name}");

        Frame Grab() => Capture(hwnd, o.Screen);

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
        int? lastDy = null;
        Frame last = prev;

        Console.WriteLine($"Capturing... (Esc to stop)  [to top: {sw.Elapsed.TotalSeconds:F1}s]");
        GetAsyncKeyState(0x1B);
        for (int n = 0; n < o.MaxFrames && totalHeight < o.MaxHeight; n++)
        {
            if ((GetAsyncKeyState(0x1B) & 0x8001) != 0) { Console.WriteLine("\nStopped (Esc)."); break; }
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
            // Match on a band around the cursor first: that's the pane being scrolled.
            int cx = Math.Clamp(pt.X - cur.OX, left, right);
            int bandL = Math.Max(left, cx - 300), bandR = Math.Min(right, cx + 300);
            int dy = FindShift(prev, cur, top, bottom, bandL, bandR, lastDy, out double score);
            if (dy <= 0) dy = FindShift(prev, cur, top, bottom, left, right, lastDy, out score);
            if (dy > 0) VoteColumns(prev, cur, top, bottom, dy, scrollVotes, staticVotes);
            if (dy <= 0)
            {
                Console.Error.WriteLine($"  frame {n}: no overlap found (score {score:F2}); appending whole viewport.");
                dy = h;
                amount = scroller.Clamp(amount / 2);
            }
            else
            {
                lastDy = dy;
                amount = scroller.Clamp(amount * (0.7 * h) / dy);
            }

            for (int y = bottom - dy + 1; y <= bottom; y++) strips.Add(cur.Row(y).ToArray());
            totalHeight += dy;
            prev = last = cur;
            Console.WriteLine($"  frame {n + 1}: dy={dy} score={score:F2} next={amount:F2} height={totalHeight}px");
        }
        Console.WriteLine($"  [frames done: {sw.Elapsed.TotalSeconds:F1}s]");
        scroller.Restore();
        Console.WriteLine($"  [restored: {sw.Elapsed.TotalSeconds:F1}s]");

        var scrollCols = new bool[W];
        for (int x = 0; x < W; x++) scrollCols[x] = scrollVotes[x] >= staticVotes[x];
        using var bmp = Compose(first, last, strips, top, bottom, scrollCols, regionKnown);
        var outPath = o.Out ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ScrollShots", $"ScrollShot_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        bmp.Save(outPath, ImageFormat.Png);
        Console.WriteLine($"Saved {bmp.Width}x{bmp.Height} in {sw.Elapsed.TotalSeconds:F1}s -> {outPath}");
        if (o.Copy) try { Clipboard.SetImage(bmp); Console.WriteLine("Copied to clipboard."); } catch { }
        if (o.Open) Process.Start(new ProcessStartInfo(outPath) { UseShellExecute = true });
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
                case "--screen": o.Screen = true; break;
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
                          --screen         capture from screen (brings window to front) instead of PrintWindow
                          --no-open        don't open the result
                          --no-copy        don't copy the result to the clipboard
                        """);
                    return null;
            }
        }
        return o;
    }

    // ---------- capture ----------

    static Frame Capture(IntPtr hwnd, bool fromScreen)
    {
        GetWindowRect(hwnd, out var wr);
        if (wr.Right - wr.Left <= 0 || wr.Bottom - wr.Top <= 0 || IsIconic(hwnd))
            throw new InvalidOperationException("the window closed or was minimized");
        if (DwmGetWindowAttribute(hwnd, 9 /*DWMWA_EXTENDED_FRAME_BOUNDS*/, out RECT fr, Marshal.SizeOf<RECT>()) != 0) fr = wr;
        var crop = Rectangle.FromLTRB(fr.Left - wr.Left, fr.Top - wr.Top, fr.Right - wr.Left, fr.Bottom - wr.Top);

        using var full = new Bitmap(wr.Right - wr.Left, wr.Bottom - wr.Top, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(full))
        {
            if (fromScreen) g.CopyFromScreen(wr.Left, wr.Top, 0, 0, full.Size);
            else { var hdc = g.GetHdc(); PrintWindow(hwnd, hdc, 2 /*PW_RENDERFULLCONTENT*/); g.ReleaseHdc(hdc); }
        }
        crop.Intersect(new Rectangle(Point.Empty, full.Size));
        var f = new Frame { W = crop.Width, H = crop.Height, OX = wr.Left + crop.Left, OY = wr.Top + crop.Top, Px = new int[crop.Width * crop.Height] };
        var bd = full.LockBits(crop, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < f.H; y++) Marshal.Copy(bd.Scan0 + y * bd.Stride, f.Px, y * f.W, f.W);
        full.UnlockBits(bd);
        for (int i = 0; i < f.Px.Length; i++) f.Px[i] |= unchecked((int)0xFF000000);
        return f;
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

    // ---------- stitching ----------

    // Tolerates a few changed pixels (blinking caret, hover effects) and changes confined to a
    // narrow vertical strip (overlay scrollbars that fade in/out after each scroll).
    static bool Same(Frame a, Frame b)
    {
        if (a.W != b.W || a.H != b.H) return false;
        int diff = 0, minX = int.MaxValue, maxX = -1;
        for (int i = 0; i < a.Px.Length; i++)
        {
            if (a.Px[i] == b.Px[i]) continue;
            diff++;
            int x = i % a.W;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (diff > 150 && maxX - minX > 24) return false;
        }
        return true;
    }

    static bool DiffBounds(Frame a, Frame b, out int top, out int bottom, out int left, out int right)
    {
        top = int.MaxValue; bottom = -1; left = int.MaxValue; right = -1;
        for (int y = 0; y < a.H; y++)
        {
            var ra = a.Row(y); var rb = b.Row(y);
            if (ra.SequenceEqual(rb)) continue;
            top = Math.Min(top, y); bottom = y;
            for (int x = 0; x < a.W; x++)
                if (ra[x] != rb[x]) { left = Math.Min(left, x); right = Math.Max(right, x); }
        }
        return bottom >= 0;
    }

    // Per column: does it move with the content (scrolling pane) or stay put (sidebar, sticky panel)?
    static void VoteColumns(Frame prev, Frame cur, int top, int bottom, int dy, int[] scroll, int[] stat)
    {
        int W = cur.W;
        for (int y = top; y + dy <= bottom; y++)
        {
            int c = y * W, s = (y + dy) * W;
            for (int x = 0; x < W; x++)
            {
                int p = cur.Px[c + x];
                bool sh = Close(p, prev.Px[s + x]), st = Close(p, prev.Px[c + x]);
                if (sh && !st) scroll[x]++;
                else if (st && !sh) stat[x]++;
            }
        }
    }

    static int Gray(int p) => (((p >> 16) & 0xFF) * 77 + ((p >> 8) & 0xFF) * 150 + (p & 0xFF) * 29) >> 8;
    static bool Close(int a, int b) => a == b || Math.Abs(Gray(a) - Gray(b)) <= 24;

    // Per-row signature: grayscale averaged into bins. Tolerant of sub-pixel scroll offsets
    // (WinUI/XAML scroll by fractional pixels, so anti-aliasing never matches exactly).
    static float[] RowSig(ReadOnlySpan<int> row, int bins, out bool informative)
    {
        var s = new float[bins];
        int n = row.Length, lo = 255, hi = 0;
        for (int x = 0; x < n; x++)
        {
            int g = Gray(row[x]);
            s[x * bins / n] += g;
            if (g < lo) lo = g; if (g > hi) hi = g;
        }
        for (int k = 0; k < bins; k++) { int w = (k + 1) * n / bins - k * n / bins; if (w > 0) s[k] /= w; }
        informative = hi - lo > 24; // flat rows match anything, so they aren't evidence
        return s;
    }

    // Finds dy such that cur row (top+i) ~= prev row (top+i+dy) within the scrolling region.
    static int FindShift(Frame prev, Frame cur, int top, int bottom, int left, int right, int? hint, out double score)
    {
        int h = bottom - top + 1, bins = Math.Clamp((right - left + 1) / 4, 8, 64);
        var a = new float[h][]; var b = new float[h][]; var info = new bool[h];
        for (int i = 0; i < h; i++)
        {
            a[i] = RowSig(prev.Row(top + i)[left..(right + 1)], bins, out _);
            b[i] = RowSig(cur.Row(top + i)[left..(right + 1)], bins, out info[i]);
        }

        var err = new double[h];
        Array.Fill(err, double.MaxValue);
        double best = double.MaxValue;
        var valid = new List<double>();
        for (int dy = 1; dy < h; dy++)
        {
            if (h - dy < h / 5) break; // need real overlap; tiny overlaps match by accident
            double sum = 0; int cnt = 0;
            for (int i = 0; i + dy < h; i++)
            {
                if (!info[i]) continue;
                var x = b[i]; var y = a[i + dy];
                double d = 0;
                for (int k = 0; k < bins; k++) d += Math.Abs(x[k] - y[k]);
                sum += d; cnt++;
            }
            if (cnt < 8) continue;
            err[dy] = sum / (cnt * bins);
            valid.Add(err[dy]);
            best = Math.Min(best, err[dy]);
        }
        score = 0;
        if (valid.Count == 0) return -1;
        valid.Sort();
        double median = valid[valid.Count / 2];
        score = median > 0 ? 1 - best / median : (best == 0 ? 1 : 0);
        if (best > 6 || score < 0.6) return -1;

        int pick = -1;
        for (int dy = 1; dy < h; dy++)
        {
            if (err[dy] > best * 1.15 + 0.3) continue;
            if (pick < 0) { pick = dy; continue; }
            if (hint is int hv) { if (Math.Abs(dy - hv) < Math.Abs(pick - hv)) pick = dy; }
            else if (err[dy] < err[pick]) pick = dy;
        }
        return pick;
    }
    static Bitmap Compose(Frame first, Frame last, List<int[]> strips, int top, int bottom, bool[] scrollCols, bool scrolled)
    {
        int W = first.W;
        if (!scrolled) { top = 0; bottom = first.H - 1; }
        int height = (bottom + 1) + strips.Count + (last.H - 1 - bottom);
        var bmp = new Bitmap(W, height, PixelFormat.Format32bppArgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, W, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        int y = 0;
        void Put(ReadOnlySpan<int> row) { Marshal.Copy(row.ToArray(), 0, bd.Scan0 + y * bd.Stride, W); y++; }

        for (int r = 0; r <= bottom; r++) Put(first.Row(r));

        // Columns that don't scroll (e.g. a fixed sidebar) are extended with the
        // background color instead of repeating slices of the static content.
        var counts = new Dictionary<int, int>();
        for (int r = top; r <= bottom; r++)
            foreach (int p in first.Row(r)) counts[p] = counts.GetValueOrDefault(p) + 1;
        int bg = counts.Count > 0 ? counts.MaxBy(kv => kv.Value).Key : unchecked((int)0xFFFFFFFF);
        var tmp = new int[W];
        foreach (var s in strips)
        {
            for (int x = 0; x < W; x++) tmp[x] = scrollCols[x] ? s[x] : bg;
            Put(tmp);
        }
        for (int r = bottom + 1; r < last.H; r++) Put(last.Row(r));
        bmp.UnlockBits(bd);
        return bmp;
    }

    static string Title(IntPtr h) { var sb = new System.Text.StringBuilder(256); GetWindowText(h, sb, 256); return sb.ToString(); }

    static IntPtr FindTopWindow(string titlePart)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h) && !IsIconic(h) && Title(h).Contains(titlePart, StringComparison.OrdinalIgnoreCase)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // ---------- scrolling (never touches the real mouse) ----------

    interface IScroller
    {
        string Name { get; }
        double InitialAmount { get; }
        double Clamp(double amount);
        bool ToTop();               // false when nothing more to do
        bool Down(double amount);   // false when already at the end
        void Restore() { }          // put the scroll position back where the user had it
    }

    // Finds the scrollable pane (read-only UI Automation: reading never activates the app).
    sealed class Pane
    {
        public ScrollPattern Sp = null!;
        public string Name = "";
        public POINT Point; // inside the pane's visible part

        public static Pane? Find(IntPtr hwnd, POINT? at)
        {
            try
            {
                AutomationElement? el = null;
                if (at is POINT p)
                {
                    // Walk up from the element at the point to the nearest vertically scrollable pane.
                    var e = AutomationElement.FromPoint(new System.Windows.Point(p.X, p.Y));
                    var walker = TreeWalker.ControlViewWalker;
                    while (e != null && !IsVScrollable(e)) e = walker.GetParent(e);
                    el = e;
                }
                if (el == null)
                {
                    // Largest vertically scrollable element in the window.
                    var root = AutomationElement.FromHandle(hwnd);
                    var all = root.FindAll(TreeScope.Subtree, new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true));
                    double bestArea = 0;
                    foreach (AutomationElement e in all)
                    {
                        if (!IsVScrollable(e)) continue;
                        var r = e.Current.BoundingRectangle;
                        if (r.Width * r.Height > bestArea) { bestArea = r.Width * r.Height; el = e; }
                    }
                }
                if (el == null) return null;

                // Pane bounds can extend past the window; aim at the middle of the visible part.
                var b = el.Current.BoundingRectangle;
                GetWindowRect(hwnd, out var wr);
                double l = Math.Max(b.Left, wr.Left), t = Math.Max(b.Top, wr.Top), r2 = Math.Min(b.Right, wr.Right), b2 = Math.Min(b.Bottom, wr.Bottom);
                var pt = at is POINT ap && ap.X >= l && ap.X < r2 && ap.Y >= t && ap.Y < b2
                    ? ap : new POINT { X = (int)((l + r2) / 2), Y = (int)((t + b2) / 2) };
                return new Pane
                {
                    Sp = (ScrollPattern)el.GetCurrentPattern(ScrollPattern.Pattern),
                    Name = $"{el.Current.ControlType.ProgrammaticName.Replace("ControlType.", "")} \"{el.Current.Name}\"",
                    Point = pt,
                };
            }
            catch (Exception ex) { Console.Error.WriteLine($"UI Automation unavailable: {ex.Message}"); return null; }
        }

        static bool IsVScrollable(AutomationElement e) =>
            e.TryGetCurrentPattern(ScrollPattern.Pattern, out var o) && ((ScrollPattern)o).Current.VerticallyScrollable;
    }

    // Fallback only: UI Automation scroll calls make many apps (XAML) activate themselves.
    sealed class UiaScroller(Pane pane) : IScroller
    {
        readonly ScrollPattern sp = pane.Sp;
        readonly double startH = pane.Sp.Current.HorizontalScrollPercent, startV = pane.Sp.Current.VerticalScrollPercent;
        public string Name => $"UI Automation calls on {pane.Name} (may bring the app to the front)";
        public double InitialAmount { get { double v = sp.Current.VerticalViewSize; return v > 0 && v < 100 ? v * 0.5 : 5; } }
        public double Clamp(double a) => Math.Clamp(a, 0.01, 50);
        public bool ToTop() { sp.SetScrollPercent(ScrollPattern.NoScroll, 0); return false; }
        public bool Down(double amount)
        {
            double cur = sp.Current.VerticalScrollPercent;
            if (cur < 0 || cur >= 99.999) return false;
            sp.SetScrollPercent(ScrollPattern.NoScroll, Math.Min(100, cur + amount));
            return true;
        }
        public void Restore()
        {
            try { sp.SetScrollPercent(sp.Current.HorizontallyScrollable ? startH : ScrollPattern.NoScroll, startV); }
            catch (Exception ex) { Console.Error.WriteLine($"Couldn't restore scroll position: {ex.Message}"); }
        }
    }

    // Default: wheel messages posted straight to the pane's window. No input injection, no activation.
    sealed class WheelScroller : IScroller
    {
        readonly IntPtr target;
        readonly POINT pt;
        readonly ScrollPattern? sp; // read-only, to know where we are
        readonly double startV;
        double pctPerNotch;
        public string Name { get; }

        public WheelScroller(IntPtr root, POINT pt, Pane? pane)
        {
            this.pt = pt;
            sp = pane?.Sp;
            target = DeepestChild(root, pt);
            startV = Pct;
            Name = $"posted wheel messages{(pane != null ? $" to {pane.Name}" : "")}";
        }

        double Pct { get { try { return sp?.Current.VerticalScrollPercent ?? -1; } catch { return -1; } } }

        public double InitialAmount => 3;
        public double Clamp(double a) => Math.Clamp(a, 1, 40);
        public bool ToTop()
        {
            if (sp == null) { Post(-15); return true; } // no position info: caller watches the pixels
            SeekTo(0, 20);
            return false;
        }
        double lastPct = -1; int lastNotches;
        public bool Down(double notches)
        {
            double now = Pct;
            if (lastPct >= 0 && now > lastPct && lastNotches > 0) pctPerNotch = (now - lastPct) / lastNotches; // calibrates Restore
            if (sp != null && now >= 99.999) return false;
            lastPct = now; lastNotches = (int)Math.Round(notches);
            Post(lastNotches);
            return true;
        }
        public void Restore()
        {
            if (sp != null && startV >= 0) SeekTo(startV, 40);
        }

        // Posts wheel messages in bursts, watching the position via UI Automation reads, until it reaches target %.
        void SeekTo(double target, int maxNotches)
        {
            var sw = Stopwatch.StartNew();
            double last = Pct; long lastMove = 0;
            while (sw.ElapsedMilliseconds < 15000 && sw.ElapsedMilliseconds - lastMove < 1000)
            {
                double diff = target - last;
                if (Math.Abs(diff) < 0.2 || (target <= 0 && last <= 0.001) || (target >= 100 && last >= 99.999)) break;
                int n = pctPerNotch > 0 ? (int)Math.Round(diff / pctPerNotch) : Math.Sign(diff) * maxNotches;
                if (n == 0) n = Math.Sign(diff);
                Post(Math.Clamp(n, -maxNotches, maxNotches));
                // Wait for the position to settle (smooth scrolling animates).
                double prev = double.NaN;
                for (int i = 0; i < 20; i++)
                {
                    Thread.Sleep(30);
                    double now = Pct;
                    if (now == prev) break;
                    prev = now;
                }
                if (Math.Abs(prev - last) > 0.0001) { last = prev; lastMove = sw.ElapsedMilliseconds; }
            }
        }

        void Post(int notches) // positive = down
        {
            int delta = -120 * notches;
            var wp = (IntPtr)((delta & 0xFFFF) << 16);
            var lp = (IntPtr)(((pt.Y & 0xFFFF) << 16) | (pt.X & 0xFFFF));
            PostMessage(target, 0x020A /*WM_MOUSEWHEEL*/, wp, lp);
        }

        // Deepest child of root at the point, even if other windows cover it. WinUI 3 receives input
        // through a hidden, zero-size InputSiteWindowClass child, so prefer that when present.
        static IntPtr DeepestChild(IntPtr root, POINT screen)
        {
            var h = root;
            for (int i = 0; i < 32; i++)
            {
                var p = screen;
                ScreenToClient(h, ref p);
                var c = ChildWindowFromPointEx(h, p, 0x1 | 0x4 /*CWP_SKIPINVISIBLE | CWP_SKIPTRANSPARENT*/);
                if (c == IntPtr.Zero || c == h) break;
                h = c;
            }
            var site = FindWindowEx(h, IntPtr.Zero, "InputSiteWindowClass", null);
            return site != IntPtr.Zero ? site : h;
        }
    }
    // ---------- interop ----------
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; }

    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr wp, IntPtr lp);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] static extern IntPtr ChildWindowFromPointEx(IntPtr h, POINT p, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder sb, int max);
    [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
}
