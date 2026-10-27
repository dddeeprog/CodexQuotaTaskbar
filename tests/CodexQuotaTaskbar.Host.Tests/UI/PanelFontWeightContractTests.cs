using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using CodexQuotaTaskbar.Core.Quota;
using CodexQuotaTaskbar.Host.Settings;
using CodexQuotaTaskbar.Host.Shortcuts;
using CodexQuotaTaskbar.Host.UI;
using Button = System.Windows.Controls.Button;
using MenuItem = System.Windows.Controls.MenuItem;
using TextBox = System.Windows.Controls.TextBox;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class PanelFontWeightContractTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Menu_pages_keep_body_and_dynamic_action_labels_semibold(int section)
    {
        RunInSta(() =>
        {
            var window = new TrayMenuWindow(AppSettings.Default);
            try
            {
                window.OpenSection((MenuSection)section);
                Layout(window);
                Assert.Equal(FontWeights.SemiBold, window.FontWeight);
                string[] buttons = section switch
                {
                    0 => ["OpenCodexButton", "RefreshButton", "AppearanceButton", "PreferencesButton", "CheckUpdatesButton", "ExitButton"],
                    1 => ["ClassicStyleButton", "UnifiedStyleButton", "AllTaskbarsButton", "CompanionWhiteBallButton", "CompanionSignalLightButton", "CompanionBongoCatButton", "BongoSize160Button", "BongoSize192Button", "BongoSize240Button", "CompanionMotionButton", "CompanionTypingButton"],
                    _ => ["DotRealtimeButton", "SubscriptionLoginButton", "TrayIconButton", "NotificationsButton", "SubscriptionDetailsButton", "AutomaticUpdatesButton", "AutostartButton"],
                };
                foreach (var name in buttons)
                {
                    var button = Named<Button>(window, name);
                    Assert.Equal(OverlayTypography.Interface, button.FontFamily);
                    Assert.Equal(FontWeights.SemiBold, button.FontWeight);
                    Assert.Equal(13, button.FontSize);
                    Assert.Equal(32, button.Height);
                    Assert.All(VisualDescendants<TextBlock>(button), text => Assert.Equal(FontWeights.SemiBold, text.FontWeight));
                }
                foreach (var name in new[] { "TitleText", "VersionText", "PageTitleText" })
                    Assert.Equal(FontWeights.SemiBold, Named<TextBlock>(window, name).FontWeight);
                AssertUnshown(window);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Menu_back_label_is_semibold_but_its_chevron_keeps_normal_weight()
    {
        RunInSta(() =>
        {
            var window = new TrayMenuWindow(AppSettings.Default);
            try
            {
                window.OpenSection(MenuSection.Appearance);
                Layout(window);
                var back = Named<Button>(window, "PageBackButton");
                var label = Assert.IsType<TextBlock>(back.Content);
                Assert.Equal(FontWeights.SemiBold, back.FontWeight);
                Assert.Equal(FontWeights.SemiBold, label.FontWeight);
                var runs = label.Inlines.OfType<Run>().ToArray();
                Assert.Equal(2, runs.Length);
                Assert.Equal("‹", runs[0].Text);
                Assert.Equal(FontWeights.Normal, runs[0].FontWeight);
                Assert.Equal(" 返回", runs[1].Text);
                Assert.Equal(FontWeights.SemiBold, runs[1].FontWeight);
                AssertUnshown(window);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Detail_static_and_regenerated_quota_body_values_and_reset_status_are_semibold()
    {
        RunInSta(() =>
        {
            var window = new QuotaPopoverWindow();
            try
            {
                var now = new DateTimeOffset(2026, 10, 25, 10, 0, 0, TimeSpan.Zero);
                QuotaCreditsSnapshot[] states = [new(true, false, 12345.68m), new(true, true, null), new(true, false, null)];
                foreach (var credits in states)
                {
                    window.Apply(QuotaSnapshot.Available([
                        new(QuotaWindowKind.Primary, 100, 300, now.AddHours(3)),
                        new(QuotaWindowKind.Secondary, 100, 10080, now.AddDays(5)),
                    ], now, "PRO 5X", extraCredits: credits));
                    Layout(window);
                    Assert.Equal(FontWeights.SemiBold, window.FontWeight);
                    foreach (var name in new[] { "TitleText", "AccountSectionTitle", "PlanText", "ExpiryText", "ExtraCreditsText", "QuotaSectionTitle", "ShortcutsSectionTitle" })
                        Assert.Equal(FontWeights.SemiBold, Named<TextBlock>(window, name).FontWeight);
                    foreach (var name in new[] { "RefreshButton", "OpenCodexButton", "SubscriptionLoginButton" })
                        Assert.Equal(FontWeights.SemiBold, Named<Button>(window, name).FontWeight);
                    var rows = VisualDescendants<TextBlock>(Named<StackPanel>(window, "RowsPanel")).ToArray();
                    Assert.Equal(6, rows.Length);
                    Assert.All(rows, text => Assert.Equal(FontWeights.SemiBold, text.FontWeight));
                    Assert.Equal(2, rows.Count(text => text.FontSize == 13));
                    Assert.Equal(2, rows.Count(text => text.FontSize == 20 && text.Text == "100%"));
                    Assert.Equal(2, rows.Count(text => text.FontSize == 11 && text.Text.EndsWith(" 重置", StringComparison.Ordinal)));
                }
                AssertUnshown(window);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Detail_empty_quota_and_shortcut_failure_status_inherit_semibold()
    {
        RunInSta(() =>
        {
            var window = new QuotaPopoverWindow();
            try
            {
                window.Apply(QuotaSnapshot.Unavailable("Synthetic unavailable"));
                window.ApplyShortcuts(new([], "Synthetic unavailable"));
                Layout(window);
                var empty = Assert.Single(VisualDescendants<TextBlock>(Named<StackPanel>(window, "RowsPanel")));
                Assert.Equal("Synthetic unavailable", empty.Text); // Failure is not a successful empty quota set.
                Assert.Equal(11, empty.FontSize);
                Assert.Equal(FontWeights.SemiBold, empty.FontWeight);
                var status = Named<TextBlock>(window, "ShortcutsStatusText");
                Assert.Equal(Visibility.Visible, status.Visibility);
                Assert.Equal(11, status.FontSize);
                Assert.Equal(FontWeights.SemiBold, status.FontWeight);
                AssertUnshown(window);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Website_context_actions_are_semibold_while_icon_and_symbol_exceptions_are_preserved()
    {
        RunInSta(() =>
        {
            var window = new QuotaPopoverWindow();
            try
            {
                window.ApplyShortcuts(new([new WebShortcut("synthetic", "Example", new Uri("https://example.com"))]));
                Layout(window);
                var grid = Named<WrapPanel>(window, "ShortcutsGrid");
                var site = Assert.Single(grid.Children.OfType<Button>(), button => Equals(button.Tag, "synthetic"));
                var placeholder = Assert.IsType<TextBlock>(Assert.IsType<Border>(site.Content).Child);
                Assert.Equal(22, placeholder.FontSize);
                Assert.Equal(FontWeights.Medium, placeholder.FontWeight);
                var menu = Assert.IsType<System.Windows.Controls.ContextMenu>(site.ContextMenu);
                Assert.False(menu.IsOpen);
                Assert.Equal(OverlayTypography.Interface, menu.FontFamily);
                Assert.Equal(12, menu.FontSize);
                Assert.Equal(FontWeights.SemiBold, menu.FontWeight);
                var actions = menu.Items.OfType<MenuItem>().ToArray();
                Assert.Equal(3, actions.Length);
                Assert.All(actions, action => Assert.Equal(FontWeights.SemiBold, action.FontWeight));
                var add = Named<Button>(window, "AddShortcutButton");
                Assert.Equal("+", add.Content);
                Assert.Equal(25, add.FontSize);
                Assert.Equal(FontWeights.Light, add.FontWeight);
                var close = Named<Button>(window, "CloseButton");
                Assert.Equal("×", close.Content);
                Assert.Equal(16, close.FontSize);
                Assert.Equal(FontWeights.Normal, close.FontWeight);
                AssertUnshown(window);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Add_website_subpanel_keeps_body_input_and_actions_semibold_without_opening_a_picker()
    {
        RunInSta(() =>
        {
            var window = new WebShortcutAddWindow();
            try
            {
                Layout(window);
                Assert.Equal(FontWeights.SemiBold, window.FontWeight);
                var input = Named<TextBox>(window, "UrlInput");
                Assert.Equal(14, input.FontSize);
                Assert.Equal(FontWeights.SemiBold, input.FontWeight);
                foreach (var name in new[] { "SelectedIconText", "ErrorText" })
                    Assert.Equal(FontWeights.SemiBold, Named<TextBlock>(window, name).FontWeight);
                Assert.All(LogicalDescendants<TextBlock>(window), text => Assert.Equal(FontWeights.SemiBold, text.FontWeight));
                foreach (var name in new[] { "ChooseIconButton", "ClearIconButton", "CancelButton", "AcceptButton" })
                {
                    var button = Named<Button>(window, name);
                    Assert.Equal(12, button.FontSize);
                    Assert.Equal(30, button.Height);
                    Assert.Equal(FontWeights.SemiBold, button.FontWeight);
                }
                Assert.Null(window.SelectedIconPath);
                Assert.Null(window.AcceptedRequest);
                AssertUnshown(window);
            }
            finally { window.Close(); }
        });
    }

    private static T Named<T>(Window window, string name) where T : FrameworkElement =>
        Assert.IsType<T>(window.FindName(name));

    private static void Layout(Window window)
    {
        var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
        root.Measure(new System.Windows.Size(window.Width, Math.Min(window.MaxHeight, 552)));
        root.Arrange(new Rect(0, 0, window.Width, root.DesiredSize.Height));
        root.UpdateLayout();
    }

    private static void AssertUnshown(Window window)
    {
        Assert.False(window.IsVisible);
        Assert.Equal(IntPtr.Zero, new WindowInteropHelper(window).Handle);
        Assert.False(window.IsKeyboardFocusWithin);
    }

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T value) yield return value;
            foreach (var descendant in VisualDescendants<T>(child)) yield return descendant;
        }
    }

    private static IEnumerable<T> LogicalDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T value) yield return value;
            foreach (var descendant in LogicalDescendants<T>(child)) yield return descendant;
        }
    }

    private static void RunInSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Panel font-weight contract verification did not finish in time.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
