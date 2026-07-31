using System.Runtime.InteropServices;
using CodexQuotaTaskbar.CompatibilityProbe.Windows;
using CodexQuotaTaskbar.Core.Compatibility;
using Microsoft.Win32;

namespace CodexQuotaTaskbar.Core.Tests.Compatibility;

public sealed class WindowsIdentityReaderTests
{
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    [Fact]
    public void Reads_valid_ubr_from_64_bit_local_machine_registry()
    {
        var platform = FakePlatform.Valid();
        platform.RegistryValues[(RegistryHive.LocalMachine, RegistryView.Registry64, CurrentVersionKey, "UBR")] = 1234;

        var identity = new WindowsIdentityReader(platform).Read();

        Assert.Equal(new WindowsBuildIdentity(10, 0, 26200, 1234, Architecture.X64), identity);
    }

    [Fact]
    public void Does_not_read_ubr_from_the_wrong_registry_view()
    {
        var platform = FakePlatform.Valid();
        platform.RegistryValues[(RegistryHive.LocalMachine, RegistryView.Registry32, CurrentVersionKey, "UBR")] = 1234;

        var identity = new WindowsIdentityReader(platform).Read();

        Assert.Null(identity.UpdateBuildRevision);
        Assert.Equal(CompatibilityDecision.ProbeRequired, Evaluate(identity));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1234")]
    [InlineData(-1)]
    [InlineData(2147483648L)]
    public void Invalid_or_missing_ubr_is_incomplete(object? value)
    {
        var platform = FakePlatform.Valid();
        if (value is not null)
        {
            platform.RegistryValues[(RegistryHive.LocalMachine, RegistryView.Registry64, CurrentVersionKey, "UBR")] = value;
        }

        var identity = new WindowsIdentityReader(platform).Read();

        Assert.Null(identity.UpdateBuildRevision);
        Assert.Equal(CompatibilityDecision.ProbeRequired, Evaluate(identity));
    }

    [Fact]
    public void Registry_access_failure_is_incomplete()
    {
        var platform = FakePlatform.Valid();
        platform.RegistryFailure = new UnauthorizedAccessException();

        var identity = new WindowsIdentityReader(platform).Read();

        Assert.Null(identity.UpdateBuildRevision);
        Assert.Equal(CompatibilityDecision.ProbeRequired, Evaluate(identity));
    }

    [Fact]
    public void RtlGetVersion_failure_is_incomplete_and_fails_closed()
    {
        var platform = FakePlatform.Valid();
        platform.Version = null;

        var identity = new WindowsIdentityReader(platform).Read();

        Assert.Null(identity.MajorVersion);
        Assert.Null(identity.MinorVersion);
        Assert.Null(identity.BuildNumber);
        Assert.Equal(CompatibilityDecision.ProbeRequired, Evaluate(identity));
    }

    [Fact]
    public void Incomplete_RtlGetVersion_result_fails_closed()
    {
        var platform = FakePlatform.Valid();
        platform.Version = new WindowsVersionNumbers(10, null, 26200);

        var identity = new WindowsIdentityReader(platform).Read();

        Assert.Null(identity.MinorVersion);
        Assert.Equal(CompatibilityDecision.ProbeRequired, Evaluate(identity));
    }

    private static CompatibilityDecision Evaluate(WindowsBuildIdentity identity) =>
        CompatibilityPolicy.EvaluatePreflight(identity, TaskbarSignature.Unknown).Decision;

    private sealed class FakePlatform : IWindowsIdentityPlatform
    {
        public WindowsVersionNumbers? Version { get; set; }

        public Architecture Architecture { get; set; }

        public Exception? RegistryFailure { get; set; }

        public Dictionary<(RegistryHive, RegistryView, string, string), object?> RegistryValues { get; } = [];

        public static FakePlatform Valid() => new()
        {
            Version = new WindowsVersionNumbers(10, 0, 26200),
            Architecture = Architecture.X64,
        };

        public bool TryReadVersion(out WindowsVersionNumbers version)
        {
            version = Version ?? default;
            return Version is not null;
        }

        public object? ReadRegistryValue(
            RegistryHive hive,
            RegistryView view,
            string subKey,
            string valueName)
        {
            if (RegistryFailure is not null)
            {
                throw RegistryFailure;
            }

            return RegistryValues.GetValueOrDefault((hive, view, subKey, valueName));
        }
    }
}
