# Windows Publish

This document records the manual Windows release flow for `ccusage-gui` v0.1.

## Prerequisites

- Windows 10 or later.
- .NET 10 SDK for publishing.
- For the default framework-dependent package, target machines need the .NET 10 Desktop Runtime.
- `ccusage` CLI must be installed on the target machine and available on `PATH`.
  - npm/pnpm/yarn installations commonly expose `ccusage.cmd` on Windows.
  - The app resolves extensionless `ccusage` commands against `PATHEXT` so published builds can launch those shims with `UseShellExecute=false`.

## Default publish command

Run from the `ccusage-gui` directory:

```bat
dotnet publish src\CCUsage.App\CCUsage.App.csproj -c Release -r win-x64 --self-contained false
```

Output directory:

```text
src\CCUsage.App\bin\Release\net10.0\win-x64\publish\
```

Start the app with:

```bat
src\CCUsage.App\bin\Release\net10.0\win-x64\publish\CCUsage.App.exe
```

## Optional self-contained publish

Use this when the target machine should not require a separately installed .NET Desktop Runtime:

```bat
dotnet publish src\CCUsage.App\CCUsage.App.csproj -c Release -r win-x64 --self-contained true
```

The self-contained output is larger than the default framework-dependent output.

## Smoke test checklist

After publishing, verify these items on a Windows machine:

1. Launch `CCUsage.App.exe` from the publish directory.
2. Confirm the Dashboard tab appears without crashing.
3. Click `刷新数据` on Dashboard.
4. Confirm local `ccusage` data is loaded.
5. Open the Reports tab and click `刷新列表`.
6. Confirm daily rows are visible when cached daily data exists.
7. Confirm the tray icon/menu is present when supported by the platform environment.
8. Use tray menu `Open` to show the window.
9. Use tray menu `Exit` to close the app.

## Known limitations

- No installer is produced in v0.1.
- No code signing is configured.
- No auto-update flow is configured.
- The default package is framework-dependent and requires the .NET 10 Desktop Runtime on target machines.
- Refresh depends on a working local `ccusage` CLI installation and readable local usage logs.
- Tray icon behavior may vary by Windows shell and Avalonia platform support.
