# Changelog

## 1.0.0 - 2026-09-17

- Added direct switching to virtual desktops 1–9 with `Alt+1` through `Alt+9`.
- Added compatibility paths for Windows 10 and multiple Windows 11 virtual desktop interfaces.
- Added a verified system-shortcut fallback when the private COM interface is unavailable.
- Isolated hotkey handling from COM work to keep rapid input responsive.
- Added recovery after Explorer restarts, HRESULT checks, retry handling, logging, and a safe self-test mode.
- Added session-aware desktop detection for RDP and multi-session environments.
- Fixed newly created or removed desktops not updating the registered `Alt+number` shortcut range until the app restarted.
