using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CodexQuotaTaskbar.Host.Settings;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

[Collection("Native UI isolation")]
public sealed class MenuTraceWindowTests
{
    [Fact]
    public void Enabled_observer_only_attaches_inside_episode_and_close_removes_subscriptions() => Sta(menu =>
    {
        using var session = new MenuTraceSession(true);
        using var observer = new MenuTraceWindowObserver(menu, session);
        Assert.False(Attached(observer));
        observer.Begin();
        Assert.True(Attached(observer));
        Assert.Equal(nint.Zero, new WindowInteropHelper(menu).Handle);
        Assert.False(menu.IsVisible); Assert.False(menu.IsLoaded);
        menu.Close();
        Assert.False(Attached(observer));
    });

    [Fact]
    public void Background_deadline_revokes_sampling_while_dispatcher_is_blocked_then_cleans_up() => Sta(menu =>
    {
        using var session = new MenuTraceSession(true);
        using var observer = new MenuTraceWindowObserver(menu, session);
        observer.Begin();
        Assert.True(Attached(observer));
        // No Dispatcher frames or CompositionTarget.Rendering notifications occur here.
        Thread.Sleep(750);
        Assert.False(observer.Active);
        Assert.True(Attached(observer));
        Pump();
        Assert.False(Attached(observer));
        Assert.Equal(nint.Zero, new WindowInteropHelper(menu).Handle);
        Assert.False(menu.IsVisible); Assert.False(menu.IsLoaded);
    });

    [Fact]
    public void Observer_stop_cleans_up_immediately_and_old_cleanup_cannot_detach_new_episode() => Sta(menu =>
    {
        using var session = new MenuTraceSession(true);
        using var observer = new MenuTraceWindowObserver(menu, session);
        observer.Begin(); observer.Stop(MenuTraceStop.Hidden);
        Assert.False(Attached(observer));
        observer.Begin(); Pump();
        Assert.True(observer.Active); Assert.True(Attached(observer));
        observer.Stop(MenuTraceStop.Exception);
        Assert.False(Attached(observer));
    });

    [Fact]
    public void Disabled_observer_does_not_create_a_handle_or_subscribe_to_rendering() => Sta(menu =>
    {
        Assert.False(MenuTraceSession.Local.Enabled); // ProcessPath excludes testhost even with inherited opt-in.
        using var session = new MenuTraceSession(false);
        using var observer = new MenuTraceWindowObserver(menu, session);
        observer.Begin();
        Assert.False(observer.Active); Assert.False(Attached(observer));
        Assert.Equal(nint.Zero, new WindowInteropHelper(menu).Handle);
    });

    [Fact]
    public void Real_hidden_show_entry_counts_accepted_placement_failures_and_reentry_rejects_without_budget() => Sta(menu =>
    {
        var output = new List<byte[]>();
        using var session = new MenuTraceSession(true, writer: output.Add);
        using var observer = new MenuTraceWindowObserver(menu, session);
        typeof(TrayMenuWindow).GetField("menuTrace", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(menu, observer);
        // A genuine unusable page fails at the first existing height gate, before
        // screen placement/EnsureHandle/Show. No visibility or Loaded is fabricated.
        ((FrameworkElement)menu.FindName("MenuHeader")).MinHeight = 1000;
        menu.RunPopupRequest(() => menu.ShowAt(default));
        session.FlushAsync().GetAwaiter().GetResult();
        using (var armed = JsonDocument.Parse(output[^1])) Assert.Equal(0, armed.RootElement.GetProperty("Accepted").GetInt32());
        for (var index = 0; index < 4; index++) menu.ShowAt(default);
        session.FlushAsync().GetAwaiter().GetResult();
        using var json = JsonDocument.Parse(output[^1]);
        Assert.Equal(3, json.RootElement.GetProperty("Accepted").GetInt32());
        foreach (var episode in json.RootElement.GetProperty("Episodes").EnumerateArray())
        {
            Assert.Equal("PlacementFailed", episode.GetProperty("Stop").GetString());
            Assert.False(episode.GetProperty("Shown").GetBoolean());
            var accepted = episode.GetProperty("Entries")[1];
            Assert.True(accepted.GetProperty("State").GetProperty("Preparing").GetBoolean());
        }
        Assert.False(Attached(observer));
        Assert.Equal(nint.Zero, new WindowInteropHelper(menu).Handle);
        Assert.False(menu.IsVisible); Assert.False(menu.IsLoaded);
    });

    [Fact]
    public void Original_request_exception_propagates_and_diagnostic_subscriptions_are_removed() => Sta(menu =>
    {
        using var session = new MenuTraceSession(true);
        using var observer = new MenuTraceWindowObserver(menu, session);
        typeof(TrayMenuWindow).GetField("menuTrace", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(menu, observer);
        observer.Begin();
        var expected = new InvalidOperationException("Business exception");
        var actual = Assert.Throws<InvalidOperationException>(() => menu.RunPopupRequest(() => throw expected));
        Assert.Same(expected, actual);
        Assert.False(observer.Active); Assert.False(Attached(observer));
        Assert.False(menu.IsPopupRequestInProgress);
    });

    [Theory]
    [InlineData(0x18)]
    [InlineData(0x06)]
    [InlineData(0x46)]
    [InlineData(0x47)]
    public void Own_message_observation_preserves_handled_and_returns_zero(int message) => Sta(menu =>
    {
        using var session = new MenuTraceSession(true);
        using var observer = new MenuTraceWindowObserver(menu, session);
        observer.Begin();
        var method = typeof(MenuTraceWindowObserver).GetMethod("OnMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var handled in new[] { false, true })
        {
            object[] arguments = [nint.Zero, message, (nint)1, nint.Zero, handled];
            Assert.Equal(nint.Zero, method.Invoke(observer, arguments));
            Assert.Equal(handled, arguments[4]);
        }
        Assert.Equal(nint.Zero, new WindowInteropHelper(menu).Handle);
    });

    [Theory]
    [InlineData(false, 600)]
    [InlineData(false, 601)]
    [InlineData(true, 600)]
    [InlineData(true, 601)]
    public async Task Diagnostic_read_stages_stop_before_next_getter_after_deadline(bool initial, long completedAt)
    {
        long now = 0; var output = new List<byte[]>(); var reads = new List<int>();
        using var session = new MenuTraceSession(true, () => now, 1000, output.Add, (_, _) => new CaptureLease());
        MenuTraceState Capture()
        {
            session.Read(1, () => { reads.Add(1); now = 599; return 0; });
            session.Read(1, () => { reads.Add(2); now = completedAt; return 0; });
            // Uses the same diagnostic stage boundary as the real WPF/native
            // collector: an in-flight getter may finish, but the next must not start.
            session.Read(1, () => { reads.Add(3); return 0; });
            return new MenuTraceState(NativeValue: 42);
        }
        bool retained;
        if (initial) retained = session.Begin(1, capture: Capture);
        else
        {
            Assert.True(session.Begin(1)); now = 599;
            retained = session.Observe(1, MenuTraceKind.FrameSample, Capture);
        }
        Assert.Equal(new[] { 1, 2 }, reads);
        Assert.False(retained); Assert.False(session.IsActive(1));
        await session.FlushAsync();
        using var json = JsonDocument.Parse(output[^1]);
        var episode = json.RootElement.GetProperty("Episodes")[0];
        Assert.Equal("Deadline", episode.GetProperty("Stop").GetString());
        Assert.Equal(0, episode.GetProperty("Samples").GetInt32());
        Assert.DoesNotContain(episode.GetProperty("Entries").EnumerateArray(), entry =>
            entry.GetProperty("State").GetProperty("NativeValue").GetInt32() == 42);
    }

    private sealed class CaptureLease : IDisposable { public void Dispose() { } }

    private static bool Attached(MenuTraceWindowObserver observer) =>
        (bool)typeof(MenuTraceWindowObserver).GetField("renderingAttached", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(observer)!;
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void Sta(Action<TrayMenuWindow> action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            TrayMenuWindow? menu = null;
            try { menu = new(AppSettings.Default); action(menu); }
            catch (Exception failure) { error = failure; }
            finally { menu?.Close(); dispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
