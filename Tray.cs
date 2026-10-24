using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScrollShot;

static partial class Program
{
    // Tray icon + global hotkey. Pressing the hotkey captures the pane under the mouse
    // (the cursor position is only read, never moved). Nothing here takes focus.
    internal static unsafe class Tray
    {
        const uint WM_TRAY = PInvoke.WM_APP + 1, WM_DONE = PInvoke.WM_APP + 2;
        const uint CMD_FOLDER = 1, CMD_EXIT = 2;

        static HWND wnd;
        static Options opts = null!;
        static bool busy;
        static string? lastPath, doneError;
        static HICON idleIcon, busyIcon;
        static uint taskbarCreated;
        static string folder = "";

        public static int Run(Options o)
        {
            opts = o;
            using var mutex = new Mutex(true, "ScrollShot.Tray", out bool isNew);
            if (!isNew) { Message("ScrollShot is already running in the tray."); return 0; }
            if (!TryParseHotkey(o.Hotkey, out var mods, out uint vk)) { Message($"Couldn't understand hotkey \"{o.Hotkey}\". Example: Ctrl+Alt+S"); return 1; }

            var log = new StreamWriter(Path.Combine(Path.GetTempPath(), "ScrollShot.log"), append: false) { AutoFlush = true };
            var syncLog = TextWriter.Synchronized(log);
            Console.SetOut(syncLog); Console.SetError(syncLog);
            Console.WriteLine($"Tray started {DateTime.Now}, hotkey {o.Hotkey}");

            folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ScrollShots");
            bool autoOpen = o.ForceOpen;
            o.Open = false; // never pull focus away from what you're doing; the notification opens it on click
            opts.ForceOpen = autoOpen;

            var hinst = PInvoke.GetModuleHandle((char*)null);
            fixed (char* cls = "ScrollShotTray")
            {
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(WNDCLASSEXW),
                    lpfnWndProc = &WndProc,
                    hInstance = hinst,
                    lpszClassName = cls,
                };
                PInvoke.RegisterClassEx(&wc);
                wnd = PInvoke.CreateWindowEx(0, new PCWSTR(cls), new PCWSTR(cls), 0, 0, 0, 0, 0, HWND.Null, HMENU.Null, new HINSTANCE(hinst.Value), null);
            }
            if (!PInvoke.RegisterHotKey(wnd, 1, mods | HOT_KEY_MODIFIERS.MOD_NOREPEAT, vk))
            {
                Message($"{o.Hotkey} is already used by another app. Start with --hotkey to pick another.");
                return 1;
            }
            taskbarCreated = PInvoke.RegisterWindowMessage("TaskbarCreated");

            MakeIcons();
            AddIcon();
            Balloon("ScrollShot is running", $"Hover any app and press {o.Hotkey} for a full-page capture. Esc stops.");

            MSG msg;
            while (PInvoke.GetMessage(&msg, HWND.Null, 0, 0) > 0)
            {
                PInvoke.TranslateMessage(&msg);
                PInvoke.DispatchMessage(&msg);
            }

            var nid = NewData();
            PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, &nid);
            PInvoke.UnregisterHotKey(wnd, 1);
            PInvoke.DestroyWindow(wnd);
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        static LRESULT WndProc(HWND h, uint msg, WPARAM w, LPARAM l)
        {
            try
            {
                if (msg == PInvoke.WM_HOTKEY) { OnHotkey(); return default; }
                if (msg == WM_TRAY)
                {
                    uint evt = (uint)(l.Value & 0xFFFF);
                    if (evt == PInvoke.WM_CONTEXTMENU) ShowMenu((short)(w.Value & 0xFFFF), (short)((w.Value >> 16) & 0xFFFF));
                    else if (evt == PInvoke.NIN_BALLOONUSERCLICK && lastPath != null && File.Exists(lastPath)) Output.Open(lastPath);
                    return default;
                }
                if (msg == WM_DONE) { OnDone(); return default; }
                if (msg == PInvoke.WM_SETTINGCHANGE) { MakeIcons(); SetIcon(busy ? busyIcon : idleIcon); }
                if (msg == taskbarCreated && taskbarCreated != 0) AddIcon(); // Explorer restarted
            }
            catch (Exception ex) { Console.WriteLine(ex); }
            return PInvoke.DefWindowProc(h, msg, w, l);
        }

        static void OnHotkey()
        {
            Console.WriteLine($"Hotkey pressed (busy={busy})");
            if (busy) return;
            PInvoke.GetCursorPos(out Point pt);
            nint target = Win.RootAt(pt);
            if (target == 0) return;
            busy = true;
            SetIcon(busyIcon, "ScrollShot - capturing... (Esc to stop)");
            var t = new Thread(() =>
            {
                string? path = null, error = null;
                try { Uia.Init(); path = CaptureToFile(opts, target, pt, usePoint: true); }
                catch (Exception ex) { error = ex.Message; Console.WriteLine(ex); }
                lastPath = path ?? lastPath;
                doneError = path == null ? error ?? "" : null;
                PInvoke.PostMessage(wnd, WM_DONE, default, default);
            }) { IsBackground = true };
            t.Start();
        }

        static void OnDone()
        {
            busy = false;
            SetIcon(idleIcon, $"ScrollShot ({opts.Hotkey})");
            if (doneError != null) Balloon("ScrollShot", "Capture failed" + (doneError.Length > 0 ? ": " + doneError : "."), warning: true);
            else if (lastPath != null)
            {
                if (opts.ForceOpen) Output.Open(lastPath);
                else Balloon("Full-page capture saved", $"Copied to clipboard. Click to open {Path.GetFileName(lastPath)}");
            }
        }

        static void ShowMenu(int x, int y)
        {
            var menu = PInvoke.CreatePopupMenu();
            Item(menu, MENU_ITEM_FLAGS.MF_STRING | MENU_ITEM_FLAGS.MF_GRAYED, 0, $"Hover an app and press {opts.Hotkey}");
            Item(menu, MENU_ITEM_FLAGS.MF_STRING, CMD_FOLDER, "Open ScrollShots folder");
            Item(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, null);
            Item(menu, MENU_ITEM_FLAGS.MF_STRING, CMD_EXIT, "Exit");
            PInvoke.SetForegroundWindow(wnd); // required so the menu closes when you click elsewhere
            var cmd = (uint)PInvoke.TrackPopupMenuEx(menu, 0x100 | 0x2 /*TPM_RETURNCMD | TPM_RIGHTBUTTON*/, x, y, wnd, (TPMPARAMS*)null).Value;
            PInvoke.PostMessage(wnd, PInvoke.WM_NULL, default, default);
            PInvoke.DestroyMenu(menu);
            if (cmd == CMD_FOLDER) { Directory.CreateDirectory(folder); Output.Open(folder); }
            else if (cmd == CMD_EXIT) PInvoke.PostQuitMessage(0);
        }

        static void Item(HMENU menu, MENU_ITEM_FLAGS flags, uint id, string? text)
        {
            fixed (char* p = text) PInvoke.AppendMenu(menu, flags, id, new PCWSTR(p));
        }

        // ---------- notify icon ----------

        static NOTIFYICONDATAW NewData() => new()
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = wnd,
            uID = 1,
        };

        static void MakeIcons()
        {
            var oldIdle = idleIcon; var oldBusy = busyIcon;
            idleIcon = Glyph.MakeIcon(false);
            busyIcon = Glyph.MakeIcon(true);
            if (oldIdle != default) PInvoke.DestroyIcon(oldIdle);
            if (oldBusy != default) PInvoke.DestroyIcon(oldBusy);
        }

        static void AddIcon()
        {
            var nid = NewData();
            nid.uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP;
            nid.uCallbackMessage = WM_TRAY;
            nid.hIcon = busy ? busyIcon : idleIcon;
            nid.szTip = $"ScrollShot ({opts.Hotkey})";
            PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, &nid);
            nid.Anonymous.uVersion = 4; // NOTIFYICON_VERSION_4
            PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_SETVERSION, &nid);
        }

        static void SetIcon(HICON icon, string? tip = null)
        {
            var nid = NewData();
            nid.uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP | (tip != null ? NOTIFY_ICON_DATA_FLAGS.NIF_TIP : 0);
            nid.hIcon = icon;
            if (tip != null) nid.szTip = tip;
            PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, &nid);
        }

        static void Balloon(string title, string text, bool warning = false)
        {
            var nid = NewData();
            nid.uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_INFO;
            nid.szInfoTitle = title;
            nid.szInfo = text;
            nid.dwInfoFlags = warning ? NOTIFY_ICON_INFOTIP_FLAGS.NIIF_WARNING : NOTIFY_ICON_INFOTIP_FLAGS.NIIF_USER | NOTIFY_ICON_INFOTIP_FLAGS.NIIF_LARGE_ICON;
            nid.hBalloonIcon = idleIcon;
            PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, &nid);
        }

        static void Message(string text) =>
            PInvoke.MessageBox(HWND.Null, text, "ScrollShot", MESSAGEBOX_STYLE.MB_OK | MESSAGEBOX_STYLE.MB_ICONINFORMATION);

        static bool TryParseHotkey(string s, out HOT_KEY_MODIFIERS mods, out uint vk)
        {
            mods = 0; vk = 0;
            foreach (var raw in s.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string k = raw.ToUpperInvariant();
                switch (k)
                {
                    case "CTRL": case "CONTROL": mods |= HOT_KEY_MODIFIERS.MOD_CONTROL; break;
                    case "ALT": mods |= HOT_KEY_MODIFIERS.MOD_ALT; break;
                    case "SHIFT": mods |= HOT_KEY_MODIFIERS.MOD_SHIFT; break;
                    case "WIN": mods |= HOT_KEY_MODIFIERS.MOD_WIN; break;
                    default:
                        if (k.Length == 1 && char.IsAsciiLetterOrDigit(k[0])) vk = k[0];
                        else if (k[0] == 'F' && int.TryParse(k[1..], out int fn) && fn is >= 1 and <= 24) vk = (uint)(0x70 + fn - 1);
                        else if (k is "PRTSC" or "PRINTSCREEN" or "SNAPSHOT") vk = 0x2C;
                        else return false;
                        break;
                }
            }
            return vk != 0;
        }
    }
}
