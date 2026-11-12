using CodexQuotaTaskbar.Host.UI;
using CodexQuotaTaskbar.Host.Platform;
using CodexQuotaTaskbar.Host.Overlay;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class OverlayLifecycleDiagnosticsTests
{
    [Theory]
    [InlineData(false, true, true, false, (int)OverlayVisibilityReason.Available)]
    [InlineData(false, true, true, true, (int)OverlayVisibilityReason.Fullscreen)]
    [InlineData(false, true, false, false, (int)OverlayVisibilityReason.Available)]
    [InlineData(true, true, true, false, (int)OverlayVisibilityReason.Locked)]
    [InlineData(false, false, true, false, (int)OverlayVisibilityReason.TaskbarHidden)]
    public void Fullscreen_switch_only_overrides_fullscreen_visibility(bool locked, bool taskbarVisible,
        bool fullscreen, bool hide, int expected)
    {
        var foreground = fullscreen ? ForegroundDisposition.Fullscreen : ForegroundDisposition.Normal;
        Assert.Equal((OverlayVisibilityReason)expected, OverlayCoordinator.ResolveVisibilityReason(locked, taskbarVisible, foreground, hide));
        Assert.Equal(OverlayVisibilityReason.CaptureOverlay, OverlayCoordinator.ResolveVisibilityReason(
            false, true, ForegroundDisposition.CaptureOverlay, hide));
    }

    [Fact]
    public void Policy_polling_is_deduplicated_but_reason_handle_and_process_changes_are_retained()
    {
        var log = new OverlayLifecycleDiagnostics();
        void Policy(long hwnd, OverlayVisibilityReason reason, int pid) => log.Record(
            OverlayLifecycleEvent.VisibilityPolicy, owner: 1, hwnd: hwnd, reason: reason,
            relatedPid: pid, deduplicate: true);
        for (var i = 0; i < 100; i++) Policy(10, OverlayVisibilityReason.Fullscreen, 20);
        Assert.Single(log.Entries);
        Policy(10, OverlayVisibilityReason.Available, 20);
        Policy(11, OverlayVisibilityReason.Available, 20);
        Policy(11, OverlayVisibilityReason.Available, 21);
        Assert.Equal(4, log.Entries.Count);
    }

    [Fact]
    public void Repeated_input_is_not_deduplicated_and_history_is_bounded()
    {
        var log = new OverlayLifecycleDiagnostics();
        for (var i = 0; i < 300; i++) log.Record(OverlayLifecycleEvent.ContextInvoke, inputTimestamp: i);
        Assert.Equal(128, log.Entries.Count);
        Assert.Equal(172, log.Entries[0].InputTimestamp);
        Assert.Equal(299, log.Entries[^1].InputTimestamp);
        Assert.True(log.Entries[^1].Tick >= log.Entries[0].Tick);
    }
}
