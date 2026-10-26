using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using CodexQuotaTaskbar.Host.Settings;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class TrayMenuTypingTests
{
    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    public void Typing_toggle_changes_only_its_own_preference_and_reflects_it_immediately(bool enabled, bool ball, bool reduced) => Sta(() =>
    {
        var original = new AppSettings(false, false, false, 31, false, true, 27, true,
            ball, reduced, false, true, enabled);
        var menu = new TrayMenuWindow(original);
        try
        {
            var button = Assert.IsType<Button>(menu.FindName("CompanionTypingButton"));
            Assert.Equal("跟随键盘敲击", AutomationProperties.GetName(button));
            Assert.Equal(enabled ? "已选中" : "未选中", AutomationProperties.GetItemStatus(button));
            Assert.Contains("当前物理键位", Assert.IsType<string>(button.ToolTip), StringComparison.Ordinal);
            var changes = new List<AppSettings>();
            menu.SettingsChangeRequested += (_, value) => changes.Add(value);

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(original with { CompanionTyping = !enabled }, Assert.Single(changes));
            Assert.Equal(!enabled ? "已选中" : "未选中", AutomationProperties.GetItemStatus(button));
            Assert.Equal("跟随键盘敲击", button.Content);
            Assert.Equal(!enabled ? "On" : null, button.Tag);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(original, changes[1]);
            Assert.False(menu.IsVisible);
            Assert.Equal(IntPtr.Zero, new WindowInteropHelper(menu).Handle);
        }
        finally { menu.Close(); }
    });

    [Fact]
    public void Applying_paused_preferences_keeps_an_enabled_choice_and_does_not_emit_a_change() => Sta(() =>
    {
        var menu = new TrayMenuWindow(AppSettings.Default);
        try
        {
            var changes = new List<AppSettings>();
            menu.SettingsChangeRequested += (_, value) => changes.Add(value);
            var button = Assert.IsType<Button>(menu.FindName("CompanionTypingButton"));
            menu.ApplySettings(AppSettings.Default with { CompanionTyping = true, CompanionBall = false, ReduceCompanionMotion = true });
            Assert.Equal("已选中", AutomationProperties.GetItemStatus(button));
            Assert.Empty(changes);
            menu.ApplySettings(AppSettings.Default);
            Assert.Equal("未选中", AutomationProperties.GetItemStatus(button));
            Assert.Empty(changes);
        }
        finally { menu.Close(); }
    });

    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Offscreen menu test did not finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
