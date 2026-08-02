using System.Windows.Threading;
using CodexQuotaTaskbar.Core.Overlay;
using CodexQuotaTaskbar.Core.Quota;
using CodexQuotaTaskbar.Host.Platform;
using CodexQuotaTaskbar.Host.UI;
using Microsoft.Win32;

namespace CodexQuotaTaskbar.Host.Overlay;

internal sealed class OverlayCoordinator : IDisposable
{
    private const double CapsuleWidth = 218;
    private const double CapsuleHeight = 42;
    private const double PopoverWidth = 328;
    private readonly TaskbarTopologySource topology = new();
    private readonly Dictionary<string, QuotaCapsuleWindow> windows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScreenRect> userPositions = new(StringComparer.Ordinal);
    private readonly DispatcherTimer timer;
    private bool showAllTaskbars;
    private bool sessionLocked;
    private QuotaSnapshot snapshot = QuotaSnapshot.Unavailable("正在连接 Codex…");
    private QuotaPopoverWindow? popover;
    private QuotaCapsuleWindow? popoverOwner;

    internal OverlayCoordinator(bool showAllTaskbars)
    {
        this.showAllTaskbars = showAllTaskbars;
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(750), DispatcherPriority.Background, (_, _) => Reconcile(), Dispatcher.CurrentDispatcher);
        SystemEvents.DisplaySettingsChanged += OnEnvironmentChanged;
        SystemEvents.UserPreferenceChanged += OnEnvironmentChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    internal event EventHandler? RefreshRequested;
    internal event EventHandler? OpenCodexRequested;
    internal event EventHandler? ContextRequested;

    internal void Start()
    {
        Reconcile();
        timer.Start();
    }

    internal void SetShowAllTaskbars(bool value)
    {
        showAllTaskbars = value;
        Reconcile();
    }

    internal void Apply(QuotaSnapshot value)
    {
        snapshot = value;
        foreach (var window in windows.Values)
        {
            window.Apply(value);
        }
        popover?.Apply(value);
    }

    private void Reconcile()
    {
        var anchors = topology.Capture()
            .Where(anchor => showAllTaskbars || anchor.IsPrimary)
            .ToDictionary(anchor => anchor.MonitorId, StringComparer.Ordinal);

        foreach (var stale in windows.Keys.Except(anchors.Keys, StringComparer.Ordinal).ToArray())
        {
            windows[stale].Close();
            windows.Remove(stale);
            userPositions.Remove(stale);
        }

        foreach (var anchor in anchors.Values)
        {
            if (!windows.TryGetValue(anchor.MonitorId, out var window))
            {
                window = CreateWindow(anchor.MonitorId);
                windows.Add(anchor.MonitorId, window);
                window.Apply(snapshot);
                window.Show();
            }

            var shouldShow = anchor.IsVisible && !sessionLocked && !TaskbarTopologySource.IsMonitorFullscreen(anchor);
            if (!shouldShow)
            {
                window.Hide();
                if (popover?.Owner == window)
                {
                    popover.Close();
                }
                continue;
            }

            if (!window.IsVisible)
            {
                window.Show();
            }
            if (!window.IsUserDragging)
            {
                window.Place(userPositions.TryGetValue(anchor.MonitorId, out var userPosition)
                    ? userPosition
                    : OverlayPlacementCalculator.Calculate(anchor, CapsuleWidth, CapsuleHeight, 12, 8));
            }
        }
    }

    private QuotaCapsuleWindow CreateWindow(string monitorId)
    {
        var window = new QuotaCapsuleWindow(monitorId);
        window.PrimaryInvoked += (_, _) => TogglePopover(window);
        window.ContextInvoked += (_, _) => ContextRequested?.Invoke(window, EventArgs.Empty);
        window.UserMoved += (previous, current) => OnWindowMoved(window, previous, current);
        return window;
    }

    internal static ScreenRect CalculateFollowerBounds(ScreenRect follower, ScreenRect previousOwner, ScreenRect currentOwner) =>
        new(
            follower.Left + currentOwner.Left - previousOwner.Left,
            follower.Top + currentOwner.Top - previousOwner.Top,
            follower.Right + currentOwner.Left - previousOwner.Left,
            follower.Bottom + currentOwner.Top - previousOwner.Top);

    private void OnWindowMoved(QuotaCapsuleWindow window, ScreenRect previous, ScreenRect current)
    {
        userPositions[window.MonitorId] = current;
        if (popover is null || !ReferenceEquals(popoverOwner, window))
        {
            return;
        }

        var handle = new System.Windows.Interop.WindowInteropHelper(popover).Handle;
        if (!NativeMethods.GetWindowRect(handle, out var nativeBounds))
        {
            return;
        }

        var follower = CalculateFollowerBounds(
            new ScreenRect(nativeBounds.Left, nativeBounds.Top, nativeBounds.Right, nativeBounds.Bottom),
            previous,
            current);
        _ = NativeMethods.SetWindowPos(handle, NativeMethods.HwndTopmost,
            follower.Left, follower.Top, follower.Width, follower.Height,
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
    }

    private void TogglePopover(QuotaCapsuleWindow owner)
    {
        if (popover is not null)
        {
            popover.Close();
            popover = null;
            return;
        }

        var details = new QuotaPopoverWindow { Owner = owner };
        popover = details;
        popoverOwner = owner;
        details.Apply(snapshot);
        details.RefreshRequested += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        details.OpenCodexRequested += (_, _) => OpenCodexRequested?.Invoke(this, EventArgs.Empty);
        details.Closed += (_, _) =>
        {
            popover = null;
            popoverOwner = null;
        };
        details.Show();
        var anchor = topology.Capture().FirstOrDefault(value => value.MonitorId == owner.MonitorId);
        var dpi = anchor?.Dpi ?? 96;
        var scale = dpi / 96d;
        var width = checked((int)Math.Round(PopoverWidth * scale));
        var height = checked((int)Math.Round(Math.Max(220, details.ActualHeight) * scale));
        var bounds = anchor is null
            ? new ScreenRect(owner.PhysicalBounds.Right - width, owner.PhysicalBounds.Top - height, owner.PhysicalBounds.Right, owner.PhysicalBounds.Top)
            : PopoverPlacementCalculator.Calculate(anchor, owner.PhysicalBounds, PopoverWidth, Math.Max(240, details.ActualHeight), 8);
        _ = NativeMethods.SetWindowPos(new System.Windows.Interop.WindowInteropHelper(details).Handle, NativeMethods.HwndTopmost,
            bounds.Left, bounds.Top, width, height, NativeMethods.SwpShowWindow);
        details.Activate();
    }

    private void OnEnvironmentChanged(object? sender, EventArgs eventArgs) => Reconcile();

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs eventArgs)
    {
        sessionLocked = eventArgs.Reason == SessionSwitchReason.SessionLock
            || (sessionLocked && eventArgs.Reason != SessionSwitchReason.SessionUnlock);
        Reconcile();
    }

    public void Dispose()
    {
        timer.Stop();
        SystemEvents.DisplaySettingsChanged -= OnEnvironmentChanged;
        SystemEvents.UserPreferenceChanged -= OnEnvironmentChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        popover?.Close();
        foreach (var window in windows.Values)
        {
            window.Close();
        }
        windows.Clear();
    }
}
