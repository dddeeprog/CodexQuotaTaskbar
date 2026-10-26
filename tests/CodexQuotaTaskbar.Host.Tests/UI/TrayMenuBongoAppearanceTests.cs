using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using CodexQuotaTaskbar.Host.Settings;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class TrayMenuBongoAppearanceTests
{
    [Fact]
    public void Cat_and_status_display_choices_are_independent_and_never_enable_input() => Sta(() =>
    {
        var original = new AppSettings(false, false, false, 31, false, true, 27, true,
            false, false, false, true, false);
        var menu = new TrayMenuWindow(original);
        try
        {
            var changes = new List<AppSettings>();
            menu.SettingsChangeRequested += (_, value) => changes.Add(value);
            var bongo = Named(menu, "CompanionBongoCatButton");
            var white = Named(menu, "CompanionWhiteBallButton");

            bongo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            var bongoSettings = original with { BongoCatVisible = true };
            Assert.Equal(bongoSettings, Assert.Single(changes));
            Assert.False(changes[0].CompanionTyping);
            Assert.Equal("已开启", AutomationProperties.GetItemStatus(bongo));
            Assert.Equal("未选中", AutomationProperties.GetItemStatus(white));
            white.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(bongoSettings with { CompanionBall = true }, changes[1]);
            Assert.Equal("已选中", AutomationProperties.GetItemStatus(white));
            Assert.Equal("已开启", AutomationProperties.GetItemStatus(bongo));
            Named(menu, "CompanionSignalLightButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(changes[1] with { CompanionBall = false }, changes[2]);
            Assert.True(changes[2].EffectiveBongoCatVisible);
            bongo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(changes[2] with { BongoCatVisible = false }, changes[3]);
            Assert.False(changes[3].CompanionBallEnabled);
            Assert.False(menu.IsVisible);
            Assert.Equal(IntPtr.Zero, new WindowInteropHelper(menu).Handle);
        }
        finally { menu.Close(); }
    });

    [Theory]
    [InlineData(160)]
    [InlineData(192)]
    [InlineData(240)]
    public void Size_presets_change_only_size_and_remain_available_when_motion_is_reduced(int size) => Sta(() =>
    {
        var original = AppSettings.Default with { CompanionAppearance = "bongo-cat", ReduceCompanionMotion = true };
        var menu = new TrayMenuWindow(original);
        try
        {
            var changes = new List<AppSettings>();
            menu.SettingsChangeRequested += (_, value) => changes.Add(value);
            var choice = Named(menu, $"BongoSize{size}Button");
            Assert.True(choice.IsEnabled);

            choice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(original with { BongoSizeDip = size }, Assert.Single(changes));
            Assert.Equal("已选中", AutomationProperties.GetItemStatus(choice));
            Assert.False(changes[0].CompanionTyping);
        }
        finally { menu.Close(); }
    });

    [Fact]
    public void Applying_appearance_keeps_user_preferences_and_does_not_emit_or_open_anything() => Sta(() =>
    {
        var menu = new TrayMenuWindow(AppSettings.Default);
        try
        {
            var changes = new List<AppSettings>();
            menu.SettingsChangeRequested += (_, value) => changes.Add(value);
            foreach (var size in new[] { 160, 192, 240 }) Assert.False(Named(menu, $"BongoSize{size}Button").IsEnabled);
            var value = AppSettings.Default with { CompanionAppearance = "bongo-cat", BongoSizeDip = 160,
                CompanionBall = false, BongoCatVisible = true };

            menu.ApplySettings(value);

            Assert.Empty(changes);
            Assert.True(Named(menu, "BongoSize160Button").IsEnabled);
            Assert.Equal("已选中", AutomationProperties.GetItemStatus(Named(menu, "BongoSize160Button")));
            Assert.Equal("已开启", AutomationProperties.GetItemStatus(Named(menu, "CompanionBongoCatButton")));
            Assert.Equal("未选中", AutomationProperties.GetItemStatus(Named(menu, "CompanionTypingButton")));
            Assert.False(menu.IsVisible);
            Assert.Equal(IntPtr.Zero, new WindowInteropHelper(menu).Handle);
        }
        finally { menu.Close(); }
    });

    private static Button Named(TrayMenuWindow menu, string name) => Assert.IsType<Button>(menu.FindName(name));

    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Offscreen Bongo appearance menu test did not finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
