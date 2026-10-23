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
using CodexQuotaTaskbar.Host.UI;
using CodexQuotaTaskbar.Host.Resets;

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
    private BrowserSubscriptionStore? browserStore;
    private CodexSubscriptionMetadataService? subscriptionService;
    private SubscriptionBrowserWindow? subscriptionBrowser;
    private readonly DotChatProcess dotChat = new();
    private CodexResetFeedClient? resetFeedClient;
    private Task? resetFeedLoop;
    private Task resetRefreshTask = Task.CompletedTask;
    private bool shuttingDown;
    private bool cleanupCompleted;

    protected override async void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);
        try
        {
            if (DotChatProcess.TryOpen(eventArgs.Args, this)) return;
            if (UpdateApplyOptions.TryParse(eventArgs.Args, out var updateOptions))
            {
                await UpdateApplier.ApplyAsync(updateOptions!, CancellationToken.None);
                Shutdown();
                return;
            }

            var options = HostOptions.Parse(eventArgs.Args);
            // Small always-on-top surfaces do not need a per-process GPU shader/compiler cache.
            // Keep an explicit hardware override for machines where software rendering costs more CPU.
            if (options.SoftwareRendering)
                System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            singleInstance = new Mutex(true, "Local\\CodexQuotaTaskbar.Host", out var created);
            if (!created)
            {
                singleInstance.Dispose();
                singleInstance = null;
                await RequestShutdownAsync();
                return;
            }

            settingsStore = SettingsStore.CreateDefault();
            var settings = await settingsStore.LoadAsync(lifetime.Token);
            if (options.DotRealtime)
            {
                settings = settings with { DotRealtime = true };
                await settingsStore.SaveAsync(settings, lifetime.Token);
            }
            lowQuotaGate = new LowQuotaGate(settings.LowQuotaThreshold);
            coordinator = new OverlayCoordinator(settings.ShowAllTaskbars, settings.EffectiveMaterialTransparencyPercent, settings.UnifiedPanel);
            coordinator.SetCompanion(settings.CompanionBallEnabled, settings.ReduceCompanionMotion);
            tray = new TrayController(settings);
            tray.MaterialTransparencyPreviewRequested += (_, value) => coordinator?.SetMaterialTransparency(value);
            tray.CompanionPreviewRequested += (_, _) => coordinator?.ToggleCompanionTour();
            browserStore = new BrowserSubscriptionStore();
            subscriptionService = new CodexSubscriptionMetadataService(browserStore: browserStore);
            provider = options.Demo ? new DemoQuotaProvider() : new CodexRateLimitProvider(settings.SubscriptionDetailsEnabled, subscriptionService);
            provider.SetSubscriptionDetailsEnabled(settings.SubscriptionDetailsEnabled);
            updateService = options.Demo ? null : new GitHubUpdateService();
            resetFeedClient = options.Demo ? null : new CodexResetFeedClient();

            coordinator.RefreshRequested += OnRefreshRequested;
            coordinator.OpenCodexRequested += OnOpenCodexRequested;
            coordinator.OpenSubscriptionLoginRequested += OnOpenSubscriptionLoginRequested;
            coordinator.OpenDotChatRequested += OnOpenDotChatRequested;
            coordinator.DotAnchorChanged += dotChat.UpdateAnchor;
            coordinator.OpenSessionRequested += OnOpenSessionRequested;
            coordinator.OpenResetSourceRequested += OnOpenResetSourceRequested;
            coordinator.ContextRequested += (sender, _) => tray.ShowContextMenu(sender as CodexQuotaTaskbar.Host.UI.QuotaCapsuleWindow);
            tray.RefreshRequested += OnRefreshRequested;
            tray.OpenCodexRequested += OnOpenCodexRequested;
            tray.OpenSubscriptionLoginRequested += OnOpenSubscriptionLoginRequested;
            tray.OpenDotChatRequested += OnOpenDotChatRequested;
            tray.CheckUpdatesRequested += OnCheckUpdatesRequested;
            tray.SettingsChanged += OnSettingsChanged;
            tray.ExitRequested += async (_, _) => await RequestShutdownAsync();
            provider.SnapshotChanged += OnSnapshotChanged;
            provider.SessionsChanged += OnSessionsChanged;

            coordinator.Apply(provider.Current);
            coordinator.ApplySessions(provider.CurrentSessions);
            coordinator.Start();
            if (settings.DotRealtime && !options.Demo) StartDotReceiving();
            if (resetFeedClient is not null)
            {
                resetFeedLoop = ResetFeedLoopAsync(lifetime.Token);
            }
            else
            {
                coordinator.ApplyResets(ResetFeedSnapshot.Unavailable("演示模式不读取在线公告"));
            }
            if (options.SubscriptionLogin && !options.Demo)
            {
                OnOpenSubscriptionLoginRequested(this, EventArgs.Empty);
            }
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
            System.Windows.MessageBox.Show(exception.Message, "点岛", MessageBoxButton.OK, MessageBoxImage.Error);
            await RequestShutdownAsync();
        }
    }

    private void OnOpenSubscriptionLoginRequested(object? sender, EventArgs eventArgs)
    {
        if (shuttingDown || browserStore is null || subscriptionService is null || provider is not CodexRateLimitProvider) return;
        if (dotChat.IsRunning)
        {
            System.Windows.MessageBox.Show("请先关闭 Dot 聊天，再打开订阅网页登录。两者共用网页登录资料。", "点岛");
            return;
        }
        tray?.EnableSubscriptionDetails();
        if (subscriptionBrowser is not null)
        {
            subscriptionBrowser.WindowState = WindowState.Normal;
            subscriptionBrowser.Activate();
            return;
        }
        subscriptionBrowser = new SubscriptionBrowserWindow(browserStore, subscriptionService);
        subscriptionBrowser.SubscriptionUpdated += OnRefreshRequested;
        subscriptionBrowser.LoginCleared += OnBrowserLoginCleared;
        subscriptionBrowser.Closed += (_, _) => subscriptionBrowser = null;
        subscriptionBrowser.Show();
        subscriptionBrowser.Activate();
    }

    private void OnOpenDotChatRequested(object? sender, EventArgs eventArgs)
    {
        if (shuttingDown) return;
        if (sender is QuotaCapsuleWindow && tray?.Settings.DotRealtime != true) return;
        tray?.EnableDotRealtime();
        subscriptionBrowser?.Close();
        try { dotChat.Show(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            System.Windows.MessageBox.Show("Dot 聊天暂时无法启动，请重试。", "点岛");
        }
    }

    private void StartDotReceiving()
    {
        subscriptionBrowser?.Close();
        try { dotChat.Start(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        { tray?.ShowUpdateNotification("Dot 未连接", "实时接收未能启动，请从菜单重新开启。"); }
    }

    private async void OnBrowserLoginCleared(object? sender, EventArgs eventArgs)
    {
        try
        {
            if (provider is CodexRateLimitProvider liveProvider)
            {
                await liveProvider.ClearBrowserSubscriptionMetadataAsync(lifetime.Token);
                await liveProvider.RefreshAsync(lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
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
        if (cancellationToken.IsCancellationRequested) return;
        if (updateService is null || !await updateGate.WaitAsync(0))
        {
            if (manual)
            {
                tray?.ShowUpdateNotification("正在检查更新", "已有更新检查正在进行。", showWhenTrayHidden: true);
            }
            return;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (manual)
            {
                tray?.ShowUpdateNotification("检查更新", "正在连接 GitHub 正式发行版…");
            }

            var release = await updateService.CheckAsync(cancellationToken);
            if (release is null)
            {
                if (manual)
                {
                    tray?.ShowUpdateNotification("暂无可用更新", $"当前版本为 v{ProductVersion.Text}，没有更高版本的正式更新。", showWhenTrayHidden: true);
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
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            var message = exception switch
            {
                OperationCanceledException => "更新请求超时或中断，请稍后重试。",
                InvalidDataException => "发行信息或更新包校验失败，请稍后重试。",
                System.Text.Json.JsonException => "发行信息无效，请稍后重试。",
                _ => "无法从 GitHub 获取或安装更新，请稍后重试。",
            };
            tray?.ShowUpdateNotification(
                "更新失败",
                manual ? message : "自动更新暂时失败，稍后会重试。",
                System.Windows.Forms.ToolTipIcon.Warning,
                showWhenTrayHidden: manual);
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
            await Task.WhenAll(provider?.RefreshAsync(lifetime.Token) ?? Task.CompletedTask, RefreshResetFeedAsync());
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
            System.Windows.MessageBox.Show("未找到可打开的 Codex 应用。", "点岛", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async Task ResetFeedLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await RefreshResetFeedAsync();
                await Task.Delay(TimeSpan.FromMinutes(15), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private Task RefreshResetFeedAsync()
    {
        if (resetFeedClient is null || shuttingDown) return Task.CompletedTask;
        if (resetRefreshTask.IsCompleted) resetRefreshTask = RefreshResetFeedCoreAsync();
        return resetRefreshTask;
    }

    private async Task RefreshResetFeedCoreAsync()
    {
        try
        {
            var snapshot = await resetFeedClient!.FetchAsync(lifetime.Token);
            if (!shuttingDown) coordinator?.ApplyResets(snapshot);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private static void OnOpenResetSourceRequested(Uri uri)
    {
        // Only the fixed data-source credit is navigable, never a remote post URL.
        if (!IsAllowedResetSource(uri)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            System.Windows.MessageBox.Show("无法打开公告来源，请稍后重试。", "点岛", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    internal static bool IsAllowedResetSource(Uri uri) =>
        uri.IsAbsoluteUri && string.Equals(uri.AbsoluteUri, CodexResetFeedClient.SiteUri.AbsoluteUri, StringComparison.Ordinal);

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
        if (settings.DotRealtime) StartDotReceiving(); else dotChat.Dispose();
        if (!settings.SubscriptionDetailsEnabled) subscriptionBrowser?.Close();
        coordinator?.SetUnifiedPanel(settings.UnifiedPanel);
        coordinator?.SetCompanion(settings.CompanionBallEnabled, settings.ReduceCompanionMotion);
        coordinator?.SetShowAllTaskbars(settings.ShowAllTaskbars);
        coordinator?.SetMaterialTransparency(settings.EffectiveMaterialTransparencyPercent);
        var refreshSubscriptionDetails = provider?.SetSubscriptionDetailsEnabled(settings.SubscriptionDetailsEnabled) == true;
        if (settingsStore is not null)
        {
            try
            {
                await settingsStore.SaveAsync(settings, lifetime.Token);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                System.Windows.MessageBox.Show("设置暂时无法保存。", "点岛", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
        }
        if (refreshSubscriptionDetails && provider is not null)
        {
            try
            {
                await provider.RefreshAsync(lifetime.Token);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
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
        tray?.FlushPendingSettings();
        if (settingsStore is not null && tray is not null)
        {
            try
            {
                // Preserve the final slider value even when Quit follows a drag immediately.
                await settingsStore.SaveAsync(tray.Settings, CancellationToken.None);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                System.Windows.MessageBox.Show("本次设置未能保存，下次启动可能恢复原值。", "点岛", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        lifetime.Cancel();
        subscriptionBrowser?.Close();
        dotChat.Dispose();
        coordinator?.Dispose();
        tray?.Dispose();
        if (provider is not null)
        {
            await provider.DisposeAsync().ConfigureAwait(true);
        }
        updateService?.Dispose();
        if (resetFeedLoop is not null) await resetFeedLoop;
        await resetRefreshTask;
        resetFeedClient?.Dispose();
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
            subscriptionBrowser?.Close();
            dotChat.Dispose();
            coordinator?.Dispose();
            tray?.Dispose();
            if (provider is not null)
            {
                _ = Task.Run(async () => await provider.DisposeAsync()).Wait(TimeSpan.FromSeconds(5));
            }
            updateService?.Dispose();
            resetFeedClient?.Dispose();
            singleInstance?.Dispose();
        }
        lifetime.Dispose();
        base.OnExit(eventArgs);
    }
}
