using System.IO;
using CodexQuotaTaskbar.Host.Diagnostics;
using System.Text.Json;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.Diagnostics;

// Only task-owned local files; no app, account, system save dialog, or network.
public sealed class DiagnosticExportTests : IDisposable
{
    private readonly string root = Path.Combine(TestSupport.RepositoryPaths.Root, "artifacts", "reliability", "diagnostics", "fixtures", Guid.NewGuid().ToString("N"));
    public DiagnosticExportTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private string Put(string name, string contents) { var path = Path.Combine(root, name); File.WriteAllText(path, contents); return path; }
    private string Build() => DiagnosticExport.Build(root);
    private static bool Write(string path, string json) => DiagnosticLogStore.WriteSnapshot(path, json);
    private static string Sample => "{\"Schema\":1,\"Entries\":[{\"Utc\":\"2026-10-30T00:00:00Z\",\"Kind\":\"Footer\",\"Visual\":\"Failed\",\"Reason\":\"OwnerHidden\",\"Version\":\"0.1.16\",\"Message\":\"secret@example.test token path C:\\\\private\"}]}";

    [Fact]
    public void Export_reserializes_allowed_fields_and_ignores_receipt_and_unknown_files()
    {
        var receipt = Put("target.update-transaction.json", "PRIVATE-RECEIPT-NONCE-PATH");
        var unknown = Put("notes.json", "PRIVATE-USER-DATA");
        Put("reliability-100.json", Sample);
        var json = Build();
        Assert.Contains("OwnerHidden", json); Assert.Contains("Failed", json);
        Assert.DoesNotContain("private", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json); Assert.DoesNotContain("Message", json);
        Assert.Equal("PRIVATE-RECEIPT-NONCE-PATH", File.ReadAllText(receipt));
        Assert.Equal("PRIVATE-USER-DATA", File.ReadAllText(unknown));
    }

    [Theory]
    [InlineData("999")]
    [InlineData("\"999\"")]
    [InlineData("\"secret@example.test\"")]
    public void Unknown_numeric_and_text_enums_cannot_leak_through_export(string value)
    {
        Put("reliability-100.json", Sample.Replace("\"OwnerHidden\"", value));
        using var result = JsonDocument.Parse(Build());
        Assert.Empty(result.RootElement.GetProperty("Logs").EnumerateArray().SelectMany(l => l.GetProperty("Entries").EnumerateArray()));
        Assert.True(result.RootElement.GetProperty("OmittedEntries").GetInt32() > 0);
    }

    [Fact]
    public void Corrupt_half_written_oversized_and_exclusive_logs_are_safely_reported()
    {
        Put("reliability-1.json", "{ PRIVATE-EXCEPTION-BODY");
        Put("companion-2.json", new string('x', 262145));
        var locked = Put("overlay-3.json", Sample);
        using var held = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var json = Build();
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(3, parsed.RootElement.GetProperty("OmittedFiles").GetInt32());
        Assert.DoesNotContain("PRIVATE", json); Assert.DoesNotContain(root, json); Assert.DoesNotContain("Exception", json);
    }

    [Fact]
    public void Cross_restart_retention_bounds_owned_files_and_preserves_unknown_material()
    {
        var unknown = Put("receipt.json", "KEEP");
        var expired = Put("overlay-11.json", "{}"); File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-9));
        for (var i = 100; i < 121; i++) Put((i % 2 == 0 ? "companion-" : "reliability-") + i + ".json", "{}");
        Put("overlay-4.json.tmp", "{}");
        Assert.True(Write(Path.Combine(root, "reliability-999.json"), Sample));
        Assert.False(File.Exists(expired)); Assert.Equal("KEEP", File.ReadAllText(unknown));
        var owned = Directory.GetFiles(root).Where(p => Path.GetFileName(p).StartsWith("companion-") || Path.GetFileName(p).StartsWith("overlay-") || Path.GetFileName(p).StartsWith("reliability-")).ToArray();
        Assert.InRange(owned.Length, 1, 8); Assert.InRange(owned.Sum(p => new FileInfo(p).Length), 1, 2097152);
        Assert.Contains(owned, p => p.EndsWith("reliability-999.json"));
    }

    [Fact]
    public void Oversized_snapshot_is_rejected_without_changing_existing_data()
    {
        var path = Put("reliability-100.json", "KEEP");
        Assert.False(Write(path, new string('x', 262145))); Assert.Equal("KEEP", File.ReadAllText(path));
    }

    [Fact]
    public void Unexpected_lock_file_content_is_preserved_and_prevents_writes()
    {
        var locking = Put(".diagnostics.lock", "KEEP");
        Assert.False(Write(Path.Combine(root, "reliability-100.json"), Sample));
        Assert.Equal("KEEP", File.ReadAllText(locking));
        Assert.False(File.Exists(Path.Combine(root, "reliability-100.json")));
    }

    [Theory]
    [InlineData("CodexQuotaTaskbar.exe.update.json")]
    [InlineData("CodexQuotaTaskbar.exe.update.ready")]
    [InlineData("CodexQuotaTaskbar.exe.update.bak")]
    [InlineData("CodexQuotaTaskbar.exe.update.stage")]
    [InlineData("reliability-100.json")]
    public async Task Selecting_an_owned_log_or_update_recovery_target_preserves_it(string name)
    {
        var target = Put(name, "KEEP-RECOVERY");
        var request = Request(() => target, p => DiagnosticExport.WriteAsync(root, p));
        Assert.Equal("Failed", await Run(request)); Assert.Equal("KEEP-RECOVERY", File.ReadAllText(target));
    }

    [Fact]
    public void An_exclusively_locked_expired_owned_log_prevents_a_new_write()
    {
        var locked = Put("overlay-1.json", "KEEP"); File.SetLastWriteTimeUtc(locked, DateTime.UtcNow.AddDays(-9));
        using var held = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var target = Path.Combine(root, "reliability-100.json");
        Assert.False(Write(target, Sample)); Assert.False(File.Exists(target));
    }

    [Theory]
    [InlineData("companion-0.json")]
    [InlineData("overlay--1.json")]
    [InlineData("reliability-1-extra.json")]
    public void Files_outside_the_owned_name_contract_are_untouched(string name)
    {
        var path = Put(name, "PRIVATE-UNKNOWN");
        Assert.True(Write(Path.Combine(root, "reliability-100.json"), Sample));
        Assert.Equal("PRIVATE-UNKNOWN", File.ReadAllText(path)); Assert.DoesNotContain("PRIVATE", Build());
    }
    [Fact]
    public async Task A_locked_destination_preserves_existing_output_and_removes_only_the_export_temporary_file()
    {
        var target = Put("chosen.json", "KEEP-EXISTING-OUTPUT");
        var request = Request(() => target, p => DiagnosticExport.WriteAsync(root, p));
        using var held = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal("Failed", await Run(request));
        held.Dispose();
        Assert.Equal("KEEP-EXISTING-OUTPUT", File.ReadAllText(target));
        Assert.Single(Directory.GetFiles(root));
    }

    [Theory]
    [InlineData("secret@example.test")]
    [InlineData("0.1.16-C:\\private")]
    [InlineData("0.1.16.99")]
    public void Historical_version_text_is_validated_instead_of_copied(string version)
    {
        Put("reliability-100.json", Sample.Replace("0.1.16", version));
        var json = Build(); Assert.DoesNotContain("OwnerHidden", json); Assert.DoesNotContain("secret", json); Assert.DoesNotContain("private", json);
    }
    [Fact]
    public async Task A_mid_write_failure_cannot_truncate_existing_output()
    {
        var target = Put("chosen.json", "KEEP-EXISTING-OUTPUT");
        Action<Stream, byte[]> fail = (stream, bytes) => { stream.Write(bytes, 0, 2); throw new IOException("PRIVATE-PATH-TOKEN"); };
        var request = Request(() => target, p => DiagnosticExport.WriteAsync(root, p, fail));
        Assert.Equal("Failed", await Run(request));
        Assert.Equal("KEEP-EXISTING-OUTPUT", File.ReadAllText(target)); Assert.Single(Directory.GetFiles(root));
    }
    [Fact]
    public async Task Publishing_over_an_existing_selected_file_replaces_it_with_only_safe_data()
    {
        Put("reliability-100.json", Sample); var target = Put("chosen.json", "OLD-PRIVATE-EXPORT");
        Assert.Equal("Succeeded", await Run(Request(() => target, p => DiagnosticExport.WriteAsync(root, p))));
        Assert.Contains("OwnerHidden", File.ReadAllText(target)); Assert.DoesNotContain("PRIVATE", File.ReadAllText(target));
        Assert.DoesNotContain(Directory.GetFiles(root), p => p.EndsWith(".tmp"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Reparse_ancestor_and_dangling_junction_are_rejected_without_following_the_target(bool dangling)
    {
        var target = Path.Combine(root, "junction-target"); Directory.CreateDirectory(target);
        var link = Path.Combine(root, "junction-link");
        Junction(link, target);
        try
        {
            if (dangling) Directory.Delete(target);
            else File.WriteAllText(Path.Combine(target, "reliability-100.json"), Sample);
            Assert.False(DiagnosticLogStore.SafePath(Path.Combine(link, "reliability-100.json")));
            Assert.False(Write(Path.Combine(link, "reliability-100.json"), Sample));
            Assert.DoesNotContain("OwnerHidden", DiagnosticExport.Build(link));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void A_temporary_snapshot_junction_is_untouched_and_prevents_writing()
    {
        var target = Path.Combine(root, "junction-target"); Directory.CreateDirectory(target);
        var file = Path.Combine(root, "reliability-100.json"); var link = file + ".tmp";
        Junction(link, target);
        try { Assert.False(Write(file, Sample)); Assert.Empty(Directory.GetFiles(target)); Assert.False(File.Exists(file)); }
        finally { Directory.Delete(link); }
    }

    private static void Junction(string link, string target)
    {
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '" + link.Replace("'", "''") + "' -Target '" + target.Replace("'", "''") + "' | Out-Null");
        using var process = System.Diagnostics.Process.Start(start)!;
        Assert.True(process.WaitForExit(10000), "Local fixture junction creation timed out."); Assert.Equal(0, process.ExitCode);
    }
    [Fact]
    public async Task All_three_producers_share_the_cross_restart_file_bound()
    {
        for (var i = 1; i <= 8; i++) Put("companion-" + i + ".json", "{}");
        var companionPath = Path.Combine(root, "companion-100.json");
        var companion = new CompanionDiagnostics(companionPath);
        Assert.True(companion.Observe(1, "selected", new CompanionFrame("02", 0, "PRIVATE-LABEL", false, 1, 1, 1, DateTimeOffset.UtcNow, "initial"), "02", "02", true, false));
        Assert.True(File.Exists(companionPath)); Assert.InRange(OwnedCount(), 1, 8);
        var overlayPath = Path.Combine(root, "overlay-100.json"); var overlay = new OverlayLifecycleDiagnostics(overlayPath);
        overlay.Record(OverlayLifecycleEvent.MenuFromIsland);
        for (var i = 0; i < 40 && !File.Exists(overlayPath); i++) await Task.Delay(25);
        Assert.True(File.Exists(overlayPath)); Assert.InRange(OwnedCount(), 1, 8);
        var reliabilityPath = Path.Combine(root, "reliability-100.json");
        new ReliabilityDiagnostics(reliabilityPath).Query(CodexQuotaTaskbar.Host.Resets.ResetQueryPhase.Failed);
        Assert.True(File.Exists(reliabilityPath)); Assert.InRange(OwnedCount(), 1, 8);
        var json = Build(); Assert.DoesNotContain("PRIVATE-LABEL", json);
        Assert.Contains("MenuFromIsland", json); Assert.Contains("Failed", json);
        int OwnedCount() => Directory.GetFiles(root).Count(p => DiagnosticLogStore.IsOwned(Path.GetFileName(p)));
    }

    [Fact]
    public void Companion_and_overlay_history_are_reserialized_and_unknown_strings_are_never_copied()
    {
        Put("companion-1.json", "{\"Schema\":1,\"Entries\":[{\"Utc\":\"2026-10-30T00:00:00Z\",\"Kind\":\"selected\",\"RequestedId\":\"02\",\"EffectiveId\":\"02\",\"Label\":\"PRIVATE-LABEL\",\"Gaze\":{\"TargetX\":99},\"Count\":987654}]}");
        Put("overlay-2.json", "{\"Schema\":1,\"Entries\":[{\"Utc\":\"2026-10-30T00:00:00Z\",\"Kind\":\"VisibilityPolicy\",\"Reason\":\"Fullscreen\",\"Visible\":false,\"Path\":\"PRIVATE-PATH\",\"Title\":\"PRIVATE-TITLE\"}]}");
        var json = Build(); Assert.Contains("selected", json); Assert.Contains("Fullscreen", json);
        Assert.DoesNotContain("PRIVATE", json); Assert.DoesNotContain("Gaze", json); Assert.DoesNotContain("987654", json);
    }
    [Fact]
    public void A_locked_oldest_overflow_file_prevents_worsening_the_existing_excess()
    {
        var locked = Put("overlay-1.json", "KEEP-LOCKED"); File.SetLastWriteTimeUtc(locked, DateTime.UtcNow.AddDays(-1));
        for (var i = 2; i <= 9; i++) Put("overlay-" + i + ".json", "{}");
        var unknown = Put("target.exe.update.json", "KEEP-RECOVERY");
        using (var held = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.False(Write(Path.Combine(root, "reliability-100.json"), Sample));
        Assert.False(File.Exists(Path.Combine(root, "reliability-100.json"))); Assert.Equal("KEEP-LOCKED", File.ReadAllText(locked));
        Assert.Equal("KEEP-RECOVERY", File.ReadAllText(unknown));
    }

    [Fact]
    public void Typed_update_capture_exports_stage_category_and_hresult_without_exception_text()
    {
        var log = new ReliabilityDiagnostics(Path.Combine(root, "reliability-100.json"));
        var diagnostic = CodexQuotaTaskbar.Host.Update.UpdateApplyDiagnostic.Capture(CodexQuotaTaskbar.Host.Update.UpdateApplyStage.Replace,
            new UnauthorizedAccessException("PRIVATE-PATH-EMAIL-TOKEN"));
        log.Update(diagnostic, DiagnosticUpdateOutcome.PendingRecovery);
        var json = Build(); Assert.Contains("Replace", json); Assert.Contains("AccessDenied", json); Assert.Contains("HResult", json);
        Assert.Contains("PendingRecovery", json); Assert.DoesNotContain("PRIVATE", json); Assert.Contains(ProductVersion.Text, json);
    }
    private static DiagnosticExportRequest Request(Func<string?> choose, Func<string, Task> write) => new(choose, write);
    private static async Task<string> Run(DiagnosticExportRequest request) => (await request.RunAsync()).ToString();

    [Fact]
    public async Task Cancellation_does_not_create_an_output()
    {
        var target = Path.Combine(root, "export.json");
        var request = Request(() => null, p => File.WriteAllTextAsync(target, "bad"));
        Assert.Equal("Cancelled", await Run(request)); Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task A_pending_export_rejects_reentry_and_releases_guard_after_failure()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = Request(() => Path.Combine(root, "export.json"), _ => pending.Task);
        var first = Run(request); Assert.Equal("Busy", await Run(request));
        pending.SetException(new IOException("PRIVATE-PATH-TOKEN"));
        Assert.Equal("Failed", await first); Assert.Equal("Failed", await Run(request));
    }

    [Fact]
    public async Task Successful_export_writes_only_to_selected_local_file()
    {
        Put("reliability-100.json", Sample); var target = Path.Combine(root, "chosen.json");
        var request = Request(() => target, p => DiagnosticExport.WriteAsync(root, p));
        Assert.Equal("Succeeded", await Run(request));
        Assert.Contains("OwnerHidden", File.ReadAllText(target)); Assert.DoesNotContain("secret", File.ReadAllText(target));
    }
}
