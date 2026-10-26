using System.Windows;
using CodexQuotaTaskbar.Host.Shortcuts;

namespace CodexQuotaTaskbar.Host;

public partial class App
{
    private WebShortcutsStore? webShortcutsStore;
    private WebShortcutIconService? webShortcutIcons;
    private WebShortcutManualIconStore? webShortcutManualIcons;
    private WebShortcutsSnapshot webShortcuts = new([], null, true);
    private Task webShortcutsLoadTask = Task.CompletedTask;
    private readonly SemaphoreSlim webShortcutsEditGate = new(1, 1);
    private readonly WebShortcutIconRequests shortcutIconRequests = new();
    private readonly HashSet<Task> shortcutIconTasks = [];

    private void StartWebShortcuts(bool demo)
    {
        coordinator!.OpenWebShortcutRequested += OnOpenWebShortcutRequested;
        coordinator.AddWebShortcutRequested += OnAddWebShortcutRequested;
        coordinator.DeleteWebShortcutRequested += OnDeleteWebShortcutRequested;
        coordinator.ChangeWebShortcutIconRequested += OnChangeWebShortcutIconRequested;
        coordinator.RestoreAutomaticWebShortcutIconRequested += OnRestoreAutomaticWebShortcutIconRequested;
        if (demo)
        {
            ApplyWebShortcuts(new([]));
            return;
        }
        webShortcutsStore = new WebShortcutsStore();
        webShortcutIcons = new WebShortcutIconService();
        webShortcutManualIcons = new WebShortcutManualIconStore();
        webShortcutsLoadTask = LoadWebShortcutsAsync();
    }

    private async Task LoadWebShortcutsAsync()
    {
        try
        {
            var value = await webShortcutsStore!.LoadAsync(lifetime.Token);
            if (shuttingDown || lifetime.IsCancellationRequested) return;
            ApplyWebShortcuts(value);
            QueueShortcutIcons(value.Items);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private void ApplyWebShortcuts(WebShortcutsSnapshot value)
    {
        webShortcuts = value;
        shortcutIconRequests.Retain(value.Items);
        coordinator?.ApplyShortcuts(value);
    }

    private async void OnAddWebShortcutRequested(WebShortcutAddRequest input)
    {
        if (webShortcutsStore is null || shuttingDown) return;
        try
        {
            await webShortcutsLoadTask;
            await webShortcutsEditGate.WaitAsync(lifetime.Token);
            try
            {
                if (!WebShortcut.TryNormalizeUrl(input.Address, out var uri, out var error))
                {
                    ApplyWebShortcuts(webShortcuts with { ErrorMessage = error });
                    return;
                }
                if (webShortcuts.Items.Any(item => item.TargetUri.AbsoluteUri == uri!.AbsoluteUri))
                {
                    ApplyWebShortcuts(webShortcuts with { ErrorMessage = "这个网址已经添加" });
                    return;
                }
                string? importedKey = null;
                string? iconError = null;
                if (input.LocalIconPath is { } path)
                {
                    var imported = await webShortcutManualIcons!.ImportAsync(path, lifetime.Token);
                    importedKey = imported.Key;
                    iconError = imported.ErrorMessage;
                }
                var item = new WebShortcut(Guid.NewGuid().ToString("D"), WebShortcut.DisplayNameFor(uri!), uri!)
                { ManualIconKey = importedKey };
                var saved = await WebShortcutIconCommit.SaveAsync(webShortcutsStore.SaveAsync, webShortcutManualIcons!,
                    webShortcuts.Items, [.. webShortcuts.Items, item], importedKey, lifetime.Token);
                if (shuttingDown || lifetime.IsCancellationRequested) return;
                ApplyWebShortcuts(saved.ErrorMessage is null && iconError is not null
                    ? saved with { ErrorMessage = "网站已添加，所选图标未能使用。可右键重新选择图标。" }
                    : saved);
                if (saved.ErrorMessage is null) QueueShortcutIcons(saved.Items);
            }
            finally { webShortcutsEditGate.Release(); }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async void OnDeleteWebShortcutRequested(string id)
    {
        if (webShortcutsStore is null || shuttingDown) return;
        try
        {
            await webShortcutsLoadTask;
            await webShortcutsEditGate.WaitAsync(lifetime.Token);
            try
            {
                var removed = webShortcuts.Items.FirstOrDefault(item => item.Id == id);
                if (removed is null) return;
                var saved = await WebShortcutIconCommit.SaveAsync(webShortcutsStore.SaveAsync, webShortcutManualIcons!,
                    webShortcuts.Items, webShortcuts.Items.Where(item => item.Id != id).ToArray(), null, lifetime.Token);
                if (!shuttingDown && !lifetime.IsCancellationRequested) ApplyWebShortcuts(saved);
            }
            finally { webShortcutsEditGate.Release(); }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async void OnChangeWebShortcutIconRequested(string id, string path)
    {
        if (webShortcutsStore is null || webShortcutManualIcons is null || shuttingDown) return;
        try
        {
            await webShortcutsLoadTask;
            await webShortcutsEditGate.WaitAsync(lifetime.Token);
            try
            {
                var previous = webShortcuts.Items.FirstOrDefault(item => item.Id == id);
                if (previous is null) return;
                var imported = await webShortcutManualIcons.ImportAsync(path, lifetime.Token);
                var importedKey = imported.Key;
                if (importedKey is null)
                {
                    if (!shuttingDown && !lifetime.IsCancellationRequested)
                        ApplyWebShortcuts(webShortcuts with { ErrorMessage = imported.ErrorMessage ?? "图标未能读取，原图标已保留。" });
                    return;
                }
                var replacement = previous with { ManualIconKey = importedKey };
                var saved = await WebShortcutIconCommit.SaveAsync(webShortcutsStore.SaveAsync, webShortcutManualIcons,
                    webShortcuts.Items, webShortcuts.Items.Select(item => item.Id == id ? replacement : item).ToArray(), importedKey, lifetime.Token);
                if (!shuttingDown && !lifetime.IsCancellationRequested)
                {
                    ApplyWebShortcuts(saved);
                    if (saved.ErrorMessage is null) QueueShortcutIcons(saved.Items);
                }
            }
            finally { webShortcutsEditGate.Release(); }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async void OnRestoreAutomaticWebShortcutIconRequested(string id)
    {
        if (webShortcutsStore is null || shuttingDown) return;
        try
        {
            await webShortcutsLoadTask;
            await webShortcutsEditGate.WaitAsync(lifetime.Token);
            try
            {
                var previous = webShortcuts.Items.FirstOrDefault(item => item.Id == id);
                if (previous?.ManualIconKey is null) return;
                var saved = await WebShortcutIconCommit.SaveAsync(webShortcutsStore.SaveAsync, webShortcutManualIcons!,
                    webShortcuts.Items, webShortcuts.Items.Select(item => item.Id == id
                        ? item with { ManualIconKey = null } : item).ToArray(), null, lifetime.Token);
                if (!shuttingDown && !lifetime.IsCancellationRequested)
                {
                    ApplyWebShortcuts(saved);
                    if (saved.ErrorMessage is null) QueueShortcutIcons(saved.Items);
                }
            }
            finally { webShortcutsEditGate.Release(); }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private void QueueShortcutIcons(IReadOnlyList<WebShortcut> items)
    {
        if (webShortcutIcons is null || shuttingDown || lifetime.IsCancellationRequested) return;
        foreach (var item in items)
        {
            if (!shortcutIconRequests.TryBegin(item, out var request)) continue;
            var task = LoadShortcutIconAsync(item, request);
            shortcutIconTasks.Add(task);
            _ = ObserveShortcutIconTaskAsync(task);
        }
    }

    private async Task ObserveShortcutIconTaskAsync(Task task)
    {
        try { await task; }
        finally { shortcutIconTasks.Remove(task); }
    }

    private async Task LoadShortcutIconAsync(WebShortcut item, WebShortcutIconRequests.Request request)
    {
        try
        {
            var icon = item.ManualIconKey is { } key
                ? await webShortcutManualIcons!.LoadAsync(key, lifetime.Token) ?? new WebShortcutIcon(null, null)
                : await webShortcutIcons!.FetchAsync(item.TargetUri, lifetime.Token);
            if (!shuttingDown && !lifetime.IsCancellationRequested && shortcutIconRequests.IsCurrent(request, webShortcuts.Items))
                coordinator?.ApplyShortcutIcon(item.Id, icon);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (shuttingDown || lifetime.IsCancellationRequested) { }
    }

    private void OnOpenWebShortcutRequested(Uri uri)
    {
        if (RestrictedDemo) return;
        if (!IsAllowedWebShortcut(uri)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            System.Windows.MessageBox.Show("无法打开网站，请稍后重试。", "点岛", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    internal static bool IsAllowedWebShortcut(Uri uri) => uri.IsAbsoluteUri &&
        WebShortcut.TryNormalizeUrl(uri.AbsoluteUri, out _, out _);

    private async Task StopWebShortcutsAsync()
    {
        await webShortcutsLoadTask;
        await webShortcutsEditGate.WaitAsync();
        webShortcutsEditGate.Release();
        await Task.WhenAll(shortcutIconTasks.ToArray());
        DisposeWebShortcuts();
    }

    private void DisposeWebShortcuts() => webShortcutIcons?.Dispose();
}
