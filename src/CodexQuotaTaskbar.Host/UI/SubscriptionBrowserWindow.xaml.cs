using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using CodexQuotaTaskbar.Host.Provider;
using Microsoft.Web.WebView2.Core;

namespace CodexQuotaTaskbar.Host.UI;

public partial class SubscriptionBrowserWindow : Window
{
    private readonly BrowserSubscriptionStore store;
    private readonly CodexSubscriptionMetadataService service;
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<SubscriptionBrowserWindow> children = [];
    private readonly bool popup;
    private CoreWebView2Environment? environment;
    private Task<bool>? initialization;
    private string? requestNonce;
    private string? requestAccount;
    private bool closed;
    private bool busy;
    private bool clearing;
    private bool browserFailed;

    internal SubscriptionBrowserWindow(BrowserSubscriptionStore store, CodexSubscriptionMetadataService service,
        CoreWebView2Environment? environment = null, bool popup = false)
    {
        this.store = store;
        this.service = service;
        this.environment = environment;
        this.popup = popup;
        InitializeComponent();
        HomeButton.IsEnabled = ReloadButton.IsEnabled = false;
        if (popup) ReadButton.Visibility = ClearButton.Visibility = Visibility.Collapsed;
        Loaded += async (_, _) => await InitializeAsync();
        ReadButton.Click += async (_, _) => await ReadAsync();
        ClearButton.Click += async (_, _) => await ClearAsync();
        HomeButton.Click += (_, _) => NavigateSafely(reload: false);
        ReloadButton.Click += (_, _) => NavigateSafely(reload: true);
        RuntimeButton.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("https://developer.microsoft.com/microsoft-edge/webview2/#download-section") { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception) { StatusText.Text = "无法打开浏览器，请到 Microsoft 官网安装 WebView2 Runtime。"; }
        };
        Closed += (_, _) =>
        {
            closed = true;
            lifetime.Cancel();
            foreach (var child in children.ToArray()) child.Close();
            Browser.Dispose();
        };
    }

    internal event EventHandler? SubscriptionUpdated;
    internal event EventHandler? LoginCleared;

    private Task<bool> InitializeAsync() => initialization ??= InitializeCoreAsync();

    private async Task<bool> InitializeCoreAsync()
    {
        try
        {
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
            environment ??= await CoreWebView2Environment.CreateAsync(null,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexQuotaTaskbar", "WebView2"));
            if (closed) return false;
            await Browser.EnsureCoreWebView2Async(environment);
            if (closed) return false;
            var core = Browser.CoreWebView2;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsWebMessageEnabled = !popup;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.ServerCertificateErrorDetected += (_, args) => args.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
            core.NavigationStarting += (_, args) =>
            {
                if (clearing)
                {
                    args.Cancel = args.Uri != "about:blank";
                    return;
                }
                if (!BrowserSubscriptionProtocol.CanNavigate(args.Uri))
                {
                    args.Cancel = true;
                    StatusText.Text = "已阻止离开受支持的官方登录站点。";
                    return;
                }
                CancelRead();
                ReadButton.IsEnabled = false;
                AddressText.Text = new Uri(args.Uri).GetLeftPart(UriPartial.Authority);
            };
            core.NavigationCompleted += (_, args) =>
            {
                if (clearing) return;
                ReadButton.IsEnabled = !busy && !browserFailed && args.IsSuccess && BrowserSubscriptionProtocol.IsChatGptOrigin(core.Source);
                if (!args.IsSuccess) StatusText.Text = "网页未能加载，请检查网络后重新加载。额度岛仍可正常使用。";
            };
            core.WebMessageReceived += OnWebMessage;
            core.NewWindowRequested += OnNewWindow;
            core.WindowCloseRequested += (_, _) => Close();
            core.ProcessFailed += (_, _) =>
            {
                browserFailed = true;
                CancelRead();
                HomeButton.IsEnabled = ReloadButton.IsEnabled = false;
                StatusText.Text = "浏览器遇到问题，请关闭此窗口后重新打开。额度岛不受影响。";
            };
            ClearButton.IsEnabled = true;
            HomeButton.IsEnabled = ReloadButton.IsEnabled = true;
            if (!popup) core.Navigate("https://chatgpt.com/auth/login");
            return true;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            if (!closed)
            {
                StatusText.Text = "尚未安装 Microsoft WebView2 Runtime，安装后重新打开本窗口即可。无需安装 .NET。";
                RuntimeButton.Visibility = Visibility.Visible;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (!closed) StatusText.Text = "浏览器暂时无法启动，请关闭此窗口后重试。额度岛仍可正常使用。";
        }
        return false;
    }

    private async void OnNewWindow(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        using var deferral = args.GetDeferral();
        args.Handled = true;
        if (closed || clearing || !BrowserSubscriptionProtocol.CanNavigate(args.Uri) || children.Count >= 3) return;
        try
        {
            var child = new SubscriptionBrowserWindow(store, service, environment, popup: true) { Owner = this };
            children.Add(child);
            child.Closed += (_, _) => children.Remove(child);
            child.Show();
            if (await child.InitializeAsync() && !closed && !child.closed)
                args.NewWindow = child.Browser.CoreWebView2;
            else if (!child.closed) child.Close();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            if (!closed) StatusText.Text = "登录子窗口无法打开，请重新加载后重试。";
        }
    }

    private async Task ReadAsync()
    {
        if (closed || busy || !BrowserSubscriptionProtocol.IsChatGptOrigin(Browser.CoreWebView2?.Source)) return;
        busy = true;
        ReadButton.IsEnabled = ClearButton.IsEnabled = false;
        var nonce = Guid.NewGuid().ToString("N");
        requestNonce = nonce;
        try
        {
            var account = await service.GetCurrentAccountIdAsync(lifetime.Token);
            if (closed || requestNonce != nonce) return;
            if (string.IsNullOrWhiteSpace(account))
            {
                StatusText.Text = "请先在 Codex 桌面版登录，再来读取同一账号的订阅。";
                CancelRead();
                return;
            }
            requestAccount = account;
            StatusText.Text = "正在通过当前网页登录读取订阅…";
            await Browser.ExecuteScriptAsync(BrowserSubscriptionProtocol.ReadScript());
            if (closed || requestNonce != nonce) return;
            Browser.CoreWebView2!.PostWebMessageAsJson(JsonSerializer.Serialize(new { kind = "cqtb.readSubscription", nonce, accountId = account }));
            await Task.Delay(TimeSpan.FromSeconds(25), lifetime.Token);
            if (!closed && requestNonce == nonce)
            {
                StatusText.Text = "读取超时。请完成页面上的登录或验证后重试。";
                CancelRead();
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException)
        {
            if (!closed) { StatusText.Text = "暂时无法读取，请重新加载网页后重试。"; CancelRead(); }
        }
    }

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (closed || requestNonce is not { } nonce || requestAccount is not { } account ||
            !BrowserSubscriptionProtocol.IsChatGptOrigin(Browser.CoreWebView2.Source)) return;
        try
        {
            var result = BrowserSubscriptionProtocol.Parse(args.Source, args.WebMessageAsJson, nonce, account, DateTimeOffset.UtcNow);
            if (result is null) return;
            var currentAccount = await service.GetCurrentAccountIdAsync(lifetime.Token);
            if (closed || requestNonce != nonce) return;
            if (!string.Equals(account, currentAccount, StringComparison.Ordinal))
            {
                StatusText.Text = "Codex 账号已切换，本次结果已丢弃。请登录对应账号后重新读取。";
            }
            else if (result.ExpiresAt is { } date)
            {
                if (store.TrySave(account, date, DateTimeOffset.UtcNow))
                {
                    StatusText.Text = $"已更新：订阅有效至 {date.ToLocalTime():yyyy-MM-dd HH:mm}。现在可以关闭窗口啦。";
                    SubscriptionUpdated?.Invoke(this, EventArgs.Empty);
                }
                else StatusText.Text = "已读取日期，但无法保存到本机，请检查文件权限。";
            }
            else StatusText.Text = result.Status switch
            {
                "auth_required" => "尚未登录或登录已失效，请在页面完成登录后重试。",
                "verification_required" => "服务仍要求网页验证。请在页面手动完成验证后重试；若仍被拒绝，暂时无法读取。",
                "account_mismatch" => "网页返回的账号或工作区与 Codex 当前使用的不同。请确认两边选择了相同的个人或团队工作区，不必先退出账号。",
                "account_unverified" => "暂时无法核实网页返回的账号信息，这不代表你登录错了。请重新加载网页后再试。",
                "not_provided" => "已连接，但服务没有返回有效的订阅截止日期。不会用额度重置时间代替。",
                _ => "暂时无法获取订阅，请检查网络或稍后重试。",
            };
            CancelRead();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException)
        {
            if (!closed) { StatusText.Text = "无法应用订阅结果，请重试。"; CancelRead(); }
        }
    }

    private async Task ClearAsync()
    {
        if (closed || busy || Browser.CoreWebView2 is null) return;
        if (System.Windows.MessageBox.Show(this, "将退出此窗口的网页登录，并删除本机保存的网页订阅日期。不会退出 Codex 或其他浏览器。", "清除网页登录", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        CancelRead();
        busy = true;
        clearing = true;
        ReadButton.IsEnabled = ClearButton.IsEnabled = false;
        HomeButton.IsEnabled = ReloadButton.IsEnabled = false;
        try
        {
            foreach (var child in children.ToArray()) child.Close();
            Browser.CoreWebView2.Stop();
            var blankReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnBlankReady(object? sender, CoreWebView2NavigationCompletedEventArgs args)
            {
                if (Browser.CoreWebView2.Source == "about:blank" && args.IsSuccess) blankReady.TrySetResult();
            }
            Browser.CoreWebView2.NavigationCompleted += OnBlankReady;
            try
            {
                Browser.CoreWebView2.Navigate("about:blank");
                await blankReady.Task.WaitAsync(TimeSpan.FromSeconds(10), lifetime.Token);
            }
            finally { if (!closed) Browser.CoreWebView2.NavigationCompleted -= OnBlankReady; }
            await Browser.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
            if (closed) return;
            if (store.Clear()) LoginCleared?.Invoke(this, EventArgs.Empty);
            else { StatusText.Text = "网页登录已清除，但订阅缓存删除失败，请检查文件权限。"; return; }
            StatusText.Text = "网页登录及网页订阅缓存已清除。";
            clearing = false;
            Browser.CoreWebView2.Navigate("https://chatgpt.com/auth/login");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException or TimeoutException)
        {
            if (!closed) StatusText.Text = "未能完整清除网页登录，请关闭窗口后重试。";
        }
        finally
        {
            clearing = false;
            if (!closed) { HomeButton.IsEnabled = ReloadButton.IsEnabled = !browserFailed; CancelRead(); }
        }
    }

    private void CancelRead()
    {
        requestNonce = requestAccount = null;
        busy = clearing;
        if (!closed)
        {
            ClearButton.IsEnabled = !clearing && !browserFailed && Browser.CoreWebView2 is not null;
            ReadButton.IsEnabled = !clearing && !browserFailed && BrowserSubscriptionProtocol.IsChatGptOrigin(Browser.CoreWebView2?.Source);
        }
    }

    private void NavigateSafely(bool reload)
    {
        if (closed || clearing || browserFailed || Browser.CoreWebView2 is null) return;
        try
        {
            if (reload) Browser.CoreWebView2.Reload();
            else Browser.CoreWebView2.Navigate("https://chatgpt.com/auth/login");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            browserFailed = true;
            CancelRead();
            HomeButton.IsEnabled = ReloadButton.IsEnabled = false;
            StatusText.Text = "浏览器暂时无法导航，请关闭此窗口后重新打开。额度岛不受影响。";
        }
    }
}
