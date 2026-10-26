using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexQuotaTaskbar.Host.Bongo;

internal enum CoreConsentState { Undecided, Declined, Accepted }
internal enum CoreConsentDecision { Closed, Declined, Accepted }

internal sealed record CoreConsentReceipt(
    [property: JsonPropertyName("schema")] int Schema,
    [property: JsonPropertyName("componentId")] string ComponentId,
    [property: JsonPropertyName("termsVersion")] string TermsVersion,
    [property: JsonPropertyName("termsSha256")] string TermsSha256,
    [property: JsonPropertyName("officialSha256")] string OfficialSha256,
    [property: JsonPropertyName("choice")] string Choice,
    [property: JsonPropertyName("confirmedAtUtc")] DateTimeOffset ConfirmedAtUtc);

internal interface ICoreConsentStore
{
    Task<CoreConsentState> LoadAsync(CoreTermsDocument document, CancellationToken cancellationToken);
    Task SaveDecisionAsync(CoreTermsDocument document, CoreConsentDecision decision, CancellationToken cancellationToken);
}

internal sealed class CoreConsentStore : ICoreConsentStore
{
    internal const int SchemaVersion = 1;
    internal const int MaximumReceiptBytes = 4096;
    internal const string FileName = "core-component-consent.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, MaxDepth = 8 };
    private readonly string path;
    private readonly SemaphoreSlim saveGate = new(1, 1);

    internal CoreConsentStore(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal))
            throw new ArgumentException("确认记录必须使用本地绝对路径。", nameof(path));
        this.path = Path.GetFullPath(path);
    }

    internal static CoreConsentStore CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexQuotaTaskbar", FileName));

    public async Task<CoreConsentState> LoadAsync(CoreTermsDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        try
        {
            AssertOrdinaryPath();
            if (!File.Exists(path) || new FileInfo(path).Length is <= 0 or > MaximumReceiptBytes) return CoreConsentState.Undecided;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            if (stream.Length is <= 0 or > MaximumReceiptBytes) return CoreConsentState.Undecided;
            // Bound the actual read too: growth/replacement between path checks cannot allocate an unbounded JSON input.
            var bytes = new byte[MaximumReceiptBytes + 1];
            var used = 0;
            while (used < bytes.Length)
            {
                var count = await stream.ReadAsync(bytes.AsMemory(used), cancellationToken);
                if (count == 0) break;
                used += count;
            }
            if (used is <= 0 or > MaximumReceiptBytes) return CoreConsentState.Undecided;
            var receipt = JsonSerializer.Deserialize<CoreConsentReceipt>(bytes.AsSpan(0, used), Json);
            return Evaluate(receipt, document);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return CoreConsentState.Undecided;
        }
    }

    public async Task SaveDecisionAsync(CoreTermsDocument document, CoreConsentDecision decision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (decision is not (CoreConsentDecision.Accepted or CoreConsentDecision.Declined))
            throw new ArgumentOutOfRangeException(nameof(decision), "关闭不应保存为确认。");
        await saveGate.WaitAsync(cancellationToken);
        try
        {
            AssertOrdinaryPath();
            var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("确认记录路径无效。");
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $".{FileName}.{Guid.NewGuid():N}.tmp");
            try
            {
                var receipt = new CoreConsentReceipt(SchemaVersion, CoreTermsDocument.ComponentId, document.Version,
                    document.ProductSha256, document.OfficialSha256,
                    decision == CoreConsentDecision.Accepted ? "accepted" : "declined", DateTimeOffset.UtcNow);
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
                {
                    await JsonSerializer.SerializeAsync(stream, receipt, Json, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
                AssertOrdinaryPath();
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { saveGate.Release(); }
    }

    internal static CoreConsentState Evaluate(CoreConsentReceipt? receipt, CoreTermsDocument document)
    {
        if (receipt is null || receipt.Schema != SchemaVersion || receipt.ComponentId != CoreTermsDocument.ComponentId
            || receipt.TermsVersion != document.Version || receipt.TermsSha256 != document.ProductSha256
            || receipt.OfficialSha256 != document.OfficialSha256 || receipt.ConfirmedAtUtc == default)
            return CoreConsentState.Undecided;
        return receipt.Choice switch { "accepted" => CoreConsentState.Accepted, "declined" => CoreConsentState.Declined, _ => CoreConsentState.Undecided };
    }

    private void AssertOrdinaryPath()
    {
        string? current = path;
        while (!string.IsNullOrEmpty(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("确认记录路径不能经过链接。"); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }
}
