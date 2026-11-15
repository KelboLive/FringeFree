# FringeFree

A tiny Windows tray app that reduces colour fringing on text on **QD-OLED and WOLED monitors**, with a one-click switch for **Brave** too.

OLED monitors use subpixel layouts (triangular on QD-OLED, RWBG/RGWB on WOLED) that Windows ClearType doesn't expect. ClearType assumes a vertical RGB stripe, so small text can show coloured halos. FringeFree switches Windows' text antialiasing between presets so you can pick the one that looks best on your panel. It doesn't change the physical subpixels, so it reduces fringing rather than removing it entirely.

## Features

- **Tray icon with three presets**
  - **Grayscale**: no colour subpixel rendering. Usually best on WOLED.
  - **Reduced ClearType (50%)**: keeps some sharpness with weaker colour. Worth trying on QD-OLED.
  - **Windows default**: standard ClearType.
- **Applies instantly** to the system font-smoothing setting and the DirectWrite ClearType level, and notifies running windows.
- **Backs up your original settings** on first run, with a **Restore my original settings** menu item.
- **Brave support**: adds Chromium's `--disable-lcd-text` flag to Brave's shortcuts (desktop, Start menu, taskbar pin) so Brave uses grayscale text. Includes a **Restart Brave now** button. The flag is re-applied on launch in case a Brave update recreates the shortcuts.
- **Tells you which browsers need a restart** after you change a preset.
- **Start with Windows** toggle, and a shortcut to the built-in ClearType tuner.
- **Lightweight**: one C# file, no installer, no dependencies, no admin rights, nothing injected into other apps.

## Install

1. Run `build.bat`. It builds `FringeFree.exe` using the C# compiler already included with Windows.
2. Run `FringeFree.exe`. An "Aa" icon appears in the system tray; click it to choose a preset.
3. Fully close and reopen your browser (and any app that still looks fringed).

## What it changes

All changes are per-user (`HKCU`):

| Setting | Location |
| --- | --- |
| `FontSmoothing`, `FontSmoothingType` (1 = grayscale, 2 = ClearType) | `Control Panel\Desktop` |
| `ClearTypeLevel` (0, 50, 100) | `Software\Microsoft\Avalon.Graphics\DISPLAY*` |
| Brave shortcut arguments | `.lnk` files that point at `brave.exe` |
| Backup and preferences | `%APPDATA%\OledTextFix\backup.ini`, `HKCU\Software\OledTextFix` |

## Limitations

- Apps with their own text renderer (for example Adobe Reader) ignore these settings.
- Windows reportedly applies one text setting to all monitors, so a second non-OLED monitor gets the same mode.
- On QD-OLED, grayscale text still shows some fringing at the top and bottom of glyphs because of the triangular layout. The best fix is a panel with a V-Stripe or RGB-stripe layout, or higher pixel density.
- Shortcut patching skips folders that need admin rights. Windows can cache pinned taskbar shortcuts; re-pin Brave if the taskbar icon doesn't pick up the flag.
- Only Windows is supported.
