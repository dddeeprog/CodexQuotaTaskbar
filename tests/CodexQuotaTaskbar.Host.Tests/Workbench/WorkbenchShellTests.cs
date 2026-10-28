using System.Text.Json;
using CodexQuotaTaskbar.Host.Settings;
using CodexQuotaTaskbar.Host.Workbench;

namespace CodexQuotaTaskbar.Host.Tests.Workbench;

public sealed class WorkbenchShellTests
{
    [Fact]
    public void Native_shortcut_conflict_is_reported_and_dispose_releases_only_its_own_registration()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            using var first = new WorkbenchHotkey(); using var second = new WorkbenchHotkey();
            try
            {
                Assert.True(first.Register("Ctrl+Alt+F24"), first.Error);
                Assert.False(second.Register("Ctrl+Alt+F24")); Assert.NotNull(second.Error);
                first.Dispose(); Assert.True(second.Register("Ctrl+Alt+F24"), second.Error);
            }
            catch (Exception error) { failure = error; }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    [Theory]
    [InlineData("Ctrl+Alt+G", true)] [InlineData("Win+Shift+F12", true)]
    [InlineData("G", false)] [InlineData("Shift+G", false)]
    [InlineData("Ctrl+Ctrl+G", false)] [InlineData("Ctrl+Alt+Enter", false)]
    public void Shortcut_requires_explicit_safe_modifiers_and_a_supported_key(string input, bool valid)
        => Assert.Equal(valid, WorkbenchHotkey.TryParse(input, out _, out _));

    [Fact]
    public void Old_settings_load_without_opting_into_a_visible_workbench()
    {
        var old = JsonSerializer.Deserialize<AppSettings>("{\"ShowAllTaskbars\":true,\"LowQuotaThreshold\":10}")!;
        Assert.Null(old.Workbench);
        var normalized = new WorkbenchSettings(Width: double.NaN, Height: double.PositiveInfinity, Left: double.NaN).Normalize();
        Assert.Equal(1100, normalized.Width); Assert.Equal(720, normalized.Height); Assert.Null(normalized.Left);
    }

    [Theory]
    [InlineData(1, 1, true, false, false, false, false, true)]
    [InlineData(1, 2, true, false, false, false, false, false)]
    [InlineData(1, 1, true, false, true, false, false, false)]
    [InlineData(1, 1, true, false, false, true, false, false)]
    [InlineData(1, 1, true, false, false, false, true, false)]
    [InlineData(1, 1, true, true, false, false, false, false)]
    public void Deferred_dismissal_does_not_close_new_activation_owned_dialogs_pins_or_capture(
        long requested, long current, bool visible, bool active, bool pinned, bool captured, bool interacting, bool expected)
        => Assert.Equal(expected, WorkbenchWindow.ShouldAutoHide(requested, current, visible, active, pinned, captured, interacting));

    [Fact]
    public void Window_construction_and_pin_guard_do_not_create_a_native_window()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var view = new WorkbenchWindow(new());
            try
            {
                view.SetCaptureActive(true); view.SetPinned(true); Assert.False(view.Pinned);
                view.SetCaptureActive(false); view.SetPinned(true); Assert.True(view.Topmost);
                Assert.Equal(0, new System.Windows.Interop.WindowInteropHelper(view).Handle);
                Assert.False(view.CanStartInteraction);
                Assert.NotNull(view.FindName("ChooseDirectoryButton"));
                Assert.Equal(System.Windows.Visibility.Collapsed, view.StatusBorder.Visibility);
                Assert.Empty(view.StatusLabel.Text);
                Assert.Contains("Ctrl+Alt+G", Assert.IsType<string>(view.HotkeyButton.ToolTip));
                Assert.NotNull(view.PinButton.ToolTip);
                view.SetStatus("快捷键已被其他应用占用。");
                Assert.Equal(System.Windows.Visibility.Visible, view.StatusBorder.Visibility);
                view.SetStatus("");
                Assert.Equal(System.Windows.Visibility.Collapsed, view.StatusBorder.Visibility);
            }
            catch (Exception error) { failure = error; }
            finally { view.CloseForShutdown(); System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
