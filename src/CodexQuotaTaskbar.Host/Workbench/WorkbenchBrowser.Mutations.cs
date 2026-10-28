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
        var menu = view.CreateContextMenu();
        string? openedRoot = null;
        WorkspaceEntry? target = null;
        bool Current() => mode == "files" && Root == openedRoot && Selection == target;
        MenuItem Add(string label, Action action)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => { if (item.IsEnabled && view.CanStartInteraction && Current()) action(); };
            menu.Items.Add(item); return item;
        }
        var open = Add("用默认程序打开", () => OpenExternal(target!.Path, false));
        var reveal = Add("在资源管理器中定位", () => OpenExternal(target!.Path, true));
        var copy = Add("复制路径", () => CopyPath(target!.Path));
        menu.Items.Add(new Separator { Style = (Style)view.Resources[typeof(Separator)] });
        var newFile = Add("新建文件…", () => Create(false));
        var newDirectory = Add("新建文件夹…", () => Create(true));
        var rename = Add("重命名…", Rename); var recycle = Add("移到回收站…", Recycle);
        menu.Opened += (_, _) =>
        {
            openedRoot = Root; target = Selection;
            open.IsEnabled = reveal.IsEnabled = copy.IsEnabled = target is { IsLink: false };
            newFile.IsEnabled = newDirectory.IsEnabled = Root is not null && !IsWriting && target is not { IsDirectory: true, IsLink: true };
            rename.IsEnabled = recycle.IsEnabled = target is { IsLink: false } && !IsWriting;
        };
        tree.ContextMenu = menu;
        tree.PreviewMouseRightButtonDown += (_, args) =>
        {
            SelectContextTarget(tree, ItemsControl.ContainerFromElement(null, args.OriginalSource as DependencyObject) as TreeViewItem);
        };
        tree.KeyDown += (_, args) =>
        {
            if (args.Key == Key.F2) { Rename(); args.Handled = true; }
            else if (args.Key == Key.Delete) { Recycle(); args.Handled = true; }
        };
    }
    internal void SelectContextTarget(TreeView tree, TreeViewItem? item)
    {
        view.CloseContextMenu(); view.BeginPreviewSelection();
        selectingContextTarget = true;
        try
        {
            if (item is not null) { item.IsSelected = true; Selection = item.Tag as WorkspaceEntry; item.Focus(); }
            else { if (tree.SelectedItem is TreeViewItem previous) previous.IsSelected = false; Selection = null; }
        }
        finally { selectingContextTarget = false; }
    }
    private void CopyPath(string path)
    {
        if (!view.CanStartInteraction || Root is null) return;
        try { System.Windows.Clipboard.SetText(WorkspacePaths.Validate(Root, path)); view.SetStatus("路径已复制。"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ExternalException)
        { view.SetStatus("无法复制路径：" + error.Message); }
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
