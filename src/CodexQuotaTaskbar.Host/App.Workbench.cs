using CodexQuotaTaskbar.Host.Workbench;

namespace CodexQuotaTaskbar.Host;

public partial class App
{
    private WorkbenchCoordinator? workbench;
    private WorkbenchBrowser? workbenchBrowser;
    private WorkbenchGitPanel? workbenchGit;
    private void StartWorkbench(bool demo, Settings.AppSettings settings)
    {
        if (demo || tray is null) return;
        workbench = new WorkbenchCoordinator(settings.Workbench ?? new(), value => tray?.SetWorkbenchSettings(value));
        workbench.WindowCreated += view =>
        {
            workbenchBrowser = new WorkbenchBrowser(view, workbench, tray.Settings.Workbench ?? new());
            workbenchGit = new WorkbenchGitPanel(view, workbenchBrowser, workbench, tray.Settings.Workbench ?? new());
        };
        tray.OpenWorkbenchRequested += (_, _) => workbench?.Open();
    }
    private async Task StopWorkbenchAsync()
    { if (workbench is not null) { await workbench.DisposeAsync(); workbenchGit?.Dispose(); workbenchBrowser?.Dispose(); workbench = null; } }
}
