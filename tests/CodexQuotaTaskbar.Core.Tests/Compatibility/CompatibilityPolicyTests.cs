using System.Reflection;
using System.Runtime.InteropServices;
using CodexQuotaTaskbar.Core.Compatibility;

namespace CodexQuotaTaskbar.Core.Tests.Compatibility;

public sealed class CompatibilityPolicyTests
{
    private static readonly WindowsBuildIdentity ApprovedIdentity =
        new(10, 0, 26200, 1234, Architecture.X64);

    [Fact]
    public void Final_gate_allows_only_an_exact_candidate_with_a_bound_live_gate_receipt()
    {
        var fixture = TrustFixture.Create();
        var signature = TaskbarSignature.Approve(
            ApprovedIdentity,
            fixture.Observation,
            fixture.StructureProof,
            fixture.Catalog);
        var receipt = CreateLiveGateReceipt(
            signature,
            fixture.Observation.ExplorerIdentity,
            fixture.Observation.CaptureToken);

        var result = CompatibilityPolicy.EvaluateFinal(ApprovedIdentity, signature, receipt);

        Assert.Equal(CompatibilityDecision.Compatible, result.Decision);
    }

    [Fact]
    public void Approved_candidate_preflight_still_requires_the_later_live_gate()
    {
        var fixture = TrustFixture.Create();
        var signature = TaskbarSignature.Approve(
            ApprovedIdentity,
            fixture.Observation,
            fixture.StructureProof,
            fixture.Catalog);

        var result = CompatibilityPolicy.EvaluatePreflight(ApprovedIdentity, signature);

        Assert.Equal(CompatibilityDecision.ProbeRequired, result.Decision);
    }

    [Fact]
    public void Stale_live_gate_receipt_from_another_capture_cannot_be_replayed()
    {
        var fixture = TrustFixture.Create();
        var signature = TaskbarSignature.Approve(
            ApprovedIdentity,
            fixture.Observation,
            fixture.StructureProof,
            fixture.Catalog);
        var staleReceipt = CreateLiveGateReceipt(
            signature,
            fixture.Observation.ExplorerIdentity,
            new CaptureLeaseToken());

        var result = CompatibilityPolicy.EvaluateFinal(
            ApprovedIdentity,
            signature,
            staleReceipt);

        Assert.Equal(CompatibilityDecision.ProbeRequired, result.Decision);
    }

    [Fact]
    public void Live_gate_receipt_can_be_consumed_only_once()
    {
        var fixture = TrustFixture.Create();
        var signature = TaskbarSignature.Approve(
            ApprovedIdentity,
            fixture.Observation,
            fixture.StructureProof,
            fixture.Catalog);
        var receipt = CreateLiveGateReceipt(
            signature,
            fixture.Observation.ExplorerIdentity,
            fixture.Observation.CaptureToken);

        var first = CompatibilityPolicy.EvaluateFinal(ApprovedIdentity, signature, receipt);
        var replay = CompatibilityPolicy.EvaluateFinal(ApprovedIdentity, signature, receipt);

        Assert.Equal(CompatibilityDecision.Compatible, first.Decision);
        Assert.Equal(CompatibilityDecision.ProbeRequired, replay.Decision);
    }

    [Fact]
    public void Production_catalog_is_empty_and_collect_only_fails_closed()
    {
        var fixture = TrustFixture.Create();
        var signature = TaskbarSignature.Approve(
            ApprovedIdentity,
            fixture.Observation,
            fixture.StructureProof,
            ApprovedBaselineCatalog.Production);

        var result = CompatibilityPolicy.EvaluatePreflight(ApprovedIdentity, signature);

        Assert.Equal(CompatibilityDecision.ProbeRequired, result.Decision);
    }

    [Fact]
    public void Exact_staging_catalog_candidate_is_never_eligible_for_final_compatible()
    {
        var fixture = TrustFixture.Create();
        var stagingBaseline = new ApprovedCompatibilityBaseline(
            ApprovedIdentity,
            fixture.Observation.Explorer,
            fixture.Observation.Modules,
            TaskbarSignature.RequiredXamlTypeParentSignature);
        var signature = TaskbarSignature.Approve(
            ApprovedIdentity,
            fixture.Observation,
            fixture.StructureProof,
            new ApprovedBaselineCatalog([stagingBaseline]));
        var receipt = CreateLiveGateReceipt(
            signature,
            fixture.Observation.ExplorerIdentity,
            fixture.Observation.CaptureToken);

        var result = CompatibilityPolicy.EvaluateFinal(ApprovedIdentity, signature, receipt);

        Assert.True(signature.IsApproved);
        Assert.Equal(CompatibilityDecision.ProbeRequired, result.Decision);
    }

    [Theory]
    [InlineData(26100, 1234)]
    [InlineData(26201, 1234)]
    [InlineData(26200, 1235)]
    public void Build_or_ubr_without_an_exact_approved_key_fails_closed(int build, int ubr)
    {
        var fixture = TrustFixture.Create();
        var identity = ApprovedIdentity with { BuildNumber = build, UpdateBuildRevision = ubr };
        var signature = TaskbarSignature.Approve(
            identity,
            fixture.Observation,
            fixture.StructureProof,
            fixture.Catalog);

        Assert.Equal(
            CompatibilityDecision.ProbeRequired,
            CompatibilityPolicy.EvaluatePreflight(identity, signature).Decision);
    }

    [Fact]
    public void Final_gate_rejects_an_exact_catalog_entry_for_an_unrecorded_build()
    {
        var fixture = TrustFixture.Create();
        var unrecordedIdentity = ApprovedIdentity with { BuildNumber = 26100 };
        var baseline = new ApprovedCompatibilityBaseline(
            unrecordedIdentity,
            fixture.Observation.Explorer,
            fixture.Observation.Modules,
            TaskbarSignature.RequiredXamlTypeParentSignature);
        var signature = TaskbarSignature.Approve(
            unrecordedIdentity,
            fixture.Observation,
            fixture.StructureProof,
            new ApprovedBaselineCatalog([baseline]));
        var receipt = CreateLiveGateReceipt(
            signature,
            fixture.Observation.ExplorerIdentity,
            fixture.Observation.CaptureToken);

        var result = CompatibilityPolicy.EvaluateFinal(
            unrecordedIdentity,
            signature,
            receipt);

        Assert.True(signature.IsApproved);
        Assert.Equal(CompatibilityDecision.ProbeRequired, result.Decision);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Explorer_version_or_sha_mismatch_fails_closed(bool changeVersion, bool changeHash)
    {
        var fixture = TrustFixture.Create();
        var explorer = fixture.Observation.Explorer with
        {
            FileVersion = changeVersion ? "10.0.26200.9999" : fixture.Observation.Explorer.FileVersion,
            Sha256 = changeHash ? Hash('f') : fixture.Observation.Explorer.Sha256,
        };
        var observation = fixture.Observation with { Explorer = explorer };

        var signature = TaskbarSignature.Approve(
            ApprovedIdentity,
            observation,
            fixture.StructureProof,
            fixture.Catalog);

        Assert.Equal(
            CompatibilityDecision.ProbeRequired,
            CompatibilityPolicy.EvaluatePreflight(ApprovedIdentity, signature).Decision);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("near-name")]
    [InlineData("version")]
    [InlineData("hash")]
    public void Non_exact_module_set_fails_closed(string mutation)
    {
        var fixture = TrustFixture.Create();
        var modules = fixture.Observation.Modules.ToList();
        switch (mutation)
        {
            case "missing":
                modules.RemoveAt(0);
                break;
            case "extra":
                modules.Add(new TrustedFileSignature("Taskbar.Extra.dll", "10.0.26200.1", Hash('e')));
                break;
            case "near-name":
                modules[0] = modules[0] with { FileName = "Taskbar.View.near.dll" };
                break;
            case "version":
                modules[0] = modules[0] with { FileVersion = "10.0.26200.2" };
                break;
            case "hash":
                modules[0] = modules[0] with { Sha256 = Hash('d') };
                break;
        }

        var signature = TaskbarSignature.Approve(
            ApprovedIdentity,
            fixture.Observation with { Modules = modules },
            fixture.StructureProof,
            fixture.Catalog);

        Assert.Equal(
            CompatibilityDecision.ProbeRequired,
            CompatibilityPolicy.EvaluatePreflight(ApprovedIdentity, signature).Decision);
    }

    [Theory]
    [InlineData("SystemTray.SystemTrayFrame>grid")]
    [InlineData("SystemTray.SystemTrayFrame>Grid>Border")]
    public void Arbitrary_or_near_xaml_signature_fails_closed(string xamlSignature)
    {
        var fixture = TrustFixture.Create();
        var proof = new TrustedStructureProof(
            fixture.Observation.ExplorerIdentity,
            fixture.Observation.CaptureToken,
            fixture.Observation.DisplayProof.HostBindings,
            xamlSignature);

        var signature = TaskbarSignature.Approve(
            ApprovedIdentity,
            fixture.Observation,
            proof,
            fixture.Catalog);

        Assert.Equal(
            CompatibilityDecision.ProbeRequired,
            CompatibilityPolicy.EvaluatePreflight(ApprovedIdentity, signature).Decision);
    }

    [Fact]
    public void Catalog_cannot_approve_a_non_required_xaml_signature()
    {
        const string wrongXaml = "SystemTray.SystemTrayFrame>Grid>Injected";
        var fixture = TrustFixture.Create();
        var proof = new TrustedStructureProof(
            fixture.Observation.ExplorerIdentity,
            fixture.Observation.CaptureToken,
            fixture.Observation.DisplayProof.HostBindings,
            wrongXaml);
        var baseline = new ApprovedCompatibilityBaseline(
            ApprovedIdentity,
            fixture.Observation.Explorer,
            fixture.Observation.Modules,
            wrongXaml);

        var signature = TaskbarSignature.Approve(
            ApprovedIdentity,
            fixture.Observation,
            proof,
            new ApprovedBaselineCatalog([baseline]));

        Assert.False(signature.IsApproved);
        Assert.Equal(
            CompatibilityDecision.ProbeRequired,
            CompatibilityPolicy.EvaluatePreflight(ApprovedIdentity, signature).Decision);
    }

    [Fact]
    public void Structure_proof_from_a_restarted_explorer_fails_closed()
    {
        var fixture = TrustFixture.Create();
        var restarted = fixture.Observation.ExplorerIdentity with
        {
            StartTimeUtcTicks = fixture.Observation.ExplorerIdentity.StartTimeUtcTicks + 1,
        };
        var proof = new TrustedStructureProof(
            restarted,
            fixture.Observation.CaptureToken,
            fixture.Observation.DisplayProof.HostBindings,
            TaskbarSignature.RequiredXamlTypeParentSignature);

        var signature = TaskbarSignature.Approve(
            ApprovedIdentity,
            fixture.Observation,
            proof,
            fixture.Catalog);

        Assert.Equal(
            CompatibilityDecision.ProbeRequired,
            CompatibilityPolicy.EvaluatePreflight(ApprovedIdentity, signature).Decision);
    }

    [Fact]
    public void Structure_and_display_host_bindings_must_match_exactly()
    {
        var fixture = TrustFixture.Create();
        var proof = new TrustedStructureProof(
            fixture.Observation.ExplorerIdentity,
            fixture.Observation.CaptureToken,
            [new TaskbarHostBinding(999, "Shell_TrayWnd")],
            TaskbarSignature.RequiredXamlTypeParentSignature);

        var signature = TaskbarSignature.Approve(
            ApprovedIdentity,
            fixture.Observation,
            proof,
            fixture.Catalog);

        Assert.Equal(
            CompatibilityDecision.ProbeRequired,
            CompatibilityPolicy.EvaluatePreflight(ApprovedIdentity, signature).Decision);
    }

    [Fact]
    public void Matching_non_taskbar_host_bindings_cannot_be_trusted()
    {
        var fixture = TrustFixture.Create();
        TaskbarHostBinding[] invalidHosts = [new(100, "Shell_TrayWnd_Near")];
        var displayProof = new TrustedDisplayProof(
            fixture.Observation.ExplorerIdentity,
            fixture.Observation.CaptureToken,
            invalidHosts);
        var observation = fixture.Observation with { DisplayProof = displayProof };
        var structureProof = new TrustedStructureProof(
            fixture.Observation.ExplorerIdentity,
            fixture.Observation.CaptureToken,
            invalidHosts,
            TaskbarSignature.RequiredXamlTypeParentSignature);

        var signature = TaskbarSignature.Approve(
            ApprovedIdentity,
            observation,
            structureProof,
            fixture.Catalog);

        Assert.False(signature.IsApproved);
    }

    [Fact]
    public void Observed_snapshot_copies_mutable_module_inputs()
    {
        var fixture = TrustFixture.Create();
        var originalModule = fixture.Observation.Modules[0];
        var mutableModules = fixture.Observation.Modules.ToList();
        var observation = new ObservedTaskbarSnapshot(
            fixture.Observation.ExplorerIdentity,
            fixture.Observation.CaptureToken,
            fixture.Observation.Explorer,
            mutableModules,
            fixture.Observation.DisplayProof);

        mutableModules[0] = originalModule with { Sha256 = Hash('f') };

        Assert.Equal(originalModule, observation.Modules[0]);
    }

    [Fact]
    public void Signature_cannot_be_reused_for_a_different_windows_identity()
    {
        var fixture = TrustFixture.Create();
        var signature = TaskbarSignature.Approve(
            ApprovedIdentity,
            fixture.Observation,
            fixture.StructureProof,
            fixture.Catalog);

        var result = CompatibilityPolicy.EvaluatePreflight(
            ApprovedIdentity with { UpdateBuildRevision = 1235 },
            signature);

        Assert.Equal(CompatibilityDecision.ProbeRequired, result.Decision);
    }

    [Fact]
    public void Trust_constructors_and_policy_are_not_public_success_apis()
    {
        Assert.False(typeof(TaskbarSignature).IsPublic);
        Assert.Null(typeof(TaskbarSignature).GetMethod("Verified", BindingFlags.Public | BindingFlags.Static));
        Assert.False(typeof(CompatibilityPolicy).IsPublic);
        Assert.Empty(typeof(CompatibilityResult).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.All(
            typeof(LiveGateReceipt).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
            constructor => Assert.True(constructor.IsPrivate));
        Assert.DoesNotContain(
            typeof(CompatibilityPolicy).GetMethods(BindingFlags.Public | BindingFlags.Static),
            method => method.GetParameters().Any(parameter => parameter.ParameterType == typeof(bool)));
    }

    [Theory]
    [InlineData(Architecture.X86)]
    [InlineData(Architecture.Arm64)]
    public void Clearly_unsupported_architecture_is_unsupported(Architecture architecture)
    {
        var result = CompatibilityPolicy.EvaluatePreflight(
            ApprovedIdentity with { Architecture = architecture },
            TaskbarSignature.Unknown);

        Assert.Equal(CompatibilityDecision.Unsupported, result.Decision);
    }

    [Theory]
    [InlineData(6, 3)]
    [InlineData(11, 0)]
    public void Clearly_unsupported_platform_is_unsupported(int major, int minor)
    {
        var result = CompatibilityPolicy.EvaluatePreflight(
            ApprovedIdentity with { MajorVersion = major, MinorVersion = minor },
            TaskbarSignature.Unknown);

        Assert.Equal(CompatibilityDecision.Unsupported, result.Decision);
    }

    [Fact]
    public void Incomplete_ubr_fails_closed()
    {
        var result = CompatibilityPolicy.EvaluatePreflight(
            ApprovedIdentity with { UpdateBuildRevision = null },
            TaskbarSignature.Unknown);

        Assert.Equal(CompatibilityDecision.ProbeRequired, result.Decision);
    }

    private static string Hash(char character) => new(character, 64);

    private static LiveGateReceipt CreateLiveGateReceipt(
        TaskbarSignature signature,
        ExplorerInstanceIdentity explorerIdentity,
        CaptureLeaseToken captureToken)
    {
        var constructor = typeof(LiveGateReceipt)
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single();
        return (LiveGateReceipt)constructor.Invoke([signature, explorerIdentity, captureToken]);
    }

    private static ApprovedBaselineCatalog CreateProductionCatalog(
        IReadOnlyList<ApprovedCompatibilityBaseline> baselines)
    {
        var constructor = typeof(ApprovedBaselineCatalog)
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(candidate => candidate.GetParameters().Length == 2);
        return (ApprovedBaselineCatalog)constructor.Invoke([baselines, true]);
    }

    private sealed record TrustFixture(
        ObservedTaskbarSnapshot Observation,
        TrustedStructureProof StructureProof,
        ApprovedBaselineCatalog Catalog)
    {
        public static TrustFixture Create()
        {
            var explorerIdentity = new ExplorerInstanceIdentity(
                42,
                638000000000000000,
                7,
                "S-1-5-21-fixture",
                @"C:\Windows\explorer.exe");
            var captureToken = new CaptureLeaseToken();
            TaskbarHostBinding[] hosts = [new(100, "Shell_TrayWnd"), new(101, "Shell_SecondaryTrayWnd")];
            var displayProof = new TrustedDisplayProof(explorerIdentity, captureToken, hosts);
            var explorer = new TrustedFileSignature("explorer.exe", "10.0.26200.1", Hash('a'));
            TrustedFileSignature[] modules =
            [
                new("Taskbar.View.dll", "10.0.26200.1", Hash('b')),
                new("Windows.UI.Xaml.dll", "10.0.26200.1", Hash('c')),
            ];
            var observation = new ObservedTaskbarSnapshot(
                explorerIdentity,
                captureToken,
                explorer,
                modules,
                displayProof);
            var structureProof = new TrustedStructureProof(
                explorerIdentity,
                captureToken,
                hosts,
                TaskbarSignature.RequiredXamlTypeParentSignature);
            var baseline = new ApprovedCompatibilityBaseline(
                ApprovedIdentity,
                explorer,
                modules,
                TaskbarSignature.RequiredXamlTypeParentSignature);
            return new TrustFixture(
                observation,
                structureProof,
                CreateProductionCatalog([baseline]));
        }
    }
}
