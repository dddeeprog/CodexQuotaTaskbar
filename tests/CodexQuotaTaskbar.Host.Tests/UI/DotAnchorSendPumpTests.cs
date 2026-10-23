using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodexQuotaTaskbar.Host.Lifetime;

namespace CodexQuotaTaskbar.Host.Tests.Lifetime;

// Fake timer callbacks and an in-memory transport only. No process, native window, UI drag or wall-clock pacing.
public sealed class DotAnchorSendPumpTests
{
    private static DotAnchor Anchor(int value) => new(value, value + 10, value + 40,
        -4096, -2048, 8192, 4096, 96, true, 15);

    [Fact]
    public async Task Start_without_any_anchor_stays_idle()
    {
        var timer = new TimerProbe();
        var calls = new List<DotAnchor>();
        var pump = new DotAnchorSendPump(value => { calls.Add(value); return Task.FromResult(true); }, timer.Set);
        pump.Start();
        await pump.FlushAsync();
        Assert.Empty(calls);
        Assert.False(timer.Active);
    }

    [Fact]
    public async Task Inactive_updates_are_retained_and_successful_unchanged_anchor_returns_to_idle()
    {
        var timer = new TimerProbe();
        var calls = new List<DotAnchor>();
        var pump = new DotAnchorSendPump(value => { calls.Add(value); return Task.FromResult(true); }, timer.Set);
        pump.Update(Anchor(1));
        pump.Update(Anchor(2));
        Assert.DoesNotContain(true, timer.Calls);
        Assert.Empty(calls);
        pump.Start();
        Assert.True(timer.Active);
        await pump.FlushAsync();
        Assert.Equal(new[] { Anchor(2) }, calls);
        Assert.False(timer.Active);
        await pump.FlushAsync();
        pump.Update(Anchor(2));
        Assert.False(timer.Active);
        Assert.Single(calls);
        pump.Update(Anchor(3));
        Assert.True(timer.Active);
        await pump.FlushAsync();
        Assert.Equal(new[] { Anchor(2), Anchor(3) }, calls);
        Assert.False(timer.Active);
    }

    [Fact]
    public async Task Two_simulated_seconds_of_high_rate_updates_send_only_the_latest_at_each_fake_tick()
    {
        var timer = new TimerProbe();
        var calls = new List<DotAnchor>();
        var pump = new DotAnchorSendPump(value => { calls.Add(value); return Task.FromResult(true); }, timer.Set);
        pump.Update(Anchor(0));
        pump.Start();
        // 1000 updates/s for 2 s, with a caller-controlled tick every 16 simulated ms.
        for (var millisecond = 0; millisecond < 2000; millisecond++)
        {
            pump.Update(Anchor(millisecond));
            if (millisecond % 16 != 0) continue;
            Assert.True(timer.Active);
            await pump.FlushAsync();
            Assert.Equal(Anchor(millisecond), calls[^1]);
            Assert.False(timer.Active);
        }
        Assert.True(timer.Active);
        await pump.FlushAsync();
        Assert.Equal(126, calls.Count);
        Assert.Equal(Anchor(1999), calls[^1]);
        Assert.Equal(calls.Count, calls.Distinct().Count());
        Assert.False(timer.Active);
    }

    [Fact]
    public async Task One_inflight_send_coalesces_busy_updates_without_arming_or_queueing_writes()
    {
        var timer = new TimerProbe();
        var calls = new List<DotAnchor>();
        var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximumActive = 0;
        async Task<bool> Send(DotAnchor value)
        {
            maximumActive = Math.Max(maximumActive, Interlocked.Increment(ref active));
            calls.Add(value);
            try { return calls.Count == 1 ? await first.Task : true; }
            finally { Interlocked.Decrement(ref active); }
        }
        var pump = new DotAnchorSendPump(Send, timer.Set);
        pump.Update(Anchor(0));
        pump.Start();
        var sending = pump.FlushAsync();
        Assert.Single(calls);
        Assert.False(timer.Active);
        timer.Calls.Clear();
        var duplicateTicks = new List<Task>();
        for (var i = 1; i <= 2000; i++)
        {
            pump.Update(Anchor(i));
            if (i % 16 == 0) duplicateTicks.Add(pump.FlushAsync());
        }
        Assert.Single(calls);
        Assert.DoesNotContain(true, timer.Calls);
        first.SetResult(true);
        await sending;
        await Task.WhenAll(duplicateTicks);
        Assert.Single(calls);
        Assert.True(timer.Active);
        await pump.FlushAsync();
        Assert.Equal(new[] { Anchor(0), Anchor(2000) }, calls);
        Assert.Equal(1, maximumActive);
        Assert.False(timer.Active);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Gate_busy_result_retries_the_latest_and_does_not_mark_it_successful(bool updateBeforeRetry)
    {
        var timer = new TimerProbe();
        var calls = new List<DotAnchor>();
        var pump = new DotAnchorSendPump(value =>
        {
            calls.Add(value);
            return Task.FromResult(calls.Count != 1);
        }, timer.Set);
        pump.Update(Anchor(1));
        pump.Start();
        await pump.FlushAsync();
        Assert.True(timer.Active);
        if (updateBeforeRetry) pump.Update(Anchor(9));
        await pump.FlushAsync();
        Assert.Equal(new[] { Anchor(1), Anchor(updateBeforeRetry ? 9 : 1) }, calls);
        Assert.False(timer.Active);
        await pump.FlushAsync();
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public async Task Stop_prevents_old_completion_or_inactive_updates_from_rescheduling()
    {
        var timer = new TimerProbe();
        var calls = new List<DotAnchor>();
        var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pump = new DotAnchorSendPump(value =>
        {
            calls.Add(value);
            return calls.Count == 1 ? first.Task : Task.FromResult(true);
        }, timer.Set);
        pump.Update(Anchor(1));
        pump.Start();
        var sending = pump.FlushAsync();
        pump.Stop();
        timer.Calls.Clear();
        pump.Update(Anchor(2));
        first.SetResult(true);
        await sending;
        await pump.FlushAsync();
        Assert.DoesNotContain(true, timer.Calls);
        Assert.False(timer.Active);
        Assert.Single(calls);
        pump.Start();
        Assert.True(timer.Active);
        await pump.FlushAsync();
        Assert.Equal(new[] { Anchor(1), Anchor(2) }, calls);
        Assert.False(timer.Active);
    }

    [Fact]
    public async Task Restart_keeps_one_inflight_and_old_generation_cannot_mark_the_same_anchor_sent()
    {
        var timer = new TimerProbe();
        var calls = new List<DotAnchor>();
        var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximumActive = 0;
        async Task<bool> Send(DotAnchor value)
        {
            maximumActive = Math.Max(maximumActive, Interlocked.Increment(ref active));
            calls.Add(value);
            try { return calls.Count == 1 ? await first.Task : true; }
            finally { Interlocked.Decrement(ref active); }
        }
        var pump = new DotAnchorSendPump(Send, timer.Set);
        pump.Update(Anchor(1));
        pump.Start();
        var oldSending = pump.FlushAsync();
        pump.Stop();
        pump.Start();
        // Same value deliberately: Start must establish a new generation, not inherit old success.
        var tickWhileOldSendPending = pump.FlushAsync();
        Assert.Single(calls);
        first.SetResult(true);
        await oldSending;
        await tickWhileOldSendPending;
        Assert.Single(calls);
        Assert.True(timer.Active);
        await pump.FlushAsync();
        Assert.Equal(new[] { Anchor(1), Anchor(1) }, calls);
        Assert.Equal(1, maximumActive);
        Assert.False(timer.Active);
        pump.Update(Anchor(1));
        Assert.False(timer.Active);
    }

    private sealed class TimerProbe
    {
        internal bool Active { get; private set; }
        internal List<bool> Calls { get; } = [];
        internal void Set(bool active) { Active = active; Calls.Add(active); }
    }
}

public sealed class DotAnchorMailboxTests
{
    private static DotAnchor Anchor(int value) => new(value, value + 10, value + 40,
        -4096, -2048, 8192, 4096, 96, true, 15);

    [Fact]
    public void High_rate_posts_keep_one_dispatcher_callback_and_apply_only_latest()
    {
        var scheduler = new ManualScheduler();
        var applied = new List<DotAnchor>();
        var mailbox = new DotAnchorMailbox(scheduler.Schedule, applied.Add);
        for (var i = 0; i < 2000; i++) mailbox.Post(Anchor(i));
        Assert.Equal(1, scheduler.Count);
        scheduler.Drain();
        Assert.Equal(new[] { Anchor(1999) }, applied);
        Assert.Equal(1, scheduler.MaximumQueued);
        Assert.Equal(0, scheduler.Count);
        mailbox.Post(Anchor(2000));
        scheduler.Drain();
        Assert.Equal(new[] { Anchor(1999), Anchor(2000) }, applied);
    }

    [Fact]
    public void Posts_during_apply_are_coalesced_without_losing_the_final_anchor()
    {
        var scheduler = new ManualScheduler();
        var applied = new List<DotAnchor>();
        DotAnchorMailbox? mailbox = null;
        mailbox = new DotAnchorMailbox(scheduler.Schedule, value =>
        {
            applied.Add(value);
            if (value != Anchor(0)) return;
            mailbox!.Post(Anchor(1));
            mailbox.Post(Anchor(2));
        });
        mailbox.Post(Anchor(0));
        scheduler.Drain();
        Assert.Equal(new[] { Anchor(0), Anchor(2) }, applied);
        Assert.Equal(1, scheduler.MaximumQueued);
        Assert.Equal(0, scheduler.Count);
    }

    [Fact]
    public void Clear_is_a_reply_barrier_and_new_posts_reuse_the_single_queued_callback()
    {
        var scheduler = new ManualScheduler();
        var applied = new List<DotAnchor>();
        var mailbox = new DotAnchorMailbox(scheduler.Schedule, applied.Add);
        mailbox.Post(Anchor(1));
        mailbox.Clear();
        // Represents the reliable reply command applying its own anchor on the UI thread.
        applied.Add(Anchor(99));
        mailbox.Post(Anchor(2));
        Assert.Equal(1, scheduler.Count);
        scheduler.Drain();
        Assert.Equal(new[] { Anchor(99), Anchor(2) }, applied);
        Assert.Equal(1, scheduler.MaximumQueued);
    }

    [Fact]
    public void Clear_without_a_new_post_makes_the_old_callback_a_noop_and_allows_later_posts()
    {
        var scheduler = new ManualScheduler();
        var applied = new List<DotAnchor>();
        var mailbox = new DotAnchorMailbox(scheduler.Schedule, applied.Add);
        mailbox.Post(Anchor(1));
        mailbox.Clear();
        scheduler.Drain();
        Assert.Empty(applied);
        Assert.Equal(0, scheduler.Count);
        mailbox.Post(Anchor(2));
        scheduler.Drain();
        Assert.Equal(new[] { Anchor(2) }, applied);
        Assert.Equal(1, scheduler.MaximumQueued);
    }

    private sealed class ManualScheduler
    {
        private readonly Queue<Action> actions = new();
        internal int Count => actions.Count;
        internal int MaximumQueued { get; private set; }
        internal void Schedule(Action callback)
        {
            actions.Enqueue(callback);
            MaximumQueued = Math.Max(MaximumQueued, actions.Count);
        }
        internal void Drain()
        {
            var iterations = 0;
            while (actions.TryDequeue(out var callback))
            {
                Assert.True(++iterations <= 8, "Mailbox did not drain within a bounded number of callbacks.");
                callback();
            }
        }
    }
}
