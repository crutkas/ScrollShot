using System.Diagnostics;
using System.Drawing;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScrollShot;

static partial class Program
{
    internal static unsafe class Win
    {
        public static RECT WindowRect(nint h) { PInvoke.GetWindowRect((HWND)h, out var r); return r; }

        public static nint RootAt(Point p) => PInvoke.GetAncestor(PInvoke.WindowFromPoint(p), GET_ANCESTOR_FLAGS.GA_ROOT);

        public static string Title(nint h)
        {
            char* buf = stackalloc char[512];
            int n = PInvoke.GetWindowText((HWND)h, buf, 512);
            return new string(buf, 0, Math.Max(0, n));
        }

        public static nint FindTopWindow(string titlePart)
        {
            HWND h = HWND.Null;
            while ((h = PInvoke.FindWindowEx(HWND.Null, h, null, null)) != HWND.Null)
            {
                if (PInvoke.IsWindowVisible(h) && !PInvoke.IsIconic(h) && Title(h).Contains(titlePart, StringComparison.OrdinalIgnoreCase))
                    return h;
            }
            return 0;
        }

        // Deepest child of root at the point, even if other windows cover it. WinUI 3 receives input
        // through a hidden, zero-size InputSiteWindowClass child, so prefer that when present.
        public static nint DeepestChild(nint root, Point screen)
        {
            var h = (HWND)root;
            for (int i = 0; i < 32; i++)
            {
                var p = screen;
                PInvoke.ScreenToClient(h, &p);
                var c = PInvoke.ChildWindowFromPointEx(h, p, CWP_FLAGS.CWP_SKIPINVISIBLE | CWP_FLAGS.CWP_SKIPTRANSPARENT);
                if (c == HWND.Null || c == h) break;
                h = c;
            }
            var site = PInvoke.FindWindowEx(h, HWND.Null, "InputSiteWindowClass", null);
            return site != HWND.Null ? site : h;
        }

        // Positive notches scroll down. Posted (not injected), so the real mouse and focus are untouched.
        // One message per notch: some apps (e.g. RichEdit/Notepad) scroll a fixed step per message
        // regardless of the delta, while others handle either form the same.
        public static void PostWheel(nint target, Point pt, int notches)
        {
            int delta = notches > 0 ? -120 : 120;
            var wp = (nuint)(uint)((delta & 0xFFFF) << 16);
            var lp = (nint)(((pt.Y & 0xFFFF) << 16) | (pt.X & 0xFFFF));
            for (int i = 0; i < Math.Abs(notches); i++)
                PInvoke.PostMessage((HWND)target, PInvoke.WM_MOUSEWHEEL, new WPARAM(wp), new LPARAM(lp));
        }
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
        double Position => -1;      // vertical scroll percent if known, else -1
        void Back(double amount);   // scroll up a bit (undo part of an overshooting step)
    }

    // Default: wheel messages posted straight to the pane's window. No input injection, no activation.
    sealed class WheelScroller : IScroller
    {
        readonly nint target;
        readonly Point pt;
        readonly Uia.Pane? pane; // read-only position info
        readonly double startV;
        double pctPerNotch;
        double lastPct = -1; int lastNotches;
        public string Name { get; }

        public WheelScroller(nint root, Point pt, Uia.Pane? pane)
        {
            this.pt = pt;
            this.pane = pane;
            target = Win.DeepestChild(root, pt);
            startV = Pct;
            Name = $"posted wheel messages{(pane != null ? $" to {pane.Name}" : "")}";
        }

        double Pct => pane?.VerticalPercent ?? -1;
        public double Position => Pct;
        public void Back(double notches) => Win.PostWheel(target, pt, -Math.Max(1, (int)Math.Round(notches)));

        public double InitialAmount => 3;
        public double Clamp(double a) => Math.Clamp(a, 1, 40);

        public bool ToTop()
        {
            if (pane == null) { Win.PostWheel(target, pt, -15); return true; } // no position info: caller watches pixels
            SeekTo(0, 40);
            return false;
        }

        public bool Down(double notches)
        {
            double now = Pct;
            if (lastPct >= 0 && now > lastPct && lastNotches > 0) pctPerNotch = (now - lastPct) / lastNotches; // calibrates Restore
            if (pane != null && now >= 99.999) return false;
            lastPct = now; lastNotches = (int)Math.Round(notches);
            Win.PostWheel(target, pt, lastNotches);
            return true;
        }

        public void Restore()
        {
            if (pane != null && startV >= 0) SeekTo(startV, 40);
        }

        // Posts wheel messages in bursts, watching the position via UI Automation reads, until it reaches target %.
        void SeekTo(double targetPct, int maxNotches)
        {
            var sw = Stopwatch.StartNew();
            double last = Pct; long lastMove = 0;
            int flips = 0, lastSign = 0;
            while (sw.ElapsedMilliseconds < 15000 && sw.ElapsedMilliseconds - lastMove < 1000)
            {
                double diff = targetPct - last;
                // Wheel steps are coarse; within half a step is as close as we can get.
                double tolerance = Math.Max(0.2, pctPerNotch * 0.6);
                if (Math.Abs(diff) < tolerance || (targetPct <= 0 && last <= 0.001) || (targetPct >= 100 && last >= 99.999)) break;
                int sign = Math.Sign(diff);
                if (lastSign != 0 && sign != lastSign && ++flips >= 3) break; // oscillating around the target
                lastSign = sign;
                int n = pctPerNotch > 0 ? (int)Math.Round(diff / pctPerNotch) : sign * maxNotches;
                if (n == 0) n = sign;
                Win.PostWheel(target, pt, Math.Clamp(n, -maxNotches, maxNotches));
                double prev = Settle(last);
                if (Math.Abs(prev - last) > 0.0001) { last = prev; lastMove = sw.ElapsedMilliseconds; }
            }
        }

        // Smooth scrolling animates: wait for the position to start moving, then to stop.
        double Settle(double from)
        {
            double cur = Pct;
            for (int i = 0; i < 12 && Math.Abs(cur - from) < 0.0001; i++) { Thread.Sleep(25); cur = Pct; }
            for (int i = 0; i < 40; i++)
            {
                Thread.Sleep(40);
                double now = Pct;
                if (Math.Abs(now - cur) < 0.0001) break;
                cur = now;
            }
            return cur;
        }
    }
}
