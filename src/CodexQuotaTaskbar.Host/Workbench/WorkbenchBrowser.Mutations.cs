using MenuItem = System.Windows.Controls.MenuItem;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TreeView = System.Windows.Controls.TreeView;

namespace CodexQuotaTaskbar.Host.Workbench;

internal sealed partial class WorkbenchBrowser
{
    partial void ConfigureFileActions(TreeView tree)
    {
        var menu = new System.Windows.Controls.ContextMenu();
        void Add(string label, Action action)
        { var item = new MenuItem { Header = label }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Add("新建文件…", () => Create(false)); Add("新建文件夹…", () => Create(true));
        Add("重命名…", Rename); Add("移到回收站…", Recycle);
        tree.ContextMenu = menu;
        IDisposable? interaction = null;
        menu.Opened += (_, _) => interaction = view.ProtectInteraction();
        menu.Closed += (_, _) => { interaction?.Dispose(); interaction = null; };
        tree.PreviewMouseRightButtonDown += (_, args) =>
        {
            var element = args.OriginalSource as DependencyObject;
            while (element is not null && element is not TreeViewItem) element = System.Windows.Media.VisualTreeHelper.GetParent(element);
            if (element is TreeViewItem item) item.IsSelected = true;
        };
        tree.KeyDown += (_, args) =>
        {
            if (args.Key == Key.F2) { Rename(); args.Handled = true; }
            else if (args.Key == Key.Delete) { Recycle(); args.Handled = true; }
        };
    }
    private string MutationParent() => Selection is { IsDirectory: true } selected ? selected.Path
        : Selection is { } file ? Path.GetDirectoryName(file.Path)! : Root!;
    private void Create(bool directory)
    {
        if (Root is null || IsWriting || !view.CanStartInteraction) return;
        var name = view.Prompt(directory ? "新建文件夹" : "新建文件");
        if (name is null) return;
        var root = Root; var parent = MutationParent();
        BeginFileWrite(async () => { await files.CreateAsync(root, parent, name, directory, CancellationToken.None); });
    }
    private void Rename()
    {
        if (Root is null || Selection is not { } selected || IsWriting || !view.CanStartInteraction) return;
        var name = view.Prompt("重命名", selected.Name); if (name is null) return;
        var root = Root;
        BeginFileWrite(async () => { await files.RenameAsync(root, selected.Path, name, CancellationToken.None); });
    }
    private void Recycle()
    {
        if (Root is null || Selection is not { } selected || IsWriting || !view.CanStartInteraction) return;
        using var scope = view.ProtectInteraction();
        if (System.Windows.MessageBox.Show(view, $"将此项目移到回收站？\n\n{selected.Path}", "移到回收站", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        var root = Root;
        BeginFileWrite(() => files.RecycleAsync(root, selected.Path, CancellationToken.None));
    }
    private void BeginFileWrite(Func<Task> operation)
    {
        if (!view.CanStartInteraction || IsWriting) return;
        async Task Run()
        {
            try { await operation(); view.SetStatus("文件操作已完成，未自动暂存。"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or COMException or OperationCanceledException)
            { view.SetStatus("文件操作未完成：" + error.Message); }
            finally { await RefreshAsync(); RefreshGitRequested?.Invoke(); }
        }
        coordinator.PendingWrite = Run();
    }
}
