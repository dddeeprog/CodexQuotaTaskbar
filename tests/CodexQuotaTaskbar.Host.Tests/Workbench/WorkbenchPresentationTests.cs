using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexQuotaTaskbar.Host.Workbench;
using CodexQuotaTaskbar.Host.UI;
using CodexQuotaTaskbar.Host.Settings;

namespace CodexQuotaTaskbar.Host.Tests.Workbench;

public sealed class WorkbenchPresentationTests
{
    [Fact]
    public async Task Actual_presenters_fit_normal_compact_and_scaled_client_areas_without_creating_windows()
    {
        await using var fixture = new GitTestWorkspace(); await fixture.InitializeAsync();
        var initial = "public sealed class Workspace\n{\n    public string Name => \"示例项目\";\n    public bool Pinned => false;\n}\n";
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "sample.txt"), initial);
        await GitTestWorkspace.RunAsync(fixture.Root, "add", "--", "sample.txt");
        await GitTestWorkspace.RunAsync(fixture.Root, "commit", "-m", "Synthetic presenter fixture");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "sample.txt"), initial.Replace("false", "true"));
        foreach (var directory in new[] { "assets", "docs", "src", "tests" }) Directory.CreateDirectory(Path.Combine(fixture.Root, directory));
        foreach (var name in new[] { ".gitignore", "DESIGN.md", "PRODUCT.md" }) await File.WriteAllTextAsync(Path.Combine(fixture.Root, name), "Synthetic fixture only.");
        var readme = Path.Combine(fixture.Root, "README.md");
        await File.WriteAllTextAsync(readme, "# 示例项目\n\n这里展示的是合成项目文件，用于核对工作台界面。\n\n## 目录\n\nassets/    图片与图标\ndocs/      项目说明\nsrc/       应用源码\ntests/     自动验证\n\n## 开始使用\n\n从左侧选择文件以查看内容。\n在 Git 中核对改动和暂存范围。\n\n本文仅作真实控件呈现，不来自用户的项目或消息。\n");
        var git = new WorkspaceGit(); var state = await git.ReadAsync(fixture.Root, default);
        var change = state.Changes.Single(c => c.Path == "sample.txt"); var diff = await git.DiffAsync(state, change, default);
        var entries = await new WorkspaceFiles().ListAsync(fixture.Root, fixture.Root, default);
        var preview = await new WorkspaceFiles().PreviewAsync(fixture.Root, readme, default);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var view = new WorkbenchWindow(new());
            var coordinator = new WorkbenchCoordinator(new(Directory: fixture.Root), _ => { }, enableShortcut: false);
            using var browser = new WorkbenchBrowser(view, coordinator, new(Directory: fixture.Root));
            using var panel = new WorkbenchGitPanel(view, browser, coordinator, new());
            try
            {
                var root = (Grid)view.Content; view.Content = null;
                root.Resources.MergedDictionaries.Add(view.Resources);
                root.SetValue(TextElement.FontFamilyProperty, view.FontFamily); root.SetValue(TextElement.FontSizeProperty, 13d);
                root.SetResourceReference(TextElement.ForegroundProperty, "WorkbenchText");
                view.DirectoryLabel.Text = "示例项目 · 合成验证数据";
                view.RepositoryLabel.Text = "main · C:\\示例项目（合成路径）";
                var allocationBefore = GC.GetAllocatedBytesForCurrentThread();
                browser.RenderDirectory(entries, 0, default);
                Assert.False(view.IsPreviewExpanded);
                Assert.Equal(400, view.Width); Assert.InRange(view.Height, 200, 440);
                Assert.Equal(Visibility.Collapsed, view.PreviewPane.Visibility);
                RenderCompact(root, view, "initial-compact", 400);
                foreach (var width in new[] { 360, 420 }) RenderCompact(root, view, $"files-compact-{width}", width);
                RenderCompact(root, view, "files-compact-400", 400);
                var tree = Assert.IsType<TreeView>(Assert.Single(view.SidebarHost.Children));
                tree.Items.Cast<TreeViewItem>().Single(item => item.Tag is WorkspaceEntry entry && entry.Path == readme).IsSelected = true;
                browser.RenderPreview(preview);
                Render(root, "files-1100", 1100, 720, 1);
                Render(root, "files-640", 640, 480, 1);
                panel.Render(state);
                RenderCompact(root, view, "git-collapsed", 400);
                Assert.DoesNotContain(Descendants<System.Windows.Controls.Button>(root), button => button.Visibility == Visibility.Visible && button.Content is string label && label.StartsWith("提交…"));
                panel.Render(state with { Changes = [] });
                RenderCompact(root, view, "git-empty", 400);
                Assert.InRange(view.Height, 200, 300);
                Assert.Empty(Descendants<System.Windows.Controls.ListBox>(view.SidebarHost));
                var longPath = "C:\\合成示例项目\\很长的工作区名称和嵌套目录\\用于验证文字省略和完整路径提示";
                view.DirectoryLabel.Text = longPath; view.DirectoryLabel.ToolTip = longPath;
                view.RepositoryLabel.Text = "feature/合成分支 · " + longPath; view.RepositoryLabel.ToolTip = longPath;
                var many = state with { Changes = Enumerable.Range(1, 24).Select(index => new GitChange(index == 1 ? GitArea.Staged : GitArea.Worktree, $"src/合成目录/long-file-name-{index:00}.cs", null, "M")).ToArray() };
                panel.Render(many);
                RenderCompact(root, view, "git-many-long-360", 360);
                RenderCompact(root, view, "git-many-long-420", 420);
                RenderCompact(root, view, "git-many-long-400-150", 400, 1.5);
                var changeList = Assert.Single(Descendants<System.Windows.Controls.ListBox>(view.SidebarHost));
                Assert.Contains(Descendants<ScrollViewer>(changeList), scroll => scroll.ScrollableHeight > 0);
                Assert.Equal(longPath, view.DirectoryLabel.ToolTip);
                view.DirectoryLabel.Text = "示例项目 · 合成验证数据";
                view.RepositoryLabel.Text = "main · C:\\示例项目（合成路径）";
                panel.Render(state); RenderCompact(root, view, "git-collapsed", 400);
                panel.RenderDiff(diff, change);
                foreach (var (width, height, scale) in new[] { (1100, 720, 1d), (760, 480, 1d), (640, 480, 1d), (1100, 720, 1.5d) })
                {
                    Render(root, $"git-{width}-{scale:0.0}", width, height, scale);
                    if (width == 640)
                    {
                        var viewport = Descendants<ScrollViewer>(root).Single(scroll => scroll.Name == "GitDiffViewport");
                        Assert.True(viewport.ScrollableWidth > 100);
                        viewport.ScrollToRightEnd(); Render(root, "git-640-right", width, height, scale);
                        Assert.True(viewport.HorizontalOffset > 100);
                        viewport.ScrollToLeftEnd();
                    }
                }
                view.CollapsePreviewButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                RenderCompact(root, view, "git-collapsed-again", 400);
                view.ReopenPreviewButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.True(view.IsPreviewExpanded); Assert.Equal(1100, view.Width);
                Render(root, "git-reopened", 1100, 720, 1);
                Assert.Same(OverlayGlassMaterial.Surface, view.GlassBorder.Background);
                Assert.Equal(new CornerRadius(16), view.GlassBorder.CornerRadius);
                Assert.Equal(OverlayTypography.Interface.Source, view.FilesButton.FontFamily.Source);
                Assert.Equal(OverlayTypography.Interface.Source, view.SearchButton.FontFamily.Source);
                Assert.Equal(FontWeights.SemiBold, view.SearchButton.FontWeight);
                var expandedWidth = view.Width; var expandedHeight = view.Height;
                var alternate = change with { Path = "second-synthetic-file.txt" };
                panel.RenderDiff(new GitDiff([new("1", "1", "before", "after", "replace")], null), alternate);
                var alternatePane = Assert.IsType<DockPanel>(Assert.Single(view.PreviewHost.Children));
                Assert.StartsWith(alternate.Path, alternatePane.Children.OfType<TextBlock>().Single().Text);
                Assert.Equal(expandedWidth, view.Width); Assert.Equal(expandedHeight, view.Height);
                panel.RenderDiff(diff, change);
                browser.ShowSearch(); root.UpdateLayout();
                var checkbox = Assert.Single(Descendants<System.Windows.Controls.CheckBox>(root));
                var oldCell = Descendants<Border>(root).Single(border => border.Name == "OldCell" && Equals(border.Tag, "replace"));
                var newCell = Descendants<Border>(root).Single(border => border.Name == "NewCell" && Equals(border.Tag, "replace"));
                var diffRow = Descendants<ListBoxItem>(root).Single(item => item.Content is GitDiffRow row && row.Kind == "replace");
                foreach (var (highContrast, glass, name) in new[] { (false, false, "theme-no-glass-1100"), (true, true, "theme-high-contrast-1100") })
                {
                    view.ApplyAppearance(highContrast, glass); root.UpdateLayout(); diffRow.IsSelected = true;
                    Assert.Same(System.Windows.SystemColors.WindowBrush, view.GlassBorder.Background);
                    Assert.Same(System.Windows.SystemColors.WindowTextBrush, checkbox.Foreground);
                    Assert.Same(System.Windows.SystemColors.WindowTextBrush, view.DirectoryLabel.Foreground);
                    Assert.Same(System.Windows.SystemColors.WindowBrush, oldCell.Background);
                    Assert.Same(System.Windows.SystemColors.WindowBrush, newCell.Background);
                    Assert.Same(System.Windows.SystemColors.WindowTextBrush, Assert.Single(Descendants<TextBlock>(oldCell)).Foreground);
                    Assert.Same(System.Windows.SystemColors.HighlightTextBrush, view.Resources["WorkbenchSelectedText"]);
                    Render(root, name, 1100, 720, 1);
                }
                view.ApplyAppearance(false, true);
                Assert.Same(view.Resources["WorkbenchText"], checkbox.Foreground);
                Assert.NotSame(System.Windows.SystemColors.WindowBrush, oldCell.Background);
                panel.Render(state); panel.RenderDiff(diff, change);
                var dialog = new Window { Title = "核对已暂存内容 · 合成验证", Width = 520, Height = 360 };
                var message = new System.Windows.Controls.TextBox { Text = "统一工作台视觉", Height = 80, Margin = new Thickness(18) };
                view.StyleDialog(dialog, message);
                var dialogRoot = (Grid)dialog.Content; dialog.Content = null; dialogRoot.Resources.MergedDictionaries.Add(dialog.Resources);
                dialogRoot.SetValue(TextElement.ForegroundProperty, view.Foreground); dialogRoot.SetValue(TextElement.FontFamilyProperty, view.FontFamily);
                Render(dialogRoot, "dialog-520", 520, 360, 1); dialog.Close();
                RenderIncumbentSurfaces();
                Assert.Equal(0, new System.Windows.Interop.WindowInteropHelper(view).Handle);
                var proof = Environment.GetEnvironmentVariable("DIANDAO_WORKBENCH_PROOFS");
                if (proof is not null) File.WriteAllText(Path.Combine(proof, "presentation-allocation.txt"),
                    $"Managed allocations constructing/rendering actual client presenters: {GC.GetAllocatedBytesForCurrentThread() - allocationBefore} bytes.\nOffscreen rendering only; not process RAM or native/DPI field proof.");
                if (proof is not null)
                {
                    string Hash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
                    var source = CodexQuotaTaskbar.Host.Tests.TestSupport.RepositoryPaths.Root;
                    var names = new[] { "WorkbenchWindow.xaml", "WorkbenchTheme.xaml", "WorkbenchWindow.xaml.cs", "WorkbenchWindow.Appearance.cs", "WorkbenchWindow.Preview.cs", "WorkbenchSettings.cs", "WorkbenchCoordinator.cs", "WorkbenchBrowser.cs", "WorkbenchBrowser.Mutations.cs", "WorkbenchGitPanel.cs", "WorkbenchGitPanel.Mutations.cs", "WorkspaceGit.cs" };
                    var hashes = names.ToDictionary(name => name, name => Hash(System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(Path.Combine(source, "src", "CodexQuotaTaskbar.Host", "Workbench", name)).Replace("\r\n", "\n"))));
                    var assembly = typeof(WorkbenchWindow).Assembly.Location;
                    File.WriteAllText(Path.Combine(proof, "presenter-proof.json"), System.Text.Json.JsonSerializer.Serialize(new
                    {
                        sourceRoot = source, assembly, assemblySha256 = Hash(File.ReadAllBytes(assembly)), normalizedSourceSha256 = hashes,
                        screenshotsSha256 = Directory.GetFiles(proof, "*.png").ToDictionary(path => Path.GetFileName(path)!, path => Hash(File.ReadAllBytes(path))),
                        syntheticOnly = true, nativeWindowsShown = false, nativeInputInjected = false,
                    }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                }
                view.PreviewHost.Children.Add(new TextBlock { Text = "old workspace content" });
                browser.SelectRootAsync(fixture.Root).GetAwaiter().GetResult(); Assert.Empty(view.PreviewHost.Children);
                // A hidden presenter must not start file/Git reads or install watchers, even after a queued refresh.
                var status = view.StatusLabel.Text;
                browser.ShowPreviewAsync(Path.Combine(fixture.Root, "..", "outside.txt")).GetAwaiter().GetResult();
                browser.RefreshAsync().GetAwaiter().GetResult(); panel.RefreshAsync().GetAwaiter().GetResult();
                Assert.Equal(status, view.StatusLabel.Text);
                Assert.Null(typeof(WorkbenchBrowser).GetField("watcher", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(browser));
                Assert.Null(typeof(WorkbenchGitPanel).GetField("indexWatcher", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(panel));
                Assert.Null(typeof(WorkbenchGitPanel).GetField("snapshot", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(panel));
            }
            catch (Exception error) { failure = error; }
            finally { view.CloseForShutdown(); coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult(); System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void Icon_navigation_keeps_slots_names_routing_and_distinct_visual_states_without_native_input()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var view = new WorkbenchWindow(new()) { CaptureProbe = () => false };
            try
            {
                var root = (Grid)view.Content; view.Content = null;
                root.Resources.MergedDictionaries.Add(view.Resources);
                root.SetValue(TextElement.FontFamilyProperty, view.FontFamily);
                root.SetValue(TextElement.FontSizeProperty, view.FontSize);
                root.SetResourceReference(TextElement.ForegroundProperty, "WorkbenchText");
                var actions = new[] { view.PinButton, view.HotkeyButton, view.RefreshButton, view.MinimizeButton, view.CloseWorkbenchButton };
                var modes = new[] { view.FilesButton, view.SearchButton, view.GitButton };
                RenderCompact(root, view, "icons-default", 400);
                foreach (var control in actions.Cast<Control>().Concat(modes))
                {
                    Assert.False(string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(control)));
                    Assert.NotNull(control.ToolTip);
                    var icon = Assert.Single(Descendants<System.Windows.Shapes.Path>(control));
                    Assert.Equal(16, icon.Width); Assert.Equal(1.2, icon.StrokeThickness);
                    Assert.Same(control.Foreground, icon.Stroke);
                }
                // The three old text slots still determine header width, so the title and actions do not shift.
                foreach (var action in actions.Take(3))
                {
                    var size = action.RenderSize; var template = action.ContentTemplate; var style = action.Style;
                    action.ContentTemplate = null; action.Style = (Style)view.Resources["WorkbenchHeaderButton"];
                    root.UpdateLayout(); Assert.Equal(size, action.RenderSize);
                    action.ContentTemplate = template; action.Style = style; root.UpdateLayout();
                }
                var circle = (Border)view.SearchButton.Template.FindName("Chrome", view.SearchButton);
                var defaultBrush = circle.Background;
                var defaultForeground = view.SearchButton.Foreground;
                // Force only the offscreen dependency-property state; no mouse, HWND, focus, or input is created.
                var hoverKey = (DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
                view.SearchButton.SetValue(hoverKey, true); root.UpdateLayout();
                Assert.NotSame(defaultBrush, circle.Background);
                Assert.Same(view.Resources["WorkbenchHover"], circle.Background);
                RenderCompact(root, view, "icons-hover", 400);
                view.SearchButton.SetValue(hoverKey, false);
                var routes = new List<string>(); var refreshes = 0;
                view.ViewRequested += routes.Add; view.RefreshRequested += () => refreshes++;
                foreach (var mode in modes)
                {
                    mode.IsChecked = true;
                    mode.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                }
                Assert.Equal(new[] { "files", "search", "git" }, routes);
                view.RefreshButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Equal(1, refreshes);
                view.SearchButton.IsChecked = true; root.UpdateLayout();
                Assert.False(view.FilesButton.IsChecked); Assert.False(view.GitButton.IsChecked);
                Assert.Same(view.Resources["WorkbenchActive"], circle.Background);
                Assert.Same(view.Resources["WorkbenchSelectedText"], circle.BorderBrush);
                Assert.NotSame(defaultForeground, view.SearchButton.Foreground);
                Assert.Equal(new CornerRadius(14), circle.CornerRadius);
                Assert.Equal(28, circle.ActualWidth); Assert.Equal(circle.ActualWidth, circle.ActualHeight);
                view.PinButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.True(view.Pinned); Assert.True(view.Topmost); root.UpdateLayout();
                var pinCircle = (Border)view.PinButton.Template.FindName("Circle", view.PinButton);
                Assert.Same(view.Resources["WorkbenchActive"], pinCircle.Background);
                Assert.Equal("已置顶", System.Windows.Automation.AutomationProperties.GetItemStatus(view.PinButton));
                RenderCompact(root, view, "icons-selected", 400);
                foreach (var width in new[] { 360, 420 }) RenderCompact(root, view, $"icons-selected-{width}", width);
                RenderCompact(root, view, "icons-selected-150", 400, 1.5);
                view.ApplyAppearance(true, true); root.UpdateLayout();
                Assert.Same(System.Windows.SystemColors.HighlightBrush, circle.Background);
                Assert.Same(System.Windows.SystemColors.HighlightTextBrush, Assert.Single(Descendants<System.Windows.Shapes.Path>(view.SearchButton)).Stroke);
                RenderCompact(root, view, "icons-high-contrast", 400);
                view.ApplyAppearance(false, true);
                view.SearchButton.IsEnabled = false; Assert.Equal(0.42, view.SearchButton.Opacity);
                Assert.Equal(0, new System.Windows.Interop.WindowInteropHelper(view).Handle);
            }
            catch (Exception error) { failure = error; }
            finally { view.CloseForShutdown(); System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void RenderCompact(Grid root, WorkbenchWindow view, string name, int width, double scale = 1)
    {
        view.Width = width; view.FitCompactContent();
        Assert.False(view.IsPreviewExpanded); Assert.Equal(width, view.Width);
        Assert.InRange(view.Height, 200, 440); Assert.Equal(13, view.FontSize);
        Render(root, name, width, (int)view.Height, scale);
        Assert.Equal(Visibility.Collapsed, view.PreviewPane.Visibility);
        Assert.Equal(0, view.PreviewColumn.Width.Value);
        foreach (var button in Descendants<System.Windows.Controls.Button>(root).Where(button => button.ActualWidth > 0 && VisibleWithin(button, root)))
        {
            var bounds = button.TransformToAncestor(root).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
            Assert.InRange(bounds.Left, -1, root.ActualWidth); Assert.True(bounds.Right <= root.ActualWidth + 1, $"Clipped action: {button.Content}");
            Assert.InRange(bounds.Top, -1, root.ActualHeight); Assert.True(bounds.Bottom <= root.ActualHeight + 1, $"Clipped action: {button.Content}");
        }
    }
    private static bool VisibleWithin(DependencyObject item, DependencyObject root)
    {
        while (item != root)
        {
            if (item is FrameworkElement element && element.Visibility != Visibility.Visible) return false;
            item = VisualTreeHelper.GetParent(item); if (item is null) return false;
        }
        return true;
    }

    private static void RenderIncumbentSurfaces()
    {
        var menu = new TrayMenuWindow(AppSettings.Default, activateOnShow: false);
        var settings = new TrayMenuWindow(AppSettings.Default, activateOnShow: false);
        var details = new QuotaPopoverWindow();
        try
        {
            settings.OpenSection(MenuSection.Appearance);
            var comparison = new Grid(); comparison.ColumnDefinitions.Add(new() { Width = new GridLength(272) }); comparison.ColumnDefinitions.Add(new() { Width = new GridLength(272) }); comparison.ColumnDefinitions.Add(new());
            var index = 0;
            foreach (Window window in new Window[] { menu, settings, details })
            {
                var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content); window.Content = null;
                content.Resources.MergedDictionaries.Add(window.Resources);
                content.SetValue(TextElement.FontFamilyProperty, OverlayTypography.Interface);
                content.SetValue(TextElement.FontSizeProperty, 13d); content.SetValue(TextElement.FontWeightProperty, FontWeights.SemiBold);
                content.VerticalAlignment = VerticalAlignment.Top; Grid.SetColumn(content, index++); comparison.Children.Add(content);
                Assert.Equal(0, new System.Windows.Interop.WindowInteropHelper(window).Handle);
            }
            Render(comparison, "reference-point-island", 904, 570, 1);
        }
        finally { menu.Close(); settings.Close(); details.Close(); }
    }

    private static void Render(Grid root, string name, int width, int height, double scale)
    {
        var frame = root.Parent as Grid;
        if (frame is null) { frame = new Grid(); frame.Children.Add(root); }
        VisualTreeHelper.SetRootDpi(frame, new DpiScale(scale, scale));
        frame.Measure(new System.Windows.Size(width, height)); frame.Arrange(new Rect(0, 0, width, height)); frame.UpdateLayout();
        frame.InvalidateVisual(); root.InvalidateVisual(); frame.UpdateLayout();
        Assert.InRange(frame.ActualWidth, width - 1, width + 1);
        Assert.InRange(root.ActualWidth, width - root.Margin.Left - root.Margin.Right - 1, width);
        if (name != "reference-point-island")
            Assert.All(Descendants<System.Windows.Controls.Button>(root).Where(b => b.ActualWidth > 0), button =>
            { Assert.True(button.ActualHeight >= 24); Assert.True(button.ActualWidth >= 30); });
        Assert.All(Descendants<System.Windows.Controls.ListBox>(root).Where(list => list.ActualWidth > 0), list => Assert.True(list.ActualHeight >= 60, "Change selection must retain a usable viewport."));
        if (name.StartsWith("git-"))
        {
            foreach (var rows in Descendants<System.Windows.Controls.ListBox>(root).Where(list => list.Name == "GitDiffRows")) Assert.True(rows.ActualWidth >= 680);
            Assert.All(Descendants<TextBlock>(root).Where(text => text.Text.Contains("public bool Pinned")), text =>
            { Assert.Equal(TextWrapping.Wrap, text.TextWrapping); Assert.Equal(TextTrimming.None, text.TextTrimming); });
        }
        var directory = Environment.GetEnvironmentVariable("DIANDAO_WORKBENCH_PROOFS"); if (directory is null) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(frame); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        if (name.StartsWith("theme-"))
        {
            var pixels = new byte[600 * 220 * 4]; bitmap.CopyPixels(new Int32Rect(400, 180, 600, 220), pixels, 600 * 4, 0);
            Assert.True(Enumerable.Range(0, pixels.Length / 4).Count(i => pixels[i * 4 + 3] > 128 && pixels[i * 4] < 128) > 20, "The captured fallback must include rendered diff text, not an empty visual.");
        }
        using var file = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(file);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        { var child = VisualTreeHelper.GetChild(root, index); if (child is T match) yield return match; foreach (var nested in Descendants<T>(child)) yield return nested; }
    }
}
