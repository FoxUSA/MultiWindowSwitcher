<div align="center">

<img src="images/icon.png" width="96" alt="MultiWindowSwitcher logo">

# MultiWindowSwitcher

**Every Windows window, live.**

Every open and minimized window as a live preview in one grid.
Hover the edge icon — see it, click — switch.

[![Download .zip](https://img.shields.io/badge/Download-MultiWindowSwitcher.zip-2ea44f?style=for-the-badge)](https://byfox.dev/data/multiwindowswitcher/MultiWindowSwitcher.zip)
[![Live demo](https://img.shields.io/badge/Live%20demo-in%20browser-4CA6FF?style=for-the-badge)](https://byfox.dev/multiwindowswitcher/demo/?lang=en)
[![Website](https://img.shields.io/badge/Website-byfox.dev-4ecdc4?style=for-the-badge)](https://byfox.dev/en/multiwindowswitcher/)

![Windows 10 / 11](https://img.shields.io/badge/Windows-10%20%2F%2011-0078D6?logo=windows)
![English · Русский](https://img.shields.io/badge/UI-English%20%C2%B7%20%D0%A0%D1%83%D1%81%D1%81%D0%BA%D0%B8%D0%B9-444)
![Free](https://img.shields.io/badge/price-free-2ea44f)
[![License: GPL-3.0](https://img.shields.io/badge/license-GPL--3.0-blue)](LICENSE)

**English** · [Русский](README.ru.md)

</div>

See what's going on in every window at once — minimized ones too. Pick one to switch, or just look and move the mouse away — it all slides off.

- **Live previews** — every window in an even grid, previews update live. Minimized ones too: the window quietly keeps rendering.
- **Slides in from the edge** — edge icon, hot corner or tray. It slides in over any app without moving anything and slides off by itself.
- **Your own names** — double-click to give a window a big name: “Server”, “Work”. It's recognized again after a restart.
- **Order** — the grid fits any size. **Group** puts windows of the same app together, pinned ones first.
- **Events** — a window flashes on the taskbar, its tile and the edge icon light up.
- **All by mouse** — click to switch, middle button to close, right button for the menu. Show the windows with any mouse click you record — on an empty title bar or the desktop.
- **Close duplicate folders** — one folder, one File Explorer window: duplicates close at startup and whenever a folder is opened again.

## Get started

1. Unpack the [archive](https://byfox.dev/data/multiwindowswitcher/MultiWindowSwitcher.zip)
2. Run `MultiWindowSwitcher.exe`
3. Hover the icon at the right edge

Not sure yet? Try the [live demo](https://byfox.dev/multiwindowswitcher/demo/?lang=en) right in the browser.

Windows 10 / 11 (x64), requires [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0). Free, open source, no telemetry.

## Build from source

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

```powershell
git clone https://github.com/FoxUSA/MultiWindowSwitcher.git
cd MultiWindowSwitcher
powershell -ExecutionPolicy Bypass -File .\Data\build.ps1
```

The launcher `MultiWindowSwitcher.exe` appears in the repository root, the program in `Data`, your settings in `UserData`. Sources: `Data/src/App` (the program, WPF + [WPF UI](https://github.com/lepoco/wpfui)), `Data/src/Launcher` (the launcher).

## License

[GPL-3.0](LICENSE) — use, change and share freely, with a link to the author ([NOTICE](NOTICE)); programs built on this code must stay open source too.

---

<sub>Keywords: window switcher, Alt+Tab alternative, live window previews, see all open windows, minimized window preview, Windows productivity.</sub>
