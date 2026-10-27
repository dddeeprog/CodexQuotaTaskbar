using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexQuotaTaskbar.Host.Provider;

// Explicit allowlist: never persist a page result, exception message, body, HTML, URL or draft.
internal sealed record DotBridgeDiagnostic
{
    public int TotalArticles { get; init; }
    public int ReturnedMessages { get; init; }
    public int HistoricalBodiesSkipped { get; init; }
    public int MissingIds { get; init; }
    public int DuplicateIds { get; init; }
    public int UnknownRoles { get; init; }
    public int UnsafeBodies { get; init; }
    public int FullMatches { get; init; }
    public int WindowMatches { get; init; }
    public int Editors { get; init; }
    public int Sends { get; init; }
    public int IdConflicts { get; init; }
    public int NewWindowMatches { get; init; }
    public int EmptyActionContainers { get; init; }
    public int NonemptyActionContainers { get; init; }
    public int IgnoredEmptyIconControls { get; init; }
    public int UnsafeControls { get; init; }
    public int DomFallbacks { get; init; }
    public int EmptyBodies { get; init; }
    public int DomTraversalUnsafe { get; init; }
    public int DomScanTruncatedCount { get; init; }
    public bool Busy { get; init; }
    public bool ScanTruncated { get; init; }
    public bool BaselineComplete { get; init; }
    public bool ProbeBaselineComplete { get; init; }
    public DotBridgeRowDiagnostic[] Rows { get; init; } = [];

    internal static DotBridgeDiagnostic Read(JsonElement root)
    {
        if (!root.TryGetProperty("diagnostics", out var value) || value.ValueKind != JsonValueKind.Object) return new();
        int Count(string key) => value.TryGetProperty(key, out var item) && item.TryGetInt32(out var count) ? Math.Clamp(count, 0, 100000) : 0;
        bool Flag(string key) => value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.True;
        var rows = value.TryGetProperty("rows", out var list) && list.ValueKind == JsonValueKind.Array ?
            list.EnumerateArray().Take(14).Select(DotBridgeRowDiagnostic.Read).ToArray() : [];
        return new() { TotalArticles = Count("totalArticles"), ReturnedMessages = Count("returnedMessages"), MissingIds = Count("missingIds"),
            HistoricalBodiesSkipped = Count("historicalBodiesSkipped"),
            DuplicateIds = Count("duplicateIds"), UnknownRoles = Count("unknownRoles"), UnsafeBodies = Count("unsafeBodies"), FullMatches = Count("fullMatches"),
            WindowMatches = Count("windowMatches"), Editors = Count("editors"), Sends = Count("sends"), Busy = Flag("busy"),
            ScanTruncated = Flag("scanTruncated"), BaselineComplete = Flag("baselineComplete"), ProbeBaselineComplete = Flag("probeBaselineComplete"),
            IdConflicts = Count("idConflicts"), NewWindowMatches = Count("newWindowMatches"), Rows = rows,
            EmptyActionContainers = Count("emptyActionContainers"), NonemptyActionContainers = Count("nonemptyActionContainers"),
            IgnoredEmptyIconControls = Count("ignoredEmptyIconControls"), UnsafeControls = Count("unsafeControls"),
            DomFallbacks = Count("domFallbacks"), EmptyBodies = Count("emptyBodies"), DomTraversalUnsafe = Count("domTraversalUnsafe"),
            DomScanTruncatedCount = Count("domScanTruncatedCount") };
    }

    internal static string? HashId(string? id) => string.IsNullOrEmpty(id) ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..16];
    internal static string SafeSource(string? source) => source is "article" or "descendant" or "article-attribute" or "descendant-attribute" or
        "message-row.self" or "message-row" or "message-content" or "message-body" or "bubble-fallback" or "article-fallback" or
        "message-body-dom" or "message-content-dom" or "body-with-controls" or "ambiguous-body" or "truncated-body" or
        "conflict" or "missing" or "unknown" ? source : "unknown";
}

internal sealed record DotBridgeRowDiagnostic(int DomIndex, string Role, string IdSource, string RoleSource, string TextSource,
    bool IdMissing, bool IdDuplicate, bool Confirmable, bool PendingMatch, bool InBefore, bool InWindow,
    int InnerTextLength, int TextContentLength, int DomTextLength, bool DomFallbackUsed, bool DomTraversalSafe,
    int ExcludedHidden, int ExcludedActions, int UnsupportedNodes, bool DomScanTruncated, bool TextContentMatchesPending)
{
    internal static DotBridgeRowDiagnostic Read(JsonElement value)
    {
        bool Flag(string key) => value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.True;
        int Count(string key) => value.TryGetProperty(key, out var item) && item.TryGetInt32(out var count) ? Math.Clamp(count, 0, 1000000) : 0;
        string Source(string key) => value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? DotBridgeDiagnostic.SafeSource(item.GetString()) : "unknown";
        var role = value.TryGetProperty("role", out var roleValue) && roleValue.ValueKind == JsonValueKind.String ? roleValue.GetString() : "unknown";
        var index = value.TryGetProperty("domIndex", out var indexValue) && indexValue.TryGetInt32(out var number) ? Math.Clamp(number, 0, 100000) : 0;
        return new(index, role is "user" or "assistant" ? role : "unknown", Source("idSource"), Source("roleSource"), Source("textSource"),
            Flag("idMissing"), Flag("idDuplicate"), Flag("confirmable"), Flag("pendingMatch"), Flag("inBefore"), Flag("inWindow"),
            Count("innerTextLength"), Count("textContentLength"), Count("domTextLength"), Flag("domFallbackUsed"), Flag("domTraversalSafe"),
            Count("excludedHidden"), Count("excludedActions"), Count("unsupportedNodes"), Flag("domScanTruncated"), Flag("textContentMatchesPending"));
    }
}
