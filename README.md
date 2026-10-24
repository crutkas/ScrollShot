# ScrollShot

Full-page (scrolling) screenshots for any Windows app — like Edge's "Full page capture", but for desktop apps.

ScrollShot never moves your mouse or steals focus. It scrolls the app with posted wheel messages,
captures it with `PrintWindow` (the window can sit behind others), and stitches the frames together.

## Use

Run `ScrollShot.exe` with no arguments to put it in the tray. Hover over any app and press **Ctrl+Alt+S**.
The capture is saved to `Pictures\ScrollShots`, copied to the clipboard, and announced with a notification
(click it to open). Press **Esc** to stop a capture early (useful for infinite feeds).

Command line:

```
ScrollShot.exe --window "Settings"     # capture a window by title
ScrollShot.exe --at 900,700            # capture the pane at a screen point
ScrollShot.exe --hotkey "Ctrl+Shift+F12"  # tray mode with a different hotkey
ScrollShot.exe --help                  # all options
```

## How it works

1. Finds the scrollable pane via UI Automation (read-only, so the app isn't activated).
2. Scrolls to the top, then down in steps by posting `WM_MOUSEWHEEL` to the pane's window
   (for WinUI 3, its hidden `InputSiteWindowClass` child).
3. Captures each step with `PrintWindow(PW_RENDERFULLCONTENT)` and finds the overlap with a fuzzy
   row-signature match (tolerates sub-pixel scrolling and overlay scrollbars).
4. Keeps fixed headers/footers once, fills non-scrolling columns (sidebars) with the background,
   restores the original scroll position, and saves a PNG.

`--uia` scrolls via UI Automation calls instead, for apps that ignore wheel messages; note this can
bring the app to the front.

## Build

Requires the .NET 10 SDK.

```
dotnet build -c Release
```

Browsers are not a target yet (they throttle rendering when hidden).

## License

MIT - see [LICENSE](LICENSE).
