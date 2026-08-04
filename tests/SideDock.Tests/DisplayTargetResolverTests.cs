using SideDock;

namespace SideDock.Tests;

public sealed class DisplayTargetResolverTests
{
    [Fact]
    public void PreferredDisplayIsMatchedCaseInsensitively()
    {
        var displays = new[]
        {
            CreateDisplay(1, "pnp:monitor-a", isPrimary: true),
            CreateDisplay(2, "pnp:monitor-b")
        };

        var resolution = DisplayTargetResolver.Resolve(displays, "PNP:MONITOR-B", (nint)1);

        Assert.Equal("pnp:monitor-b", resolution.Monitor.DisplayId);
        Assert.False(resolution.ShouldPersistPreference);
    }

    [Fact]
    public void MissingPreferredDisplayUsesCurrentWithoutReplacingPreference()
    {
        var displays = new[]
        {
            CreateDisplay(9, "pnp:remote", isPrimary: true)
        };

        var resolution = DisplayTargetResolver.Resolve(displays, "pnp:physical", (nint)9);

        Assert.Equal("pnp:remote", resolution.Monitor.DisplayId);
        Assert.False(resolution.ShouldPersistPreference);
    }

    [Fact]
    public void RestoredPreferredDisplayReplacesTemporaryFallback()
    {
        var remoteOnly = new[]
        {
            CreateDisplay(9, "pnp:remote", isPrimary: true)
        };
        var restoredDisplays = new[]
        {
            CreateDisplay(1, "pnp:physical", isPrimary: true),
            CreateDisplay(9, "pnp:remote")
        };

        var fallback = DisplayTargetResolver.Resolve(remoteOnly, "pnp:physical", (nint)9);
        var restored = DisplayTargetResolver.Resolve(restoredDisplays, "pnp:physical", fallback.Monitor.Handle);

        Assert.Equal("pnp:remote", fallback.Monitor.DisplayId);
        Assert.Equal("pnp:physical", restored.Monitor.DisplayId);
        Assert.False(restored.ShouldPersistPreference);
    }

    [Fact]
    public void MissingPreferenceUsesCurrentDisplayAndRequestsPersistence()
    {
        var displays = new[]
        {
            CreateDisplay(1, "pnp:primary", isPrimary: true),
            CreateDisplay(2, "pnp:current")
        };

        var resolution = DisplayTargetResolver.Resolve(displays, null, (nint)2);

        Assert.Equal("pnp:current", resolution.Monitor.DisplayId);
        Assert.True(resolution.ShouldPersistPreference);
    }

    [Fact]
    public void ManualSelectionFindsStableDisplayId()
    {
        var displays = new[]
        {
            CreateDisplay(1, "pnp:first", isPrimary: true),
            CreateDisplay(2, "pnp:selected")
        };

        var selected = DisplayTargetResolver.FindById(displays, "PNP:SELECTED");

        Assert.NotNull(selected);
        Assert.Equal((nint)2, selected.Handle);
    }

    private static DisplayMonitor CreateDisplay(
        int handle,
        string displayId,
        bool isPrimary = false)
    {
        var bounds = new NativeRect(0, 0, 1920, 1080);
        return new DisplayMonitor(
            (nint)handle,
            displayId,
            @"\\.\DISPLAY" + handle,
            displayId,
            isPrimary,
            new MonitorLayout(bounds, bounds, MonitorLayout.DefaultDpi));
    }
}
