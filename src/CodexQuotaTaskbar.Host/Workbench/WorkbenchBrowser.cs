using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FontFamily = System.Windows.Media.FontFamily;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using TreeView = System.Windows.Controls.TreeView;
using ListBox = System.Windows.Controls.ListBox;

namespace CodexQuotaTaskbar.Host.Workbench;

internal sealed partial class WorkbenchBrowser : IDisposable
{
    private readonly WorkbenchWindow view;
    private readonly WorkbenchCoordinator coordinator;
    private readonly WorkspaceFiles files = new();
    private CancellationTokenSource reads = new();
    private FileSystemWatcher? watcher;
    private readonly DispatcherTimer debounce;
    private long generation;
    private bool disposed;
    private string mode = "files";
    private string? selectedFile;
    private string searchQuery = "";
    private bool searchContent;
    private FrameworkElement? searchPanel;
    private CancellationTokenSource? searchReads;
    private long searchGeneration;
    internal string? Root { get; private set; }
    internal event Action? WorkspaceChanged;
    internal event Action? RefreshGitRequested;
    internal event Action? ChooseGitRequested;
    internal Action? ShowGit { get; set; }
    internal bool IsWriting => !coordinator.PendingWrite.IsCompleted;
    internal WorkspaceEntry? Selection { get; private set; }

    internal WorkbenchBrowser(WorkbenchWindow view, WorkbenchCoordinator coordinator, WorkbenchSettings settings)
    {
        this.view = view; this.coordinator = coordinator;
        Root = settings.Directory;
        debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(450) };
        debounce.Tick += (_, _) => { debounce.Stop(); if (view.IsVisible) { _ = RefreshAsync(); RefreshGitRequested?.Invoke(); } };
        view.DirectoryRequested += ChooseDirectory;
        view.RepositoryRequested += () => ChooseGitRequested?.Invoke();
        view.RefreshRequested += () => { _ = RefreshAsync(); RefreshGitRequested?.Invoke(); };
        view.ViewRequested += ShowView;
        view.IsVisibleChanged += (_, _) =>
        {
            if (view.IsVisible) { StartWatching(); _ = RefreshAsync(); }
            else { StopReading(); view.PreviewHost.Children.Clear(); }
        };
        view.DirectoryLabel.Text = Root ?? "选择一个本机文件夹开始";
        view.DirectoryLabel.ToolTip = Root;
        view.ChooseRepositoryButton.IsEnabled = false;
    }

    private async void ChooseDirectory()
    {
        if (IsWriting) { view.SetStatus("写操作尚未结束，请完成后再切换工作区。"); return; }
        using var scope = view.ProtectInteraction();
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = "选择工作区目录", Multiselect = false };
        if (picker.ShowDialog(view) != true) return;
        try { await SelectRootAsync(picker.FolderName); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { view.SetStatus(error.Message); }
    }
    internal async Task SelectRootAsync(string root)
    {
        if (IsWriting) throw new InvalidOperationException("写操作期间不能切换工作区。");
        WorkspacePaths.Validate(root, root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("工作区目录不存在。");
        StopReading(); Root = Path.GetFullPath(root); selectedFile = null; Selection = null;
        view.ResetPreview(); view.SidebarHost.Children.Clear(); searchPanel = null;
        view.DirectoryLabel.Text = Root; view.DirectoryLabel.ToolTip = Root;
        coordinator.UpdateSelection(Root, null); WorkspaceChanged?.Invoke();
        StartWatching(); await RefreshAsync();
    }
    private void ShowView(string requested)
    { mode = requested; view.ResetPreview(); view.SetViewMode(mode); if (mode == "git") ShowGit?.Invoke(); else _ = RefreshAsync(); }
    private void StopReading()
    { generation++; searchGeneration++; reads.Cancel(); reads.Dispose(); reads = new(); searchReads?.Dispose(); searchReads = null; watcher?.Dispose(); watcher = null; debounce.Stop(); }
    private void StartWatching()
    {
        watcher?.Dispose(); watcher = null;
        if (!view.IsVisible || Root is null || !Directory.Exists(Root)) return;
        try
        {
            watcher = new FileSystemWatcher(Root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite };
            FileSystemEventHandler changed = (_, _) => ScheduleRefresh();
            watcher.Changed += changed; watcher.Created += changed; watcher.Deleted += changed;
            watcher.Renamed += (_, _) => ScheduleRefresh();
            watcher.Error += (_, _) => view.Dispatcher.BeginInvoke(() => view.SetStatus("目录监听中断，请点击刷新重新读取。"));
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { view.SetStatus("暂时无法监听此目录，可使用刷新按钮。" + error.Message); }
    }
    private void ScheduleRefresh()
    {
        if (disposed || view.Dispatcher.HasShutdownStarted) return;
        _ = view.Dispatcher.BeginInvoke(() => { if (!disposed && view.IsVisible) { debounce.Stop(); debounce.Start(); } });
    }
    internal async Task RefreshAsync()
    {
        if (disposed || !view.IsVisible) return;
        if (Root is null) return;
        var current = generation; var token = reads.Token;
        try
        {
            if (mode == "git") return;
            if (mode == "search") { if (searchPanel is null || !view.SidebarHost.Children.Contains(searchPanel)) ShowSearch(); return; }
            var entries = await files.ListAsync(Root, Root, token);
            if (current != generation || !view.IsVisible || mode != "files") return;
            RenderDirectory(entries, current, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { if (current == generation) view.SetStatus("无法读取工作区：" + error.Message); }
    }
    internal void RenderDirectory(IReadOnlyList<WorkspaceEntry> entries, long current, CancellationToken token)
    {
        var tree = new TreeView { Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0) };
        view.SetViewMode("files");
        foreach (var entry in entries.Take(2000)) tree.Items.Add(TreeItem(entry, current, token));
        if (entries.Count > 2000) view.SetStatus("此目录显示前 2000 项，请用搜索查找其他文件，或直接打开子文件夹。");
        tree.SelectedItemChanged += (_, args) =>
        {
            if (args.NewValue is not TreeViewItem { Tag: WorkspaceEntry item }) return;
            Selection = item;
            if (!item.IsDirectory && !item.IsLink) _ = ShowPreviewAsync(item.Path);
        };
        ConfigureFileActions(tree);
        view.SidebarHost.Children.Clear(); view.SidebarHost.Children.Add(tree);
        view.FitCompactContent();
    }
    private TreeViewItem TreeItem(WorkspaceEntry entry, long current, CancellationToken token)
    {
        var item = new TreeViewItem { Header = entry.Name + (entry.IsLink ? "（链接）" : ""), Tag = entry, ToolTip = entry.Path };
        if (entry.IsDirectory && !entry.IsLink)
        {
            item.Items.Add("正在读取…");
            item.Expanded += async (_, args) =>
            {
                if (args.OriginalSource != item || item.Items.Count != 1 || item.Items[0] is not string) return;
                try
                {
                    var children = await files.ListAsync(Root!, entry.Path, token);
                    if (current != generation || !view.IsVisible) return;
                    item.Items.Clear(); foreach (var child in children) item.Items.Add(TreeItem(child, current, token));
                    view.FitCompactContent();
                }
                catch (OperationCanceledException) { }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { item.Items.Clear(); item.Items.Add("无法读取：" + error.Message); }
            };
        }
        return item;
    }
    partial void ConfigureFileActions(TreeView tree);

    internal void ShowSearch()
    {
        var panel = new DockPanel();
        var controls = new StackPanel { Margin = new Thickness(6) };
        view.SetViewMode("search");
        var input = new TextBox { Text = searchQuery, ToolTip = "按 Enter 搜索所选工作区" };
        input.TextChanged += (_, _) => searchQuery = input.Text;
        var content = new System.Windows.Controls.CheckBox { Content = "搜索文本内容", IsChecked = searchContent, Margin = new Thickness(0, 10, 0, 10) };
        content.Checked += (_, _) => searchContent = true; content.Unchecked += (_, _) => searchContent = false;
        var go = new Button { Content = "搜索", HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        controls.Children.Add(input); controls.Children.Add(content); controls.Children.Add(go);
        DockPanel.SetDock(controls, Dock.Top); panel.Children.Add(controls);
        var results = new ListBox(); panel.Children.Add(results);
        async Task Search()
        {
            if (Root is null || string.IsNullOrWhiteSpace(input.Text)) return;
            searchReads?.Cancel(); searchReads?.Dispose(); searchReads = CancellationTokenSource.CreateLinkedTokenSource(reads.Token);
            var request = ++searchGeneration; var current = generation; var token = searchReads.Token; go.IsEnabled = false;
            try
            {
                var found = await files.SearchAsync(Root, input.Text, content.IsChecked == true, token);
                if (current != generation || request != searchGeneration || !view.IsVisible) return;
                results.ItemsSource = found.Hits;
                view.FitCompactContent();
                view.SetStatus(found.Truncated ? "显示前 500 条结果，请缩小搜索范围。" : $"找到 {found.Hits.Count} 条结果。");
            }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { view.SetStatus(error.Message); }
            finally { go.IsEnabled = true; }
        }
        go.Click += async (_, _) => await Search();
        input.KeyDown += async (_, args) => { if (args.Key == Key.Enter) { args.Handled = true; await Search(); } };
        results.SelectionChanged += (_, _) => { if (results.SelectedItem is WorkspaceSearchHit hit) _ = ShowPreviewAsync(hit.Path); };
        searchPanel = panel; view.SidebarHost.Children.Clear(); view.SidebarHost.Children.Add(panel);
        view.FitCompactContent();
    }
    internal async Task ShowPreviewAsync(string path)
    {
        if (Root is null || disposed || !view.IsVisible) return;
        selectedFile = path; var current = generation; var revision = view.BeginPreviewSelection(); var token = reads.Token;
        try
        {
            var preview = await files.PreviewAsync(Root, path, token);
            if (current != generation || revision != view.PreviewRevision || path != selectedFile || !view.IsVisible) return;
            var bitmap = preview.Image is { } bytes ? await WorkspaceFiles.DecodeImageAsync(bytes, token) : null;
            if (current != generation || revision != view.PreviewRevision || path != selectedFile || !view.IsVisible) return;
            RenderPreview(preview, bitmap);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or FileFormatException)
        { if (current == generation && revision == view.PreviewRevision && path == selectedFile && view.IsVisible) view.SetStatus("无法预览：" + error.Message); }
    }
    internal void RenderPreview(WorkspacePreview preview, System.Windows.Media.Imaging.BitmapSource? image = null)
    {
        var path = preview.Path;
        var panel = new DockPanel(); var tools = new System.Windows.Controls.WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        var open = new Button { Content = "用默认程序打开" }; var reveal = new Button { Content = "在资源管理器中定位" };
        open.Click += (_, _) => OpenExternal(path, false); reveal.Click += (_, _) => OpenExternal(path, true);
        tools.Children.Add(open); tools.Children.Add(reveal); DockPanel.SetDock(tools, Dock.Top); panel.Children.Add(tools);
        var title = new TextBlock { Text = Path.GetRelativePath(Root!, path), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(title, Dock.Top); panel.Children.Add(title);
        if (preview.Text is { } text)
            panel.Children.Add(new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0) });
        else if (image is not null) panel.Children.Add(new System.Windows.Controls.Image { Source = image, Stretch = Stretch.Uniform });
        else panel.Children.Add(new TextBlock { Text = preview.Notice, TextWrapping = TextWrapping.Wrap });
        view.PreviewHost.Children.Clear(); view.PreviewHost.Children.Add(panel);
        view.OpenPreview("预览");
    }
    private void OpenExternal(string path, bool reveal)
    {
        if (!view.CanStartInteraction) return;
        try
        {
            path = WorkspacePaths.Validate(Root!, path);
            if (reveal)
            {
                var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                start.ArgumentList.Add("/select," + path); Process.Start(start)?.Dispose();
            }
            else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { view.SetStatus(error.Message); }
    }
    public void Dispose() { if (disposed) return; disposed = true; StopReading(); reads.Dispose(); }
}
