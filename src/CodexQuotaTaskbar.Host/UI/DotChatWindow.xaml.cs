using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CodexQuotaTaskbar.Host.Lifetime;
using CodexQuotaTaskbar.Host.Provider;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Brushes = System.Windows.Media.Brushes;
using SystemColors = System.Windows.SystemColors;

namespace CodexQuotaTaskbar.Host.UI;

public partial class DotChatWindow : Window
{
    private const string DotUrl = "https://chatgpt.com/dots/home";
    private readonly Process? parent;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer ownerWatch = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly string script;
    private readonly Stopwatch opened = Stopwatch.StartNew();
    private readonly DotMessageTracker tracker = new(DateTimeOffset.UtcNow);
    private readonly WebView2 browser = new();
    private readonly Window pageWindow;
    private readonly HashSet<string> beforeSend = new(StringComparer.Ordinal);
    private DotMessage[] messages = [];
    private DotAnchor? anchor;
    private readonly DotAnchorMailbox anchorMailbox;
    private System.Drawing.Rectangle? lastPlacement;
    private bool? appliedHighContrast;
    private int? appliedTransparency;
    private const uint SwpNoSize = 0x0001;
    internal int PlacementCount { get; private set; }
    internal int PlacementResizeCount { get; private set; }
    internal int PlacementStyleCount { get; private set; }
    private double maxPlacementMilliseconds;
    private bool closed, started, busy, needsReview, ready, wanted, attention;
    private bool composing;
    private int compositionGeneration;
    private bool imeEnterDown;
    private string? pendingText;
    private DateTime pendingSince;
    private bool pendingBaselineComplete, webDraftPresent;
    private bool receiptStructureReady = true;
    private long draftRevision, pendingDraftRevision;
    private string attemptState = "none", lastReadError = "none";
    private string? attemptId, confirmedIdHash;
    private DateTime? lastReadUtc, lastErrorUtc;
    private int consecutiveReadFailures;
    private DotBridgeDiagnostic bridgeDiagnostic = new();
    private int dismissCount, replyCount, dismissCountAtAttempt, replyCountAtAttempt;
    private DateTime? lastDismissUtc, lastReplyUtc, attemptStartedUtc, confirmedUtc;
    private bool? draftClearedOnReceipt;
    private long? receiptNativeDraftRevision, receiptPendingDraftRevision;
    private int? confirmedTextLength;
    internal bool Connected => ready;
    internal WebView2 Browser => browser;

    internal DotChatWindow(Process? parent = null)
    {
        this.parent = parent;
        InitializeComponent();
        anchorMailbox = new(action => Dispatcher.BeginInvoke(DispatcherPriority.Render, action),
            value => { if (!closed) ApplyAnchor(value); });
        InitializeAutoReply();
        using var resource = typeof(DotChatWindow).Assembly.GetManifestResourceStream("CodexQuotaTaskbar.Host.Provider.DotChatBridge.js")!;
        using var reader = new StreamReader(resource);
        script = reader.ReadToEnd();
        pageWindow = new Window
        {
            Title = "Dot 官方网页登录 · 点岛", Width = 850, Height = 680,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            FontFamily = OverlayTypography.Interface, Background = Brushes.White,
            ShowActivated = false, ShowInTaskbar = false,
        };
        var pageLayout = new DockPanel();
        var toolbar = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(8) };
        AddPageButton("返回 Dot", () => browser.CoreWebView2?.Navigate(DotUrl));
        AddPageButton("重新加载", () => browser.CoreWebView2?.Reload());
        AddPageButton("收起网页", HidePage);
        DockPanel.SetDock(toolbar, Dock.Top);
        pageLayout.Children.Add(toolbar); pageLayout.Children.Add(browser); pageWindow.Content = pageLayout;
        void AddPageButton(string title, Action action)
        {
            var button = new Button { Content = title, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 8, 0) };
            button.Click += (_, _) => action(); toolbar.Children.Add(button);
        }
        pageWindow.Closing += (_, e) => { if (!closed) { e.Cancel = true; HidePage(); } };
        Closing += (_, e) => { if (!closed) { e.Cancel = true; Dismiss(); } };
        CloseButton.Click += (_, _) => Dismiss();
        PageButton.Click += (_, _) => ShowPage();
        MessageButton.Click += (_, _) => Reply();
        GlassBorder.MouseLeftButtonUp += (_, e) =>
        {
            if (Composer.IsMouseOver || PageButton.IsMouseOver || CloseButton.IsMouseOver || MessageButton.IsMouseOver) return;
            Reply(); e.Handled = true;
        };
        SendButton.Click += async (_, _) => await SendAsync();
        Input.TextChanged += (_, _) => { CancelAutoReply(); draftRevision++; UpdateSend(); };
        Input.AddHandler(TextCompositionManager.PreviewTextInputStartEvent,
            new TextCompositionEventHandler((_, e) =>
            {
                if (e.TextComposition.AutoComplete == TextCompositionAutoComplete.Off)
                { CancelAutoReply(); composing = true; compositionGeneration++; }
            }), true);
        Input.AddHandler(TextCompositionManager.PreviewTextInputUpdateEvent,
            new TextCompositionEventHandler((_, e) =>
            {
                if (e.TextComposition.AutoComplete == TextCompositionAutoComplete.Off)
                { CancelAutoReply(); composing = true; compositionGeneration++; }
            }), true);
        Input.AddHandler(TextCompositionManager.PreviewTextInputEvent, new TextCompositionEventHandler((_, e) =>
        {
            // Keep the confirming key in the IME path, even when completion arrives before KeyDown.
            if (e.TextComposition.AutoComplete != TextCompositionAutoComplete.Off || !composing) return;
            var completedGeneration = compositionGeneration;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (compositionGeneration == completedGeneration) composing = false;
            }));
        }), true);
        Input.PreviewKeyDown += async (_, e) => await HandleInputKeyAsync(e, Keyboard.Modifiers);
        Input.PreviewKeyUp += (_, e) =>
        {
            if (e.Key == Key.Enter || (e.Key == Key.ImeProcessed && e.ImeProcessedKey == Key.Enter)) imeEnterDown = false;
        };
        Input.LostKeyboardFocus += (_, _) => { composing = false; compositionGeneration++; imeEnterDown = false; };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Dismiss(); } };
        SizeChanged += (_, _) => { lastPlacement = null; Reposition(); };
        ownerWatch.Tick += (_, _) =>
        {
            try { if (this.parent?.HasExited == true) Stop(); }
            catch (InvalidOperationException) { Stop(); }
        };
        SourceInitialized += (_, _) =>
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var style = Platform.NativeMethods.GetWindowLongPtr(handle, Platform.NativeMethods.GwlExStyle).ToInt64();
            Platform.NativeMethods.SetWindowLongPtr(handle, Platform.NativeMethods.GwlExStyle, new nint(style | Platform.NativeMethods.WsExToolWindow));
        };
    }

    internal async Task HandleInputKeyAsync(KeyEventArgs e, ModifierKeys modifiers)
    {
        CancelAutoReply();
        // WPF marks keys owned by the IME before it has processed candidate confirmation.
        if (e.Key == Key.ImeProcessed)
        {
            if (e.ImeProcessedKey == Key.Enter) imeEnterDown = true;
            return;
        }
        if (e.Key != Key.Enter) return;
        if (composing) { imeEnterDown = true; return; }
        if (modifiers != ModifierKeys.None) return;
        e.Handled = true;
        if (!imeEnterDown && !e.IsRepeat) await SendAsync();
    }

    internal void Start()
    {
        if (started || closed) return;
        started = true;
        ownerWatch.Start();
        _ = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        // Initialize the browser in a non-activating, invisible login host, not in the native bubble.
        pageWindow.Opacity = 0; pageWindow.Show(); pageWindow.Hide(); pageWindow.Opacity = 1;
        _ = ConnectAsync();
        if (parent is not null) _ = ReadCommandsAsync();
    }

    private async Task ReadCommandsAsync()
    {
        try
        {
            using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            while (!closed)
            {
                var line = await input.ReadLineAsync(lifetime.Token).ConfigureAwait(false);
                if (line is null) break;
                if (line.Length > 4096) continue;
                DotCommand? command;
                try { command = JsonSerializer.Deserialize<DotCommand>(line); } catch (JsonException) { continue; }
                if (command is null) continue;
                if (command is { Kind: "anchor", Anchor: { } value }) { anchorMailbox.Post(value); continue; }
                // An explicit action is a barrier: an older queued placement must not run after its anchor.
                anchorMailbox.Clear();
                await Dispatcher.InvokeAsync(() => HandleCommand(command));
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        finally { anchorMailbox.Clear(); if (!closed) await Dispatcher.InvokeAsync(Stop); }
    }

    private void HandleCommand(DotCommand? command)
    {
        if (closed || command is null) return;
        if (command.Anchor is { } value) ApplyAnchor(value);
        if (command.Kind == "reply") Reply();
    }

    internal void ApplyAnchor(DotAnchor value)
    {
        anchor = value;
        var highContrast = SystemParameters.HighContrast;
        if (appliedTransparency != value.Transparency || appliedHighContrast != highContrast)
        {
            appliedTransparency = value.Transparency;
            appliedHighContrast = highContrast;
            PlacementStyleCount++;
            GlassBorder.Background = highContrast ? SystemColors.WindowBrush : OverlayGlassMaterial.CreateSurfaceBrush(value.Transparency);
            if (highContrast)
            {
                Foreground = Input.Foreground = Input.CaretBrush = MessageText.Foreground = StatusText.Foreground = SystemColors.WindowTextBrush;
                GlassBorder.BorderBrush = SystemColors.WindowTextBrush;
            }
        }
        if (!value.Visible) { Hide(); pageWindow.Hide(); }
        else if (wanted && !IsVisible) Present();
        if (autoReplyAwaitingPresentation && IsVisible) ExpandReplyForNewMessage();
        Reposition();
    }

    internal static System.Drawing.Rectangle CalculateBounds(DotAnchor a, double width, double height)
    {
        var scale = Math.Clamp(a.Dpi / 96d, 0.5, 5);
        var w = Math.Max(1, Math.Min((int)Math.Ceiling(width * scale), a.Right - a.Left - 16));
        var h = Math.Max(1, Math.Min((int)Math.Ceiling(height * scale), a.WorkBottom - a.Top - 16));
        var x = Math.Clamp(a.X - w / 2, a.Left + 8, Math.Max(a.Left + 8, a.Right - w - 8));
        var above = a.Y - h - 4;
        var y = above >= a.Top + 8 ? above : a.Bottom + 4;
        y = Math.Clamp(y, a.Top + 8, Math.Max(a.Top + 8, a.WorkBottom - h - 8));
        return new(x, y, w, h);
    }

    private void Reposition()
    {
        if (anchor is not { } a || !IsVisible) return;
        var bounds = CalculateBounds(a, Width, Math.Max(ActualHeight, 80));
        if (lastPlacement == bounds) return;
        var positionOnly = lastPlacement is { } previous && previous.Size == bounds.Size;
        var startedAt = Stopwatch.GetTimestamp();
        if (Platform.NativeMethods.SetWindowPos(new System.Windows.Interop.WindowInteropHelper(this).Handle,
            Platform.NativeMethods.HwndTopmost, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            Platform.NativeMethods.SwpNoActivate | (positionOnly ? SwpNoSize : 0)))
        {
            lastPlacement = bounds;
            PlacementCount++;
            if (!positionOnly) PlacementResizeCount++;
            maxPlacementMilliseconds = Math.Max(maxPlacementMilliseconds, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        }
    }

    private void Present()
    {
        wanted = true;
        if (closed || anchor?.Visible != true) return;
        if (!IsVisible)
        {
            ShowActivated = false; Show(); UpdateLayout(); Reposition();
            if (SystemParameters.ClientAreaAnimation)
                Shell.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        }
    }

    internal void Reply()
    {
        CancelAutoReply();
        replyCount++; lastReplyUtc = DateTime.UtcNow;
        HidePage(reviewed: false);
        if (!attention && tracker.Latest is { } latest) MessageText.Text = latest.Text;
        Present();
        Composer.Visibility = Visibility.Visible;
        MessageScroller.MaxHeight = 160;
        UpdateLayout(); Reposition(); Activate(); Input.Focus();
        WriteDiagnostics(force: true);
    }

    private void Dismiss()
    {
        CancelAutoReply();
        dismissCount++; lastDismissUtc = DateTime.UtcNow;
        wanted = false; tracker.Dismiss(); Composer.Visibility = Visibility.Collapsed;
        MessageScroller.MaxHeight = 112; Hide();
        // Keep the connection and unsent draft in memory. Only Stop disposes the browser.
        WriteDiagnostics(force: true);
    }

    private void ShowPage()
    {
        if (closed) return;
        CancelAutoReply();
        SetMemoryLevel(CoreWebView2MemoryUsageTargetLevel.Normal);
        pageWindow.ShowInTaskbar = true; pageWindow.Show(); pageWindow.Activate();
    }

    private void HidePage() => HidePage(reviewed: true);

    private void HidePage(bool reviewed)
    {
        pageWindow.Hide(); pageWindow.ShowInTaskbar = false;
        if (reviewed && pendingText is null) needsReview = false;
        SetMemoryLevel(CoreWebView2MemoryUsageTargetLevel.Low); UpdateSend();
    }

    private void SetMemoryLevel(CoreWebView2MemoryUsageTargetLevel level)
    {
        try { if (browser.CoreWebView2 is { } core) core.MemoryUsageTargetLevel = level; }
        catch (Exception e) when (e is NotImplementedException or COMException or InvalidOperationException) { }
    }

    private void NotifyProblem(string message)
    {
        ready = false; attention = true; UpdateSend();
        if (MessageText.Text == message) return;
        MessageText.Text = message; StatusText.Text = "点击回复 · 需要时可用“网页登录”检查";
        Present();
    }

    private async Task ConnectAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexQuotaTaskbar", "WebView2"));
            if (closed) return;
            await browser.EnsureCoreWebView2Async(environment);
            if (closed) return;
            var core = browser.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false; core.Settings.IsWebMessageEnabled = false;
            core.Settings.AreDevToolsEnabled = false; core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.ServerCertificateErrorDetected += (_, e) => e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
            core.NavigationStarting += (_, e) =>
            {
                ready = false; UpdateSend();
                if (!BrowserSubscriptionProtocol.CanNavigate(e.Uri)) e.Cancel = true;
            };
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (BrowserSubscriptionProtocol.CanNavigate(e.Uri))
                {
                    if (pageWindow.IsVisible) core.Navigate(e.Uri);
                    else NotifyProblem("Dot 需要登录或验证，可先在这里保留草稿，再点击“网页登录”。");
                }
                else NotifyProblem("此链接不在受支持的登录站点内，未打开。");
            };
            core.ProcessFailed += (_, _) => NotifyProblem("Dot 网页已断开，请关闭实时接收后重新开启。");
            SetMemoryLevel(CoreWebView2MemoryUsageTargetLevel.Low);
            core.Navigate(DotUrl);
            await DotReadPolling.RunAsync(async () =>
            {
                if (!busy) await RefreshAsync();
            }, error =>
            {
                consecutiveReadFailures++;
                lastReadError = DotReadPolling.ErrorKind(error); lastErrorUtc = DateTime.UtcNow;
                NotifyProblem("暂时无法读取 Dot，正在继续检查；不会重发，草稿已保留。");
                WriteDiagnostics(force: true);
            }, () => closed, lifetime.Token);
        }
        catch (OperationCanceledException) when (closed) { }
        catch (WebView2RuntimeNotFoundException) { if (!closed) NotifyProblem("需要 Microsoft WebView2 Runtime 才能连接 Dot。"); }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            if (!closed)
            {
                lastReadError = DotReadPolling.ErrorKind(ex); lastErrorUtc = DateTime.UtcNow;
                NotifyProblem("Dot 暂时无法连接，请关闭实时接收后重试。");
                WriteDiagnostics(force: true);
            }
        }
    }

    private async Task<JsonDocument> CallAsync(string action, string text = "")
    {
        var probe = new { pendingText, beforeIds = beforeSend.ToArray(), baselineComplete = pendingBaselineComplete };
        var json = await browser.ExecuteScriptAsync($"{script}({JsonSerializer.Serialize(action)}, {JsonSerializer.Serialize(text)}, {JsonSerializer.Serialize(probe)})");
        if (closed) throw new OperationCanceledException(lifetime.Token);
        return JsonDocument.Parse(json);
    }

    private async Task RefreshAsync()
    {
        busy = true;
        try
        {
            using var result = await CallAsync("read");
            ApplyReadResult(result.RootElement);
        }
        finally { busy = false; if (!closed) { UpdateSend(); WriteDiagnostics(); } }
    }

    internal void ApplyReadResult(JsonElement root)
    {
            var previousMessageId = tracker.Latest?.Id;
            var newReply = false;
            var state = ReadBridgeString(root, "state");
            bridgeDiagnostic = DotBridgeDiagnostic.Read(root);
            receiptStructureReady = bridgeDiagnostic.BaselineComplete;
            if (root.TryGetProperty("draftEmpty", out var draftEmpty)) webDraftPresent = !draftEmpty.GetBoolean();
            ready = state == "ready";
            if (root.TryGetProperty("messages", out var list))
            {
                if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 12) throw new JsonException("Invalid message window");
                messages = list.EnumerateArray().Select(m => new DotMessage(
                    ReadBridgeString(m, "id"), ReadBridgeString(m, "role"), ReadBridgeString(m, "text"),
                    DateTimeOffset.TryParse(ReadBridgeString(m, "time"), out var time) ? time : null,
                    m.TryGetProperty("confirmable", out var confirmable) && confirmable.GetBoolean(),
                    ReadSource(m, "idSource"), ReadSource(m, "roleSource"), ReadSource(m, "textSource"))).ToArray();
                if (tracker.Observe(messages) is { } message)
                {
                    newReply = message.Id != previousMessageId;
                    attention = false; MessageText.Text = message.Text;
                    if (pendingText is null) StatusText.Text = "点击回复";
                    Present();
                }
            }
            else messages = [];
            CheckPendingSend();
            if (newReply) ExpandReplyForNewMessage();
            if (ready && !needsReview)
            {
                if (attention || MessageText.Text == "正在连接 Dot…")
                {
                    attention = false;
                    MessageText.Text = "Dot 已连接，有新消息会在这里告诉你。";
                    StatusText.Text = "点击回复";
                    if (Composer.Visibility != Visibility.Visible) { wanted = false; Hide(); }
                }
            }
            else if (state == "page_required" && opened.Elapsed.TotalSeconds > 20 && !needsReview)
                NotifyProblem("Dot 需要登录、验证，或网页结构已变化。");
            lastReadUtc = DateTime.UtcNow; consecutiveReadFailures = 0;
            UpdateSend(); WriteDiagnostics();
    }

    private static string ReadSource(JsonElement value, string name) =>
        value.TryGetProperty(name, out var source) ? DotBridgeDiagnostic.SafeSource(source.GetString()) : "unknown";

    private static string ReadBridgeString(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String)
            throw new JsonException("Invalid bridge field");
        return field.GetString()!;
    }

    private void CheckPendingSend()
    {
        if (pendingText is null) return;
        var expected = pendingText;
        var normalized = Normalize(expected);
        var candidates = messages.Where(m => pendingBaselineComplete && m.Confirmable && !string.IsNullOrWhiteSpace(m.Id) && m.Role == "user" &&
            !beforeSend.Contains(m.Id) && Normalize(m.Text) == normalized).Take(2).ToArray();
        if (candidates.Length == 1 && messages.Count(m => m.Id == candidates[0].Id) == 1)
        {
            // Text equality alone must not erase a newer edit that happens to have identical text.
            receiptNativeDraftRevision = draftRevision; receiptPendingDraftRevision = pendingDraftRevision;
            draftClearedOnReceipt = Input.Text == expected && draftRevision == pendingDraftRevision;
            confirmedTextLength = expected.Length; confirmedUtc = DateTime.UtcNow;
            if (draftClearedOnReceipt == true) Input.Clear();
            confirmedIdHash = DotBridgeDiagnostic.HashId(candidates[0].Id); attemptState = "confirmed";
            pendingText = null; beforeSend.Clear(); needsReview = false;
            pendingBaselineComplete = false;
            if (Input.Text.Length == 0) Composer.Visibility = Visibility.Collapsed;
            StatusText.Text = "已发送，等待 Dot 回复…";
            WriteDiagnostics(force: true);
            return;
        }
        if (!needsReview && DateTime.UtcNow - pendingSince > TimeSpan.FromSeconds(30))
        {
            // A timeout is not evidence of failure. Keep this attempt associated with later observations.
            needsReview = true;
            attemptState = "unknown";
            NotifyProblem("未确认发送结果，请在网页检查后再操作，避免重复发送。");
        }
    }

    internal async Task SendAsync()
    {
        if (!CanSend()) return;
        CancelAutoReply();
        busy = true; UpdateSend();
        var text = Input.Text;
        var revisionAtStart = draftRevision;
        attemptId = Guid.NewGuid().ToString("N"); attemptState = "preparing"; confirmedIdHash = null;
        attemptStartedUtc = DateTime.UtcNow; confirmedUtc = null; confirmedTextLength = null;
        draftClearedOnReceipt = null; receiptNativeDraftRevision = receiptPendingDraftRevision = null;
        dismissCountAtAttempt = dismissCount; replyCountAtAttempt = replyCount;
        beforeSend.Clear(); pendingBaselineComplete = false;
        WriteDiagnostics(force: true);
        try
        {
            using var prepared = await CallAsync("prepare", text);
            var prepareState = ReadBridgeString(prepared.RootElement, "state");
            if (prepareState != "prepared" || !ReadBaseline(prepared.RootElement))
            {
                if (prepareState == "draft_present") webDraftPresent = true;
                attemptState = "not_dispatched";
                NotifyProblem("尚未发送：网页有草稿、正在回复或暂时无法核验回执。内容已保留，可用网页登录检查。");
                return;
            }
            await Task.Delay(500, lifetime.Token);
            // Preserve the attempt before crossing the boundary that may click Send on the webpage.
            pendingText = text; pendingSince = DateTime.UtcNow; pendingDraftRevision = revisionAtStart;
            attemptState = "dispatching"; WriteDiagnostics(force: true);
            using var submitted = await CallAsync("submit", text);
            ApplySubmitResult(submitted.RootElement);
        }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or JsonException)
        {
            if (!closed)
            {
                // A prepare exception cannot have crossed Submit. A submit exception may have clicked Send.
                if (pendingText is not null) { needsReview = true; attemptState = "unknown"; pendingBaselineComplete = false; }
                else attemptState = "not_dispatched";
                lastReadError = DotReadPolling.ErrorKind(ex); lastErrorUtc = DateTime.UtcNow;
                NotifyProblem(pendingText is null ? "尚未发送：输入准备未完成，草稿已保留。" : "发送结果不确定，正在继续核对；不会自动重发。");
            }
        }
        finally { busy = false; if (!closed) { UpdateSend(); WriteDiagnostics(force: true); } }
    }

    internal void ApplySubmitResult(JsonElement result)
    {
        var state = ReadBridgeString(result, "state");
        if (state == "dispatched")
        {
            pendingBaselineComplete = ReadBaseline(result);
            webDraftPresent = false; attemptState = "dispatched";
            StatusText.Text = "正在确认发送…";
        }
        else if (state is "not_sent" or "page_required" or "invalid_text" or "receiving" or "receipt_unavailable" or "invalid_action")
        {
            // These bridge results are defined before the only .click() boundary. No automatic retry.
            pendingText = null; beforeSend.Clear(); pendingBaselineComplete = false; needsReview = false;
            attemptState = "not_dispatched"; webDraftPresent = true;
            NotifyProblem("尚未发送：网页输入或回执条件不满足，文字已保留。可用网页登录检查。");
        }
        else
        {
            needsReview = true; attemptState = "unknown"; pendingBaselineComplete = false;
            NotifyProblem("发送结果不确定，正在继续核对；不会自动重发。");
        }
    }

    private bool ReadBaseline(JsonElement root)
    {
        if (!root.TryGetProperty("baselineComplete", out var complete) || !complete.GetBoolean() ||
            !root.TryGetProperty("beforeIds", out var ids) || ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() > 2048) return false;
        var current = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids.EnumerateArray())
        {
            if (id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()) || id.GetString()!.Length > 256 || !current.Add(id.GetString()!)) return false;
        }
        beforeSend.Clear(); beforeSend.UnionWith(current);
        pendingBaselineComplete = true;
        return true;
    }

    private static string Normalize(string value) => string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private bool CanSend() => !closed && !busy && ready && receiptStructureReady && !needsReview && !webDraftPresent && !pageWindow.IsVisible && pendingText is null && !string.IsNullOrWhiteSpace(Input.Text);
    private void UpdateSend()
    {
        SendButton.IsEnabled = CanSend();
        if (pendingText is not null)
            StatusText.Text = needsReview ? (draftRevision == pendingDraftRevision ? "发送结果待核对 · 草稿已保留" : "发送结果待核对 · 新草稿已保留") : "正在确认发送…";
        else if (webDraftPresent && Composer.Visibility == Visibility.Visible)
            StatusText.Text = "网页有现有草稿 · 请用网页登录处理，原生草稿已保留";
        else if (!receiptStructureReady && Composer.Visibility == Visibility.Visible)
            StatusText.Text = "当前网页回执结构暂不可核验 · 尚未发送，可用网页登录检查";
        WriteDiagnostics();
    }

    internal void Stop()
    {
        if (closed) return;
        CancelAutoReply();
        closed = true; lifetime.Cancel(); ownerWatch.Stop();
        browser.Dispose(); pageWindow.Close();
        Input.Clear(); pendingText = null; messages = []; beforeSend.Clear();
        parent?.Dispose(); Close();
    }
}
