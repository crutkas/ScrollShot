using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ScrollShot;

static partial class Program
{
    // Tray icon + global hotkey. Pressing the hotkey captures the pane under the mouse
    // (the cursor position is only read, never moved).
    static class TrayApp
    {
        public static int Run(Options o)
        {
            using var mutex = new Mutex(true, "ScrollShot.Tray", out bool isNew);
            if (!isNew) { MessageBox.Show("ScrollShot is already running in the tray.", "ScrollShot"); return 0; }
            if (!TryParseHotkey(o.Hotkey, out uint mods, out uint vk))
            {
                MessageBox.Show($"Couldn't understand hotkey \"{o.Hotkey}\". Example: Ctrl+Alt+S", "ScrollShot");
                return 1;
            }

            Application.EnableVisualStyles();
            var log = new StreamWriter(Path.Combine(Path.GetTempPath(), "ScrollShot.log"), append: false) { AutoFlush = true };
            var syncLog = TextWriter.Synchronized(log);
            Console.SetOut(syncLog); Console.SetError(syncLog);
            Console.WriteLine($"Tray started {DateTime.Now}, hotkey {o.Hotkey}");
            using var host = new HotkeyHost();
            if (!RegisterHotKey(host.Handle, 1, mods | 0x4000 /*MOD_NOREPEAT*/, vk))
            {
                MessageBox.Show($"{o.Hotkey} is already used by another app. Start with --hotkey to pick another.", "ScrollShot");
                return 1;
            }

            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ScrollShots");
            var idleIcon = Glyph.MakeIcon(false);
            var busyIcon = Glyph.MakeIcon(true);
            using var icon = new NotifyIcon { Icon = idleIcon, Visible = true, Text = $"ScrollShot ({o.Hotkey})" };
            bool busy = false;
            // Follow taskbar light/dark and accent color changes.
            UserPreferenceChangedEventHandler onTheme = (_, _) => host.BeginInvoke(() =>
            {
                var oldIdle = idleIcon; var oldBusy = busyIcon;
                idleIcon = Glyph.MakeIcon(false); busyIcon = Glyph.MakeIcon(true);
                icon.Icon = busy ? busyIcon : idleIcon;
                oldIdle.Dispose(); oldBusy.Dispose();
            });
            SystemEvents.UserPreferenceChanged += onTheme;
            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem($"Hover an app and press {o.Hotkey}") { Enabled = false });
            menu.Items.Add("Open ScrollShots folder", null, (_, _) =>
            {
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, _) => Application.Exit());
            icon.ContextMenuStrip = menu;
            icon.ShowBalloonTip(3000, "ScrollShot is running", $"Hover any app and press {o.Hotkey} for a full-page capture. Esc stops.", ToolTipIcon.None);

            string? lastPath = null;
            bool autoOpen = o.ForceOpen;
            o.Open = false; // never pull focus away from what you're doing; the notification opens it on click
            icon.BalloonTipClicked += (_, _) => { if (lastPath != null && File.Exists(lastPath)) Process.Start(new ProcessStartInfo(lastPath) { UseShellExecute = true }); };
            host.Pressed += () =>
            {
                Console.WriteLine($"Hotkey pressed (busy={busy})");
                if (busy) return;
                GetCursorPos(out var pt);
                var hwnd = GetAncestor(WindowFromPoint(pt), 2 /*GA_ROOT*/);
                if (hwnd == IntPtr.Zero) return;
                busy = true;
                icon.Icon = busyIcon;
                icon.Text = "ScrollShot - capturing... (Esc to stop)";
                var t = new Thread(() =>
                {
                    string? path = null; string? error = null;
                    try { path = CaptureToFile(o, hwnd, pt, usePoint: true); }
                    catch (Exception ex) { error = ex.Message; Console.WriteLine(ex); }
                    host.BeginInvoke(() =>
                    {
                        busy = false;
                        icon.Icon = idleIcon;
                        icon.Text = $"ScrollShot ({o.Hotkey})";
                        if (path == null) icon.ShowBalloonTip(4000, "ScrollShot", "Capture failed" + (error != null ? ": " + error : "."), ToolTipIcon.Warning);
                        else
                        {
                            lastPath = path;
                            if (autoOpen) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                            else icon.ShowBalloonTip(5000, "Full-page capture saved", $"Copied to clipboard. Click to open {Path.GetFileName(path)}", ToolTipIcon.None);
                        }
                    });
                });
                t.SetApartmentState(ApartmentState.STA);
                t.IsBackground = true;
                t.Start();
            };

            Application.Run();
            SystemEvents.UserPreferenceChanged -= onTheme;
            UnregisterHotKey(host.Handle, 1);
            icon.Visible = false;
            return 0;
        }

        sealed class HotkeyHost : Control
        {
            public event Action? Pressed;
            public HotkeyHost() => CreateHandle();
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x0312 /*WM_HOTKEY*/) Pressed?.Invoke();
                base.WndProc(ref m);
            }
        }

        static bool TryParseHotkey(string s, out uint mods, out uint vk)
        {
            mods = 0; vk = 0;
            foreach (var raw in s.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                switch (raw.ToLowerInvariant())
                {
                    case "ctrl": case "control": mods |= 0x2; break;
                    case "alt": mods |= 0x1; break;
                    case "shift": mods |= 0x4; break;
                    case "win": mods |= 0x8; break;
                    default:
                        string k = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;
                        if (k.Equals("PrtSc", StringComparison.OrdinalIgnoreCase) || k.Equals("PrintScreen", StringComparison.OrdinalIgnoreCase)) k = "PrintScreen";
                        if (!Enum.TryParse<Keys>(k, true, out var key)) return false;
                        vk = (uint)key;
                        break;
                }
            }
            return vk != 0;
        }
    }

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
}
