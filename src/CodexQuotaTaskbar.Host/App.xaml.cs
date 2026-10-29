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
    private readonly UpdateLaunchState updateLaunchState = new();
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
    private readonly DotChatProcess dotChat = new(inline: true);
    private CodexResetFeedClient? resetFeedClient;
    private Task? resetFeedLoop;
    private Task resetRefreshTask = Task.CompletedTask;
    private ResetScheduleClient? resetScheduleClient;
    private Task? resetScheduleLoop;
    private Task resetScheduleRefreshTask = Task.CompletedTask;
    private bool shuttingDown;
    private bool cleanupCompleted;
    private EventWaitHandle? updateStop;
    private RegisteredWaitHandle? updateStopRegistration;
    private DemoIntegrationContext? demoIntegration;
    private Task? demoProofLoop;
    private bool RestrictedDemo => demoIntegration is not null;

    protected override async void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);
        try
        {
            if (eventArgs.Args is ["--installer-setup"])
            {
                await RunInstallerSetupAsync();
                return;
            }
            if (DotChatProcess.TryOpen(eventArgs.Args, this)) return;
            if (UpdateApplyOptions.TryParse(eventArgs.Args, out var updateOptions))
            {
                try { await UpdateApplier.ApplyAsync(updateOptions!, CancellationToken.None); Diagnostics.ReliabilityDiagnostics.Local.UpdateReady(); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
                    or System.ComponentModel.Win32Exception or TimeoutException)
                {
                    if (error is UpdateApplyException diagnostic)
                    {
                        var outcome = Diagnostics.ReliabilityDiagnostics.ClassifyUpdateOutcome(diagnostic);
                        Diagnostics.ReliabilityDiagnostics.Local.Update(diagnostic.Failure, outcome);
                        if (diagnostic.CleanupFailure is { } cleanup) Diagnostics.ReliabilityDiagnostics.Local.Update(cleanup, outcome);
                        if (diagnostic.RecoveryFailure is { } recovery) Diagnostics.ReliabilityDiagnostics.Local.Update(recovery, outcome);
                    }
                    var result = new UpdateStatusWindow();
                    result.Apply(UpdateStatus.Failure(error, retryAllowed: false) with
                    { Title = "安装未完成", Message = error is UpdateApplyException apply ? apply.RecoveryOutcome
                        : "更新程序未能完成替换或启动。请保留当前文件并检查以下原因；不会自动再次执行安装。" });
                    result.ShowDialog();
                }
                Shutdown();
                return;
            }
            // Malformed updater arguments must never become a normal startup.
            if (eventArgs.Args.FirstOrDefault() == "--apply-update") { Shutdown(); return; }
            string? updateNonce = null;
            var startupArguments = eventArgs.Args;
            if (startupArguments.FirstOrDefault() == "--update-ready")
            {
                if (startupArguments.Length != 2 || !Guid.TryParseExact(startupArguments[1], "N", out _)) { Shutdown(); return; }
                updateNonce = startupArguments[1]; startupArguments = [];
                updateStop = new EventWaitHandle(false, EventResetMode.ManualReset, UpdateStartup.StopEventName(updateNonce));
                updateStopRegistration = ThreadPool.RegisterWaitForSingleObject(updateStop,
                    (_, _) => Dispatcher.BeginInvoke(() => _ = RequestShutdownAsync()), null, Timeout.Infinite, true);
            }
            var options = HostOptions.Parse(startupArguments);
            if (options.DemoDataRoot is { } proofRoot)
            {
                demoIntegration = DemoIntegrationContext.CreateOrOpen(proofRoot);
                CompanionDiagnostics.ConfigureDemo(null);
            }
            // Small always-on-top surfaces do not need a per-process GPU shader/compiler cache.
            // Keep an explicit hardware override for machines where software rendering costs more CPU.
            if (options.SoftwareRendering)
                System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            singleInstance = new Mutex(true, demoIntegration?.MutexName ?? "Local\\CodexQuotaTaskbar.Host", out var created);
            if (!created)
            {
                singleInstance.Dispose();
                singleInstance = null;
                await RequestShutdownAsync();
                return;
            }

            settingsStore = demoIntegration is null ? SettingsStore.CreateDefault() : new SettingsStore(demoIntegration.SettingsPath);
            var settings = await settingsStore.LoadAsync(lifetime.Token);
            if (RestrictedDemo) settings = RestrictDemoSettings(settings);
            await InitializeCoreConsentAsync();
            dotChat.SetReplyAllowed(false);
            if (options.DotRealtime)
            {
                settings = settings with { DotRealtime = true };
                await settingsStore.SaveAsync(settings, lifetime.Token);
            }
            lowQuotaGate = new LowQuotaGate(settings.LowQuotaThreshold);
            coordinator = new OverlayCoordinator(settings.ShowAllTaskbars, settings.EffectiveMaterialTransparencyPercent, settings.UnifiedPanel,
                CreateCorePermittedRuntime,
                observePointer: !RestrictedDemo);
            ApplyCorePresentation(settings);
            coordinator.SetInlineDotMode(true);
            coordinator.SetDotPresentationEnabled(settings.DotRealtime && !RestrictedDemo);
            dotChat.SetReplyAllowed(coordinator.DotReplyAllowed);
            coordinator.DotReplyAvailabilityChanged += dotChat.SetReplyAllowed;
            dotChat.StateChanged += state => coordinator?.ApplyInlineDotState(state);
            dotChat.OpenInlineRequested += (_, _) => coordinator?.OpenInlineDot();
            coordinator.InlineDotActionRequested += action => _ = dotChat.SendInlineActionAsync(action);
            tray = new TrayController(settings, restrictedDemo: RestrictedDemo);
            tray.SetCoreConsentStatus(coreConsent?.State ?? Bongo.CoreConsentState.Undecided, coreTerms is not null);
            tray.CoreTermsRequested += OnCoreTermsRequested;
            tray.ExportDiagnosticsRequested += OnExportDiagnosticsRequested;
            tray.SetContextAvoidanceProvider(coordinator.GetMenuObstacles);
            tray.MenuBoundsChanged += coordinator.SetMenuBounds;
            coordinator.CloseContextRequested += (_, _) => tray?.CloseContextMenu();
            tray.MaterialTransparencyPreviewRequested += (_, value) => coordinator?.SetMaterialTransparency(value);
            browserStore = options.Demo ? null : new BrowserSubscriptionStore();
            subscriptionService = options.Demo ? null : new CodexSubscriptionMetadataService(browserStore: browserStore);
            var recoveryTracker = new QuotaResetTracker(options.Demo ? null : ResetConfirmationStore.CreateDefault());
            coordinator.SetResetTracker(recoveryTracker);
            provider = options.Demo ? new DemoQuotaProvider() : new CodexRateLimitProvider(settings.SubscriptionDetailsEnabled, subscriptionService,
                recoveryTracker);
            if (provider is CodexRateLimitProvider liveProvider)
                liveProvider.ResetConfirmationChanged += until => Dispatcher.BeginInvoke(() =>
                {
                    if (!shuttingDown) coordinator?.ApplyConfirmedReset(until);
                });
            provider.SetSubscriptionDetailsEnabled(settings.SubscriptionDetailsEnabled);
            updateService = options.Demo ? null : new GitHubUpdateService();
            resetFeedClient = options.Demo ? null : new CodexResetFeedClient();
            resetScheduleClient = options.Demo ? null : new ResetScheduleClient();
            if (resetScheduleClient is { } scheduleSource)
                scheduleSource.QueryStateChanged += state =>
                {
                    if (Dispatcher.HasShutdownStarted) return;
                    _ = Dispatcher.InvokeAsync(() =>
                    {
                        if (!shuttingDown && !lifetime.IsCancellationRequested && scheduleSource.QueryState == state)
                            coordinator?.ApplyResetQueryState(state);
                    });
                };

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
            tray.CheckUpdatesRequested += OnCheckUpdatesRequested;
            tray.SettingsChanged += OnSettingsChanged;
            tray.ExitRequested += async (_, _) => await RequestShutdownAsync();
            provider.SnapshotChanged += OnSnapshotChanged;
            provider.SessionsChanged += OnSessionsChanged;

            coordinator.Apply(provider.Current);
            coordinator.ApplySessions(provider.CurrentSessions);
            coordinator.Start();
            if (updateNonce is not null)
            {
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                var executable = Environment.ProcessPath ?? throw new InvalidOperationException();
                UpdateStartup.PublishReady(executable, updateNonce, UpdatePackageIdentity.Read(executable, ProductVersion.Value),
                    new(process.Id, process.StartTime.ToUniversalTime().Ticks));
            }
            StartTypingInteraction(options.Demo, settings);
            if (RestrictedDemo) demoProofLoop = DemoProofLoopAsync(lifetime.Token);
            if (settings.DotRealtime && !options.Demo) StartDotReceiving();
            if (resetFeedClient is not null)
            {
                resetFeedLoop = ResetFeedLoopAsync(lifetime.Token);
            }
            else
            {
                coordinator.ApplyResets(ResetFeedSnapshot.Unavailable("演示模式不读取在线公告"));
            }
            if (resetScheduleClient is not null)
                resetScheduleLoop = ResetScheduleLoopAsync(lifetime.Token);
            else
                coordinator.ApplyResetSchedule(ResetScheduleSnapshot.Unavailable("演示模式不读取在线预告"));
            StartWebShortcuts(options.Demo);
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
            if (!RestrictedDemo)
                System.Windows.MessageBox.Show(exception.Message, "点岛", MessageBoxButton.OK, MessageBoxImage.Error);
            await RequestShutdownAsync();
        }
    }

    private void OnOpenSubscriptionLoginRequested(object? sender, EventArgs eventArgs)
    {
        if (RestrictedDemo) return;
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
        if (RestrictedDemo) return;
        if (shuttingDown) return;
        if (sender is QuotaCapsuleWindow && tray?.Settings.DotRealtime != true) return;
        tray?.EnableDotRealtime();
        subscriptionBrowser?.Close();
        if (coordinator is { } current) dotChat.SetReplyAllowed(current.DotReplyAllowed);
        try { dotChat.Show(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            System.Windows.MessageBox.Show("Dot 聊天暂时无法启动，请重试。", "点岛");
        }
    }

    private void StartDotReceiving()
    {
        if (RestrictedDemo) return;
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
        if (RestrictedDemo) return;
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
                tray?.RestoreUpdatePresentation();
            }
            return;
        }

        try
        {
            tray?.BeginUpdatePresentation();
            var workflow = new UpdateWorkflow(updateService.CheckAsync,
                (release, token, progress) => updateService.DownloadAndStageAsync(release, token, progress),
                _ => throw new InvalidOperationException(), (status, requested) => tray?.PresentUpdate(status, requested), RequestShutdownAsync,
                updateLaunchState, updateService.LaunchInstallerAsync);
            await workflow.RunAsync(manual, cancellationToken);
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
            await Task.WhenAll(provider?.RefreshAsync(lifetime.Token) ?? Task.CompletedTask,
                RefreshResetFeedAsync(), RefreshResetScheduleAsync());
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
    }

    private void OnOpenCodexRequested(object? sender, EventArgs eventArgs)
    {
        if (RestrictedDemo) return;
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

    private void OnOpenResetSourceRequested(Uri uri)
    {
        if (RestrictedDemo) return;
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

    private async Task ResetScheduleLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await RefreshResetScheduleAsync();
                await Task.Delay(ResetScheduleClient.PollInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private Task RefreshResetScheduleAsync()
    {
        if (resetScheduleClient is null || shuttingDown) return Task.CompletedTask;
        // These entry points run on the dispatcher; manual refresh joins the active poll.
        if (resetScheduleRefreshTask.IsCompleted) resetScheduleRefreshTask = RefreshResetScheduleCoreAsync();
        return resetScheduleRefreshTask;
    }

    private async Task RefreshResetScheduleCoreAsync()
    {
        try
        {
            var value = await resetScheduleClient!.FetchAsync(lifetime.Token);
            if (!shuttingDown && !lifetime.IsCancellationRequested) coordinator?.ApplyResetSchedule(value);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (shuttingDown || lifetime.IsCancellationRequested) { }
    }

    private void OnOpenSessionRequested(string threadId)
    {
        if (RestrictedDemo) return;
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
        if (RestrictedDemo) settings = RestrictDemoSettings(settings);
        // Close the send gate before any asynchronous settings/backend work.
        // Both companion presentations are notification-only; pending receipts keep polling.
        dotChat.SetReplyAllowed(false);
        if (settings.DotRealtime) StartDotReceiving(); else dotChat.Dispose();
        if (!settings.SubscriptionDetailsEnabled) subscriptionBrowser?.Close();
        coordinator?.SetUnifiedPanel(settings.UnifiedPanel);
        ApplyCorePresentation(settings);
        coordinator?.SetDotPresentationEnabled(settings.DotRealtime && !RestrictedDemo);
        ApplyTypingInteraction(settings);
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
        catch (Exception error) when (RestrictedDemo)
        {
            WriteDemoShutdownStage("exit-task-faulted", error);
            throw;
        }
    }

    private async Task RequestShutdownAsync()
    {
        if (shuttingDown)
        {
            return;
        }

        shuttingDown = true;
        await StopWorkbenchAsync();
        CloseCoreConsentWindow();
        WriteDemoShutdownStage("request-start");
        StopTypingInteraction();
        tray?.FlushPendingSettings();
        if (settingsStore is not null && tray is not null)
        {
            try
            {
                // Preserve the final slider value even when Quit follows a drag immediately.
                WriteDemoShutdownStage("settings-save-begin");
                await settingsStore.SaveAsync(tray.Settings, CancellationToken.None);
                WriteDemoShutdownStage("settings-save-end");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                System.Windows.MessageBox.Show("本次设置未能保存，下次启动可能恢复原值。", "点岛", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        lifetime.Cancel();
        WriteDemoShutdownStage("lifetime-cancelled");
        subscriptionBrowser?.Close();
        dotChat.Dispose();
        WriteDemoShutdownStage("dot-disposed");
        if (coordinator is not null) await coordinator.StopBongoAsync();
        WriteDemoShutdownStage("bongo-stopped");
        WriteDemoProof(finished: true);
        WriteDemoShutdownStage("coordinator-dispose-begin");
        coordinator?.Dispose();
        WriteDemoShutdownStage("coordinator-dispose-end");
        tray?.Dispose();
        WriteDemoShutdownStage("tray-disposed");
        if (provider is not null)
        {
            await provider.DisposeAsync().ConfigureAwait(true);
        }
        WriteDemoShutdownStage("provider-disposed");
        updateService?.Dispose();
        if (resetFeedLoop is not null) await resetFeedLoop;
        await resetRefreshTask;
        resetFeedClient?.Dispose();
        if (resetScheduleLoop is not null) await resetScheduleLoop;
        await resetScheduleRefreshTask;
        resetScheduleClient?.Dispose();
        WriteDemoShutdownStage("resets-stopped");
        await StopWebShortcutsAsync();
        WriteDemoShutdownStage("shortcuts-stopped");
        if (demoProofLoop is not null) await demoProofLoop;
        WriteDemoShutdownStage("demo-loop-stopped");
        singleInstance?.ReleaseMutex();
        singleInstance?.Dispose();
        WriteDemoShutdownStage("mutex-released");
        cleanupCompleted = true;
        WriteDemoShutdownStage("shutdown-call");
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs eventArgs)
    {
        updateStopRegistration?.Unregister(null);
        updateStop?.Dispose();
        WriteDemoShutdownStage("onexit-start");
        if (!cleanupCompleted)
        {
            CloseCoreConsentWindow();
            StopTypingInteraction();
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
            resetScheduleClient?.Dispose();
            DisposeWebShortcuts();
            singleInstance?.Dispose();
        }
        lifetime.Dispose();
        WriteDemoShutdownStage("onexit-end");
        base.OnExit(eventArgs);
    }
}
