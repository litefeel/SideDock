# AGENTS.md

## Project Overview

SideDock is a Windows-only WPF sidebar application inspired by the detached Microsoft Edge sidebar. The main app lives in `src/SideDock` and targets `net10.0-windows`.

The installer is built with WiX from `installer/SideDock.wxs`. Release packaging produces two MSI variants:

- `SideDock-<version>-<runtime>.msi`: self-contained, includes the .NET runtime.
- `SideDock-<version>-<runtime>-no-runtime.msi`: framework-dependent, does not include the .NET runtime.

## Environment Requirements

- Windows 10 LTSC 21H2 or Windows 11.
- .NET 10 SDK for build, publish, and development.
- Microsoft Edge WebView2 Runtime on machines running the app.
- The `no-runtime` MSI requires .NET 10 to already be installed on the target machine.

## Common Commands

Restore and build:

```powershell
dotnet restore src\SideDock\SideDock.csproj
dotnet build src\SideDock\SideDock.csproj --configuration Release --no-restore
```

Run tests:

```powershell
dotnet test tests\SideDock.Tests\SideDock.Tests.csproj --configuration Release
```

Run:

```powershell
dotnet run --project src\SideDock\SideDock.csproj
```

Build the default installer:

```cmd
scripts\Build-Installer.cmd
```

Build a specific version and runtime:

```cmd
scripts\Build-Installer.cmd 0.0.11 win-x64
```

Build the framework-dependent `no-runtime` installer:

```cmd
scripts\Build-Installer.cmd 0.0.11 win-x64 no-runtime
```

Expected installer outputs are written under `artifacts\installer`.

## Coding Guidelines

- Keep changes scoped to the requested behavior.
- Follow the existing WPF/C# style in `src/SideDock`.
- Keep nullable annotations enabled and avoid introducing nullable warnings.
- Prefer simple project-local helpers over new dependencies unless the change clearly needs a package.
- Update `README.md` when user-facing behavior, requirements, installer behavior, or release steps change.
- Do not commit generated build output from `artifacts`, `.dotnet_home`, `.nuget`, `.codex-build`, `.buildverify`, `bin`, or `obj`.

## AI Change Safety

Before editing `src/SideDock/MainWindow.xaml`, `src/SideDock/MainWindow.xaml.cs`, or `src/SideDock/AppBarManager.cs`, inspect recent history for those files and preserve documented appbar behavior unless the user explicitly asks to change it:

```powershell
git log --stat -- src\SideDock\MainWindow.xaml src\SideDock\MainWindow.xaml.cs src\SideDock\AppBarManager.cs
```

For changes touching appbar behavior, collapsed or expanded layout, fullscreen hiding, WebView lifecycle, dock side, pinning, resizing, settings, or installer/release behavior, review `docs/REGRESSION.md` before editing and report which relevant regression cases were validated before finishing.

## Validation

For code changes, run at least:

```powershell
dotnet build src\SideDock\SideDock.csproj --configuration Release
```

If the change touches logic covered by tests, also run:

```powershell
dotnet run --project tests\SideDock.Tests\SideDock.Tests.csproj --configuration Release
```

For installer or release workflow changes, also run:

```cmd
scripts\Build-Installer.cmd
```

If the change specifically touches framework-dependent packaging, run:

```cmd
scripts\Build-Installer.cmd 0.0.11 win-x64 no-runtime
```

Use the actual project version when validating a release candidate.

## Commit Policy

- 每个功能一个 commit。
- 更新版本号要单独一个 commit。
- Do not combine unrelated features, fixes, installer changes, or documentation updates into a single commit.
- If a request contains multiple independent features, split the implementation into separate commits.
- Keep commit messages specific to the feature or fix being committed.
- For multi-step work, identify the intended commit slices before editing and complete one slice at a time.
- Keep mechanical refactors separate from behavior changes unless the refactor is only a small local part of that behavior change.
- Do not stage unrelated files together. Before each commit, review `git status --short` and the staged diff to confirm the commit contains only one coherent change.
- If validation for one slice fails, fix that slice before starting another slice.
- Do not include version bumps in feature, fix, refactor, or documentation commits.

## Release Policy

GitHub releases should use a tag like `v0.0.11` or `0.0.11`. Release tags must have exactly three numeric version parts because Windows Installer product versions do not support prerelease labels.

The GitHub Release page must clearly describe:

- What changed in the release.
- Which bugs were fixed, if any.
- That `SideDock-<version>-<runtime>.msi` includes the .NET runtime.
- That `SideDock-<version>-<runtime>-no-runtime.msi` is smaller but requires .NET 10 to already be installed.
- Do not include routine version bump entries like `Bump app and installer version to <version>` in `Changes`; version bumps are expected for every release.

Use this release note structure unless the user asks for another format:

```markdown
## Changes

- ...

## Requirements

- Windows 10 LTSC 21H2 or Windows 11.
- Microsoft Edge WebView2 Runtime.
- .NET 10 is required only when installing the `no-runtime` MSI.
```
