using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using CodexQuotaTaskbar.Core.Sessions;
using CodexQuotaTaskbar.Host.Settings;
using CodexQuotaTaskbar.Host.Tests.TestSupport;
using CodexQuotaTaskbar.Host.UI;
using WpfBrush = System.Windows.Media.Brush;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class MaterialTransparencyTests
{
    [Theory]
    [InlineData(-20, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 0.99)]
    [InlineData(35, 0.65)]
    [InlineData(100, 0)]
    [InlineData(120, 0)]
    public void Material_opacity_clamps_percent_and_defaults_to_opaque(int percent, double expected)
    {
        Assert.Equal(expected, OverlayGlassMaterial.MaterialOpacity(percent), 10);
    }

    [Fact]
    public void Material_brushes_are_frozen_and_repeated_changes_do_not_multiply_opacity()
    {
        var original = OverlayGlassMaterial.CreateSurfaceBrush();
        var changed = OverlayGlassMaterial.WithTransparency(original, 35);
        var repeated = OverlayGlassMaterial.WithTransparency(changed, 35);
        Assert.Equal(1, original.Opacity);
        Assert.NotSame(original, changed);
        Assert.Equal(0.65, changed.Opacity, 10);
        Assert.Equal(0.65, repeated.Opacity, 10);
        Assert.Equal(1, OverlayGlassMaterial.WithTransparency(repeated, 0).Opacity);

        WpfBrush[] brushes = [changed, repeated,
            OverlayGlassMaterial.CreateSurfaceBrush(35),
            SessionStackWindow.CreateSessionSurfaceBrush(35),
            SessionStackWindow.CreateCollapsedLayerSurfaceBrush(1, 35),
            SessionStackWindow.CreateCollapsedLayerSurfaceBrush(2, 35)];
        foreach (var brush in brushes)
        {
            Assert.True(brush.IsFrozen);
            Assert.Equal(0.65, brush.Opacity, 10);
            var gradient = Assert.IsType<LinearGradientBrush>(brush);
            Assert.All(gradient.GradientStops, stop => Assert.Equal(255, stop.Color.A));
        }
        Assert.Equal(original.GradientStops.Select(stop => (stop.Color, stop.Offset)),
            Assert.IsType<LinearGradientBrush>(changed).GradientStops.Select(stop => (stop.Color, stop.Offset)));
    }

    [Fact]
    public void All_window_materials_update_without_fading_text_icons_or_windows() => Sta(() =>
    {
        var island = new QuotaCapsuleWindow("transparency-island");
        var details = new QuotaPopoverWindow();
        var sessions = new SessionStackWindow("transparency-sessions");
        var menu = new TrayMenuWindow(AppSettings.Default);
        try
        {
            sessions.Apply(Sessions("one", "two", "three"));
            var windows = new Window[] { island, details, sessions, menu };
            var originalWindowOpacities = windows.Select(window => window.Opacity).ToArray();
            var originalForegrounds = new[]
            {
                Named<TextBlock>(island, "FirstValue").Foreground.ToString(),
                Named<TextBlock>(details, "TitleText").Foreground.ToString(),
                Named<TextBlock>(menu, "TitleText").Foreground.ToString(),
            };

            foreach (var percent in new[] { 0, 37, 37, 80, 100, 0 })
            {
                island.SetMaterialTransparency(percent);
                details.SetMaterialTransparency(percent);
                sessions.SetMaterialTransparency(percent);
                menu.ApplySettings(AppSettings.Default with { MaterialTransparencyPercent = percent });

                AssertSurface(Named<Border>(island, "GlassBorder").Background, percent);
                AssertSurface(Named<Border>(island, "SessionBadge").Background, percent);
                AssertSurface(Named<Border>(details, "GlassBorder").Background, percent);
                AssertSurface(Named<Border>(menu, "GlassBorder").Background, percent);
                AssertSessionSurfaces(sessions, percent);
                Assert.Equal(originalWindowOpacities, windows.Select(window => window.Opacity));
                Assert.Equal(originalForegrounds, new[]
                {
                    Named<TextBlock>(island, "FirstValue").Foreground.ToString(),
                    Named<TextBlock>(details, "TitleText").Foreground.ToString(),
                    Named<TextBlock>(menu, "TitleText").Foreground.ToString(),
                });
                Assert.Equal(1, Named<TextBlock>(island, "FirstValue").Opacity);
                Assert.Equal(1, Named<TextBlock>(details, "TitleText").Opacity);
                Assert.Equal(1, Named<TextBlock>(menu, "MaterialTransparencyValue").Opacity);
                Assert.Equal(1, Named<System.Windows.Shapes.Path>(sessions, "ExpandChevron").Opacity);
                Assert.Equal(1, Named<System.Windows.Shapes.Path>(menu, "ExitIcon").Opacity);
            }
        }
        finally
        {
            foreach (var window in new Window[] { island, details, sessions, menu }) window.Close();
        }
    });

    [Fact]
    public void New_expanded_sessions_and_removal_ghosts_keep_the_selected_material() => Sta(() =>
    {
        var window = new SessionStackWindow("transparency-new-sessions");
        try
        {
            window.SetMaterialTransparency(63);
            window.Apply(Sessions("one", "two", "three"));
            AssertSessionSurfaces(window, 63);
            Named<Button>(window, "ExpandButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(window.IsExpanded);
            var panel = Named<StackPanel>(window, "SessionsPanel");
            Assert.Equal(3, panel.Children.Count);
            AssertSessionSurfaces(window, 63);

            Named<Button>(window, "ExpandButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(window.IsExpanded);
            window.Apply(Sessions("one"));
            AssertSessionSurfaces(window, 63);
            var ghosts = Named<Grid>(window, "LayerTransitionOverlay").Children.OfType<Border>().ToArray();
            if (SystemParameters.ClientAreaAnimation) Assert.Equal(2, ghosts.Length);
            foreach (var ghost in ghosts) AssertSurface(ghost.Background, 63);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Slider_previews_immediately_and_flushes_only_the_latest_pending_setting() => Sta(() =>
    {
        var menu = new TrayMenuWindow(AppSettings.Default);
        try
        {
            var previews = new List<int>();
            var commits = new List<AppSettings>();
            menu.MaterialTransparencyPreviewRequested += (_, value) => previews.Add(value);
            menu.SettingsChangeRequested += (_, value) => commits.Add(value);
            var slider = Named<Slider>(menu, "MaterialTransparencySlider");
            Assert.Equal(0, slider.Minimum);
            Assert.Equal(100, slider.Maximum);
            Assert.Equal(1, slider.SmallChange);
            Assert.Equal(1, slider.TickFrequency);
            Assert.True(slider.IsSnapToTickEnabled);
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(slider)));

            slider.Value = 31;
            Assert.Equal(new[] { 31 }, previews);
            Assert.Empty(commits);
            AssertSurface(Named<Border>(menu, "GlassBorder").Background, 31);
            Assert.Equal("31%", Named<TextBlock>(menu, "MaterialTransparencyValue").Text);
            slider.Value = 74;
            slider.Value = 74;
            Assert.Equal(new[] { 31, 74 }, previews);
            Assert.Empty(commits);

            menu.FlushMaterialTransparencyChange();
            Assert.Equal(74, Assert.Single(commits).MaterialTransparencyPercent);
            menu.FlushMaterialTransparencyChange();
            Assert.Single(commits);
        }
        finally
        {
            menu.Close();
        }
    });

    [Fact]
    public void Saved_nondefault_settings_initialize_and_apply_without_emitting_change_events() => Sta(() =>
    {
        var menu = new TrayMenuWindow(AppSettings.Default with { MaterialTransparencyPercent = 42 });
        try
        {
            var slider = Named<Slider>(menu, "MaterialTransparencySlider");
            Assert.Equal(42, slider.Value);
            Assert.Equal("42%", Named<TextBlock>(menu, "MaterialTransparencyValue").Text);
            AssertSurface(Named<Border>(menu, "GlassBorder").Background, 42);
            Assert.Equal(!UsesSystemSurface, slider.IsEnabled);
            var previews = 0;
            var commits = 0;
            menu.MaterialTransparencyPreviewRequested += (_, _) => previews++;
            menu.SettingsChangeRequested += (_, _) => commits++;
            foreach (var percent in new[] { 42, 17, 17, 100, 0 })
            {
                menu.ApplySettings(AppSettings.Default with { MaterialTransparencyPercent = percent });
                Assert.Equal(percent, slider.Value);
                AssertSurface(Named<Border>(menu, "GlassBorder").Background, percent);
            }
            menu.FlushMaterialTransparencyChange();
            Assert.Equal(0, previews);
            Assert.Equal(0, commits);
            Assert.False(string.IsNullOrWhiteSpace(Named<TextBlock>(menu, "MaterialTransparencyHint").Text));
        }
        finally
        {
            menu.Close();
        }
    });

    [Fact]
    public void Other_menu_changes_flush_and_preserve_the_pending_slider_value() => Sta(() =>
    {
        var menu = new TrayMenuWindow(AppSettings.Default);
        try
        {
            var commits = new List<AppSettings>();
            menu.SettingsChangeRequested += (_, value) => commits.Add(value);
            Named<Slider>(menu, "MaterialTransparencySlider").Value = 58;
            Named<Button>(menu, "NotificationsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(2, commits.Count);
            Assert.All(commits, saved => Assert.Equal(58, saved.MaterialTransparencyPercent));
            Assert.True(commits[0].LowQuotaNotifications);
            Assert.False(commits[1].LowQuotaNotifications);
            menu.FlushMaterialTransparencyChange();
            Assert.Equal(2, commits.Count);
            Named<Button>(menu, "NotificationsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(commits[^1].LowQuotaNotifications);
            Named<Button>(menu, "NotificationsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(commits[^1].LowQuotaNotifications);
            Assert.Equal(4, commits.Count);
            Assert.All(commits, saved => Assert.Equal(58, saved.MaterialTransparencyPercent));
        }
        finally
        {
            menu.Close();
        }
    });

    [Fact]
    public void Slider_changes_are_automatically_coalesced_after_the_debounce() => Sta(() =>
    {
        var menu = new TrayMenuWindow(AppSettings.Default);
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        try
        {
            var commits = new List<AppSettings>();
            var frame = new DispatcherFrame();
            timeout.Tick += (_, _) => frame.Continue = false;
            menu.SettingsChangeRequested += (_, value) =>
            {
                commits.Add(value);
                frame.Continue = false;
            };
            var slider = Named<Slider>(menu, "MaterialTransparencySlider");
            slider.Value = 18;
            slider.Value = 69;
            Assert.Empty(commits);
            timeout.Start();
            Dispatcher.PushFrame(frame);
            Assert.Equal(69, Assert.Single(commits).MaterialTransparencyPercent);
            menu.FlushMaterialTransparencyChange();
            Assert.Single(commits);
        }
        finally
        {
            timeout.Stop();
            menu.Close();
        }
    });

    [Fact]
    public void Session_hover_and_pressed_states_do_not_restore_a_fixed_opaque_material()
    {
        var xaml = XDocument.Load(Path.Combine(RepositoryPaths.Root, "src", "CodexQuotaTaskbar.Host", "UI", "SessionStackWindow.xaml"));
        var hover = xaml.Descendants().Where(element => element.Name.LocalName == "Trigger" &&
            (string?)element.Attribute("Property") == "IsMouseOver").ToArray();
        Assert.Equal(2, hover.Length);
        foreach (var trigger in hover)
        {
            var background = Assert.Single(trigger.Elements(), setter => (string?)setter.Attribute("Property") == "Background");
            Assert.Equal("{Binding Background, RelativeSource={RelativeSource TemplatedParent}}", (string?)background.Attribute("Value"));
        }
    }

    [Fact]
    public void Oversized_menu_is_constrained_to_small_work_areas_without_invalid_clamp_bounds()
    {
        var workArea = new System.Drawing.Rectangle(-280, -360, 280, 360);
        var result = TrayMenuWindow.CalculateBounds(workArea,
            new System.Drawing.Point(-10, -10), new System.Drawing.Size(500, 900));
        Assert.Equal(new System.Drawing.Rectangle(-272, -352, 264, 344), result);
    }

    [Theory]
    [InlineData(108)]
    [InlineData(144)]
    public void Slider_value_and_hint_render_without_clipping_at_scaled_dpi(int dpi) => Sta(() =>
    {
        var menu = new TrayMenuWindow(AppSettings.Default with { MaterialTransparencyPercent = 38 });
        try
        {
            var content = Assert.IsAssignableFrom<FrameworkElement>(menu.Content);
            VisualTreeHelper.SetRootDpi(content, new DpiScale(dpi / 96d, dpi / 96d));
            content.Measure(new System.Windows.Size(menu.Width, double.PositiveInfinity));
            content.Arrange(new Rect(0, 0, menu.Width, content.DesiredSize.Height));
            content.UpdateLayout();
            var slider = Named<Slider>(menu, "MaterialTransparencySlider");
            Assert.True(slider.ActualWidth >= 100);
            Assert.True(slider.ActualHeight > 0);
            foreach (var name in new[] { "MaterialTransparencyLabel", "MaterialTransparencyValue", "MaterialTransparencyHint" })
            {
                var label = Named<TextBlock>(menu, name);
                var measured = new FormattedText(label.Text, CultureInfo.CurrentUICulture, label.FlowDirection,
                    new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch),
                    label.FontSize, label.Foreground, dpi / 96d);
                Assert.True(measured.WidthIncludingTrailingWhitespace <= label.ActualWidth + 0.5, name + " is clipped horizontally.");
                Assert.True(measured.Height <= label.ActualHeight + 1, name + " is clipped vertically.");
            }
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * dpi / 96d),
                (int)Math.Ceiling(content.ActualHeight * dpi / 96d), dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(content);
            Assert.True(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0);
            if (Environment.GetEnvironmentVariable("CODEX_QUOTA_UI_PROOFS") == "1")
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var directory = Path.Combine(RepositoryPaths.Root, "artifacts", "material-transparency-proof");
                Directory.CreateDirectory(directory);
                using var stream = File.Create(Path.Combine(directory, $"menu-transparency-{dpi}.png"));
                encoder.Save(stream);
            }
        }
        finally
        {
            menu.Close();
        }
    });

    private static bool UsesSystemSurface =>
        CapsuleThemePolicy.Resolve(SystemParameters.HighContrast, SystemParameters.IsGlassEnabled) == CapsuleSurfaceMode.OpaqueSystem;

    private static void AssertSurface(WpfBrush brush, int transparencyPercent) =>
        Assert.Equal(UsesSystemSurface ? 1 : OverlayGlassMaterial.MaterialOpacity(transparencyPercent), brush.Opacity, 10);

    private static void AssertSessionSurfaces(SessionStackWindow window, int transparencyPercent)
    {
        foreach (var button in Named<StackPanel>(window, "SessionsPanel").Children.OfType<Button>())
            AssertSurface(button.Background, transparencyPercent);
        AssertSurface(Named<Border>(window, "CollapsedMiddleLayer").Background, transparencyPercent);
        AssertSurface(Named<Border>(window, "CollapsedBackLayer").Background, transparencyPercent);
        AssertSurface(Named<Button>(window, "ExpandButton").Background, transparencyPercent);
        AssertSurface(Assert.IsAssignableFrom<WpfBrush>(window.Resources["SessionPressedSurface"]), transparencyPercent);
    }

    private static CodexSessionsSnapshot Sessions(params string[] ids)
    {
        var now = DateTimeOffset.UtcNow;
        return new(ids.Select(id => new CodexSessionSnapshot(id, "测试会话 " + id, CodexSessionState.Running, now)).ToArray(), now);
    }

    private static T Named<T>(Window window, string name) where T : DependencyObject =>
        Assert.IsType<T>(window.FindName(name));

    private static void Sta(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { test(); }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Material transparency verification did not finish in time.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
