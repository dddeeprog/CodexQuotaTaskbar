using System.IO;
using System.Net.Http;
using System.Threading;
using System.Windows;
using CodexQuotaTaskbar.Host.Lifetime;
using CodexQuotaTaskbar.Host.Notifications;
using CodexQuotaTaskbar.Host.Overlay;
using CodexQuotaTaskbar.Host.Provider;
using CodexQuotaTaskbar.Host.Settings;
using CodexQuotaTaskbar.Host.Tray;
using CodexQuotaTaskbar.Host.Update;

namespace CodexQuotaTaskbar.Host;

public partial class App : System.Windows.Application
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim updateGate = new(1, 1);
    private Mutex? singleInstance;
    private OverlayCoordinator? coordinator;
    private TrayController? tray;
    private IQuotaProvider? provider;
    private SettingsStore? settingsStore;
    private LowQuotaGate? lowQuotaGate;
    private GitHubUpdateService? updateService;
    private bool shuttingDown;
    private bool cleanupCompleted;

    protected override async void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);
        try
        {
            if (UpdateApplyOptions.TryParse(eventArgs.Args, out var updateOptions))
            {
                await UpdateApplier.ApplyAsync(updateOptions!, CancellationToken.None);
                Shutdown();
                return;
            }

            var options = HostOptions.Parse(eventArgs.Args);
            singleInstance = new Mutex(true, "Local\\CodexQuotaTaskbar.Host", out var created);
            if (!created)
            {
                await RequestShutdownAsync();
                return;
            }

            settingsStore = SettingsStore.CreateDefault();
            var settings = await settingsStore.LoadAsync(lifetime.Token);
            lowQuotaGate = new LowQuotaGate(settings.LowQuotaThreshold);
            coordinator = new OverlayCoordinator(settings.ShowAllTaskbars);
            tray = new TrayController(settings);
            provider = options.Demo ? new DemoQuotaProvider() : new CodexRateLimitProvider();
            updateService = options.Demo ? null : new GitHubUpdateService();

            coordinator.RefreshRequested += OnRefreshRequested;
            coordinator.OpenCodexRequested += OnOpenCodexRequested;
            coordinator.OpenSessionRequested += OnOpenSessionRequested;
            coordinator.ContextRequested += (_, _) => tray.ShowContextMenu(avoidIsland: true);
            tray.RefreshRequested += OnRefreshRequested;
            tray.OpenCodexRequested += OnOpenCodexRequested;
            tray.CheckUpdatesRequested += OnCheckUpdatesRequested;
            tray.SettingsChanged += OnSettingsChanged;
            tray.ExitRequested += async (_, _) => await RequestShutdownAsync();
            provider.SnapshotChanged += OnSnapshotChanged;
            provider.SessionsChanged += OnSessionsChanged;

            coordinator.Apply(provider.Current);
            coordinator.ApplySessions(provider.CurrentSessions);
            coordinator.Start();
            if (options.ExitAfter is { } exitAfter)
            {
                _ = ExitAfterAsync(exitAfter, lifetime.Token);
            }
            await provider.StartAsync(lifetime.Token);
            if (updateService is not null)
            {
                _ = CleanupUpdatesAsync(lifetime.Token);
                _ = AutomaticUpdateLoopAsync(lifetime.Token);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            System.Windows.MessageBox.Show(exception.Message, "Codex 额度", MessageBoxButton.OK, MessageBoxImage.Error);
            await RequestShutdownAsync();
        }
    }

    private async void OnCheckUpdatesRequested(object? sender, EventArgs eventArgs)
    {
        await CheckForUpdatesAsync(manual: true, lifetime.Token);
    }

    private async Task AutomaticUpdateLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                if (tray?.Settings.AutomaticUpdatesEnabled == true)
                {
                    await CheckForUpdatesAsync(manual: false, cancellationToken);
                }
                await Task.Delay(TimeSpan.FromHours(6), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task CleanupUpdatesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await updateGate.WaitAsync(cancellationToken);
            try
            {
                if (updateService is not null)
                {
                    await updateService.CleanupAsync(cancellationToken);
                }
            }
            finally
            {
                updateGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task CheckForUpdatesAsync(bool manual, CancellationToken cancellationToken)
    {
        if (updateService is null || !await updateGate.WaitAsync(0, cancellationToken))
        {
            if (manual)
            {
                tray?.ShowUpdateNotification("正在检查更新", "已有更新检查正在进行。");
            }
            return;
        }

        try
        {
            if (manual)
            {
                tray?.ShowUpdateNotification("检查更新", "正在连接 GitHub 正式发行版…");
            }

            var release = await updateService.CheckAsync(cancellationToken);
            if (release is null)
            {
                if (manual)
                {
                    tray?.ShowUpdateNotification("已是最新版本", $"当前版本为 v{ProductVersion.Text}。");
                }
                return;
            }

            tray?.ShowUpdateNotification("发现新版本", $"正在下载 {release.Tag}…");
            var staged = await updateService.DownloadAndStageAsync(release, cancellationToken);
            if (!updateService.LaunchInstaller(staged))
            {
                throw new InvalidOperationException("无法启动更新程序。");
            }

            tray?.ShowUpdateNotification("正在安装更新", $"即将重启到 {release.Tag}。");
            await RequestShutdownAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            tray?.ShowUpdateNotification(
                "更新失败",
                manual ? "无法从 GitHub 获取或安装更新。" : "自动更新暂时失败，稍后会重试。",
                System.Windows.Forms.ToolTipIcon.Warning);
        }
        finally
        {
            updateGate.Release();
        }
    }

    private void OnSnapshotChanged(object? sender, CodexQuotaTaskbar.Core.Quota.QuotaSnapshot snapshot)
    {
        if (shuttingDown)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (shuttingDown)
            {
                return;
            }
            coordinator?.Apply(snapshot);
            if (tray?.Settings.LowQuotaNotifications == true && lowQuotaGate?.ShouldNotify(snapshot.Windows) == true)
            {
                tray.ShowLowQuotaNotification();
            }
        });
    }

    private void OnSessionsChanged(object? sender, CodexQuotaTaskbar.Core.Sessions.CodexSessionsSnapshot snapshot)
    {
        if (shuttingDown)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (!shuttingDown)
            {
                coordinator?.ApplySessions(snapshot);
            }
        });
    }

    private async void OnRefreshRequested(object? sender, EventArgs eventArgs)
    {
        try
        {
            if (provider is not null)
            {
                await provider.RefreshAsync(lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
    }

    private static void OnOpenCodexRequested(object? sender, EventArgs eventArgs)
    {
        try
        {
            CodexLauncher.OpenApp();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            System.Windows.MessageBox.Show("未找到可打开的 Codex 应用。", "Codex 额度", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnOpenSessionRequested(string threadId)
    {
        try
        {
            var openedSession = provider?.CurrentSessions.Sessions.FirstOrDefault(
                session => string.Equals(session.Id, threadId, StringComparison.Ordinal));
            CodexLauncher.OpenThread(threadId);
            if (ShouldDismissViewedSession(openedSession))
            {
                provider?.DismissSession(threadId);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            System.Windows.MessageBox.Show("无法打开这个 Codex 会话。", "Codex 会话", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    internal static bool ShouldDismissViewedSession(CodexQuotaTaskbar.Core.Sessions.CodexSessionSnapshot? session) =>
        session?.State == CodexQuotaTaskbar.Core.Sessions.CodexSessionState.Review;

    private async void OnSettingsChanged(object? sender, AppSettings settings)
    {
        coordinator?.SetShowAllTaskbars(settings.ShowAllTaskbars);
        if (settingsStore is not null)
        {
            try
            {
                await settingsStore.SaveAsync(settings, lifetime.Token);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                System.Windows.MessageBox.Show("设置暂时无法保存。", "Codex 额度", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private async Task ExitAfterAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
            await Dispatcher.InvokeAsync(RequestShutdownAsync).Task.Unwrap();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RequestShutdownAsync()
    {
        if (shuttingDown)
        {
            return;
        }

        shuttingDown = true;
        lifetime.Cancel();
        coordinator?.Dispose();
        tray?.Dispose();
        if (provider is not null)
        {
            await provider.DisposeAsync().ConfigureAwait(true);
        }
        updateService?.Dispose();
        singleInstance?.ReleaseMutex();
        singleInstance?.Dispose();
        cleanupCompleted = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs eventArgs)
    {
        if (!cleanupCompleted)
        {
            lifetime.Cancel();
            coordinator?.Dispose();
            tray?.Dispose();
            if (provider is not null)
            {
                _ = Task.Run(async () => await provider.DisposeAsync()).Wait(TimeSpan.FromSeconds(5));
            }
            updateService?.Dispose();
            singleInstance?.Dispose();
        }
        lifetime.Dispose();
        base.OnExit(eventArgs);
    }
}
