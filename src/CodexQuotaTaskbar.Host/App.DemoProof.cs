using System.IO;
using System.Text.Json;
using CodexQuotaTaskbar.Host.Settings;
using CodexQuotaTaskbar.Host.Lifetime;

namespace CodexQuotaTaskbar.Host;

public partial class App
{
    private List<object>? demoShutdownStages;
    private int demoProofWriteFailures;
    private DemoProofFileFailure? demoProofLastWriteFailure;

    private void WriteDemoShutdownStage(string stage, Exception? error = null)
    {
        if (demoIntegration is null) return;
        demoShutdownStages ??= [];
        if (demoShutdownStages.Count == 32) demoShutdownStages.RemoveAt(0);
        demoShutdownStages.Add(new
        {
            stage, utc = DateTimeOffset.UtcNow, processId = Environment.ProcessId,
            threadId = Environment.CurrentManagedThreadId, dispatcherAccess = Dispatcher.CheckAccess(),
            dispatcherStopping = Dispatcher.HasShutdownStarted, cleanupCompleted,
            demoLoop = demoProofLoop?.Status.ToString(), shortcutsLoad = webShortcutsLoadTask.Status.ToString(),
            errorType = error?.GetType().FullName, errorCode = error?.HResult, errorStack = error?.StackTrace,
        });
        var path = Path.Combine(demoIntegration.ContextRoot, "shutdown-stages.json");
        DemoProofFile.TryWrite(path, JsonSerializer.Serialize(demoShutdownStages,
            new JsonSerializerOptions { WriteIndented = true }), out _);
    }

    private static AppSettings RestrictDemoSettings(AppSettings settings) => settings with
    {
        StartWithWindows = false, ShowTrayIcon = false, DotRealtime = false,
        CompanionTyping = false, SubscriptionDetails = false,
    };

    private async Task DemoProofLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                // Optional observation must not fault the task later awaited by shutdown.
                // A reader may deny replacement; stop sampling and keep the failure visible.
                if (!WriteDemoProof(finished: false)) return;
                await Task.Delay(500, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private bool WriteDemoProof(bool finished)
    {
        if (demoIntegration is null) return false;
        var path = Path.Combine(demoIntegration.ContextRoot, finished ? "shutdown-proof.json" : "running-proof.json");
        var report = new
        {
            schema = 1, utc = DateTimeOffset.UtcNow, processId = Environment.ProcessId,
            executable = Environment.ProcessPath, finished, isolatedRoot = demoIntegration.ContextRoot,
            settingsPath = demoIntegration.SettingsPath, bongoCache = demoIntegration.BongoCachePath,
            separateInstanceMutex = demoIntegration.MutexName,
            proofWriteFailures = demoProofWriteFailures,
            proofLastWriteFailure = demoProofLastWriteFailure,
            realInputSourceCreated = typingInteraction is not null,
            subscriptionBrowserCreated = subscriptionBrowser is not null,
            realMetadataStoreCreated = browserStore is not null,
            realUpdatesCreated = updateService is not null, onlineResetClientCreated = resetFeedClient is not null || resetScheduleClient is not null,
            bongo = coordinator?.BongoProofState(),
        };
        if (DemoProofFile.TryWrite(path, JsonSerializer.Serialize(report,
            new JsonSerializerOptions { WriteIndented = true }), out var failure)) return true;
        demoProofWriteFailures++;
        demoProofLastWriteFailure = failure;
        WriteDemoShutdownStage("proof-write-failed");
        return false;
    }
}
