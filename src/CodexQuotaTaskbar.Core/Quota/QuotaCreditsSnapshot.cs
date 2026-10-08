namespace CodexQuotaTaskbar.Core.Quota;

public sealed record QuotaCreditsSnapshot(bool HasCredits, bool Unlimited, decimal? Balance);
