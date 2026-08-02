using System.IO;
using System.Threading;
using System.Windows;
using CodexQuotaTaskbar.Host.Lifetime;
using CodexQuotaTaskbar.Host.Notifications;
using CodexQuotaTaskbar.Host.Overlay;
using CodexQuotaTaskbar.Host.Provider;
using CodexQuotaTaskbar.Host.Settings;
using CodexQuotaTaskbar.Host.Tray;

namespace CodexQuotaTaskbar.Host;

public partial class App : System.Windows.Application
{
    private readonly CancellationTokenSource lifetime = new();
    private Mutex? singleInstance;
    private OverlayCoordinator? coordinator;
    private TrayController? tray;
    private IQuotaProvider? provider;
    private SettingsStore? settingsStore;
    private LowQuotaGate? lowQuotaGate;
    private bool shuttingDown;
    private bool cleanupCompleted;

    protected override async void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);
        try
        {
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

            coordinator.RefreshRequested += OnRefreshRequested;
            coordinator.OpenCodexRequested += OnOpenCodexRequested;
            coordinator.ContextRequested += (_, _) => tray.ShowContextMenu();
            tray.RefreshRequested += OnRefreshRequested;
            tray.OpenCodexRequested += OnOpenCodexRequested;
            tray.SettingsChanged += OnSettingsChanged;
            tray.ExitRequested += async (_, _) => await RequestShutdownAsync();
            provider.SnapshotChanged += OnSnapshotChanged;

            coordinator.Apply(provider.Current);
            coordinator.Start();
            if (options.ExitAfter is { } exitAfter)
            {
                _ = ExitAfterAsync(exitAfter, lifetime.Token);
            }
            await provider.StartAsync(lifetime.Token);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            System.Windows.MessageBox.Show(exception.Message, "Codex 额度", MessageBoxButton.OK, MessageBoxImage.Error);
            await RequestShutdownAsync();
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
            singleInstance?.Dispose();
        }
        lifetime.Dispose();
        base.OnExit(eventArgs);
    }
}
