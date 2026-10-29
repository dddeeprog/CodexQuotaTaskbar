using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexQuotaTaskbar.Core.Quota;
using CodexQuotaTaskbar.Host.Resets;
using CodexQuotaTaskbar.Host.Update;

namespace CodexQuotaTaskbar.Host.Diagnostics;

internal enum ReliabilityEvent { Quota, Query, Footer, Update }
// Selection/space decisions only: eligible does not confirm HWND exposure or a completed animation.
internal enum FooterSuppression { Eligible, CompactEligible, OwnerHidden, NoSpace, Expired, Disabled, Idle }
internal enum DiagnosticUpdateOutcome { Ready, Failed, Restored, PendingRecovery }
internal sealed record ReliabilityEntry(DateTimeOffset Utc, string Version, ReliabilityEvent Kind,
    QuotaAvailability? Availability = null, ResetQueryPhase? Query = null, ResetVisualState? Visual = null,
    FooterSuppression? Reason = null, UpdateApplyStage? Stage = null, UpdateApplyErrorKind? Error = null,
    int? HResult = null, DiagnosticUpdateOutcome? Outcome = null);

// No free-text input: account/session paths, quota balances, exception messages and URLs cannot enter this log.
internal sealed class ReliabilityDiagnostics(string? path = null)
{
    internal static ReliabilityDiagnostics Local { get; } = new(
        string.Equals(Path.GetFileName(Environment.ProcessPath), "CodexQuotaTaskbar.exe", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(DiagnosticLogStore.DefaultDirectory, $"reliability-{Environment.ProcessId}.json") : null);
    internal static readonly JsonSerializerOptions JsonOptions = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly object gate = new();
    private readonly Queue<ReliabilityEntry> entries = new();
    private readonly Dictionary<ReliabilityEvent, ReliabilityEntry> last = new();
    internal IReadOnlyList<ReliabilityEntry> Entries { get { lock (gate) return entries.ToArray(); } }
    internal void Quota(QuotaAvailability value) => Record(new(default, ProductVersion.Text, ReliabilityEvent.Quota, Availability: value));
    internal void Query(ResetQueryPhase value) => Record(new(default, ProductVersion.Text, ReliabilityEvent.Query, Query: value));
    internal void Footer(ResetVisualState visual, FooterSuppression reason) => Record(new(default, ProductVersion.Text, ReliabilityEvent.Footer, Visual: visual, Reason: reason));
    internal void Update(UpdateApplyDiagnostic diagnostic, DiagnosticUpdateOutcome outcome) => Record(new(default, ProductVersion.Text,
        ReliabilityEvent.Update, Stage: diagnostic.Stage, Error: diagnostic.Kind, HResult: diagnostic.HResult, Outcome: outcome));
    // Translate only transaction-owned reports; never retain their text or infer restoration from replacement alone.
    internal static DiagnosticUpdateOutcome ClassifyUpdateOutcome(UpdateApplyException diagnostic)
    {
        if (diagnostic.RecoveryFailure is not null) return DiagnosticUpdateOutcome.PendingRecovery;
        var report = diagnostic.RecoveryOutcome;
        if (report is null || report.Length > 160) return DiagnosticUpdateOutcome.PendingRecovery;
        if (report == "新版已就绪") return DiagnosticUpdateOutcome.Ready;
        if (report == "未替换") return DiagnosticUpdateOutcome.Failed;
        if (VersionReport(report, "已恢复 v", "")
            || VersionReport(report, "已恢复 v", "；该失败更新已阻止重复安装")
            || VersionReport(report, "已恢复 v", "，已尝试正常启动")) return DiagnosticUpdateOutcome.Restored;
        if (VersionReport(report, "保留 v", "，已尝试正常启动")) return DiagnosticUpdateOutcome.Failed;
        if (report.StartsWith("当前 v", StringComparison.Ordinal) && report.EndsWith(" 已阻止重复安装", StringComparison.Ordinal))
        {
            var versions = report["当前 v".Length..^" 已阻止重复安装".Length].Split("，失败版本 v", StringSplitOptions.None);
            if (versions.Length == 2 && CanonicalVersion(versions[0]) && CanonicalVersion(versions[1])) return DiagnosticUpdateOutcome.Failed;
        }
        // Includes exact "待恢复" / "已有更新事务待恢复", missing fields and future unknown reports.
        return DiagnosticUpdateOutcome.PendingRecovery;
    }

    private static bool VersionReport(string report, string prefix, string suffix) =>
        report.Length > prefix.Length + suffix.Length && report.StartsWith(prefix, StringComparison.Ordinal)
            && report.EndsWith(suffix, StringComparison.Ordinal) && CanonicalVersion(report[prefix.Length..(report.Length - suffix.Length)]);
    private static bool CanonicalVersion(string text) => Version.TryParse(text, out var version)
        && version.Build >= 0 && version.Revision == -1 && version.ToString(3) == text;
    internal void UpdateReady() => Record(new(default, ProductVersion.Text, ReliabilityEvent.Update, Outcome: DiagnosticUpdateOutcome.Ready));
    private static bool Valid<T>(T? value) where T : struct, Enum => value is null || Enum.IsDefined(value.Value);
    private void Record(ReliabilityEntry entry)
    {
        if (!Valid(entry.Availability) || !Valid(entry.Query) || !Valid(entry.Visual) || !Valid(entry.Reason)
            || !Valid(entry.Stage) || !Valid(entry.Error) || !Valid(entry.Outcome)) return;
        lock (gate)
        {
            if (last.TryGetValue(entry.Kind, out var previous) && previous == entry) return;
            last[entry.Kind] = entry;
            entries.Enqueue(entry with { Utc = DateTimeOffset.UtcNow });
            while (entries.Count > 192) entries.Dequeue();
            if (path is not null) DiagnosticLogStore.WriteSnapshot(path, JsonSerializer.Serialize(new { Schema = 1, Entries = entries }, JsonOptions));
        }
    }
}
