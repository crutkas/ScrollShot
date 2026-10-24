# ScrollShot

Full-page (scrolling) screenshots for any Windows app — like Edge's "Full page capture", but for desktop apps.

ScrollShot never moves your mouse or steals focus. It scrolls the app with posted wheel messages,
captures it with `PrintWindow` (the window can sit behind others), and stitches the frames together.
It's a ~1.2 MB Native AOT executable with no runtime dependencies, for x64 and arm64.

## Install

From [Releases](https://github.com/crutkas/ScrollShot/releases):

- **`ScrollShot_<version>_arm64_x64.msixbundle`** — installs for your architecture, adds a Start menu entry
  and a `scrollshot` command. If the release includes `ScrollShot.cer`, trust it once (admin):
  `Import-Certificate ScrollShot.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople`
- **`ScrollShot_<version>_<arch>.zip`** — a single portable `ScrollShot.exe`.

## Use

Run ScrollShot with no arguments (or from the Start menu) to put it in the tray. Hover over any app and
press **Ctrl+Alt+S**. The capture is saved to `Pictures\ScrollShots`, copied to the clipboard, and announced
with a notification (click it to open). Press **Esc** to stop a capture early (useful for infinite feeds).

Command line:

```
scrollshot --window "Settings"        # capture a window by title
scrollshot --at 900,700               # capture the pane at a screen point
scrollshot --hotkey "Ctrl+Shift+F12"  # tray mode with a different hotkey
scrollshot --help                     # all options
```

## How it works

1. Finds the scrollable pane with UI Automation — read-only, because invoking UIA scroll methods makes
   many apps bring themselves to the front.
2. Scrolls by posting `WM_MOUSEWHEEL` to the pane's window (for WinUI 3, its hidden
   `InputSiteWindowClass` child), one message per notch.
3. Captures each step with `PrintWindow(PW_RENDERFULLCONTENT)`. The pane's scroll percentage predicts
   each step's pixel shift; a fuzzy row-signature match confirms it (and is the fallback when no position
   is available). Steps that overshoot (apps accelerate bursts of wheel input) are backed up.
4. Keeps fixed headers/footers once, fills non-scrolling columns (sidebars) with the background,
   restores the original scroll position, and saves a PNG via Windows Imaging Component.

`--uia` scrolls via UI Automation calls instead, for apps that ignore wheel messages; this can bring the
app to the front. Browsers are not a target yet (they throttle rendering when hidden).

## Build

Requires the .NET 10 SDK, the MSVC build tools for x64 and arm64 (for Native AOT), and the
[winapp CLI](https://github.com/microsoft/WinAppCli).

```
dotnet build                              # quick dev build
./build.ps1                               # Native AOT x64 + arm64, portable zips, unsigned .msixbundle in dist/
./build.ps1 -Cert devcert.pfx             # signed (winapp cert generate --manifest packaging/Package.appxmanifest)
```

Pushing a `v*` tag runs the [release workflow](.github/workflows/release.yml), which builds the same
artifacts and publishes a GitHub release (signed if `SIGNING_PFX_BASE64`/`SIGNING_PFX_PASSWORD` secrets exist).

## License

MIT - see [LICENSE](LICENSE).
