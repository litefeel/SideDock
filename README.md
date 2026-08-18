# SideDock

SideDock is a Windows 11-only source-run sidebar app inspired by the detached Microsoft Edge sidebar.

## Requirements

- Windows 11 (build 22000 or later); Windows 10 is not supported
- .NET 10 SDK
- Microsoft Edge WebView2 Runtime

## Run

```powershell
$env:DOTNET_CLI_HOME = "$PWD\.dotnet_home"
$env:NUGET_PACKAGES = "$PWD\.nuget\packages"
dotnet run --project src\SideDock\SideDock.csproj
```

## Test

```powershell
$env:DOTNET_CLI_HOME = "$PWD\.dotnet_home"
$env:NUGET_PACKAGES = "$PWD\.nuget\packages"
dotnet test tests\SideDock.Tests\SideDock.Tests.csproj --configuration Release
```

## Build MSI Installer

Build the installer:

```cmd
scripts\Build-Installer.cmd
```

Build a specific installer version:

```cmd
scripts\Build-Installer.cmd 0.0.24 win-x64
```

Build the smaller framework-dependent installer without the .NET runtime:

```cmd
scripts\Build-Installer.cmd 0.0.24 win-x64 no-runtime
```

This publishes a self-contained `win-x64` release with `PublishReadyToRun=true`, then builds:

```text
artifacts\installer\SideDock-0.0.24-win-x64.msi
```

The no-runtime variant builds:

```text
artifacts\installer\SideDock-0.0.24-win-x64-no-runtime.msi
```

WPF does not support .NET NativeAOT, so this project uses ReadyToRun AOT publishing instead of NativeAOT.

Install:

```cmd
msiexec /i artifacts\installer\SideDock-0.0.24-win-x64.msi
```

The MSI requires Windows 11 build 22000 or later. It blocks installation on Windows 10. On a supported system, it installs SideDock for the current user under `%LOCALAPPDATA%\Programs\SideDock` and creates a Start Menu shortcut. Startup can be enabled or disabled from the app settings menu.

Uninstall from Windows Settings under installed apps, or use Programs and Features. Windows Installer removes the files and shortcut.

## Release

Create and publish a GitHub Release with a tag like `v0.0.24`. The `Release MSI` workflow will build and attach both MSI release assets from that tag:

```text
SideDock-0.0.24-win-x64.msi
SideDock-0.0.24-win-x64-no-runtime.msi
```

Release tags must use three numeric version parts, optionally prefixed with `v`, because Windows Installer product versions do not support prerelease labels.

## Visual Studio

Open `SideDock.slnx` in Visual Studio 2022 or later. Set `SideDock` as the startup project if it is not selected automatically, restore NuGet packages, then press `F5` to debug or `Ctrl+F5` to run without debugging.

The app targets `net10.0-windows10.0.22000.0`, so Visual Studio must have the .NET 10 SDK and the .NET desktop development workload installed. The target machine must run Windows 11 build 22000 or later and have Microsoft Edge WebView2 Runtime.

The sidebar starts as a persistent full-height icon rail on the right edge by default and registers its reserved edge space as a Windows appbar, so maximized windows avoid the SideDock edge. The appbar always shows every configured site button and the settings button, whether the web panel is expanded or hidden. No site is selected at startup. When another application is fullscreen on the same monitor, SideDock automatically hides while keeping its appbar space reserved, avoiding desktop relayout until fullscreen ends. Screenshot capture overlays are ignored so SideDock stays available while taking screenshots. The dock side can be changed to left or right from the settings menu. The Display submenu selects and remembers the physical monitor used by SideDock. If that monitor is temporarily unavailable, such as during a single-monitor Remote Desktop session, SideDock uses an available monitor without replacing the saved choice and moves back automatically when the preferred monitor returns. The rail loads favicons from `%LOCALAPPDATA%\SideDock\Icons`; missing entries show the configured `IconKey` while SideDock makes a lightweight `/favicon.ico` request in the background. Opening a site can replace that fallback with a higher-quality icon from the page.

Clicking a site button selects it, creates its WebView2 instance on first use, and expands the web panel. SideDock does not initialize WebView2 or load any configured page before that first explicit click. Adding a URL leaves it unselected, and removing the selected URL returns the rail to its unselected state. The app is always topmost. The expanded toolbar contains only open externally, pin, hide, and close-page buttons. Pin is shared across all sites.

When pin is enabled, the expanded web panel is also registered as appbar space, so maximized windows avoid the full expanded width. When pin is disabled, only the icon rail reserves desktop space; the expanded web panel overlays other windows.

The expanded width is capped dynamically at the current screen width minus the persistent icon rail width. Dragging the resize grip saves the expanded width to `%LOCALAPPDATA%\SideDock\appsettings.json`, and the next app launch uses that saved width.

The settings menu supports `Dark`, `Light`, and `System` theme modes. `Dark` and `Light` also set the embedded pages' preferred color scheme; `System` follows the Windows app theme preference.

Each activated site gets its own WebView2 instance, so switching icons restores that site's existing page immediately. Invisible pages use WebView2's low-memory target while their scripts and network connections continue running. The hide button collapses the panel and applies the low-memory target without disposing existing pages. The close button disposes the current page and collapses the panel; clicking that site again creates a fresh WebView2 instance.

## Configure Tools

Edit `src\SideDock\appsettings.json` to change the fixed web tools. The supported fields are:

- `Id`
- `Title`
- `Url`
- `IconKey`
- `OpenExternalFallbackEnabled`
- `ThemeMode`: `Dark`, `Light`, or `System`
- `DockSide`: `Left` or `Right`
- `StartWithWindows`: `true` or `false`
- `PreferredDisplayId`: the saved physical monitor identity managed by the Display menu
- `PreferredDisplayName`: the saved monitor name shown when that display is unavailable
- `LogLevel`: `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`, or `None`
- `LogFileSizeLimitBytes`: per-file log size limit before rolling
- `LogRetainedFileCount`: number of rolling log files to retain

## Local Logs

SideDock writes local structured logs to `%LOCALAPPDATA%\SideDock\logs` by default. Use `Open logs folder` from the settings menu to open the folder.

Logs use compact JSON lines (`sidedock-.clef`) with daily and size-based rolling. Defaults keep five files with a 2 MiB per-file limit. The logs include appbar, monitor, DPI, WebView lifecycle, startup, settings, and exception diagnostics.

For privacy, logs may include configured tool IDs, titles, configured tool URLs, and URLs of network requests that fail with a recorded connection error. They do not record successful current-page navigation URLs, external URLs opened from pages, WebView message contents, or page content.

## Failed Domains

SideDock records protocol-qualified domains whose WebView2 requests fail because of DNS, connection, proxy, tunnel, TLS/certificate, unreachable-network, disconnection, or timeout errors. This covers page navigations, page resources such as APIs, scripts, images, and fonts, and WebSocket connection failures. HTTP 4xx/5xx responses, canceled requests, and blocked content are not counted.

Counts persist across restarts in `%LOCALAPPDATA%\SideDock\failed-domains.txt`. The UTF-8 file contains one tab-separated `protocol://domain<TAB>failure count` entry per line, such as `https://example.com<TAB>3` or `ws://example.com<TAB>2`. Ports, paths, queries, and fragments are omitted. Entries are ordered by descending failure count and then by protocol-qualified domain. Each failed request or retry increments that endpoint's count, so different protocols for the same domain are counted separately.

When SideDock records a protocol-qualified domain that is not already in the file, it shows a Windows notification containing that endpoint. Different protocols for the same host notify separately, while repeated failures only increment the existing count. Clicking the notification opens `failed-domains.txt`, including from Notification Center after SideDock has exited. Clearing the failed domains makes a later failure for the same endpoint new again. Windows notification settings can suppress the popup without affecting failure recording.

When upgrading from the previous domain-only format, SideDock clears entries that do not contain a protocol because their original protocol cannot be recovered.

Use `Open failed domains file` in the settings menu to view the file. Use `Clear failed domains` and confirm the warning to reset all recorded domains and counts.
