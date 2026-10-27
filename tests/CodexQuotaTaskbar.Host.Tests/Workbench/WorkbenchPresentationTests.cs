using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexQuotaTaskbar.Host.Workbench;

namespace CodexQuotaTaskbar.Host.Tests.Workbench;

public sealed class WorkbenchPresentationTests
{
    [Fact]
    public async Task Actual_presenters_fit_normal_compact_and_scaled_client_areas_without_creating_windows()
    {
        await using var fixture = new GitTestWorkspace(); await fixture.InitializeAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "sample.txt"), "updated value\nsecond line\n");
        var git = new WorkspaceGit(); var state = await git.ReadAsync(fixture.Root, default);
        var change = state.Changes.Single(); var diff = await git.DiffAsync(state, change, default);
        var entries = await new WorkspaceFiles().ListAsync(fixture.Root, fixture.Root, default);
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
                root.SetValue(TextElement.ForegroundProperty, view.Foreground); root.Background = (System.Windows.Media.Brush)view.Resources["WorkbenchBackground"];
                view.DirectoryLabel.Text = "本机测试项目 · 所有内容为合成验证数据";
                view.RepositoryLabel.Text = "main · 合成 Git 仓库";
                var allocationBefore = GC.GetAllocatedBytesForCurrentThread();
                browser.RenderDirectory(entries, 0, default);
                Render(root, "files-1100", 1100, 720, 1);
                panel.Render(state); panel.RenderDiff(diff, change);
                foreach (var (width, height, scale) in new[] { (1100, 720, 1d), (760, 480, 1d), (640, 480, 1d), (1100, 720, 1.5d) })
                    Render(root, $"git-{width}-{scale:0.0}", width, height, scale);
                Assert.Equal(0, new System.Windows.Interop.WindowInteropHelper(view).Handle);
                var proof = Environment.GetEnvironmentVariable("DIANDAO_WORKBENCH_PROOFS");
                if (proof is not null) File.WriteAllText(Path.Combine(proof, "presentation-allocation.txt"),
                    $"Managed allocations constructing/rendering actual client presenters: {GC.GetAllocatedBytesForCurrentThread() - allocationBefore} bytes.\nOffscreen rendering only; not process RAM or native/DPI field proof.");
                view.PreviewHost.Children.Add(new TextBlock { Text = "old workspace content" });
                browser.SelectRootAsync(fixture.Root).GetAwaiter().GetResult(); Assert.Empty(view.PreviewHost.Children);
            }
            catch (Exception error) { failure = error; }
            finally { view.CloseForShutdown(); coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult(); System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void Render(Grid root, string name, int width, int height, double scale)
    {
        VisualTreeHelper.SetRootDpi(root, new DpiScale(scale, scale));
        root.Measure(new System.Windows.Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        Assert.InRange(root.ActualWidth, width - 1, width + 1);
        Assert.All(Descendants<System.Windows.Controls.Button>(root).Where(b => b.ActualWidth > 0), button =>
        { Assert.True(button.ActualHeight >= 24); Assert.True(button.ActualWidth >= 30); });
        Assert.All(Descendants<System.Windows.Controls.ListBox>(root).Where(list => list.ActualWidth > 0), list => Assert.True(list.ActualHeight >= 60, "Change selection must retain a usable viewport."));
        var directory = Environment.GetEnvironmentVariable("DIANDAO_WORKBENCH_PROOFS"); if (directory is null) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(file);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        { var child = VisualTreeHelper.GetChild(root, index); if (child is T match) yield return match; foreach (var nested in Descendants<T>(child)) yield return nested; }
    }
}
