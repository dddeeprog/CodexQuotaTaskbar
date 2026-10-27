using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;
using MenuItem = System.Windows.Controls.MenuItem;

namespace CodexQuotaTaskbar.Host.Workbench;

internal sealed partial class WorkbenchGitPanel : IDisposable
{
    private readonly WorkbenchWindow view;
    private readonly WorkbenchBrowser browser;
    private readonly WorkbenchCoordinator coordinator;
    private readonly WorkspaceGit git = new();
    private CancellationTokenSource reads = new();
    private long generation;
    private long diffGeneration;
    private string? selectedRepository;
    private GitWorkspace? snapshot;
    private FileSystemWatcher? indexWatcher;
    private readonly DispatcherTimer debounce;
    private bool disposed;
    private bool showing;
    private ListBox? list;

    internal WorkbenchGitPanel(WorkbenchWindow view, WorkbenchBrowser browser, WorkbenchCoordinator coordinator, WorkbenchSettings settings)
    {
        this.view = view; this.browser = browser; this.coordinator = coordinator; selectedRepository = settings.Repository;
        debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(450) };
        debounce.Tick += (_, _) => { debounce.Stop(); _ = RefreshAsync(); };
        browser.ShowGit = () => { showing = true; _ = RefreshAsync(); };
        browser.RefreshGitRequested += () => _ = RefreshAsync();
        browser.ChooseGitRequested += ChooseRepository;
        browser.WorkspaceChanged += () => { selectedRepository = null; _ = RefreshAsync(); };
        view.ViewRequested += mode => showing = mode == "git";
        view.IsVisibleChanged += (_, _) =>
        {
            if (view.IsVisible) _ = RefreshAsync();
            else { StopReads(); snapshot = null; list = null; }
        };
        view.ChooseRepositoryButton.IsEnabled = true;
    }

    internal async Task RefreshAsync()
    {
        if (disposed || !view.IsVisible || browser.Root is null || browser.IsWriting) return;
        StopReads(); var current = generation; var token = reads.Token;
        try
        {
            var root = browser.Root;
            if (selectedRepository is { } selection)
            {
                // Restored choices must still belong to the selected file workspace.
                try { WorkspacePaths.Validate(root, selection); }
                catch (IOException) { WorkspacePaths.Validate(selection, root); }
            }
            var found = await git.ReadAsync(selectedRepository ?? root, token);
            if (current != generation || !view.IsVisible) return;
            snapshot = found; selectedRepository = found.Root;
            // A directory inside a repo may legitimately have its Git root above it.
            // Keep the explicit root display rather than folding the file tree to that parent.
            view.RepositoryLabel.Text = $"{found.Branch} · {found.Root}"; view.RepositoryLabel.ToolTip = found.Root;
            coordinator.UpdateSelection(root, selectedRepository);
            WatchIndex(found.GitDirectory);
            if (showing) Render(found);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (current != generation) return;
            snapshot = null; view.RepositoryLabel.Text = "Git 暂不可用";
            view.RepositoryLabel.ToolTip = error.Message;
            if (showing) { view.SidebarHost.Children.Clear(); view.SidebarHost.Children.Add(new TextBlock { Text = "无法读取 Git 仓库。\n文件浏览仍可使用。\n\n" + error.Message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) }); }
        }
    }
    internal void Render(GitWorkspace repository)
    {
        view.SetViewMode("git");
        var panel = new DockPanel();
        var heading = new TextBlock { Text = $"{repository.Changes.Count} 项改动" + (repository.OperationInProgress ? " · 合并或变基进行中，仅浏览" : ""), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 6, 8, 12) };
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        list = new ListBox { ItemsSource = repository.Changes, SelectionMode = System.Windows.Controls.SelectionMode.Extended,
            ItemTemplate = (DataTemplate)view.Resources["WorkbenchGitChange"] };
        list.SelectionChanged += async (_, _) =>
        {
            if (list.SelectedItem is GitChange change) await ShowDiffAsync(repository, change);
        };
        ConfigureWrites(panel, list, repository);
        panel.Children.Add(list); view.SidebarHost.Children.Clear(); view.SidebarHost.Children.Add(panel);
        if (repository.Changes.Count == 0) view.SetStatus("此仓库没有未提交改动。");
    }
    partial void ConfigureWrites(DockPanel panel, ListBox changes, GitWorkspace repository);

    private async Task ShowDiffAsync(GitWorkspace repository, GitChange change)
    {
        var request = ++diffGeneration; var token = reads.Token;
        try
        {
            if (change.Area == GitArea.Untracked)
            { await browser.ShowPreviewAsync(Path.Combine(repository.Root, change.Path)); return; }
            var diff = await git.DiffAsync(repository, change, token);
            if (request != diffGeneration || snapshot != repository || !view.IsVisible) return;
            RenderDiff(diff, change);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { if (request == diffGeneration) view.SetStatus(error.Message); }
    }
    internal void RenderDiff(GitDiff diff, GitChange change)
    {
            var panel = new DockPanel();
            var heading = new TextBlock { Text = change.Path + (change.Area == GitArea.Staged ? "   HEAD → 已暂存" : "   已暂存 → 工作区"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
            DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
            if (diff.Notice is { } notice) panel.Children.Add(new TextBlock { Text = notice, TextWrapping = TextWrapping.Wrap });
            else
            {
                var rows = new ListBox { ItemsSource = diff.Rows, HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
                    ItemContainerStyle = (Style)view.Resources["WorkbenchDiffItem"] };
                ScrollViewer.SetHorizontalScrollBarVisibility(rows, ScrollBarVisibility.Disabled);
                rows.ItemTemplate = (DataTemplate)view.Resources["WorkbenchDiffRow"];
                panel.Children.Add(rows);
            }
            view.PreviewHost.Children.Clear(); view.PreviewHost.Children.Add(panel);
    }

    private async void ChooseRepository()
    {
        if (browser.IsWriting || view.CaptureActive || browser.Root is null) return;
        using var interaction = view.ProtectInteraction();
        try
        {
            var menu = new System.Windows.Controls.ContextMenu();
            menu.Resources.MergedDictionaries.Add(view.Resources);
            if (snapshot is { } repository)
                foreach (var path in await git.WorktreesAsync(repository, reads.Token))
                {
                    var item = new MenuItem { Header = path, ToolTip = "切换文件树和 Git 到此实际工作区" };
                    item.Click += async (_, _) => { try { await browser.SelectRootAsync(path); } catch (IOException error) { view.SetStatus(error.Message); } };
                    menu.Items.Add(item);
                }
            var choose = new MenuItem { Header = "在当前工作区内选择仓库…" };
            choose.Click += async (_, _) =>
            {
                using var scope = view.ProtectInteraction();
                var picker = new Microsoft.Win32.OpenFolderDialog { Title = "选择当前工作区内的 Git 仓库" };
                if (picker.ShowDialog(view) != true) return;
                try { WorkspacePaths.Validate(browser.Root, picker.FolderName); selectedRepository = picker.FolderName; await RefreshAsync(); }
                catch (IOException error) { view.SetStatus(error.Message); }
            };
            menu.Items.Add(choose);
            var protection = view.ProtectInteraction(); menu.Closed += (_, _) => protection.Dispose();
            menu.PlacementTarget = view.ChooseRepositoryButton; menu.IsOpen = true;
        }
        catch (OperationCanceledException) { }
        catch (IOException error) { view.SetStatus(error.Message); }
    }
    private void WatchIndex(string directory)
    {
        indexWatcher?.Dispose();
        indexWatcher = new FileSystemWatcher(directory) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
        indexWatcher.Changed += (_, _) => ScheduleRefresh(); indexWatcher.Created += (_, _) => ScheduleRefresh(); indexWatcher.Deleted += (_, _) => ScheduleRefresh(); indexWatcher.Renamed += (_, _) => ScheduleRefresh();
        indexWatcher.EnableRaisingEvents = true;
    }
    private void ScheduleRefresh()
    {
        if (disposed || view.Dispatcher.HasShutdownStarted) return;
        _ = view.Dispatcher.BeginInvoke(() => { if (view.IsVisible && !disposed) { debounce.Stop(); debounce.Start(); } });
    }
    private void StopReads()
    { generation++; diffGeneration++; reads.Cancel(); reads.Dispose(); reads = new(); indexWatcher?.Dispose(); indexWatcher = null; debounce.Stop(); }
    public void Dispose() { if (disposed) return; disposed = true; StopReads(); reads.Dispose(); }
}
