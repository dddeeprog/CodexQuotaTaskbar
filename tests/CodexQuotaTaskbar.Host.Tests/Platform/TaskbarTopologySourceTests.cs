using CodexQuotaTaskbar.Host.Platform;

namespace CodexQuotaTaskbar.Host.Tests.Platform;

public sealed class TaskbarTopologySourceTests
{
    [Fact]
    public void Shell_desktop_handle_is_not_a_fullscreen_application()
    {
        Assert.True(TaskbarTopologySource.IsDesktopSurface(100, 100, 12, 12, "Progman"));
    }

    [Theory]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    [InlineData("SHELLDLL_DefView")]
    public void Desktop_surfaces_must_belong_to_the_shell_process(string windowClass)
    {
        Assert.True(TaskbarTopologySource.IsDesktopSurface(101, 100, 12, 12, windowClass));
        Assert.False(TaskbarTopologySource.IsDesktopSurface(101, 100, 34, 12, windowClass));
    }

    [Theory]
    [InlineData("CabinetWClass")]
    [InlineData("Chrome_WidgetWin_1")]
    [InlineData("ApplicationFrameWindow")]
    public void Ordinary_windows_still_receive_fullscreen_detection(string windowClass)
    {
        Assert.False(TaskbarTopologySource.IsDesktopSurface(101, 100, 12, 12, windowClass));
    }

    [Fact]
    public void Missing_shell_or_process_identity_does_not_exempt_other_windows()
    {
        Assert.False(TaskbarTopologySource.IsDesktopSurface(0, 0, 0, 0, "WorkerW"));
        Assert.False(TaskbarTopologySource.IsDesktopSurface(101, 0, 12, 12, "WorkerW"));
        Assert.False(TaskbarTopologySource.IsDesktopSurface(101, 100, 0, 0, "WorkerW"));
    }
}
