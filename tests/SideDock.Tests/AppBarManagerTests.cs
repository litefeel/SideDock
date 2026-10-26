using Microsoft.Extensions.Logging.Abstractions;
using SideDock;

namespace SideDock.Tests;

public sealed class AppBarManagerTests
{
    public static IEnumerable<object[]> RecoveryLayouts()
    {
        foreach (var dpi in new uint[] { 96, 120, 144 })
        foreach (var origin in new[] { (-2560, 0), (1920, 0), (0, -1440), (0, 1080) })
        foreach (var leftDock in new[] { false, true })
        foreach (var mode in new[] { 0, 1, 2 })
        {
            yield return new object[] { dpi, origin.Item1, origin.Item2, leftDock, mode };
        }
    }

    [Theory]
    [MemberData(nameof(RecoveryLayouts))]
    public void ShellRestartRestoresReservationOnCurrentMonitor(
        uint dpi, int left, int top, bool leftDock, int mode)
    {
        var shell = new FakeShell();
        var manager = CreateManager(shell);
        manager.Register(48, 48, 1080, AppDockSide.Right, PrimaryLayout, "Startup");
        shell.Restart();

        var bounds = new NativeRect(left, top, left + 2560, top + 1440);
        var layout = new MonitorLayout(bounds, bounds, dpi);
        var side = leftDock ? AppDockSide.Left : AppDockSide.Right;
        var width = mode == 0 ? 48 : 430;
        var reservedWidth = DockLayoutCalculator.GetReservedWidth(
            isExpanded: mode != 0, isPinned: mode == 2, width, 48, 8);

        manager.RestoreRegistration(reservedWidth, width, layout.MonitorHeightDips, side, layout, "TaskbarCreated");

        Assert.True(shell.Registered);
        Assert.Equal(new[] { 1, 0, 2, 3 }, shell.Messages);
        Assert.Equal(manager.CallbackMessage, shell.CallbackMessage);
        Assert.NotEqual(0u, manager.TaskbarCreatedMessage);
        Assert.Equal(leftDock ? 0u : 2u, shell.Reservation.uEdge);
        var expectedWidth = (int)Math.Round((mode == 2 ? 422 : 48) * dpi / 96d);
        Assert.Equal(expectedWidth, shell.Reservation.rc.Width);
        Assert.Equal(leftDock ? left : bounds.Right - expectedWidth, shell.Reservation.rc.Left);
        Assert.Equal(top, shell.Reservation.rc.Top);
        Assert.Equal(bounds.Bottom, shell.Reservation.rc.Bottom);

        // A later shell position notification must keep the recovered dimensions.
        manager.Refresh(layout, "PositionChanged");
        Assert.Equal(expectedWidth, shell.Reservation.rc.Width);
    }

    [Fact]
    public void RepeatedNotificationsReplaceSurvivingRegistration()
    {
        var shell = new FakeShell();
        var manager = CreateManager(shell);
        manager.Register(48, 48, 1080, AppDockSide.Right, PrimaryLayout, "Startup");
        shell.Messages.Clear();

        for (var i = 0; i < 3; i++)
        {
            manager.RestoreRegistration(422, 430, 1080, AppDockSide.Left, PrimaryLayout, "TaskbarCreated");
            Assert.True(shell.Registered);
            Assert.Equal(422, shell.Reservation.rc.Width);
        }

        Assert.Equal(new[] { 1, 0, 2, 3, 1, 0, 2, 3, 1, 0, 2, 3 }, shell.Messages);
        manager.Unregister("Closing");
        Assert.False(shell.Registered);
    }

    [Fact]
    public void FailedRecoveryDoesNotPretendRegistrationSucceeded()
    {
        var shell = new FakeShell();
        var manager = CreateManager(shell);
        manager.Register(48, 48, 1080, AppDockSide.Right, PrimaryLayout, "Startup");
        shell.Restart();
        shell.RejectRegistration = true;

        manager.RestoreRegistration(48, 48, 1080, AppDockSide.Right, PrimaryLayout, "TaskbarCreated");
        Assert.False(shell.Registered);
        Assert.Equal(new[] { 1, 0 }, shell.Messages);

        shell.RejectRegistration = false;
        shell.Messages.Clear();
        manager.RestoreRegistration(48, 48, 1080, AppDockSide.Right, PrimaryLayout, "Retry");
        Assert.True(shell.Registered);
        Assert.Equal(new[] { 0, 2, 3 }, shell.Messages);
    }

    private static readonly MonitorLayout PrimaryLayout = new(
        new NativeRect(0, 0, 1920, 1080), new NativeRect(0, 0, 1920, 1040), 96);

    private static AppBarManager CreateManager(FakeShell shell)
    {
        // No real appbar is registered and no real window can be moved by these tests.
        return new AppBarManager(nint.Zero, NullLogger<AppBarManager>.Instance, shell.Send);
    }

    private sealed class FakeShell
    {
        public List<int> Messages { get; } = new();
        public bool Registered { get; private set; }
        public bool RejectRegistration { get; set; }
        public uint CallbackMessage { get; private set; }
        public AppBarManager.AppBarData Reservation { get; private set; }

        public void Restart()
        {
            Registered = false;
            Reservation = default;
            Messages.Clear();
        }

        public nuint Send(int message, ref AppBarManager.AppBarData data)
        {
            Messages.Add(message);
            switch (message)
            {
                case 0: // ABM_NEW rejects duplicate registrations.
                    if (Registered || RejectRegistration)
                    {
                        return 0;
                    }

                    Registered = true;
                    CallbackMessage = data.uCallbackMessage;
                    return 1;
                case 1: // ABM_REMOVE is harmless after the shell lost its entry.
                    Registered = false;
                    Reservation = default;
                    return 1;
                case 2: // ABM_QUERYPOS
                    return 1;
                case 3: // ABM_SETPOS cannot reserve space before ABM_NEW.
                    Assert.True(Registered);
                    Reservation = data;
                    return 1;
                default:
                    throw new InvalidOperationException($"Unexpected appbar message: {message}");
            }
        }
    }
}
