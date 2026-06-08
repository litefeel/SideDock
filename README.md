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

The sidebar starts as a persistent left-edge icon rail and always registers that rail as a Windows appbar, so maximized windows avoid the icon rail. The rail shows each site's favicon only; missing favicons use a default icon, and loaded favicons are cached under `%LOCALAPPDATA%\SideDock\Icons` for immediate display on the next launch.

Click a site icon to expand the web panel. The app is always topmost. The expanded toolbar contains only open externally, pin, hide, and close-page buttons. Pin is shared across all sites.

When pin is enabled, the expanded web panel is also registered as appbar space, so maximized windows avoid the full expanded width. When pin is disabled, only the icon rail reserves desktop space; the expanded web panel overlays other windows.

The expanded width is capped dynamically at the current screen width minus the persistent icon rail width.

Each configured site gets its own WebView2 instance, so switching icons restores that site's existing page immediately. The hide button collapses the panel while keeping page instances alive. The close button disposes the current page and collapses the panel; selecting that site again creates a fresh WebView2 instance.

## Configure Tools

Edit `src\SideDock\appsettings.json` to change the fixed web tools. The supported fields are:

- `Id`
- `Title`
- `Url`
- `IconKey`
- `OpenExternalFallbackEnabled`
