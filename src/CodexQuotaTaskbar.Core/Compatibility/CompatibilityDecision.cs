namespace CodexQuotaTaskbar.Core.Compatibility;

public enum CompatibilityDecision
{
    ProbeRequired,
    Compatible,
    Unsupported,
    UnsafePreviousActivation,
}

public sealed record CompatibilityResult
{
    internal CompatibilityResult(CompatibilityDecision decision)
    {
        Decision = decision;
    }

    public CompatibilityDecision Decision { get; }
}
