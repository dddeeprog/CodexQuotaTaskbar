using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.Json;
using CodexQuotaTaskbar.CompatibilityProbe.Reporting;
using CodexQuotaTaskbar.CompatibilityProbe.Windows;
using CodexQuotaTaskbar.Core.Compatibility;

namespace CodexQuotaTaskbar.Core.Tests.Compatibility;

public sealed class CompatibilityReportWriterTests
{
    [Fact]
    public void Collect_only_report_contains_approved_fields_and_no_absolute_paths()
    {
        var hash = new string('a', 64);
        var identity = new WindowsBuildIdentity(10, 0, 26200, 1234, Architecture.X64);
        var evidence = TaskbarModuleEvidence.Create(
            new ModuleFileEvidence("explorer.exe", "10.0.26200.1", hash),
            [new ModuleFileEvidence("Taskbar.View.dll", "10.0.26200.2", hash)],
            moduleListKnown: true,
            [new MonitorDpi(0, 144, 144)],
            monitorCount: 1,
            taskbarHostCount: 1,
            displayEvidenceKnown: true,
            EvidenceCollectionStatus.Complete,
            observation: null);
        var report = CompatibilityReport.CreateCollectOnly(
            identity,
            evidence,
            CompatibilityDecision.ProbeRequired,
            new DateTimeOffset(2026, 8, 1, 2, 3, 4, TimeSpan.Zero));
        var storage = new FakeReportStorage();
        var writer = new CompatibilityReportWriter(storage);

        var result = writer.Write(report, @"C:\reports\probe.json");

        Assert.True(result.Succeeded);
        Assert.Equal(@"C:\reports\probe.json", storage.Path);
        var json = Encoding.UTF8.GetString(storage.Content!);
        Assert.DoesNotContain(@"C:\", json, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("collectOnly", root.GetProperty("mode").GetString());
        Assert.Equal("ProbeRequired", root.GetProperty("result").GetString());
        Assert.Equal(26200, root.GetProperty("windows").GetProperty("buildNumber").GetInt32());
        Assert.Equal("explorer.exe", root.GetProperty("explorer").GetProperty("fileName").GetString());
        Assert.Equal("Taskbar.View.dll", root.GetProperty("modules")[0].GetProperty("fileName").GetString());
    }

    [Fact]
    public void Writer_rejects_relative_or_non_json_paths_before_storage()
    {
        var storage = new FakeReportStorage();
        var writer = new CompatibilityReportWriter(storage);
        var report = MinimalReport();

        var relative = writer.Write(report, @"reports\probe.json");
        var nonJson = writer.Write(report, @"C:\reports\probe.txt");

        Assert.Equal(CompatibilityReportWriteError.InvalidOutputPath, relative.Error);
        Assert.Equal(CompatibilityReportWriteError.InvalidOutputPath, nonJson.Error);
        Assert.Equal(0, storage.WriteCount);
    }

    [Fact]
    public void Storage_failure_returns_a_typed_error()
    {
        var storage = new FakeReportStorage { ThrowOnWrite = true };
        var writer = new CompatibilityReportWriter(storage);

        var result = writer.Write(MinimalReport(), @"C:\reports\probe.json");

        Assert.False(result.Succeeded);
        Assert.Equal(CompatibilityReportWriteError.StorageFailure, result.Error);
    }

    [Fact]
    public void Security_failure_returns_a_typed_error()
    {
        var storage = new FakeReportStorage
        {
            WriteException = new SecurityException("fake security boundary failure"),
        };
        var writer = new CompatibilityReportWriter(storage);

        var result = writer.Write(MinimalReport(), @"C:\reports\probe.json");

        Assert.False(result.Succeeded);
        Assert.Equal(CompatibilityReportWriteError.StorageFailure, result.Error);
    }

    [Fact]
    public void Report_creation_rejects_a_file_name_that_contains_a_path()
    {
        var evidence = TaskbarModuleEvidence.Create(
            new ModuleFileEvidence(@"C:\Users\someone\explorer.exe", "10.0.26200.1", new string('a', 64)),
            [],
            moduleListKnown: true,
            [],
            monitorCount: 0,
            taskbarHostCount: 0,
            displayEvidenceKnown: true,
            EvidenceCollectionStatus.Complete,
            observation: null);

        Assert.Throws<ArgumentException>(() => CompatibilityReport.CreateCollectOnly(
            new WindowsBuildIdentity(10, 0, 26200, 1, Architecture.X64),
            evidence,
            CompatibilityDecision.ProbeRequired,
            DateTimeOffset.UtcNow));
    }

    private static CompatibilityReport MinimalReport() =>
        CompatibilityReport.CreateCollectOnly(
            new WindowsBuildIdentity(10, 0, 26200, 1, Architecture.X64),
            TaskbarModuleEvidence.Unknown(EvidenceCollectionStatus.NoDiscoveryModules),
            CompatibilityDecision.ProbeRequired,
            DateTimeOffset.UnixEpoch);

    private sealed class FakeReportStorage : ICompatibilityReportStorage
    {
        internal string? Path { get; private set; }

        internal byte[]? Content { get; private set; }

        internal int WriteCount { get; private set; }

        internal bool ThrowOnWrite { get; init; }

        internal Exception? WriteException { get; init; }

        public void WriteAtomically(string path, ReadOnlyMemory<byte> content)
        {
            WriteCount++;
            if (WriteException is not null)
            {
                throw WriteException;
            }

            if (ThrowOnWrite)
            {
                throw new IOException("fake storage failure");
            }

            Path = path;
            Content = content.ToArray();
        }
    }
}
