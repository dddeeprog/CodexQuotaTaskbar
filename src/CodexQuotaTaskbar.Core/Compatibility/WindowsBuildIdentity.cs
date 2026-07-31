using System.Runtime.InteropServices;

namespace CodexQuotaTaskbar.Core.Compatibility;

public sealed record WindowsBuildIdentity(
    int? MajorVersion,
    int? MinorVersion,
    int? BuildNumber,
    int? UpdateBuildRevision,
    Architecture Architecture)
{
    public bool IsComplete =>
        MajorVersion is >= 0 &&
        MinorVersion is >= 0 &&
        BuildNumber is >= 0 &&
        UpdateBuildRevision is >= 0;
}
