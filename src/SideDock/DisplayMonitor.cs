namespace SideDock;

internal sealed record DisplayMonitor(
    nint Handle,
    string DisplayId,
    string DeviceName,
    string FriendlyName,
    bool IsPrimary,
    MonitorLayout Layout)
{
    public string MenuLabel
    {
        get
        {
            const string devicePrefix = @"\\.\DISPLAY";
            var displayNumber = DeviceName.StartsWith(devicePrefix, StringComparison.OrdinalIgnoreCase)
                ? DeviceName[devicePrefix.Length..]
                : string.Empty;
            var prefix = string.IsNullOrWhiteSpace(displayNumber)
                ? "Display"
                : "Display " + displayNumber;
            var label = string.IsNullOrWhiteSpace(FriendlyName)
                || FriendlyName.Equals(DeviceName, StringComparison.OrdinalIgnoreCase)
                    ? prefix
                    : prefix + " - " + FriendlyName;
            return IsPrimary ? label + " (Primary)" : label;
        }
    }
}

internal readonly record struct DisplayTargetResolution(
    DisplayMonitor Monitor,
    bool ShouldPersistPreference);

internal static class DisplayTargetResolver
{
    public static DisplayTargetResolution Resolve(
        IReadOnlyList<DisplayMonitor> displays,
        string? preferredDisplayId,
        nint currentMonitor)
    {
        if (displays.Count == 0)
        {
            throw new ArgumentException("At least one display is required.", nameof(displays));
        }

        var preferred = FindById(displays, preferredDisplayId);
        if (preferred is not null)
        {
            return new DisplayTargetResolution(preferred, false);
        }

        var fallback = displays.FirstOrDefault(display => display.Handle == currentMonitor)
            ?? displays.FirstOrDefault(display => display.IsPrimary)
            ?? displays[0];
        return new DisplayTargetResolution(
            fallback,
            string.IsNullOrWhiteSpace(preferredDisplayId));
    }

    public static DisplayMonitor? FindById(
        IReadOnlyList<DisplayMonitor> displays,
        string? displayId)
    {
        return string.IsNullOrWhiteSpace(displayId)
            ? null
            : displays.FirstOrDefault(
                display => display.DisplayId.Equals(displayId, StringComparison.OrdinalIgnoreCase));
    }
}
