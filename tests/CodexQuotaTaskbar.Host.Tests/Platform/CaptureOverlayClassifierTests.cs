using CodexQuotaTaskbar.Core.Overlay;
using CodexQuotaTaskbar.Host.Platform;

namespace CodexQuotaTaskbar.Host.Tests.Platform;

public sealed class CaptureOverlayClassifierTests
{
    private static readonly ScreenRect Screen = new(0, 0, 2560, 1440);
    // Classes, relationships and bit patterns from the manual metadata capture;
    // synthetic handles/PID. No dependency on the user's live windows.
    private static CaptureWindowFacts Capture => new(2, 10, 0, 3, "QQScreenCapture Window Class",
        0x94000000, 0x08000088, true, true, Screen);
    private static CaptureWindowFacts Front => new(1, 10, 2, 3, "Chrome_WidgetWin_1",
        0x14020000, 0x00280008, true, true, Screen);

    [Fact]
    public void Observed_owner_chain_is_capture_but_ordinary_qq_and_generic_overlays_are_not()
    {
        Assert.True(CaptureOverlayClassifier.IsQqCapture(Front, Capture, Screen, "QQ"));
        Assert.True(CaptureOverlayClassifier.IsQqCapture(Capture, Capture, Screen, "QQ"));
        Assert.False(CaptureOverlayClassifier.IsQqCapture(Front with { Owner = 0 }, Capture, Screen, "QQ"));
        Assert.False(CaptureOverlayClassifier.IsQqCapture(Front, Capture with { Class = "Chrome_WidgetWin_1" }, Screen, "QQ"));
        Assert.False(CaptureOverlayClassifier.IsQqCapture(Front, Capture, Screen, "OtherPlayer"));
        Assert.False(CaptureOverlayClassifier.IsQqCapture(default, default, Screen, "QQ"));
    }

    [Fact]
    public void Every_required_identity_visibility_geometry_and_style_constraint_is_checked()
    {
        foreach (var changed in new[] {
            Front with { Handle = 0 }, Front with { ProcessId = 11 }, Front with { ProcessId = 0 },
            Front with { Owner = 5 }, Front with { Monitor = 4 }, Front with { Monitor = 0 },
            Front with { Visible = false }, Front with { Uncloaked = false }, Front with { Class = "Game" },
            Front with { Style = Front.Style | 0x40000000 }, Front with { Style = Front.Style | 0x00C00000 },
            Front with { ExtendedStyle = Front.ExtendedStyle & ~8u },
            Front with { ExtendedStyle = Front.ExtendedStyle & ~0x80000u },
            Front with { Frame = Screen with { Bottom = 1400 } },
        }) Assert.False(CaptureOverlayClassifier.IsQqCapture(changed, Capture, Screen, "QQ"));
        foreach (var changed in new[] {
            Capture with { Handle = 0 }, Capture with { Visible = false }, Capture with { Uncloaked = false },
            Capture with { Style = 0 }, Capture with { Style = Capture.Style | 0x40000000 },
            Capture with { ExtendedStyle = 0 }, Capture with { ExtendedStyle = 0x88 },
            Capture with { Frame = Screen with { Right = 1920 } },
        }) Assert.False(CaptureOverlayClassifier.IsQqCapture(Front, changed, Screen, "QQ"));
    }

    [Fact]
    public void Negative_monitor_coordinates_are_valid_but_cross_monitor_or_maximized_frames_are_not()
    {
        var left = new ScreenRect(-2560, 0, 0, 1440);
        Assert.True(CaptureOverlayClassifier.IsQqCapture(Front with { Frame = left }, Capture with { Frame = left }, left, "QQ"));
        Assert.False(CaptureOverlayClassifier.IsQqCapture(Front, Capture with { Frame = left }, Screen, "QQ"));
        Assert.False(CaptureOverlayClassifier.IsQqCapture(Front with { Frame = Screen with { Bottom = 1400 } }, Capture, Screen, "QQ"));
    }
}
