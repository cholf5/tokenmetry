# Windows Publish

This document records the manual Windows release flow for Tokenmetry v0.1.

## Prerequisites

- Windows 10 or later.
- .NET 10 SDK for publishing.
- For the default framework-dependent package, target machines need the .NET 10 Desktop Runtime.
- `ccusage` CLI must be installed on the target machine and available on `PATH`.
  - npm/pnpm/yarn installations commonly expose `ccusage.cmd` on Windows.
  - The app resolves extensionless `ccusage` commands against `PATHEXT` so published builds can launch those shims with `UseShellExecute=false`.

## Default publish command

Run from the repository root (Tokenmetry):

```bat
dotnet publish src\Tokenmetry.App\Tokenmetry.App.csproj -c Release -r win-x64 --self-contained false
```

Output directory:

```text
src\Tokenmetry.App\bin\Release\net10.0\win-x64\publish\
```

Start the app with:

```bat
src\Tokenmetry.App\bin\Release\net10.0\win-x64\publish\Tokenmetry.App.exe
```

## Optional self-contained publish

Use this when the target machine should not require a separately installed .NET Desktop Runtime:

```bat
dotnet publish src\Tokenmetry.App\Tokenmetry.App.csproj -c Release -r win-x64 --self-contained true
```

The self-contained output is larger than the default framework-dependent output.

## Smoke test checklist

After publishing, verify these items on a Windows machine:

1. Launch `Tokenmetry.App.exe` from the publish directory.
2. Confirm the Dashboard tab appears without crashing.
3. Click `刷新数据` on Dashboard.
4. Confirm local `ccusage` data is loaded.
5. Open the Reports tab and click `刷新列表`.
6. Confirm daily rows are visible when cached daily data exists.
7. Confirm the tray icon is present in the system tray.
8. Close the main window with `X` — the process must stay alive with the tray icon visible (close = hide to tray).
9. Use tray menu `打开主窗` to recall the window; confirm the tray tooltip / 菜单首行 shows 今日成本 after the first refresh.
10. Use tray menu `退出` to exit — the process must terminate cleanly (window `X` must not be the exit path).

## Known limitations

- No installer is produced in v0.1.
- No code signing is configured.
- No auto-update flow is configured.
- The default package is framework-dependent and requires the .NET 10 Desktop Runtime on target machines.
- Refresh depends on a working local `ccusage` CLI installation and readable local usage logs.
- The tray icon is registered via Avalonia `TrayIcon.SetIcons` and hides the main window on close (A-0). On macOS the status-bar icon uses a monochrome template asset that follows the menu bar appearance; the tray tooltip has no macOS equivalent, so the today-cost is shown as the first (disabled) menu row.
