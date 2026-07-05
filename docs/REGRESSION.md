# Regression Checklist

Use this checklist before finishing changes that touch appbar behavior, collapsed or expanded layout, fullscreen hiding, WebView lifecycle, dock side, pinning, resizing, settings, display selection, DPI scaling, installer behavior, or release workflow.

## Appbar and Layout

- Collapsed rail shows every configured site button and the settings button.
- Collapsed rail uses the configured dock side and stays on the correct screen edge.
- Collapsed and expanded windows use the intended monitor, not always the primary monitor.
- Appbar reserved space is registered on the same monitor edge where SideDock is shown.
- Clicking a site button expands the web panel.
- Clicking outside site buttons does not unexpectedly expand the web panel.
- The app remains topmost.
- Missing favicons use the default icon and loaded favicons continue to display.

## Multi-Monitor and DPI Scaling

- Layout calculations work when SideDock is on a secondary monitor.
- Layout calculations work when the secondary monitor is positioned left, right, above, or below the primary monitor.
- Layout calculations work when Windows display scaling is not 100%, including at least 125% and 150% when feasible.
- Appbar reserved pixel bounds and WPF DIP sizes are converted consistently for the active monitor DPI.
- Moving between monitors with different DPI values or receiving a DPI/display change refreshes SideDock position, size, and reserved space.
- Resize preview, resize grip math, expanded width limits, and fullscreen detection use the active monitor bounds rather than primary-screen assumptions.

## WebView Lifecycle

- Switching site buttons restores that site's existing page state.
- Hide collapses the panel without disposing existing WebView instances.
- Close disposes only the current page and collapses the panel.
- Selecting a closed site creates a fresh WebView2 instance.
- Links that target a new window open externally or navigate as intended by the existing behavior.

## Pinning and Reserved Space

- With pin enabled, the expanded web panel registers appbar space so maximized windows avoid the full expanded width.
- With pin disabled, only the icon rail reserves appbar space and the expanded panel overlays other windows.
- Collapsing or closing from pinned mode restores the expected reserved width.
- Changing dock side updates appbar registration and window position consistently.

## Fullscreen Auto-Hide

- A fullscreen app on the same monitor hides SideDock.
- Leaving fullscreen restores SideDock.
- Screenshot and screen clipping overlays do not trigger fullscreen hiding.
- Shell, desktop, and taskbar windows are ignored by fullscreen detection.
- Auto-hide does not unregister appbar space in a way that causes unnecessary desktop relayout.

## Resizing and Settings

- Resize grip changes the expanded width in the expected direction for both left and right dock modes.
- Expanded width respects the configured minimum and the current screen width.
- Saved expanded width persists across app restarts.
- Startup, theme, dock side, and configured tools continue to load from `%LOCALAPPDATA%\SideDock\appsettings.json`.

## Installer and Release

- The default MSI includes the .NET runtime.
- The `no-runtime` MSI is framework-dependent and requires .NET 10 on the target machine.
- Installer outputs are written under `artifacts\installer`.
- Release notes clearly describe runtime requirements and omit routine version bump entries from `Changes`.

## Required Validation Notes

When a change touches one of these areas, the final response should include:

- The build or installer command that was run.
- The relevant checklist cases that were validated.
- Any checklist cases that could not be validated, with the reason.
