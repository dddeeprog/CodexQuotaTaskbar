using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using CodexQuotaTaskbar.Host.Settings;
using CodexQuotaTaskbar.Host.UI;
using Size = System.Windows.Size;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class CoexistingTrayMenuTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void Cat_toggle_and_status_choices_never_switch_off_each_other_or_change_input(bool cat, bool ball) => Sta(() =>
    {
        var initial = AppSettings.Default with
        {
            CompanionBall = ball, BongoCatVisible = cat, CompanionAppearance = "bongo-cat",
            CompanionTyping = true, MaterialTransparencyPercent = 27, DotRealtime = true,
        };
        var menu = new TrayMenuWindow(initial);
        try
        {
            var changes = new List<AppSettings>();
            menu.SettingsChangeRequested += (_, next) => changes.Add(next);
            Named<Button>(menu, "CompanionBongoCatButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var afterCat = initial with { BongoCatVisible = !cat };
            Assert.Equal(afterCat, Assert.Single(changes));
            Assert.Equal(ball, afterCat.CompanionBallEnabled);

            Named<Button>(menu, "CompanionWhiteBallButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(afterCat with { CompanionBall = true }, changes[1]);
            Assert.Equal("已选中", AutomationProperties.GetItemStatus(Named<Button>(menu, "CompanionWhiteBallButton")));
            Assert.Equal(!cat ? "已开启" : "已关闭", AutomationProperties.GetItemStatus(Named<Button>(menu, "CompanionBongoCatButton")));
            Named<Button>(menu, "CompanionSignalLightButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(afterCat with { CompanionBall = false }, changes[2]);
            Assert.Equal(!cat, changes[2].EffectiveBongoCatVisible);
            Assert.True(changes.All(item => item.CompanionTyping));
            Assert.False(menu.IsVisible);
            Assert.Equal(nint.Zero, new WindowInteropHelper(menu).Handle);
        }
        finally { menu.Close(); }
    });

    [Fact]
    public void A_legacy_cat_is_pinned_before_switching_status_to_lights_so_it_does_not_disappear() => Sta(() =>
    {
        var initial = AppSettings.Default with { CompanionAppearance = "bongo-cat", CompanionBall = true };
        var menu = new TrayMenuWindow(initial);
        try
        {
            var changes = new List<AppSettings>();
            menu.SettingsChangeRequested += (_, value) => changes.Add(value);
            Named<Button>(menu, "CompanionSignalLightButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var next = Assert.Single(changes);
            Assert.Equal(initial with { CompanionBall = false, BongoCatVisible = true }, next);
            Assert.True(next.EffectiveBongoCatVisible);
            Assert.False(next.CompanionBallEnabled);
            Assert.False(next.CompanionTyping);
            Assert.Equal(nint.Zero, new WindowInteropHelper(menu).Handle);
        }
        finally { menu.Close(); }
    });

    [Theory]
    [InlineData(96)] [InlineData(120)] [InlineData(144)] [InlineData(192)]
    public void Independent_cat_toggle_remains_normal_size_and_fits_the_same_width_page(int dpi) => Sta(() =>
    {
        var menu = new TrayMenuWindow(AppSettings.Default with { BongoCatVisible = true, CompanionBall = true });
        try
        {
            menu.OpenSection(MenuSection.Appearance);
            Assert.Equal(MenuSection.Appearance, menu.CurrentSection);
            var root = (FrameworkElement)menu.Content;
            VisualTreeHelper.SetRootDpi(root, new DpiScale(dpi / 96d, dpi / 96d));
            for (var index = 0; index < 3; index++)
            {
                root.Measure(new Size(menu.Width, double.PositiveInfinity));
                root.Arrange(new Rect(0, 0, menu.Width, root.DesiredSize.Height)); root.UpdateLayout();
            }
            var toggle = Named<Button>(menu, "CompanionBongoCatButton");
            var viewport = Named<ScrollViewer>(menu, "MenuScroller");
            var relativeTop = toggle.TranslatePoint(default(System.Windows.Point), viewport).Y;
            var centeredOffset = viewport.VerticalOffset + relativeTop - (viewport.ActualHeight - toggle.ActualHeight) / 2;
            viewport.ScrollToVerticalOffset(Math.Clamp(centeredOffset, 0, viewport.ScrollableHeight));
            for (var index = 0; index < 3; index++)
            {
                root.Measure(new Size(menu.Width, double.PositiveInfinity));
                root.Arrange(new Rect(0, 0, menu.Width, root.DesiredSize.Height)); root.UpdateLayout();
            }
            Assert.Equal(272, menu.Width);
            Assert.Equal(13, toggle.FontSize);
            Assert.True(toggle.ActualHeight >= 32 && toggle.ActualWidth >= 32);
            var bounds = toggle.TransformToAncestor(root).TransformBounds(new Rect(toggle.RenderSize));
            Assert.InRange(bounds.Left, 0, menu.Width); Assert.InRange(bounds.Right, 0, menu.Width);
            var viewportBounds = viewport.TransformToAncestor(root).TransformBounds(new Rect(viewport.RenderSize));
            Assert.True(bounds.Top >= viewportBounds.Top - .5 && bounds.Bottom <= viewportBounds.Bottom + .5);
            Assert.Equal(ScrollBarVisibility.Auto, viewport.VerticalScrollBarVisibility);
            Assert.Equal("已开启", AutomationProperties.GetItemStatus(toggle));
            foreach (var text in Descendants<TextBlock>(toggle).Where(text => !string.IsNullOrWhiteSpace(text.Text)))
            {
                var full = new FormattedText(text.Text, CultureInfo.CurrentUICulture, text.FlowDirection,
                    new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
                    text.FontSize, text.Foreground, dpi / 96d);
                Assert.True(full.WidthIncludingTrailingWhitespace <= text.ActualWidth + .5, text.Text);
            }
            Assert.True(Named<Button>(menu, "CompanionPreviewButton").IsEnabled);
            Assert.Equal("已选中", AutomationProperties.GetItemStatus(Named<Button>(menu, "CompanionWhiteBallButton")));
            Assert.Equal(nint.Zero, new WindowInteropHelper(menu).Handle);
        }
        finally { menu.Close(); }
    });

    private static T Named<T>(TrayMenuWindow window, string name) where T : DependencyObject => (T)window.FindName(name);
    private static IEnumerable<T> Descendants<T>(DependencyObject owner) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(owner); index++)
        {
            var child = VisualTreeHelper.GetChild(owner, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Private coexisting menu fixture timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
