using System.IO;
using CodexQuotaTaskbar.Host.Bongo;
using CodexQuotaTaskbar.Host.Settings;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host;

public partial class App
{
    private CoreTermsDocument? coreTerms;
    private CoreConsentGate? coreConsent;
    private CoreConsentWindow? coreConsentWindow;
    private bool coreConsentBusy;

    private async Task InitializeCoreConsentAsync()
    {
        if (!CoreTermsDocument.TryLoadCurrent(out coreTerms) || coreTerms is null) return;
        var store = demoIntegration is null ? CoreConsentStore.CreateDefault()
            : new CoreConsentStore(Path.Combine(demoIntegration.ContextRoot, CoreConsentStore.FileName));
        var permission = new CoreConsentGate(coreTerms, store);
        coreConsent = permission;
        await permission.InitializeAsync(lifetime.Token);
        permission.StateChanged += _ => Dispatcher.BeginInvoke(() =>
        {
            if (shuttingDown || tray is null) return;
            ApplyCorePresentation(tray.Settings);
            tray.SetCoreConsentStatus(permission.State, coreTerms is not null);
        });
    }

    // An unavailable/declined component changes presentation, never the stored user preferences.
    private void ApplyCorePresentation(AppSettings settings)
    {
        var allowed = coreConsent?.CanUseCore == true;
        var fallback = settings.EffectiveBongoCatVisible && !allowed;
        coordinator?.SetCompanion(settings.CompanionBallEnabled || fallback, settings.ReduceCompanionMotion);
        coordinator?.SetBongoCatVisibility(settings.EffectiveBongoCatVisible && allowed, settings.EffectiveBongoSizeDip);
    }

    // The coordinator retains this factory for renderer rebuilds, so recovery uses the same gate.
    private BongoRuntime CreateCorePermittedRuntime()
    {
        var payload = demoIntegration is null ? new BongoPayload() : new BongoPayload(demoIntegration.BongoCachePath);
        void RequireConsent() => (coreConsent ?? throw new InvalidOperationException("Core 组件条款不可用。")).RequireAccepted();
        return new BongoRuntime(async token =>
        {
            RequireConsent();
            var paths = await payload.EnsureAsync(token).ConfigureAwait(false);
            RequireConsent();
            return paths;
        }, RequireConsent);
    }

    // This is wired only to an explicit component-terms menu action, never startup or notifications.
    private async void OnCoreTermsRequested(object? sender, EventArgs eventArgs)
    {
        if (RestrictedDemo || shuttingDown) return;
        if (coreConsentBusy)
        {
            if (coreConsentWindow?.IsVisible == true) coreConsentWindow.Activate();
            return;
        }
        tray?.CloseContextMenu();
        if (coreTerms is null || coreConsent is null)
        {
            System.Windows.MessageBox.Show("组件条款暂不可用，Core 未启用。小白球和其他功能仍可使用。", "点岛");
            return;
        }
        coreConsentBusy = true;
        var permission = coreConsent;
        try
        {
            // No owner: the transient tray menu hides on deactivation and must not own this window.
            coreConsentWindow = new CoreConsentWindow(coreTerms)
            { WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen };
            coreConsentWindow.OpenOfficialRequested += OpenCoreOfficialPage;
            coreConsentWindow.ShowDialog();
            var decision = coreConsentWindow.Decision;
            if (shuttingDown || decision == CoreConsentDecision.Closed) return;
            if (!await permission.RecordDecisionAsync(decision, lifetime.Token)) return;
            if (decision == CoreConsentDecision.Accepted) tray?.EnableBongoAfterCoreConsent();
            if (tray is not null)
            {
                ApplyCorePresentation(tray.Settings);
                tray.SetCoreConsentStatus(permission.State, coreTerms is not null);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            System.Windows.MessageBox.Show("组件选择未能保存，Core 不会因本次确认而启用。请稍后重试。", "点岛",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
        finally
        {
            if (coreConsentWindow is not null) coreConsentWindow.OpenOfficialRequested -= OpenCoreOfficialPage;
            coreConsentWindow = null;
            coreConsentBusy = false;
        }
    }

    private static void OpenCoreOfficialPage(Uri address)
    {
        if (address != CoreTermsDocument.OfficialUri) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            System.Windows.MessageBox.Show("暂时无法打开官方条款网页。完整权利说明仍可在窗口内阅读。", "点岛");
        }
    }

    private void CloseCoreConsentWindow() => coreConsentWindow?.Close();
}
