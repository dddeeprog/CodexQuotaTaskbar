using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexQuotaTaskbar.Core.Provider;
using CodexQuotaTaskbar.Core.Quota;
using CodexQuotaTaskbar.Host.Resets;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;
public sealed class ResetCreditUseDetailsTests
{
    [Theory]
    [InlineData("final-request")]
    [InlineData("final-close")]
    [InlineData("preparing")]
    [InlineData("after-request")]
    public void Closing_revokes_even_when_checking_has_cleared_the_ui_token(string mode) => Sta(async () =>
    {
        using var fixture = new Fixture();
        var window = fixture.Window();
        try
        {
            if (mode != "preparing") await window.PrepareResetCreditUseAsync(false);
            Task? work = null;
            if (mode != "after-request")
            {
                fixture.Transport.ReadGate = new();
                work = mode == "preparing" ? window.PrepareResetCreditUseAsync(false)
                    : fixture.Service.ConfirmAsync(fixture.Service.View.Confirmation!.Token);
                Assert.Equal(ResetCreditUseState.Checking, fixture.Service.View.State);
            }
            if (mode == "final-close") window.Close(); else window.RequestClose();
            Assert.False(Named<Button>(window, "ConfirmResetCreditUseButton").IsEnabled);
            Click(window, "ConfirmResetCreditUseButton");
            if (work is not null) { fixture.Transport.ReadGate!.SetResult(fixture.Transport.Context); await work; }
            await window.PrepareResetCreditUseAsync(false);
            Assert.Null(fixture.Service.View.Confirmation);
            Assert.Equal(0, fixture.Transport.Sends);
            Assert.False(File.Exists(fixture.Path));
            AssertUnshown(window);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Confirmation_warns_and_cancel_sends_nothing() => Sta(async () =>
    {
        using var fixture = new Fixture(); var window = fixture.Window();
        try
        {
            await window.PrepareResetCreditUseAsync(false);
            var text = Named<TextBlock>(window, "ResetCreditUseConfirmationText").Text;
            foreach (var expected in new[] { "synthetic@example.invalid", "消耗1张", "符合条件", "不保证所有额度", "不可撤销", "服务端选择", "最早到期" }) Assert.Contains(expected, text);
            Assert.False(Named<Button>(window, "ConfirmResetCreditUseButton").IsDefault);
            Click(window, "CancelResetCreditUseButton");
            Assert.Null(fixture.Service.View.Confirmation); Assert.Equal(0, fixture.Transport.Sends);
            Assert.False(File.Exists(fixture.Path)); AssertUnshown(window);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Per_card_action_prepares_confirmation_and_panel_refresh_only_requests_a_read() => Sta(async () =>
    {
        using var fixture = new Fixture();
        fixture.Transport.Context = fixture.Transport.Context with
        {
            Cards = new(3, Enumerable.Range(0, 3).Select(_ => new QuotaResetCredit("codexRateLimits", "available", DateTimeOffset.UtcNow.AddDays(30))).ToArray(), DateTimeOffset.UtcNow),
        };
        var window = fixture.Window();
        try
        {
            Assert.Equal(3, UseButtons(window).Count());
            var button = UseButtons(window).Last();
            Assert.Equal("使用重置卡", button.Content);
            Assert.True(button.IsEnabled);
            Assert.Equal(Visibility.Collapsed, Named<TextBlock>(window, "ResetCreditUseStatusText").Visibility);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.NotNull(fixture.Service.View.Confirmation);
            Assert.All(UseButtons(window), b => Assert.False(b.IsEnabled));
            Click(window, "CancelResetCreditUseButton");
            var refreshes = 0;
            window.RefreshRequested += (_, _) => ++refreshes;
            Click(window, "RefreshButton");
            Assert.Equal(1, refreshes);
            Assert.Equal(0, fixture.Transport.Sends);
            window.Apply(QuotaSnapshot.Unavailable("synthetic account change"));
            Assert.Empty(UseButtons(window));
            Assert.Null(window.FindName("CheckResetCreditUseButton"));
            Assert.Null(window.FindName("UseResetCreditButton"));
            await Task.CompletedTask;
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Sent_attempt_survives_close_and_reopening_shares_its_lock() => Sta(async () =>
    {
        using var fixture = new Fixture(); var first = fixture.Window();
        await first.PrepareResetCreditUseAsync(false);
        fixture.Transport.SendGate = new();
        var sending = fixture.Service.ConfirmAsync(fixture.Service.View.Confirmation!.Token);
        first.Close(); var reopened = fixture.Window();
        try
        {
            Assert.Equal(ResetCreditUseState.Sending, fixture.Service.View.State);
            Assert.All(UseButtons(reopened), button => Assert.False(button.IsEnabled));
            await reopened.PrepareResetCreditUseAsync(false); Assert.Equal(1, fixture.Transport.Sends);
            fixture.Transport.SendGate.SetResult("unknown"); await sending;
            Assert.True(Named<Button>(reopened, "RetryResetCreditUseButton").IsEnabled);
            await reopened.PrepareResetCreditUseAsync(true);
            Assert.Contains("仍可能消耗1张", Named<TextBlock>(reopened, "ResetCreditUseConfirmationText").Text);
            Assert.Equal(1, fixture.Transport.Sends); AssertUnshown(reopened);
        }
        finally { reopened.Close(); }
    });

    [Theory]
    [InlineData(96)] [InlineData(120)] [InlineData(144)] [InlineData(192)]
    public void Offscreen_use_states_wrap_and_scroll_without_covering_footer(int dpi) => Sta(async () =>
    {
        foreach (var state in new[] { "idle", "zero", "confirm", "retry", "sending", "unknown", "completed-refresh-failed", "blocked" })
        {
            using var fixture = new Fixture(state == "blocked");
            fixture.Transport.Context = fixture.Transport.Context with { DisplayName = "synthetic-long-account-label@example.invalid · 合成验收工作区（不代表真实账号） · 账号标识 aaaaaaaa" };
            if (state == "zero") fixture.Transport.Context = fixture.Transport.Context with { Cards = new(0, [], DateTimeOffset.UtcNow) };
            Task? inFlight = null;
            if (state is "retry" or "unknown")
            {
                fixture.Transport.Outcome = "unknown";
                await fixture.Service.ConfirmAsync((await fixture.Service.PrepareConfirmationAsync(false))!.Token);
                if (state == "retry") await fixture.Service.PrepareConfirmationAsync(true);
            }
            if (state is "confirm" or "sending" or "completed-refresh-failed")
            {
                var confirmation = await fixture.Service.PrepareConfirmationAsync(false);
                if (state == "sending") { fixture.Transport.SendGate = new(); inFlight = fixture.Service.ConfirmAsync(confirmation!.Token); }
                if (state == "completed-refresh-failed") { fixture.Transport.RefreshFails = true; await fixture.Service.ConfirmAsync(confirmation!.Token); }
            }
            var window = fixture.Window();
            try
            {
                window.SetMaterialTransparency(0);
                var root = (FrameworkElement)window.Content;
                using var source = new HwndSource(new HwndSourceParameters("ResetUseOffscreenDpi") { Width = 0, Height = 0, WindowStyle = 0 });
                VisualTreeHelper.SetRootDpi(root, new DpiScale(dpi / 96d, dpi / 96d));
                root.Measure(new Size(360, 552)); root.Arrange(new Rect(0, 0, 360, Math.Min(552, root.DesiredSize.Height))); root.UpdateLayout();
                var scroll = Named<ScrollViewer>(window, "DetailsScroller");
                var footerButton = Named<Button>(window, "RefreshButton");
                var footer = footerButton.TransformToAncestor(root).TransformBounds(new Rect(footerButton.RenderSize));
                var bounds = scroll.TransformToAncestor(root).TransformBounds(new Rect(scroll.RenderSize));
                Assert.True(bounds.Bottom <= footer.Top); Assert.True(footer.Bottom <= root.ActualHeight + 0.01); AssertUnshown(window);
                if (state is "completed-refresh-failed" or "blocked" or "unknown") Assert.All(UseButtons(window), b => Assert.False(b.IsEnabled));
                if (state == "zero")
                {
                    Assert.Empty(UseButtons(window));
                    Assert.Equal(Visibility.Collapsed, Named<StackPanel>(window, "ResetCreditUseActions").Visibility);
                    Assert.Equal("当前没有可用重置卡", Named<TextBlock>(window, "ResetCreditsStatusText").Text);
                }
                Assert.True(footerButton.IsEnabled);
                Export(root, dpi, state, "top"); scroll.ScrollToEnd(); root.UpdateLayout();
                if (state is "confirm" or "retry")
                {
                    var text = Named<TextBlock>(window, "ResetCreditUseConfirmationText");
                    Assert.True(text.ActualWidth <= scroll.ViewportWidth + 0.1); Assert.True(text.ActualHeight > 5 * text.FontSize); Assert.True(scroll.ScrollableHeight > 0);
                }
                Export(root, dpi, state, "bottom");
                var output = PreviewDirectory();
                if (output is not null)
                {
                    using var stream = new FileStream(Path.Combine(output, $"use-{dpi}-{state}.json"), FileMode.CreateNew);
                    JsonSerializer.Serialize(stream, new { dpi, state, synthetic = true, visible = window.IsVisible, hwnd = 0,
                        serviceState = fixture.Service.View.State.ToString(), status = Named<TextBlock>(window, "ResetCreditUseStatusText").Text,
                        useEnabled = UseButtons(window).Any(b => b.IsEnabled),
                        scrollHeight = scroll.ViewportHeight, scrollExtent = scroll.ExtentHeight, footerTop = footer.Top,
                        scrollBottom = bounds.Bottom, footerBottom = footer.Bottom, height = root.ActualHeight, width = root.ActualWidth }, new JsonSerializerOptions { WriteIndented = true });
                }
            }
            finally { window.Close(); if (inFlight is not null) { fixture.Transport.SendGate!.SetResult("unknown"); await inFlight; } }
        }
    });
    private static T Named<T>(Window window, string name) where T : class => Assert.IsType<T>(window.FindName(name));
    private static IEnumerable<Button> UseButtons(Window window) => Named<StackPanel>(window, "ResetCreditsRows").Children
        .OfType<Grid>().SelectMany(row => row.Children.OfType<Button>());
    private static void Click(Window window, string name) => Named<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void AssertUnshown(Window window) { Assert.Equal(nint.Zero, new WindowInteropHelper(window).Handle); Assert.False(window.IsVisible); }
    private static string? PreviewDirectory()
    {
        var output = Environment.GetEnvironmentVariable("DIANDAO_RESET_USE_PREVIEW"); if (string.IsNullOrEmpty(output)) return null;
        var full = Path.GetFullPath(output); var allowed = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/reliability/reset-use/previews/accepted"));
        if (!string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), allowed.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Preview output must remain in this task's reserved directory.");
        Directory.CreateDirectory(full); return full;
    }
    private static void Export(FrameworkElement root, int dpi, string state, string position)
    {
        var output = PreviewDirectory(); if (output is null) return;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(360 * dpi / 96d), (int)Math.Ceiling((root.ActualHeight + root.Margin.Top + root.Margin.Bottom) * dpi / 96d), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(root); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(Path.Combine(output, $"use-{dpi}-{state}-{position}.png"), FileMode.CreateNew); encoder.Save(stream);
    }
    private static void Sta(Func<Task> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext()); var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () => { try { await action(); } catch (Exception exception) { failure = exception; } finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(40)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private sealed class FakeTransport : IResetCreditUseTransport
    {
        internal ResetCreditUseContext Context = new(new string('a', 64), "synthetic-connection:1", "synthetic@example.invalid · 合成工作区", new(3, null, DateTimeOffset.UtcNow), true);
        internal TaskCompletionSource<ResetCreditUseContext?>? ReadGate;
        internal TaskCompletionSource<string>? SendGate;
        internal int Sends; internal string Outcome = "reset"; internal bool RefreshFails;
        public Task<ResetCreditUseContext?> ReadContextAsync(CancellationToken cancellationToken) => ReadGate?.Task ?? Task.FromResult<ResetCreditUseContext?>(Context);
        public Task<string> ConsumeAsync(ResetCreditUseContext context, string requestKey, CancellationToken cancellationToken) { ++Sends; return SendGate?.Task ?? Task.FromResult(Outcome); }
        public Task RefreshAsync(CancellationToken cancellationToken) => RefreshFails ? Task.FromException(new IOException("synthetic refresh failure")) : Task.CompletedTask;
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodexQuotaTaskbar-tests", "reset-use-ui", Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(directory, "pending.json"); internal readonly FakeTransport Transport = new(); internal readonly ResetCreditUseService Service;
        internal Fixture(bool blocked = false) { Directory.CreateDirectory(directory); if (blocked) File.WriteAllText(Path, "synthetic invalid record"); Service = new(Transport, new ResetCreditPendingStore(Path)); }
        internal QuotaPopoverWindow Window()
        {
            var window = new QuotaPopoverWindow();
            var snapshot = AppServerRateLimitsParser.Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":25,\"windowDurationMins\":300,\"resetsAt\":1893606400},\"secondary\":{\"usedPercent\":50,\"windowDurationMins\":10080}},\"rateLimitResetCredits\":{\"availableCount\":3,\"credits\":null}}", DateTimeOffset.UtcNow, "Plus");
            window.Apply(snapshot with { ResetCredits = Transport.Context.Cards }); window.SetResetCreditUseService(Service); return window;
        }
        public void Dispose() { Service.Dispose(); Directory.Delete(directory, true); }
    }
}
