using CodexQuotaTaskbar.Host.Bongo;
using CodexQuotaTaskbar.Host.Settings;

namespace CodexQuotaTaskbar.Host.Installer;

// Called only by the interactive installer mode; it does not start the ordinary Host or any account service.
internal static class InstallerSetup
{
    internal static async Task<CoreConsentState> CompleteAsync(CoreTermsDocument document, ICoreConsentStore store,
        SettingsStore settings, Func<CoreConsentDecision> choose, Func<CancellationToken, Task> install, CancellationToken token)
    {
        var permission = new CoreConsentGate(document, store);
        await permission.InitializeAsync(token);
        if (permission.State == CoreConsentState.Undecided)
        {
            var choice = choose();
            if (choice == CoreConsentDecision.Closed) return CoreConsentState.Undecided;
            if (!await permission.RecordDecisionAsync(choice, token)) return permission.State;
        }
        if (permission.CanUseCore)
        {
            await settings.InitializeInstallerDefaultsAsync(token);
            permission.RequireAccepted();
            await install(token);
        }
        return permission.State;
    }
}
