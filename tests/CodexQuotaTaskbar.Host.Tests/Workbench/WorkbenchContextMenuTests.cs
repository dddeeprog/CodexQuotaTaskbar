using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexQuotaTaskbar.Host.Workbench;
using ContextMenu = System.Windows.Controls.ContextMenu;
using ListBox = System.Windows.Controls.ListBox;
using MenuItem = System.Windows.Controls.MenuItem;
using Size = System.Windows.Size;

namespace CodexQuotaTaskbar.Host.Tests.Workbench;

public sealed class WorkbenchContextMenuTests
{
    [Fact]
    public void Repeated_open_and_stale_close_preserve_exactly_one_interaction_lease() => Sta(() =>
    {
        var view = new WorkbenchWindow(new());
        try
        {
            var first = view.CreateContextMenu(); var second = view.CreateContextMenu();
            Open(first); Open(first); Assert.Equal(1, Private<int>(view, "ownedInteraction"));
            Open(second); Assert.Equal(1, Private<int>(view, "ownedInteraction"));
            Close(first); Assert.Equal(1, Private<int>(view, "ownedInteraction"));
            Close(second); Close(second); Assert.Equal(0, Private<int>(view, "ownedInteraction"));
            Open(first); view.SetViewMode("search"); Assert.Equal(0, Private<int>(view, "ownedInteraction"));
            Open(second); view.CloseForShutdown(); Assert.Equal(0, Private<int>(view, "ownedInteraction"));
            Assert.Equal(IntPtr.Zero, new WindowInteropHelper(view).Handle);
            Assert.False(first.StaysOpen);
            Assert.Same(DependencyProperty.UnsetValue, first.ReadLocalValue(ContextMenu.PlacementProperty));
        }
        finally { if (!Private<bool>(view, "closing")) view.CloseForShutdown(); }
    });

    [Fact]
    public void Explorer_context_selection_keeps_compact_preview_and_updates_applicable_actions() => Sta(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "synthetic-menu-no-disk-io");
        var view = new WorkbenchWindow(new());
        var coordinator = new WorkbenchCoordinator(new(Directory: root), _ => { }, enableShortcut: false);
        using var browser = new WorkbenchBrowser(view, coordinator, new(Directory: root));
        try
        {
            var file = new WorkspaceEntry(Path.Combine(root, "中文 [literal] #.txt"), "中文 [literal] #.txt", false, false);
            browser.RenderDirectory([file, new(Path.Combine(root, "文件夹"), "文件夹", true, false)], 0, default);
            var tree = Assert.IsType<TreeView>(Assert.Single(view.SidebarHost.Children));
            var item = Assert.IsType<TreeViewItem>(tree.Items[0]);
            browser.SelectContextTarget(tree, item);
            Assert.Equal(file, browser.Selection); Assert.False(view.IsPreviewExpanded);
            Assert.Empty(view.PreviewHost.Children); Assert.Equal(400, view.Width);
            var menu = tree.ContextMenu; Open(menu);
            Assert.Equal(new[] { "用默认程序打开", "在资源管理器中定位", "复制路径", "新建文件…", "新建文件夹…", "重命名…", "移到回收站…" }, Items(menu).Select(i => i.Header));
            Assert.All(Items(menu), i => Assert.True(i.IsEnabled));
            Render(menu, "files-menu");
            Close(menu); browser.SelectContextTarget(tree, null); Open(menu);
            Assert.False(Item(menu, "复制路径").IsEnabled); Assert.False(Item(menu, "重命名…").IsEnabled);
            Assert.True(Item(menu, "新建文件…").IsEnabled);
            Close(menu); browser.SelectContextTarget(tree, item); Open(menu);
            Assert.Equal(file, browser.Selection); Assert.True(Item(menu, "复制路径").IsEnabled);
            var pending = new TaskCompletionSource(); coordinator.PendingWrite = pending.Task;
            try
            {
                Close(menu); Open(menu);
                Assert.False(Item(menu, "新建文件…").IsEnabled); Assert.False(Item(menu, "移到回收站…").IsEnabled);
                Assert.True(Item(menu, "用默认程序打开").IsEnabled);
            }
            finally { pending.SetResult(); }
            view.ApplyAppearance(true, false); Render(menu, "files-system-menu");
            var parent = Assert.IsType<TreeViewItem>(tree.Items[1]); parent.Items.Clear();
            var nested = new WorkspaceEntry(Path.Combine(root, "文件夹", "nested.txt"), "nested.txt", false, false);
            var header = new TextBlock { Text = nested.Name };
            var child = new TreeViewItem { Header = header, Tag = nested };
            parent.Items.Add(child); parent.IsExpanded = true; Layout(view);
            Assert.Same(child, ItemsControl.ContainerFromElement(null, header));
            browser.SelectContextTarget(tree, child); Assert.Equal(nested, browser.Selection);
            Assert.False(view.IsPreviewExpanded);
            Assert.False(view.IsVisible); Assert.Equal(IntPtr.Zero, new WindowInteropHelper(view).Handle);
        }
        finally { view.CloseForShutdown(); coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    });

    [Fact]
    public void Git_context_selection_preserves_multiple_selection_and_never_starts_a_diff_on_right_click() => Sta(() =>
    {
        WithGit((view, browser, git, repository, coordinator) =>
        {
            git.Render(repository);
            Layout(view);
            var list = Private<ListBox>(git, "list");
            list.SelectedItems.Add(repository.Changes[0]); list.SelectedItems.Add(repository.Changes[1]);
            var selected = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0));
            git.SelectContextTarget(list, selected);
            Assert.Equal(2, list.SelectedItems.Count); Assert.False(view.IsPreviewExpanded);
            Assert.Empty(view.PreviewHost.Children);
            Open(list.ContextMenu);
            Assert.False(Item(list.ContextMenu, "查看差异").IsEnabled);
            Assert.True(Item(list.ContextMenu, "暂存所选").IsEnabled);
            Assert.True(Item(list.ContextMenu, "取消暂存").IsEnabled);
            Render(list.ContextMenu, "git-mixed-menu");
            var other = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(2));
            git.SelectContextTarget(list, other); Assert.Single(list.SelectedItems);
            Open(list.ContextMenu);
            Assert.True(Item(list.ContextMenu, "查看差异").IsEnabled);
            Assert.True(Item(list.ContextMenu, "暂存所选").IsEnabled);
            Assert.False(Item(list.ContextMenu, "取消暂存").IsEnabled);
            Render(list.ContextMenu, "git-untracked-menu");
            git.SelectContextTarget(list, null); Open(list.ContextMenu);
            Assert.All(Items(list.ContextMenu), item => Assert.False(item.IsEnabled));
            Assert.True(coordinator.PendingWrite.IsCompletedSuccessfully);
        });
    });

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Busy_or_conflicted_repository_keeps_context_menu_read_only(bool operation, bool conflict) => Sta(() =>
    {
        WithGit((view, browser, git, repository, coordinator) =>
        {
            var state = repository with { OperationInProgress = operation,
                Changes = conflict ? [repository.Changes[1], new(GitArea.Conflict, "conflict.txt", null, "UU")] : repository.Changes };
            git.Render(state); Layout(view);
            var list = Private<ListBox>(git, "list"); list.SelectedIndex = 0; Open(list.ContextMenu);
            Assert.True(Item(list.ContextMenu, "查看差异").IsEnabled);
            Assert.False(Item(list.ContextMenu, "暂存所选").IsEnabled);
            Assert.False(Item(list.ContextMenu, "取消暂存").IsEnabled);
            Render(list.ContextMenu, operation ? "git-busy-menu" : "git-conflict-menu");
        });
    });

    private static void WithGit(Action<WorkbenchWindow, WorkbenchBrowser, WorkbenchGitPanel, GitWorkspace, WorkbenchCoordinator> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "synthetic-menu-no-disk-io");
        var view = new WorkbenchWindow(new());
        var coordinator = new WorkbenchCoordinator(new(Directory: root), _ => { }, enableShortcut: false);
        using var browser = new WorkbenchBrowser(view, coordinator, new(Directory: root));
        using var git = new WorkbenchGitPanel(view, browser, coordinator, new());
        var repository = new GitWorkspace(root, Path.Combine(root, ".git"), "synthetic-head", "main", "synthetic-version", false,
            [new(GitArea.Staged, "中文 [literal] #.txt", null, "M"), new(GitArea.Worktree, "中文 [literal] #.txt", null, "M"), new(GitArea.Untracked, "new.txt", null, "?")]);
        try { test(view, browser, git, repository, coordinator); Assert.False(view.IsVisible); Assert.Equal(IntPtr.Zero, new WindowInteropHelper(view).Handle); }
        finally { view.CloseForShutdown(); coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }
    private static void Layout(WorkbenchWindow view)
    {
        var root = (FrameworkElement)view.Content;
        root.Measure(new Size(view.Width, view.Height)); root.Arrange(new Rect(0, 0, view.Width, view.Height)); root.UpdateLayout();
    }
    private static void Render(ContextMenu menu, string name)
    {
        if (Environment.GetEnvironmentVariable("CQT_MENU_PREVIEW") is not { Length: > 0 } directory) return;
        menu.ApplyTemplate(); menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        menu.Arrange(new Rect(new System.Windows.Point(), menu.DesiredSize)); menu.UpdateLayout();
        Assert.InRange(menu.ActualWidth, 100, 400); Assert.InRange(menu.ActualHeight, 80, 400);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth * 2), (int)Math.Ceiling(menu.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(menu); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(output);
    }
    private static T Private<T>(object owner, string field) => (T)owner.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
    private static IEnumerable<MenuItem> Items(ContextMenu menu) => menu.Items.OfType<MenuItem>();
    private static MenuItem Item(ContextMenu menu, string header) => Items(menu).Single(i => Equals(i.Header, header));
    private static void Open(ContextMenu menu) => menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
    private static void Close(ContextMenu menu) => menu.RaiseEvent(new RoutedEventArgs(ContextMenu.ClosedEvent));
    private static void Sta(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { test(); } catch (Exception e) { failure = e; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
