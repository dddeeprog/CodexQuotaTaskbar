using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using CodexQuotaTaskbar.Core.Quota;
using CodexQuotaTaskbar.Core.Sessions;
using CodexQuotaTaskbar.Host.Settings;
using CodexQuotaTaskbar.Host.Tests.TestSupport;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class UnifiedTypographyTests
{
    [Theory]
    [InlineData(108)]
    [InlineData(144)]
    public void Classic_layout_restores_two_quota_rows_logo_reset_and_external_badge(int dpi)
    {
        RunInSta(() =>
        {
            // Construct alternating modes to catch leaked shared control/style state.
            foreach (var unified in new[] { false, true, false })
            {
                var window = new QuotaCapsuleWindow("layout-choice", unified);
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    window.Apply(CreateQuota(new(true, false, 120)));
                    window.ApplySessions(new([new("real-id", "介绍项目", CodexSessionState.Running, now)], now));
                    window.SetMaterialTransparency(37);
                    Layout(window, dpi, window.DesiredHeight);
                    Assert.Equal(unified, window.IsUnifiedPanel);
                    Assert.Equal(unified ? 280 : 306, window.Width);
                    Assert.Equal(unified ? 132 : 112, window.DesiredHeight);
                    Assert.Equal(unified ? 248 : 272, Named<Border>(window, "GlassBorder").ActualWidth);
                    Assert.Equal(.63, Named<Border>(window, "GlassBorder").Background.Opacity, 5);
                    var first = Named<TextBlock>(window, "FirstLabel");
                    var second = Named<TextBlock>(window, "SecondLabel");
                    var root = (UIElement)window.Content;
                    if (!unified)
                    {
                        Assert.True(first.TranslatePoint(new(), root).Y < second.TranslatePoint(new(), root).Y);
                        Assert.Same(Named<Grid>(window, "Shell"), Named<Border>(window, "SessionBadge").Parent);
                        Assert.Empty(Named<StackPanel>(window, "PanelSessionsList").Children);
                        var summary = Named<TextBlock>(window, "ResetSummaryText");
                        Assert.Equal(OverlayTypography.Text, summary.FontFamily);
                        Assert.True(summary.ActualWidth > 140);
                        Uri? opened = null;
                        window.OpenResetSourceRequested += uri => opened = uri;
                        var link = Assert.IsType<System.Windows.Documents.Hyperlink>(window.FindName("ResetSourceLink"));
                        link.RaiseEvent(new RoutedEventArgs(System.Windows.Documents.Hyperlink.ClickEvent));
                        Assert.Equal(CodexQuotaTaskbar.Host.Resets.CodexResetFeedClient.SiteUri, opened);
                        SavePanelProof(window, dpi, 5, "classic");
                    }
                    else Assert.Null(window.FindName("ResetSummaryText"));
                    AssertRenderedText(first, dpi, 10, FontWeights.Medium);
                    AssertRenderedText(Named<TextBlock>(window, "FirstValue"), dpi, 11, FontWeights.SemiBold);
                }
                finally { window.Close(); }
            }
        });
    }

    [Fact]
    public void Menu_switches_styles_immediately_and_preserves_material_preferences()
    {
        RunInSta(() =>
        {
            var settings = AppSettings.Default with { MaterialTransparencyPercent = 37 };
            var menu = new TrayMenuWindow(settings);
            try
            {
                var requested = new List<AppSettings>();
                menu.SettingsChangeRequested += (_, value) => requested.Add(value);
                var classic = Named<System.Windows.Controls.Button>(menu, "ClassicStyleButton");
                var unified = Named<System.Windows.Controls.Button>(menu, "UnifiedStyleButton");
                Assert.Equal(Visibility.Visible, Named<System.Windows.Shapes.Path>(menu, "ClassicStyleCheck").Visibility);
                unified.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Assert.Equal(settings with { UnifiedPanel = true }, Assert.Single(requested));
                Assert.Equal(Visibility.Visible, Named<System.Windows.Shapes.Path>(menu, "UnifiedStyleCheck").Visibility);
                Assert.Equal(Visibility.Hidden, Named<System.Windows.Shapes.Path>(menu, "ClassicStyleCheck").Visibility);
                Assert.Equal("已选中", System.Windows.Automation.AutomationProperties.GetItemStatus(unified));
                classic.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Assert.Equal(settings, requested[1]);
                Assert.Equal(Visibility.Visible, Named<System.Windows.Shapes.Path>(menu, "ClassicStyleCheck").Visibility);
            }
            finally { menu.Close(); }
        });
    }

    [Theory]
    [InlineData(108, 0)]
    [InlineData(108, 1)]
    [InlineData(108, 2)]
    [InlineData(108, 5)]
    [InlineData(144, 0)]
    [InlineData(144, 1)]
    [InlineData(144, 2)]
    [InlineData(144, 5)]
    public void Integrated_panel_shares_one_surface_and_keeps_compact_rows_readable(int dpi, int count)
    {
        RunInSta(() =>
        {
            var window = new QuotaCapsuleWindow("integrated-panel-test");
            try
            {
                var now = DateTimeOffset.UtcNow;
                string[] titles = ["开题报告5.0", "介绍项目", "查找主设备模型", "这是一个超过一行可用宽度的长会话名称", "额度显示器"];
                var sessions = Enumerable.Range(0, count).Select(index => new CodexSessionSnapshot(index.ToString(), titles[index],
                    index == 2 ? CodexSessionState.Waiting : CodexSessionState.Running, now)).ToArray();
                window.Apply(CreateQuota(new(true, false, 120)));
                window.ApplySessions(new(sessions, now));
                Layout(window, dpi, window.DesiredHeight);
                Assert.Equal(280, window.Width);
                Assert.Equal(248, Named<Border>(window, "GlassBorder").ActualWidth);
                Assert.Equal(48, Named<Grid>(window, "QuotaHeader").ActualHeight);
                Assert.Equal(80 + QuotaCapsuleWindow.PanelSessionHeight(count, true), window.DesiredHeight);
                var list = Named<StackPanel>(window, "PanelSessionsList");
                var scroll = Named<ScrollViewer>(window, "PanelSessionsScroller");
                Assert.Equal(count, list.Children.Count);
                foreach (System.Windows.Controls.Button row in list.Children)
                {
                    Assert.Equal(34, row.ActualHeight);
                    var texts = ((Grid)row.Content).Children.OfType<TextBlock>().ToArray();
                    Assert.Equal(TextTrimming.CharacterEllipsis, texts[0].TextTrimming);
                    Assert.Equal(OverlayTypography.Text, texts[0].FontFamily);
                    AssertRenderedText(texts[1], dpi, 11, FontWeights.Normal);
                    if (texts[0].Text.Length < 15) AssertRenderedText(texts[0], dpi, 12, FontWeights.Medium);
                    var brush = Assert.IsType<SolidColorBrush>(row.Background);
                    Assert.Equal(0, brush.Color.A); // No second glass layer behind each row.
                }
                if (count > 3) Assert.True(scroll.ScrollableHeight > 0);
                if (count > 0)
                {
                    string? opened = null;
                    window.OpenSessionRequested += id => opened = id;
                    ((System.Windows.Controls.Button)list.Children[0]).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    Assert.Equal("0", opened);
                    var firstTop = ((FrameworkElement)list.Children[0]).TranslatePoint(new System.Windows.Point(), window.Content as UIElement).Y;
                    SavePanelProof(window, dpi, count, "expanded");
                    window.TogglePanelSessions();
                    Layout(window, dpi, window.DesiredHeight);
                    Assert.Equal(132, window.DesiredHeight);
                    Assert.Equal(firstTop, ((FrameworkElement)list.Children[0]).TranslatePoint(new System.Windows.Point(), window.Content as UIElement).Y);
                    Assert.Equal(0, scroll.VerticalOffset);
                    SavePanelProof(window, dpi, count, "collapsed");
                    window.ApplySessions(CodexSessionsSnapshot.Empty);
                    Layout(window, dpi, window.DesiredHeight);
                    Assert.Equal(80, window.DesiredHeight);
                    Assert.Empty(list.Children);
                }
            }
            finally { window.Close(); }
        });
    }

    private static void SavePanelProof(QuotaCapsuleWindow window, int dpi, int count, string state)
    {
        if (count != 5 || Environment.GetEnvironmentVariable("CODEX_QUOTA_UI_PROOFS") != "1") return;
        var content = (FrameworkElement)window.Content;
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(window.Width * dpi / 96d),
            (int)Math.Ceiling(window.DesiredHeight * dpi / 96d), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        var directory = Path.Combine(RepositoryPaths.Root, "artifacts", "material-transparency-proof");
        Directory.CreateDirectory(directory);
        using var stream = File.Create(Path.Combine(directory, $"integrated-panel-{dpi}-{state}.png"));
        encoder.Save(stream);
    }

    [Theory]
    [InlineData("QuotaCapsuleWindow.xaml")]
    [InlineData("QuotaPopoverWindow.xaml")]
    [InlineData("SessionStackWindow.xaml")]
    [InlineData("TrayMenuWindow.xaml")]
    [InlineData("SubscriptionBrowserWindow.xaml")]
    public void Every_application_window_sets_the_shared_default_font(string filename)
    {
        var xaml = XDocument.Load(Path.Combine(RepositoryPaths.Root, "src", "CodexQuotaTaskbar.Host", "UI", filename));
        var family = filename is "TrayMenuWindow.xaml" or "QuotaPopoverWindow.xaml" ? "Interface" : "Text";
        Assert.Equal("{x:Static ui:OverlayTypography." + family + "}", (string?)xaml.Root!.Attribute("FontFamily"));
    }

    [Fact]
    public void Four_overlay_surfaces_and_their_generated_text_share_one_family()
    {
        RunInSta(() =>
        {
            var capsule = new QuotaCapsuleWindow("unified-font-test");
            var details = new QuotaPopoverWindow();
            var sessions = new SessionStackWindow("unified-font-test");
            var menu = new TrayMenuWindow(AppSettings.Default);
            Window[] windows = [capsule, details, sessions, menu];
            try
            {
                capsule.Apply(CreateQuota(new(true, false, 120)));
                details.Apply(CreateQuota(new(true, false, 120)));
                sessions.Apply(new([new("font-test", "统一字体", CodexSessionState.Running, DateTimeOffset.UtcNow)], DateTimeOffset.UtcNow));
                foreach (var window in windows)
                {
                    var family = window is TrayMenuWindow or QuotaPopoverWindow ? OverlayTypography.Interface : OverlayTypography.Text;
                    Assert.Equal(family, window.FontFamily);
                    var labels = LogicalText(window).ToArray();
                    Assert.NotEmpty(labels);
                    Assert.All(labels, label => Assert.Equal(family, label.FontFamily));
                }
            }
            finally
            {
                foreach (var window in windows) window.Close();
            }
        });
    }

    [Theory]
    [InlineData(108, 9)]
    [InlineData(108, 10)]
    [InlineData(108, 20)]
    [InlineData(144, 9)]
    [InlineData(144, 10)]
    [InlineData(144, 20)]
    public void Capsule_percentages_and_badges_keep_their_sizes_and_fit_with_the_embedded_font(int dpi, int count)
    {
        RunInSta(() =>
        {
            var window = new QuotaCapsuleWindow("unified-font-test");
            try
            {
                window.Apply(CreateQuota(new(true, false, 120)));
                var now = DateTimeOffset.UtcNow;
                CodexSessionState[] states = [CodexSessionState.Waiting, CodexSessionState.Running, CodexSessionState.Review];
                var sessions = states.SelectMany(state => Enumerable.Range(0, count)
                    .Select(index => new CodexSessionSnapshot($"{state}-{index}", "字体测试", state, now))).ToArray();
                window.ApplySessions(new(sessions, now));
                // Inspect the final, unanimated text geometry without waiting for a UI dispatcher.
                foreach (var name in new[] { "WaitingLightText", "RunningLightText", "CompletedLightText" })
                {
                    var text = Named<TextBlock>(window, name);
                    text.BeginAnimation(UIElement.OpacityProperty, null);
                    text.Opacity = 1;
                    text.RenderTransform = Transform.Identity;
                }
                Layout(window, dpi, window.Height);

                foreach (var name in new[] { "FirstValue", "SecondValue" })
                {
                    var text = Named<TextBlock>(window, name);
                    Assert.Equal("100%", text.Text);
                    AssertRenderedText(text, dpi, 11, FontWeights.SemiBold);
                    Assert.True(text.ActualWidth <= 38, "The existing percentage column must remain sufficient.");
                }
                foreach (var name in new[] { "Waiting", "Running", "Completed" })
                {
                    var light = Named<Grid>(window, $"{name}Light");
                    var text = Named<TextBlock>(window, $"{name}LightText");
                    Assert.Equal(count.ToString(CultureInfo.InvariantCulture), text.Text);
                    Assert.Equal(16, light.ActualWidth);
                    Assert.Equal(16, light.ActualHeight);
                    AssertRenderedText(text, dpi, 8, FontWeights.SemiBold);
                    var bounds = text.TransformToAncestor(light).TransformBounds(new Rect(text.RenderSize));
                    Assert.True(bounds.Left >= -0.5 && bounds.Right <= 16.5, $"{name} badge overflowed horizontally.");
                    Assert.True(bounds.Top >= -0.5 && bounds.Bottom <= 16.5, $"{name} badge overflowed vertically.");

                    var tooltip = Assert.IsType<ToolTip>(light.ToolTip);
                    Assert.Equal(OverlayTypography.Text, tooltip.FontFamily);
                    LayoutElement(tooltip, dpi, 200, 100);
                    var tooltipText = Assert.Single(VisualDescendants<TextBlock>(tooltip));
                    AssertRenderedText(tooltipText, dpi, tooltip.FontSize, tooltip.FontWeight);
                }
                AssertRenderedText(Named<TextBlock>(window, "FirstLabel"), dpi, 10, FontWeights.Medium);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(108)]
    [InlineData(144)]
    public void Detail_numbers_chinese_credit_states_and_close_symbol_render_without_fallback_or_clipping(int dpi)
    {
        RunInSta(() =>
        {
            var window = new QuotaPopoverWindow();
            try
            {
                QuotaCreditsSnapshot[] creditStates = [new(true, false, 12345.68m), new(true, true, null), new(true, false, null)];
                string[] expected = ["12,345.68", "不限", "可用"];
                for (var index = 0; index < creditStates.Length; index++)
                {
                    window.Apply(CreateQuota(creditStates[index]));
                    Layout(window, dpi);
                    var credits = Named<TextBlock>(window, "ExtraCreditsText");
                    Assert.Equal(expected[index], credits.Text);
                    AssertRenderedText(credits, dpi, 13, FontWeights.SemiBold, true);
                    var percentages = VisualDescendants<TextBlock>(Named<StackPanel>(window, "RowsPanel"))
                        .Where(text => text.Text == "100%").ToArray();
                    Assert.Equal(2, percentages.Length);
                    Assert.All(percentages, text => AssertRenderedText(text, dpi, 20, FontWeights.SemiBold, true));
                }
                AssertRenderedText(Named<TextBlock>(window, "TitleText"), dpi, 15, FontWeights.SemiBold, true);
                var close = Named<Button>(window, "CloseButton");
                Assert.Equal(OverlayTypography.Interface, close.FontFamily);
                var symbol = Assert.Single(VisualDescendants<TextBlock>(close));
                Assert.Equal("×", symbol.Text);
                AssertRenderedText(symbol, dpi, 16, FontWeights.Normal, true);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static QuotaSnapshot CreateQuota(QuotaCreditsSnapshot credits)
    {
        var now = DateTimeOffset.UtcNow;
        return QuotaSnapshot.Available([
            new(QuotaWindowKind.Primary, 100, 300, now.AddHours(3)),
            new(QuotaWindowKind.Secondary, 100, 10080, now.AddDays(5)),
        ], now, "PRO 5X", extraCredits: credits);
    }

    private static void Layout(Window window, int dpi, double? fixedHeight = null) =>
        LayoutElement((FrameworkElement)window.Content, dpi, window.Width, fixedHeight ?? window.MaxHeight, fixedHeight);

    private static void LayoutElement(FrameworkElement content, int dpi, double width, double height, double? fixedHeight = null)
    {
        VisualTreeHelper.SetRootDpi(content, new DpiScale(dpi / 96d, dpi / 96d));
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, fixedHeight ?? content.DesiredSize.Height));
        content.UpdateLayout();
        Assert.Equal(dpi / 96d, VisualTreeHelper.GetDpi(content).PixelsPerDip);
    }

    private static void AssertRenderedText(TextBlock text, int dpi, double expectedSize, FontWeight expectedWeight, bool systemUi = false)
    {
        Assert.Equal(systemUi ? OverlayTypography.Interface : OverlayTypography.Text, text.FontFamily);
        Assert.Equal(expectedSize, text.FontSize);
        Assert.Equal(expectedWeight, text.FontWeight);
        var drawing = VisualTreeHelper.GetDrawing(text);
        Assert.NotNull(drawing);
        var glyphs = GlyphRuns(drawing).ToArray();
        Assert.NotEmpty(glyphs);
        foreach (var glyph in glyphs)
        {
            if (systemUi) Assert.DoesNotContain("Noto Sans SC", glyph.GlyphTypeface.FamilyNames.Values);
            else
            {
                Assert.Contains("Noto Sans SC", glyph.GlyphTypeface.FamilyNames.Values);
                Assert.Equal("pack", glyph.GlyphTypeface.FontUri.Scheme);
            }
            Assert.Equal(expectedSize, glyph.FontRenderingEmSize);
            Assert.All(glyph.GlyphIndices, index => Assert.NotEqual((ushort)0, index));
        }
        Assert.Equal(text.Text, string.Concat(glyphs.SelectMany(glyph => glyph.Characters)));
        var measured = new FormattedText(text.Text, CultureInfo.CurrentUICulture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
            text.FontSize, text.Foreground, dpi / 96d);
        Assert.True(measured.WidthIncludingTrailingWhitespace <= text.ActualWidth + 0.5,
            $"'{text.Text}' needs {measured.WidthIncludingTrailingWhitespace:0.##} DIP, but has {text.ActualWidth:0.##} DIP at {dpi} DPI.");
        Assert.True(measured.Height <= text.ActualHeight + 0.5,
            $"'{text.Text}' was vertically clipped at {dpi} DPI.");
    }

    private static T Named<T>(Window window, string name) where T : FrameworkElement =>
        Assert.IsType<T>(window.FindName(name));

    private static IEnumerable<TextBlock> LogicalText(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is TextBlock text) yield return text;
            foreach (var descendant in LogicalText(child)) yield return descendant;
        }
    }

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in VisualDescendants<T>(child)) yield return descendant;
        }
    }

    private static IEnumerable<GlyphRun> GlyphRuns(Drawing drawing)
    {
        if (drawing is GlyphRunDrawing glyph) yield return glyph.GlyphRun;
        else if (drawing is DrawingGroup group)
        {
            foreach (var child in group.Children.SelectMany(GlyphRuns)) yield return child;
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Unified typography verification did not finish in time.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
