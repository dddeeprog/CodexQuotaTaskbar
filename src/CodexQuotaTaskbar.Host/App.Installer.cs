using System.IO;
using System.Net.Http;
using System.Windows;
using CodexQuotaTaskbar.Host.Bongo;
using CodexQuotaTaskbar.Host.Installer;
using CodexQuotaTaskbar.Host.Settings;
using CodexQuotaTaskbar.Host.UI;
using MessageBox = System.Windows.MessageBox;

namespace CodexQuotaTaskbar.Host;

public partial class App
{
    private async Task RunInstallerSetupAsync()
    {
        // The installer already checks AppMutex; protect against a launch racing that check too.
        using var mutex = new Mutex(true, "Local\\CodexQuotaTaskbar.Host", out var owns);
        if (!owns) { Shutdown(2); return; }
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            if (!CoreTermsDocument.TryLoadCurrent(out var document) || document is null)
                throw new InvalidDataException("Core 条款不可用，未下载可选组件。");
            CoreConsentDecision Choose()
            {
                var view = new CoreConsentWindow(document);
                view.OpenOfficialRequested += OpenCoreOfficialPage;
                view.ShowDialog();
                view.OpenOfficialRequested -= OpenCoreOfficialPage;
                return view.Decision;
            }
            var state = await InstallerSetup.CompleteAsync(document, CoreConsentStore.CreateDefault(), SettingsStore.CreateDefault(),
                Choose, async token => { await new BongoPayload().EnsureAsync(token); }, CancellationToken.None);
            if (state != CoreConsentState.Accepted)
                MessageBox.Show("已保留基础点岛和白球。本次未下载或安装 Core，也不会启用它；以后可在外观菜单主动选择组件条款。升级不删除已有组件缓存或用户数据。", "点岛安装");
            Shutdown(0);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException
            or OperationCanceledException or InvalidOperationException)
        {
            MessageBox.Show("可选 Core 组件未能准备完成。基础点岛已保留，不会重复安装或关闭其他程序。请检查网络，稍后在外观菜单重试。", "点岛安装", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
        }
    }
}
