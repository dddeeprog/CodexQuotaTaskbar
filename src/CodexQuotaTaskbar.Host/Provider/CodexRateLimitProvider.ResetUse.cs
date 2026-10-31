using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexQuotaTaskbar.Core.Quota;
using CodexQuotaTaskbar.Host.Resets;

namespace CodexQuotaTaskbar.Host.Provider;

internal sealed partial class CodexRateLimitProvider
{
    private ResetCreditUseContext? resetUseContext;
    internal event Action? ResetUseContextInvalidated;

    private void InvalidateResetUseContext()
    {
        resetUseContext = null;
        ResetUseContextInvalidated?.Invoke();
    }

    internal static ResetCreditUseContext? CreateResetUseContext(JsonElement before, JsonElement limits,
        JsonElement after, string? connectionBefore, string? connectionAfter, long generationBefore,
        long generationAfter, QuotaResetCreditsSnapshot cards)
    {
        if (connectionBefore is not { Length: 32 } || !connectionBefore.All(char.IsAsciiHexDigit)
            || connectionBefore != connectionAfter || generationBefore != generationAfter
            || !IsStableAccountContext(before, after) || limits.ValueKind != JsonValueKind.Object
            || !limits.TryGetProperty("accountId", out var accountId) || accountId.ValueKind != JsonValueKind.String
            || !SafeContextValue(accountId.GetString(), 256)) return null;
        var beforeAccount = before.GetProperty("account");
        var afterAccount = after.GetProperty("account");
        if (!TryWorkspace(beforeAccount, out var workspace) || !TryWorkspace(afterAccount, out var afterWorkspace)
            || workspace != afterWorkspace) return null;
        // Bind the durable retry key to both supported account ID and optional
        // workspace context, including the distinction between missing/present.
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new[] { accountId.GetString(), workspace })))).ToLowerInvariant();
        var email = AccountContext(after)?.Email;
        // Readable account context is confined to the local confirmation UI.
        var label = email is { Length: > 0 and <= 320 } && email.IndexOf('@') > 0 && !email.Any(char.IsControl)
            ? email : "ChatGPT";
        if (workspace is not null) label += " · " + workspace;
        label += " · 账号标识 " + fingerprint[..8];
        var workspaceBinding = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            workspace is null ? "missing" : "provided:" + workspace))).ToLowerInvariant();
        return new(fingerprint, connectionBefore + ":" + generationBefore, label, cards, true, workspaceBinding);
    }

    private static bool TryWorkspace(JsonElement account, out string? workspace)
    {
        workspace = null;
        if (!account.TryGetProperty("workspaceName", out var value) || value.ValueKind == JsonValueKind.Null) return true;
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 and <= 128 } text
            || text.Any(char.IsControl)) return false;
        workspace = text;
        return true;
    }

    public async Task<ResetCreditUseContext?> ReadContextAsync(CancellationToken cancellationToken)
    {
        await RefreshAsync(cancellationToken);
        lock (resetGate)
        {
            var context = resetUseContext;
            return context is not null && IsCurrentResetUseContext(context)
                && Current.Availability == QuotaAvailability.Available && !Current.ResetCredits.IsStale
                && Current.ResetCredits == context.Cards ? context : null;
        }
    }

    private bool IsCurrentResetUseContext(ResetCreditUseContext context) =>
        resetUseContext is { } current && current.AccountFingerprint == context.AccountFingerprint
        && current.ConnectionEpoch == context.ConnectionEpoch
        && current.WorkspaceBinding == context.WorkspaceBinding
        && Current.Availability == QuotaAvailability.Available && !Current.ResetCredits.IsStale
        && Current.ResetCredits == context.Cards
        && context.ConnectionEpoch == client.InitializedConnectionId + ":" + Interlocked.Read(ref accountGeneration);

    public async Task<string> ConsumeAsync(ResetCreditUseContext context, string key, CancellationToken cancellationToken)
    {
        var response = await client.RequestAsync("account/rateLimitResetCredit/consume", new { idempotencyKey = key },
            cancellationToken, () =>
            {
                lock (resetGate) return IsCurrentResetUseContext(context);
            });
        return response.ValueKind == JsonValueKind.Object && response.TryGetProperty("outcome", out var outcome)
            && outcome.ValueKind == JsonValueKind.String ? outcome.GetString()! : "unknown";
    }
}
