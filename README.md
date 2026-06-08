# SideDock

SideDock is a Windows-only source-run sidebar app inspired by the detached Microsoft Edge sidebar.

## Requirements

- Windows 10 LTSC 21H2 or Windows 11
- .NET 10 SDK
- Microsoft Edge WebView2 Runtime

## Run

```powershell
$env:DOTNET_CLI_HOME = "$PWD\.dotnet_home"
$env:NUGET_PACKAGES = "$PWD\.nuget\packages"
dotnet run --project src\SideDock\SideDock.csproj
```

## Visual Studio

Open `SideDock.slnx` in Visual Studio 2022 or later. Set `SideDock` as the startup project if it is not selected automatically, restore NuGet packages, then press `F5` to debug or `Ctrl+F5` to run without debugging.

The app targets `net10.0-windows`, so Visual Studio must have the .NET 10 SDK and the .NET desktop development workload installed. The target machine also needs Microsoft Edge WebView2 Runtime.

The sidebar starts as a persistent left-edge icon rail and always registers that rail as a Windows appbar, so maximized windows avoid the icon rail. Click a site icon to expand the web panel, drag the full-height right edge to adjust the expanded width, use `PIN` to keep it open, and use `TOP` to toggle always-on-top.

When `PIN` is enabled, the expanded web panel is also registered as appbar space, so maximized windows avoid the full expanded width. When `PIN` is disabled, only the icon rail reserves desktop space; the expanded web panel overlays other windows and is forced to the top while it is open.

The expanded width is capped dynamically at the current screen width minus the persistent icon rail width.

Each configured site gets its own WebView2 instance, so switching icons restores that site's existing page immediately. SideDock does not automatically open failed pages or popups in the system browser; use the `O` button in the expanded toolbar when you explicitly want the current page in your default browser.

## Configure Tools

Edit `src\SideDock\appsettings.json` to change the fixed web tools. The supported fields are:

- `Id`
- `Title`
- `Url`
- `IconKey`
- `OpenExternalFallbackEnabled`
