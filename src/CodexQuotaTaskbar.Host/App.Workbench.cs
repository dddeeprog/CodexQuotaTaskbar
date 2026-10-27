using CodexQuotaTaskbar.Host.Workbench;

namespace CodexQuotaTaskbar.Host;

public partial class App
{
    private WorkbenchCoordinator? workbench;
    private void StartWorkbench(bool demo, Settings.AppSettings settings)
    {
        if (demo || tray is null) return;
        workbench = new WorkbenchCoordinator(settings.Workbench ?? new(), value => tray?.SetWorkbenchSettings(value));
        tray.OpenWorkbenchRequested += (_, _) => workbench?.Open();
    }
    private async Task StopWorkbenchAsync()
    { if (workbench is not null) { await workbench.DisposeAsync(); workbench = null; } }
}
