using System.Runtime.InteropServices;

namespace CodexQuotaTaskbar.Core.Compatibility;

internal static class CompatibilityPolicy
{
    private const int RecordedBuild = 26200;

    internal static CompatibilityResult EvaluatePreflight(
        WindowsBuildIdentity identity,
        TaskbarSignature signature)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(signature);

        if (identity.Architecture != Architecture.X64)
        {
            return new CompatibilityResult(CompatibilityDecision.Unsupported);
        }

        if ((identity.MajorVersion is not null && identity.MajorVersion != 10) ||
            (identity.MinorVersion is not null && identity.MinorVersion != 0))
        {
            return new CompatibilityResult(CompatibilityDecision.Unsupported);
        }

        if (!identity.IsComplete ||
            identity.BuildNumber != RecordedBuild ||
            !signature.IsApproved ||
            signature.ApprovedIdentity != identity)
        {
            return new CompatibilityResult(CompatibilityDecision.ProbeRequired);
        }

        return new CompatibilityResult(CompatibilityDecision.ProbeRequired);
    }

    internal static CompatibilityResult EvaluateFinal(
        WindowsBuildIdentity identity,
        TaskbarSignature signature,
        LiveGateReceipt liveGateReceipt)
    {
        ArgumentNullException.ThrowIfNull(liveGateReceipt);
        var preflight = EvaluatePreflight(identity, signature);
        if (preflight.Decision == CompatibilityDecision.Unsupported)
        {
            return preflight;
        }

        if (!identity.IsComplete ||
            identity.BuildNumber != RecordedBuild ||
            !signature.IsApproved ||
            !signature.IsProductionApproved ||
            signature.ApprovedIdentity != identity ||
            !liveGateReceipt.TryConsume(signature))
        {
            return new CompatibilityResult(CompatibilityDecision.ProbeRequired);
        }

        return new CompatibilityResult(CompatibilityDecision.Compatible);
    }
}
