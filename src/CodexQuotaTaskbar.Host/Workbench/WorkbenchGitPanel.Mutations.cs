using System.IO;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ListBox = System.Windows.Controls.ListBox;

namespace CodexQuotaTaskbar.Host.Workbench;

internal sealed partial class WorkbenchGitPanel
{
    private readonly Dictionary<string, string> commitDrafts = new(StringComparer.OrdinalIgnoreCase);
    partial void ConfigureWrites(DockPanel panel, ListBox changes, GitWorkspace repository)
    {
        var footer = new StackPanel { Margin = new Thickness(6, 10, 0, 2) };
        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        var stage = new Button { Content = "暂存所选" }; var unstage = new Button { Content = "取消暂存" };
        actions.Children.Add(stage); actions.Children.Add(unstage); footer.Children.Add(actions);
        var count = repository.Changes.Count(c => c.Area == GitArea.Staged);
        var commit = new Button { Content = $"提交…（{count} 个已暂存文件）", Margin = new Thickness(0, 10, 0, 0) };
        footer.Children.Add(commit);
        var allowed = !repository.OperationInProgress && repository.Changes.All(c => c.Area != GitArea.Conflict);
        stage.IsEnabled = allowed; unstage.IsEnabled = allowed; commit.IsEnabled = allowed && count != 0;
        stage.Click += (_, _) => Start(changes.SelectedItems.Cast<GitChange>().Where(c => c.Area is GitArea.Worktree or GitArea.Untracked).ToArray(), false);
        unstage.Click += (_, _) => Start(changes.SelectedItems.Cast<GitChange>().Where(c => c.Area == GitArea.Staged).ToArray(), true);
        void Start(GitChange[] selection, bool remove)
        {
            if (selection.Length == 0) { view.SetStatus("请先在列表中选择对应分区的文件。"); return; }
            BeginGitWrite(repository, () => remove ? git.UnstageAsync(repository, selection, default) : git.StageAsync(repository, selection, default), false);
        }
        commit.Click += (_, _) => ReviewCommit(repository);
        DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
    }
    private void ReviewCommit(GitWorkspace repository)
    {
        if (browser.IsWriting || !view.IsVisible || view.CaptureActive || WorkbenchCoordinator.IsCaptured()) return;
        using var protection = view.ProtectInteraction();
        var dialog = new Window { Owner = view, Title = "核对并提交已暂存内容", Width = Math.Min(600, view.ActualWidth),
            Height = Math.Min(510, view.ActualHeight), MinWidth = 400, MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Background = view.Background, Foreground = view.Foreground, FontFamily = view.FontFamily };
        var body = new DockPanel { Margin = new Thickness(18) };
        var heading = new TextBlock { Text = $"{repository.Branch} · {repository.Root}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(heading, Dock.Top); body.Children.Add(heading);
        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true }; var confirm = new Button { Content = "提交已暂存内容", IsDefault = true };
        actions.Children.Add(cancel); actions.Children.Add(confirm); DockPanel.SetDock(actions, Dock.Bottom); body.Children.Add(actions);
        var message = new TextBox { Text = commitDrafts.GetValueOrDefault(repository.Root, ""), AcceptsReturn = true, Height = 90, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, ToolTip = "填写本地提交说明" };
        message.TextChanged += (_, _) => commitDrafts[repository.Root] = message.Text;
        DockPanel.SetDock(message, Dock.Bottom); body.Children.Add(message);
        var paths = repository.Changes.Where(c => c.Area == GitArea.Staged).Select(c => c.Path).ToArray();
        var scope = new StackPanel(); scope.Children.Add(new TextBlock { Text = $"完整提交范围：{paths.Length} 个已暂存文件", Margin = new Thickness(0, 0, 0, 8) });
        scope.Children.Add(new TextBlock { Text = string.Join("\n", paths), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new ScrollViewer { Content = scope, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        confirm.Click += (_, _) => { if (!view.CanStartInteraction) return; if (!string.IsNullOrWhiteSpace(message.Text)) dialog.DialogResult = true; else message.Focus(); };
        view.StyleDialog(dialog, body); dialog.Loaded += (_, _) => message.Focus();
        if (dialog.ShowDialog() == true) BeginGitWrite(repository, () => git.CommitAsync(repository, message.Text, default), true);
    }
    private void BeginGitWrite(GitWorkspace reviewed, Func<Task<GitWorkspace>> operation, bool commit)
    {
        if (browser.IsWriting || !view.IsVisible || view.CaptureActive || WorkbenchCoordinator.IsCaptured() || snapshot != reviewed) return;
        async Task Run()
        {
            try
            {
                view.SetStatus(commit ? "正在创建本地提交…" : "正在更新暂存区…");
                snapshot = await operation();
                if (commit) commitDrafts.Remove(reviewed.Root);
                view.SetStatus(commit ? $"本地提交已完成：{snapshot.Head?[..Math.Min(12, snapshot.Head.Length)]}" : "暂存区已更新，工作区内容保持不变。");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or OperationCanceledException)
            { view.SetStatus("Git 操作未完成：" + error.Message); }
        }
        var task = Run(); coordinator.PendingWrite = task;
        _ = AfterWriteAsync(task);
    }
    private async Task AfterWriteAsync(Task task)
    {
        await task;
        if (disposed) return;
        if (!disposed && view.IsVisible) { await RefreshAsync(); await browser.RefreshAsync(); }
    }
}
