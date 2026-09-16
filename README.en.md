# LiteDesktopSwitcher

[中文](README.md)

A small Windows utility that does one thing: press `Alt+1` through `Alt+9` to switch directly to a virtual desktop.

There is no settings window or tray menu. The app runs quietly in the background, makes no network connections, collects no telemetry, and requires no additional runtime installation.

## Download and use

1. Download `LiteDesktopSwitcher-v1.0.0.zip` from [Releases](../../releases/latest).
2. Extract the archive and run `LiteDesktopSwitcher.exe`.
3. Press `Alt+1` through `Alt+9` to switch desktops.

Only shortcuts for existing desktops are registered. For example, if you have four desktops, the app registers `Alt+1` through `Alt+4`. If another app already owns a shortcut, LiteDesktopSwitcher shows a warning and retries periodically.

The app has no window. To stop it, end `LiteDesktopSwitcher.exe` in Task Manager.

## Start with Windows

Press `Win+R`, enter `shell:startup`, then place `LiteDesktopSwitcher.exe` or a shortcut to it in the folder that opens.

Stop the old version in Task Manager before updating. LiteDesktopSwitcher is single-instance, so a new copy exits immediately while an older copy is still running.

## Compatibility

- 64-bit Windows 10 or Windows 11
- The .NET Framework 4.x included with Windows
- Up to nine virtual desktops

LiteDesktopSwitcher uses the Windows virtual desktop COM interface when available. If that interface is unavailable, it falls back to the standard Windows shortcut only after confirming the current desktop. The COM interface is undocumented, so a future major Windows update may require a compatibility update.

The release executable is not code-signed. Windows SmartScreen may display a warning the first time it runs. Download releases only from this repository and compare the file against the published SHA-256 checksum.

## Logs and self-test

The log file is stored at:

```text
%LOCALAPPDATA%\LiteDesktopSwitcher\LiteDesktopSwitcher.log
```

The self-test enumerates desktops, checks the COM interface, and temporarily registers a test shortcut. It does not switch desktops:

```powershell
.\LiteDesktopSwitcher.exe --self-test
$LASTEXITCODE
Get-Content .\LiteDesktopSwitcher.selftest.txt
```

## Build from source

On 64-bit Windows 10 or Windows 11, run:

```powershell
.\build.ps1
```

The script uses the 64-bit .NET Framework C# compiler included with Windows and downloads no dependencies.

## Credits

The core interaction was inspired by [choyy/VirtualDesktopSwitcher](https://github.com/choyy/VirtualDesktopSwitcher). That project is MIT-licensed; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for its copyright notice.

## License

[MIT](LICENSE)
