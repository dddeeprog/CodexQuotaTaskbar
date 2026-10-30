using System.Text.Json;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class MenuTraceSessionTests
{
    [Fact]
    public async Task Disabled_session_never_captures_schedules_or_writes()
    {
        var captures = 0; var schedules = 0; var writes = 0;
        using var session = new MenuTraceSession(false, writer: _ => writes++, schedule: (_, _) => { schedules++; return new Lease(); });
        Assert.False(session.Begin(1));
        session.Observe(1, MenuTraceKind.FrameSample, () => { captures++; return default; });
        await session.FlushAsync();
        Assert.Equal(0, captures); Assert.Equal(0, schedules); Assert.Equal(0, writes);
    }

    [Fact]
    public async Task Accepted_attempts_share_three_budget_and_duplicate_or_rejected_requests_do_not_consume_it()
    {
        var output = new List<byte[]>();
        using var session = new MenuTraceSession(true, writer: output.Add, schedule: (_, _) => new Lease());
        for (var generation = 1; generation <= 3; generation++)
        {
            Assert.True(session.Begin(generation));
            Assert.False(session.Begin(generation));
            session.Observe(generation, MenuTraceKind.ShowRejected, () => default);
            session.Stop(generation, MenuTraceStop.Hidden);
        }
        Assert.False(session.Begin(4));
        await session.FlushAsync();
        using var json = JsonDocument.Parse(output[^1]);
        Assert.Equal(3, json.RootElement.GetProperty("Accepted").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("Remaining").GetInt32());
        Assert.Equal("Stopped", json.RootElement.GetProperty("Status").GetString());
        Assert.Equal(4, output.Count);
    }

    [Fact]
    public async Task Deadline_rejects_boundary_before_capture_and_stops_without_another_frame()
    {
        long now = 0; Action? deadline = null; var captures = 0;
        var output = new List<byte[]>();
        using var session = new MenuTraceSession(true, () => now, 1000, output.Add,
            (_, action) => { deadline = action; return new Lease(); });
        Assert.True(session.Begin(1));
        now = 599; Assert.True(session.Observe(1, MenuTraceKind.FrameSample, () => { captures++; return default; }));
        now = 600; deadline!();
        Assert.False(session.Observe(1, MenuTraceKind.FrameSample, () => { captures++; return default; }));
        Assert.False(session.IsActive(1));
        await session.FlushAsync();
        Assert.Equal(1, captures);
        using var json = JsonDocument.Parse(output[^1]);
        Assert.Equal("Deadline", json.RootElement.GetProperty("Episodes")[0].GetProperty("Stop").GetString());
    }

    [Fact]
    public void Old_generation_callbacks_cannot_stop_or_sample_the_next_episode()
    {
        var deadlines = new List<Action>();
        using var session = new MenuTraceSession(true, schedule: (_, action) => { deadlines.Add(action); return new Lease(); });
        Assert.True(session.Begin(7)); session.Stop(7, MenuTraceStop.Hidden);
        Assert.True(session.Begin(9)); deadlines[0]();
        Assert.False(session.Observe(7, MenuTraceKind.FrameSample, () => throw new Exception("Old capture")));
        session.Stop(7, MenuTraceStop.Exception);
        Assert.True(session.IsActive(9));
    }

    [Fact]
    public async Task Record_capacity_keeps_stop_summary_and_file_is_bounded_utf8()
    {
        var output = new List<byte[]>();
        using var session = new MenuTraceSession(true, writer: output.Add, schedule: (_, _) => new Lease());
        for (var generation = 1; generation <= 3; generation++)
        {
            Assert.True(session.Begin(generation));
            for (var index = 0; index < 700; index++) session.Observe(generation, MenuTraceKind.FrameSample, () => default);
            session.Stop(generation, MenuTraceStop.Hidden);
        }
        await session.FlushAsync();
        Assert.All(output, bytes => Assert.InRange(bytes.Length, 1, 524288));
        using var json = JsonDocument.Parse(output[^1]);
        Assert.InRange(json.RootElement.GetProperty("Records").GetInt32(), 1, 512);
        foreach (var episode in json.RootElement.GetProperty("Episodes").EnumerateArray())
        {
            Assert.Equal(128, episode.GetProperty("Entries").GetArrayLength());
            Assert.True(episode.GetProperty("Dropped").GetInt32() > 0);
            Assert.Equal("Hidden", episode.GetProperty("Stop").GetString());
            Assert.True(episode.GetProperty("EndTick").GetInt64() >= episode.GetProperty("StartTick").GetInt64());
        }
    }

    [Fact]
    public async Task Writer_and_capture_exceptions_are_isolated_and_stop_only_diagnostics()
    {
        using var session = new MenuTraceSession(true, writer: _ => throw new System.IO.IOException(), schedule: (_, _) => new Lease());
        Assert.True(session.Begin(1));
        Assert.False(session.Observe(1, MenuTraceKind.FrameSample, () => throw new InvalidOperationException()));
        Assert.False(session.IsActive(1));
        await session.FlushAsync();
    }

    [Fact]
    public async Task Armed_is_written_before_any_menu_and_schema_has_only_fixed_values()
    {
        var output = new List<byte[]>();
        using var session = new MenuTraceSession(true, writer: output.Add, schedule: (_, _) => new Lease());
        await session.FlushAsync();
        using var json = JsonDocument.Parse(Assert.Single(output));
        Assert.Equal("Armed", json.RootElement.GetProperty("Status").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("Accepted").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("Samples").GetInt32());
        Assert.Equal(3, json.RootElement.GetProperty("Remaining").GetInt32());
        Assert.Empty(json.RootElement.GetProperty("Episodes").EnumerateArray());
        Assert.All(typeof(MenuTraceState).GetProperties(), property => Assert.NotEqual(typeof(string), property.PropertyType));
        Assert.All(typeof(MenuTraceInput).GetProperties(), property => Assert.NotEqual(typeof(string), property.PropertyType));
    }

    [Fact]
    public async Task Dispose_unsubscribes_once_and_stale_deadline_is_harmless()
    {
        var disposed = 0; var stopped = 0; Action? deadline = null;
        var session = new MenuTraceSession(true, schedule: (_, action) => { deadline = action; return new Lease(() => disposed++); });
        session.EpisodeStopped += _ => stopped++;
        Assert.True(session.Begin(1)); session.Dispose(); session.Dispose(); deadline!();
        Assert.False(session.IsActive(1)); Assert.Equal(1, disposed); Assert.Equal(1, stopped);
        await session.FlushAsync();
    }

    private sealed class Lease(Action? dispose = null) : IDisposable { public void Dispose() => dispose?.Invoke(); }

    [Theory]
    [InlineData(599, true)]
    [InlineData(600, false)]
    [InlineData(601, false)]
    public void Boundary_admission_is_checked_before_reading_the_window(long tick, bool expected)
    {
        long now = 0; var captures = 0;
        using var session = new MenuTraceSession(true, () => now, 1000, schedule: (_, _) => new Lease());
        Assert.True(session.Begin(1)); now = tick;
        Assert.Equal(expected, session.Observe(1, MenuTraceKind.FrameSample, () => { captures++; return default; }));
        Assert.Equal(expected ? 1 : 0, captures);
        Assert.Equal(expected, session.IsActive(1));
    }

    [Fact]
    public void Completed_generation_cannot_be_reused_by_a_late_begin_callback()
    {
        using var session = new MenuTraceSession(true, schedule: (_, _) => new Lease());
        Assert.True(session.Begin(10)); session.Stop(10, MenuTraceStop.Hidden);
        Assert.False(session.Begin(10));
        Assert.False(session.Begin(9));
        Assert.True(session.Begin(11));
    }

    [Fact]
    public async Task Frame_callbacks_only_buffer_and_snapshots_write_in_version_order()
    {
        var versions = new List<int>();
        using var firstWriterEntered = new ManualResetEventSlim();
        using var releaseFirstWriter = new ManualResetEventSlim();
        using var session = new MenuTraceSession(true, writer: bytes =>
        {
            using var json = JsonDocument.Parse(bytes);
            var accepted = json.RootElement.GetProperty("Accepted").GetInt32();
            if (accepted == 0) { firstWriterEntered.Set(); Assert.True(releaseFirstWriter.Wait(TimeSpan.FromSeconds(5))); }
            versions.Add(accepted);
        }, schedule: (_, _) => new Lease());
        Assert.True(firstWriterEntered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            Assert.True(session.Begin(1));
            for (var i = 0; i < 20; i++) Assert.True(session.Observe(1, MenuTraceKind.FrameSample, () => default));
            Assert.Empty(versions);
            session.Stop(1, MenuTraceStop.Hidden);
            Assert.True(session.Begin(2)); session.Stop(2, MenuTraceStop.Hidden);
        }
        finally { releaseFirstWriter.Set(); }
        await session.FlushAsync();
        Assert.Equal(new[] { 0, 1, 2 }, versions);
    }

    [Fact]
    public async Task Full_buffer_still_reports_an_actual_show_and_placement_failure_reports_no_show()
    {
        var output = new List<byte[]>();
        using var session = new MenuTraceSession(true, writer: output.Add, schedule: (_, _) => new Lease());
        Assert.True(session.Begin(1)); session.Stop(1, MenuTraceStop.PlacementFailed);
        Assert.True(session.Begin(2));
        for (var i = 0; i < 150; i++) session.Observe(2, MenuTraceKind.FrameSample, () => default);
        session.Observe(2, MenuTraceKind.ActualShowAfter, () => default);
        session.Stop(2, MenuTraceStop.Hidden);
        await session.FlushAsync();
        using var json = JsonDocument.Parse(output[^1]);
        var episodes = json.RootElement.GetProperty("Episodes");
        Assert.False(episodes[0].GetProperty("Shown").GetBoolean());
        Assert.Equal("PlacementFailed", episodes[0].GetProperty("Stop").GetString());
        Assert.True(episodes[1].GetProperty("Shown").GetBoolean());
    }

    [Fact]
    public void File_writer_enforces_actual_bytes_before_touching_an_existing_isolated_file()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CqtMenuTraceTests-" + Guid.NewGuid().ToString("N"));
        var path = System.IO.Path.Combine(directory, "menu-trace-fixture.json");
        var write = typeof(MenuTraceSession).GetMethod("WriteFile", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        try
        {
            var exact = new byte[524288]; exact[0] = 1;
            write.Invoke(null, [path, exact]);
            Assert.Equal(524288, new System.IO.FileInfo(path).Length);
            write.Invoke(null, [path, new byte[524289]]);
            Assert.Equal(524288, new System.IO.FileInfo(path).Length);
            Assert.Equal(1, System.IO.File.ReadAllBytes(path)[0]);
        }
        finally
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory);
        }
    }

    [Theory]
    [InlineData(100, true)]
    [InlineData(600, false)]
    public void Initial_capture_cost_is_inside_the_same_deadline(long captureCost, bool expectedActive)
    {
        long now = 0; long? armedAt = null; TimeSpan? due = null;
        using var session = new MenuTraceSession(true, () => now, 1000,
            schedule: (delay, _) => { armedAt = now; due = delay; return new Lease(); });
        var active = session.Begin(1, capture: () => { now += captureCost; return default; });
        Assert.Equal(expectedActive, active);
        Assert.NotNull(armedAt); Assert.NotNull(due);
        Assert.Equal(600, armedAt.Value + due.Value.TotalMilliseconds);
    }

    [Fact]
    public async Task Product_process_gate_rejects_testhost_even_when_opted_in()
    {
        var previous = Environment.GetEnvironmentVariable("CQT_MENU_TRACE_ONCE");
        try
        {
            Environment.SetEnvironmentVariable("CQT_MENU_TRACE_ONCE", "1");
            using var session = (MenuTraceSession)typeof(MenuTraceSession).GetMethod("CreateLocal",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, null)!;
            Assert.False(session.Enabled);
            Assert.False(session.Begin(1));
            await session.FlushAsync();
        }
        finally { Environment.SetEnvironmentVariable("CQT_MENU_TRACE_ONCE", previous); }
    }

    [Theory]
    [InlineData(600)]
    [InlineData(601)]
    public async Task Observe_discards_state_when_capture_crosses_deadline_after_599_admission(long completedAt)
    {
        long now = 0; var output = new List<byte[]>();
        using var session = new MenuTraceSession(true, () => now, 1000, output.Add, (_, _) => new Lease());
        Assert.True(session.Begin(1)); now = 599;

        var retained = session.Observe(1, MenuTraceKind.FrameSample, () =>
        {
            now = completedAt;
            return new MenuTraceState(Visible: true, NativeValue: 42);
        });

        Assert.False(retained);
        Assert.False(session.IsActive(1));
        await session.FlushAsync();
        using var json = JsonDocument.Parse(output[^1]);
        var episode = json.RootElement.GetProperty("Episodes")[0];
        Assert.Equal("Deadline", episode.GetProperty("Stop").GetString());
        Assert.Equal(completedAt, episode.GetProperty("EndTick").GetInt64());
        Assert.Equal(0, episode.GetProperty("Samples").GetInt32());
        Assert.DoesNotContain(episode.GetProperty("Entries").EnumerateArray(), entry =>
            entry.GetProperty("Kind").GetString() == "FrameSample" || entry.GetProperty("State").GetProperty("NativeValue").GetInt32() == 42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deadline_revokes_during_an_inflight_getter_without_waiting_for_capture_lock(bool initial)
    {
        long now = 0; Action? deadline = null; var output = new List<byte[]>();
        using var captureEntered = new ManualResetEventSlim();
        using var releaseCapture = new ManualResetEventSlim();
        using var stopped = new ManualResetEventSlim();
        using var session = new MenuTraceSession(true, () => Volatile.Read(ref now), 1000, output.Add,
            (_, action) => { Volatile.Write(ref deadline, action); return new Lease(); });
        session.EpisodeStopped += _ => stopped.Set();
        MenuTraceState Capture()
        {
            return session.Read(1, () =>
            {
                captureEntered.Set();
                Assert.True(releaseCapture.Wait(TimeSpan.FromSeconds(5)), "Test did not release the in-flight getter.");
                return new MenuTraceState(NativeValue: 42);
            });
        }
        Task<bool> capturing;
        if (initial) capturing = Task.Run(() => session.Begin(1, capture: Capture));
        else
        {
            Assert.True(session.Begin(1)); Volatile.Write(ref now, 599);
            capturing = Task.Run(() => session.Observe(1, MenuTraceKind.FrameSample, Capture));
        }
        Task? stopping = null;
        try
        {
            Assert.True(captureEntered.Wait(TimeSpan.FromSeconds(5)));
            Volatile.Write(ref now, 600);
            var callback = Volatile.Read(ref deadline);
            Assert.NotNull(callback); // Initial deadline must be armed before capture.
            stopping = Task.Run(callback);
            Assert.True(stopped.Wait(TimeSpan.FromSeconds(2)), "Deadline was blocked behind the in-flight getter.");
        }
        finally
        {
            releaseCapture.Set();
            await capturing;
            if (stopping is not null) await stopping;
        }
        Assert.False(await capturing);
        await session.FlushAsync();
        using var json = JsonDocument.Parse(output[^1]);
        var episode = json.RootElement.GetProperty("Episodes")[0];
        Assert.Equal("Deadline", episode.GetProperty("Stop").GetString());
        Assert.Equal(0, episode.GetProperty("Samples").GetInt32());
    }
}
