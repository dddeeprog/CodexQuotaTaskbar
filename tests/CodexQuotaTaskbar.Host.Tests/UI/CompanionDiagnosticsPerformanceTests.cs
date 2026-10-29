using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using CodexQuotaTaskbar.Host.Tests.TestSupport;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

// Opt-in diagnostic producer benchmark, not a render/power/whole-app memory benchmark.
// The same compiled test DLL runs against saved baseline and candidate host DLLs.
public sealed class CompanionDiagnosticsPerformanceTests
{
    [Fact]
    public async Task Isolated_diagnostic_workload_records_metrics_when_explicitly_requested()
    {
        var run = Environment.GetEnvironmentVariable("CQT_RELIABILITY_PERF_RUN");
        if (string.IsNullOrEmpty(run)) return; // Ordinary test runs make no performance claim.
        Assert.Matches("^(baseline|after)-[0-9]+$", run);
        var root = Path.GetFullPath(Path.Combine(RepositoryPaths.Root, "artifacts/reliability/perf"));
        var directory = Path.Combine(root, "fixtures", run);
        Assert.False(Directory.Exists(directory), "Each measurement run owns a new fixture; do not reuse previous payloads.");
        Directory.CreateDirectory(directory);
        var metrics = new List<object>();
        foreach (var scenario in new[] { "repeat", "low-frequency", "burst", "control-rebuild" })
        {
            var warm = new CompanionDiagnostics(Path.Combine(directory, "warm", "companion-1.json"));
            for (var i = 0; i < 16; i++) Observe(warm, 1, i);
            await Flush(warm);
            var path = Path.Combine(directory, scenario, "companion-1.json");
            var diagnostic = new CompanionDiagnostics(path);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var retainedBefore = GC.GetTotalMemory(true);
            var allocation = GC.GetTotalAllocatedBytes(true);
            var cpu = Process.GetCurrentProcess().TotalProcessorTime;
            var clock = Stopwatch.StartNew();
            var accepted = 0;
            var count = scenario switch { "repeat" => 100000, "low-frequency" => 12, "burst" => 600, _ => 2000 };
            for (var i = 0; i < count; i++)
            {
                if (Observe(diagnostic, scenario == "control-rebuild" ? i + 1 : 1, scenario == "repeat" ? 0 : i)) accepted++;
                if (scenario == "control-rebuild") typeof(CompanionDiagnostics).GetMethod("ReleaseControl", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(diagnostic, [i + 1]);
                if (scenario == "low-frequency" && i != count - 1) await Task.Delay(250);
            }
            Assert.Equal(scenario == "repeat" ? 1 : count, accepted);
            var producerLoopElapsedMs = clock.Elapsed.TotalMilliseconds;
            await Flush(diagnostic);
            var totalMs = clock.Elapsed.TotalMilliseconds;
            var cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds;
            var allocated = GC.GetTotalAllocatedBytes(true) - allocation;
            var retained = GC.GetTotalMemory(true) - retainedBefore;
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var disk = json.RootElement.GetProperty("Entries").EnumerateArray().ToArray();
            var memory = diagnostic.Entries;
            Assert.Equal(Math.Min(192, scenario == "repeat" ? 1 : count), disk.Length);
            Assert.Equal(scenario == "repeat" ? 1L : count, disk[^1].GetProperty("Sequence").GetInt64());
            Assert.Equal(scenario == "repeat" ? 1L : count, disk[^1].GetProperty("Draw").GetInt64());
            Assert.Equal(memory.Select(e => e.Sequence), disk.Select(e => e.GetProperty("Sequence").GetInt64()));
            Assert.Equal(memory.Select(e => e.ControlId), disk.Select(e => e.GetProperty("ControlId").GetInt32()));
            Assert.Equal(memory.Select(e => e.Kind), disk.Select(e => e.GetProperty("Kind").GetString()));
            Assert.Equal(memory.Select(e => e.Draw), disk.Select(e => e.GetProperty("Draw").GetInt64()));
            Assert.Equal(memory.ToArray(), JsonSerializer.Deserialize<CompanionDiagnosticEntry[]>(json.RootElement.GetProperty("Entries").GetRawText()));
            var last = (System.Collections.IDictionary)typeof(CompanionDiagnostics).GetField("last", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(diagnostic)!;
            var writes = typeof(CompanionDiagnostics).GetField("snapshotWrites", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(diagnostic);
            var attempts = typeof(CompanionDiagnostics).GetField("snapshotWriteAttempts", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(diagnostic);
            metrics.Add(new { scenario, calls = count, accepted, producerLoopElapsedMs, totalIncludingFlushMs = totalMs, cpuProcessMs = cpuMs,
                allocatedProcessBytes = allocated, retainedManagedDeltaBytes = retained, retainedIdentityCount = last.Count,
                snapshotWriteAttempts = attempts ?? accepted, successfulSnapshotWrites = writes, writeCountBasis = attempts is null ? "attempts inferred from Task4 source: one synchronous WriteSnapshot attempt per accepted event; individual success unknown (Store bool ignored); final disk verified" : "direct attempt and successful DiagnosticLogStore return counters",
                finalRecords = disk.Length, lastSequence = disk[^1].GetProperty("Sequence").GetInt64(), finalDiskMatchesMemory = true,
                actualDelayMs = scenario == "low-frequency" ? 250 : 0, selectedAtStepSeconds = 12 });
            GC.KeepAlive(diagnostic);
        }
        var host = typeof(CompanionDiagnostics).Assembly.Location;
        var harness = typeof(CompanionDiagnosticsPerformanceTests).Assembly.Location;
        File.WriteAllText(Path.Combine(root, run + ".json"), JsonSerializer.Serialize(new { run, utc = DateTimeOffset.UtcNow,
            framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            processorCount = Environment.ProcessorCount, hostSha256 = Hash(host), harnessSha256 = Hash(harness),
            warmup = "16 accepted transition calls per scenario, plus final flush", measurementDomain = "isolated testhost process CPU and all-thread allocations; GC retained delta includes runtime noise; dictionary count is diagnostic cache only",
            intervalCaveat = "Low-frequency fixture uses 250ms real spacing and synthetic SelectedAtUtc +12s. It does not emulate production automatic 12-second wall cadence.", metrics }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static bool Observe(CompanionDiagnostics diagnostic, int control, int index)
    {
        var frame = new CompanionFrame("02", 0, "SYNTHETIC", false, 1, index + 1, index + 1,
            DateTimeOffset.UnixEpoch.AddSeconds(index * 12), index == 0 ? "initial" : "automatic");
        return diagnostic.Observe(control, index % 2 == 0 ? "selected" : "presented", frame, "02", "02", true, false);
    }
    private static async Task Flush(CompanionDiagnostics diagnostic)
    {
        // Task4 host has synchronous persistence and no Flush API. No substitute baseline algorithm.
        var method = typeof(CompanionDiagnostics).GetMethod("FlushAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        if (method?.Invoke(diagnostic, null) is Task<bool> task) Assert.True(await task);
    }
}
