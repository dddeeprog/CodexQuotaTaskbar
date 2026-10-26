using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CodexQuotaTaskbar.Host.Provider;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CodexQuotaTaskbar.DotBridgeProbe;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length != 1 || !Directory.Exists(args[0])) return;
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        new Application().Run(new ProbeWindow(Path.GetFullPath(args[0])));
    }
}

internal sealed class ProbeWindow : Window
{
    private readonly TextBlock status = new() { Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock nativeReply = new() { Margin = new Thickness(12), FontSize = 18 };
    private readonly ContentControl surface = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer sampler = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Dictionary<int, long> observedChildren = [];
    private readonly StreamWriter log;
    private readonly string script;
    private readonly string token = "DOT-WV-" + Guid.NewGuid().ToString("N")[..8];
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private WebView2? browser;
    private CoreWebView2Environment? environment;
    private string phase = "baseline";
    private string lastState = "";
    private bool requestAttempted;
    private bool closed;

    internal ProbeWindow(string output)
    {
        Title = "额度岛 · Dot 网页桥接验证（非正式版）";
        Width = 1060; Height = 800; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(32, 32, 34)); Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        var panel = new DockPanel();
        DockPanel.SetDock(status, Dock.Top); panel.Children.Add(status);
        DockPanel.SetDock(nativeReply, Dock.Bottom); panel.Children.Add(nativeReply);
        panel.Children.Add(surface); Content = panel;
        var path = Path.Combine(output, $"measurement-{Environment.ProcessId}.jsonl");
        log = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        using var resource = typeof(ProbeWindow).Assembly.GetManifestResourceStream("CodexQuotaTaskbar.DotBridgeProbe.Bridge.js")!;
        using var reader = new StreamReader(resource); script = reader.ReadToEnd();
        sampler.Tick += (_, _) => Sample();
        Loaded += async (_, _) => await RunAsync();
        Closed += (_, _) => { closed = true; lifetime.Cancel(); sampler.Stop(); ReleaseBrowser(); log.Dispose(); };
    }

    private void Record(object data)
    {
        if (!closed) log.WriteLine(JsonSerializer.Serialize(new { seconds = Math.Round(clock.Elapsed.TotalSeconds, 2), phase, data }));
    }
    private async Task Delay(int seconds) => await Task.Delay(TimeSpan.FromSeconds(seconds), lifetime.Token);

    private async Task RunAsync()
    {
        try
        {
            status.Text = "正在采集打开前基线。只测试收发，不修改额度岛；需要登录时请手动完成。";
            Record(new { kind = "start", pid = Environment.ProcessId, token, note = "Private bytes sum; WS sum can double-count shared pages. No forced GC or process killing." });
            sampler.Start(); Sample(); await Delay(8);
            for (var cycle = 1; cycle <= 2; cycle++)
            {
                phase = $"open-{cycle}";
                await OpenAsync();
                var ready = await WaitForAsync(90, "ready", "reply_received");
                if (ready == "ready" && !requestAttempted)
                {
                    requestAttempted = true;
                    var prepared = await InspectAsync("send");
                    if (prepared == "draft_prepared")
                    {
                        await Delay(1);
                        var sent = await InspectAsync("submit");
                        Record(new { kind = "submit", result = sent });
                        ready = await WaitForAsync(90, "reply_received");
                    }
                }
                phase = ready == "reply_received" ? $"chat-loaded-{cycle}" : $"not-chat-ready-{cycle}";
                Record(new { kind = "outcome", state = ready });
                status.Text = ready == "reply_received" ? "已在原生面板读取到 Dot 测试回复；采集加载后内存。" : "尚未验证到 Dot 会话；本次只测当前网页的内存，不能算聊天成功。";
                await Delay(12);
                phase = $"closed-{cycle}"; Sample(); ReleaseBrowser();
                status.Text = "网页控件已释放，等待子进程自然退出；不强制结束、不强制垃圾回收。";
                await Delay(30); Sample();
                if (ready != "reply_received") break;
            }
            phase = "complete"; Record(new { kind = "complete" }); Close();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (lifetime.IsCancellationRequested) return;
            Record(new { kind = "error", type = ex.GetType().Name, hresult = ex.HResult });
            phase = "closed-error"; ReleaseBrowser();
            try { await Delay(15); Sample(); Close(); } catch (OperationCanceledException) { }
        }
    }

    private async Task OpenAsync()
    {
        // Same app-owned profile as the existing visible subscription login. Never import browser cookies.
        var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexQuotaTaskbar", "WebView2");
        environment = await CoreWebView2Environment.CreateAsync(null, profile);
        lifetime.Token.ThrowIfCancellationRequested();
        environment.BrowserProcessExited += BrowserExited;
        browser = new WebView2(); surface.Content = browser;
        await browser.EnsureCoreWebView2Async(environment);
        lifetime.Token.ThrowIfCancellationRequested();
        var core = browser.CoreWebView2;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, args) => args.Cancel = true;
        core.ServerCertificateErrorDetected += (_, args) => args.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
        core.NewWindowRequested += (_, args) => { args.Handled = true; status.Text = "网页登录弹窗需要单独适配；本轮不自动切换登录方式。"; };
        core.NavigationStarting += (_, args) => args.Cancel = !BrowserSubscriptionProtocol.CanNavigate(args.Uri);
        core.NavigationCompleted += (_, args) => Record(new { kind = "navigation", ok = args.IsSuccess, status = args.HttpStatusCode, page = ClassifyPage(core.Source) });
        core.ProcessFailed += (_, args) => Record(new { kind = "browser_failed", type = args.ProcessFailedKind.ToString() });
        Record(new { kind = "browser_opened", browserPid = core.BrowserProcessId, runtime = environment.BrowserVersionString });
        core.Navigate("https://chatgpt.com/dots/home");
        status.Text = "可见的官方 Dot 页面。若需验证请手动完成；仅识别已知 Dot 会话后发送一次测试。";
    }

    private void BrowserExited(object? sender, CoreWebView2BrowserProcessExitedEventArgs args)
    {
        Record(new { kind = "browser_exited", pid = args.BrowserProcessId, exit = args.BrowserProcessExitKind.ToString() });
        if (sender is CoreWebView2Environment env) env.BrowserProcessExited -= BrowserExited;
    }

    private static string ClassifyPage(string source) => Uri.TryCreate(source, UriKind.Absolute, out var uri)
        ? uri.Host == "chatgpt.com" && uri.AbsolutePath == "/dots/home" ? "dot" : uri.Host == "chatgpt.com" ? "chatgpt-other" : "login-or-other-allowed" : "unknown";

    private async Task<string> InspectAsync(string action)
    {
        if (browser?.CoreWebView2 is not { } core || ClassifyPage(core.Source) != "dot") return "not_dot_page";
        var result = await core.ExecuteScriptAsync(script + "(" + JsonSerializer.Serialize(token) + "," + JsonSerializer.Serialize(action) + ")");
        using var doc = JsonDocument.Parse(result);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return "bridge_unavailable";
        var state = doc.RootElement.GetProperty("state").GetString() ?? "unknown";
        if (state == "reply_received") nativeReply.Text = "原生面板收到：" + token;
        if (state != lastState) { Record(new { kind = "bridge_state", state }); lastState = state; }
        return state;
    }

    private async Task<string> WaitForAsync(int seconds, params string[] expected)
    {
        var until = clock.Elapsed.TotalSeconds + seconds;
        var state = "not_ready";
        while (clock.Elapsed.TotalSeconds < until)
        {
            await Delay(2); state = await InspectAsync("read");
            if (expected.Contains(state)) return state;
        }
        return state;
    }

    private void ReleaseBrowser()
    {
        if (browser is null) return;
        Sample(); var releasing = browser; browser = null; surface.Content = null;
        releasing.Dispose(); environment = null;
        Record(new { kind = "webview_disposed" });
    }

    private void Sample()
    {
        foreach (var id in ProcessTree.Descendants(Environment.ProcessId))
        {
            try { using var p = Process.GetProcessById(id); if (p.ProcessName == "msedgewebview2") observedChildren.TryAdd(id, p.StartTime.ToUniversalTime().Ticks); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        using var self = Process.GetCurrentProcess(); self.Refresh();
        var rows = new List<object>(); long privateBytes = 0, workingSet = 0;
        foreach (var (id, started) in observedChildren)
        {
            try
            {
                using var p = Process.GetProcessById(id);
                if (p.HasExited || p.StartTime.ToUniversalTime().Ticks != started) continue;
                privateBytes += p.PrivateMemorySize64; workingSet += p.WorkingSet64;
                rows.Add(new { pid = id, name = p.ProcessName, privateBytes = p.PrivateMemorySize64, workingSet = p.WorkingSet64 });
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        Record(new { kind = "memory", hostPrivate = self.PrivateMemorySize64, hostWorkingSet = self.WorkingSet64, childPrivate = privateBytes, childWorkingSet = workingSet, childCount = rows.Count, children = rows });
    }
}

internal static class ProcessTree
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Entry
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr Heap;
        public uint ModuleId, Threads, ParentId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Exe;
    }
    [DllImport("kernel32.dll")] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snapshot, ref Entry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snapshot, ref Entry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    internal static IEnumerable<int> Descendants(int root)
    {
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) return [];
        var all = new List<(int Id, int Parent)>();
        try
        {
            var entry = new Entry { Size = (uint)Marshal.SizeOf<Entry>(), Exe = "" };
            if (Process32FirstW(snapshot, ref entry)) do { all.Add(((int)entry.ProcessId, (int)entry.ParentId)); } while (Process32NextW(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        using var rootProcess = Process.GetProcessById(root);
        var found = new Dictionary<int, long> { [root] = rootProcess.StartTime.ToUniversalTime().Ticks };
        while (true)
        {
            var before = found.Count;
            foreach (var item in all)
            {
                if (found.ContainsKey(item.Id) || !found.TryGetValue(item.Parent, out var parentStart)) continue;
                try
                {
                    using var child = Process.GetProcessById(item.Id);
                    var childStart = child.StartTime.ToUniversalTime().Ticks;
                    // A stale parent PID can point at a newer unrelated process after PID reuse.
                    if (childStart >= parentStart) found.Add(item.Id, childStart);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            if (before == found.Count) break;
        }
        found.Remove(root); return found.Keys;
    }
}
