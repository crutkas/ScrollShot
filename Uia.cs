using System.Drawing;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.System.Variant;
using Windows.Win32.UI.Accessibility;

namespace ScrollShot;

static partial class Program
{
    // UI Automation via COM. Used read-only (find the pane, read its scroll position):
    // invoking UIA scroll methods makes many apps (XAML) bring themselves to the front.
    internal static unsafe class Uia
    {
        [ThreadStatic] static nint automation; // IUIAutomation*

        public static void Init()
        {
            PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);
            if (automation != 0) return;
            try { CUIAutomation8.CreateInstance(out IUIAutomation* a).ThrowOnFailure(); automation = (nint)a; }
            catch (Exception ex) { Console.Error.WriteLine($"UI Automation unavailable: {ex.Message}"); }
        }

        static IUIAutomation* A => (IUIAutomation*)automation;

        internal sealed class Pane
        {
            public IUIAutomationScrollPattern* Sp;
            public string Name = "";
            public Point Point; // inside the pane's visible part

            public double VerticalPercent { get { try { return Sp->CurrentVerticalScrollPercent; } catch { return -1; } } }
        }

        public static Pane? FindPane(nint hwnd, Point? at)
        {
            if (A == null) return null;
            try
            {
                IUIAutomationElement* el = null;
                if (at is Point p)
                {
                    // Walk up from the element at the point to the nearest vertically scrollable pane.
                    var e = A->ElementFromPoint(p);
                    var walker = A->ControlViewWalker;
                    while (e != null && ScrollPatternOf(e) == null)
                    {
                        var parent = walker->GetParentElement(e);
                        e->Release();
                        e = parent;
                    }
                    walker->Release();
                    el = e;
                }
                if (el == null)
                {
                    // Largest vertically scrollable element in the window.
                    var root = A->ElementFromHandle((HWND)hwnd);
                    var v = new VARIANT();
                    v.Anonymous.Anonymous.vt = VARENUM.VT_BOOL;
                    v.Anonymous.Anonymous.Anonymous.boolVal = VARIANT_BOOL.VARIANT_TRUE;
                    var cond = A->CreatePropertyCondition(UIA_PROPERTY_ID.UIA_IsScrollPatternAvailablePropertyId, v);
                    var all = root->FindAll(TreeScope.TreeScope_Subtree, cond);
                    double bestArea = 0;
                    for (int i = 0; i < all->Length; i++)
                    {
                        var e = all->GetElement(i);
                        var sp = ScrollPatternOf(e);
                        if (sp != null)
                        {
                            sp->Release();
                            var r = e->CurrentBoundingRectangle;
                            double area = (double)(r.right - r.left) * (r.bottom - r.top);
                            if (area > bestArea) { bestArea = area; if (el != null) el->Release(); el = e; continue; }
                        }
                        e->Release();
                    }
                    all->Release(); cond->Release(); root->Release();
                }
                if (el == null) return null;

                // Pane bounds can extend past the window; aim at the middle of the visible part.
                var b = el->CurrentBoundingRectangle;
                var wr = Win.WindowRect(hwnd);
                int l = Math.Max(b.left, wr.left), t = Math.Max(b.top, wr.top), r2 = Math.Min(b.right, wr.right), b2 = Math.Min(b.bottom, wr.bottom);
                var pt = at is Point ap && ap.X >= l && ap.X < r2 && ap.Y >= t && ap.Y < b2 ? ap : new Point((l + r2) / 2, (t + b2) / 2);
                var name = el->CurrentName;
                var pane = new Pane { Sp = ScrollPatternOf(el), Name = $"{ControlTypeName(el->CurrentControlType)} \"{name}\"", Point = pt };
                PInvoke.SysFreeString(name);
                el->Release();
                return pane.Sp != null ? pane : null;
            }
            catch (Exception ex) { Console.Error.WriteLine($"UI Automation: {ex.Message}"); return null; }
        }

        // Returns an AddRef'd vertically scrollable ScrollPattern, or null.
        static IUIAutomationScrollPattern* ScrollPatternOf(IUIAutomationElement* e)
        {
            var unk = e->GetCurrentPattern(UIA_PATTERN_ID.UIA_ScrollPatternId);
            if (unk == null) return null;
            IUIAutomationScrollPattern* sp = null;
            bool ok = unk->QueryInterface(out sp).Succeeded;
            unk->Release();
            if (!ok || sp == null) return null;
            if (sp->CurrentVerticallyScrollable) return sp;
            sp->Release();
            return null;
        }

        static string ControlTypeName(UIA_CONTROLTYPE_ID id) => (int)id switch
        {
            50030 => "Document", 50033 => "Pane", 50008 => "List", 50026 => "Group", 50025 => "Custom",
            50028 => "DataGrid", 50023 => "Tree", 50032 => "Window", 50036 => "Table", _ => "Element",
        };
    }

    // Fallback only (--uia): UI Automation scroll calls; many apps come to the front when invoked.
    sealed unsafe class UiaScroller(Uia.Pane pane) : IScroller
    {
        readonly double startH = Get(() => pane.Sp->CurrentHorizontallyScrollable ? pane.Sp->CurrentHorizontalScrollPercent : -1);
        readonly double startV = pane.VerticalPercent;
        static double Get(Func<double> f) { try { return f(); } catch { return -1; } }

        public string Name => $"UI Automation calls on {pane.Name} (may bring the app to the front)";
        public double InitialAmount { get { double v = Get(() => pane.Sp->CurrentVerticalViewSize); return v > 0 && v < 100 ? v * 0.5 : 5; } }
        public double Clamp(double a) => Math.Clamp(a, 0.01, 50);
        public bool ToTop() { pane.Sp->SetScrollPercent(-1, 0); return false; }
        public double Position => pane.VerticalPercent;
        public void Back(double amount) { double cur = pane.VerticalPercent; if (cur > 0) pane.Sp->SetScrollPercent(-1, Math.Max(0, cur - amount)); }
        public bool Down(double amount)
        {
            double cur = pane.VerticalPercent;
            if (cur < 0 || cur >= 99.999) return false;
            pane.Sp->SetScrollPercent(-1, Math.Min(100, cur + amount));
            return true;
        }
        public void Restore()
        {
            try { pane.Sp->SetScrollPercent(startH, startV); }
            catch (Exception ex) { Console.Error.WriteLine($"Couldn't restore scroll position: {ex.Message}"); }
        }
    }
}
