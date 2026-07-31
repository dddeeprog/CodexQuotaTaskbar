using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CodexQuotaTaskbar.CompatibilityProbe.Windows;
using CodexQuotaTaskbar.Core.Compatibility;

namespace CodexQuotaTaskbar.Core.Tests.Compatibility;

public sealed class ModuleSignatureReaderTests
{
    private static readonly WindowsBuildIdentity ApprovedIdentity =
        new(10, 0, 26200, 1234, Architecture.X64);

    [Fact]
    public void Public_reader_is_collect_only_and_cannot_accept_caller_asserted_structure_success()
    {
        var readMethods = typeof(ModuleSignatureReader)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.Name == nameof(ModuleSignatureReader.Read))
            .ToArray();

        var evidence = new ModuleSignatureReader(FakePlatform.Complete()).Read();
        var result = CompatibilityPolicy.EvaluatePreflight(ApprovedIdentity, evidence.Signature);

        Assert.Single(readMethods);
        Assert.Empty(readMethods[0].GetParameters());
        Assert.Equal(CompatibilityDecision.ProbeRequired, result.Decision);
        Assert.Null(evidence.XamlTypeParentSignature);
    }

    [Fact]
    public void Exact_fixture_baseline_and_bound_proof_create_only_an_approved_preflight_candidate()
    {
        var evidence = new ModuleSignatureReader(FakePlatform.Complete()).Read();
        var observation = Assert.IsType<ObservedTaskbarSnapshot>(evidence.Observation);
        var proof = new TrustedStructureProof(
            observation.ExplorerIdentity,
            observation.CaptureToken,
            observation.DisplayProof.HostBindings,
            TaskbarSignature.RequiredXamlTypeParentSignature);
        var baseline = new ApprovedCompatibilityBaseline(
            ApprovedIdentity,
            observation.Explorer,
            observation.Modules,
            TaskbarSignature.RequiredXamlTypeParentSignature);

        var signature = evidence.Approve(
            ApprovedIdentity,
            proof,
            new ApprovedBaselineCatalog([baseline]));

        Assert.True(signature.IsApproved);
        Assert.Equal(
            CompatibilityDecision.ProbeRequired,
            CompatibilityPolicy.EvaluatePreflight(ApprovedIdentity, signature).Decision);
    }

    [Fact]
    public void Shell_window_and_taskbar_hosts_must_have_the_same_owner()
    {
        var platform = FakePlatform.Complete();
        platform.ShellWindowOwnerProcessId = 43;

        var evidence = new ModuleSignatureReader(platform).Read();

        AssertWholeSnapshotUnknown(evidence);
        Assert.Equal(EvidenceCollectionStatus.ExplorerSelectionUnknown, evidence.CollectionStatus);
    }

    [Fact]
    public void Shell_owner_change_during_capture_makes_snapshot_unknown()
    {
        var platform = FakePlatform.Complete();
        platform.ShellWindowOwnerSnapshots = [42, 43];

        var evidence = new ModuleSignatureReader(platform).Read();

        AssertWholeSnapshotUnknown(evidence);
        Assert.Equal(EvidenceCollectionStatus.ExplorerSelectionUnknown, evidence.CollectionStatus);
    }

    [Fact]
    public void Ambiguous_taskbar_owner_pids_make_the_whole_snapshot_unknown()
    {
        var platform = FakePlatform.Complete();
        platform.HostSnapshots =
        [
            [
                new RawTaskbarHost(100, "Shell_TrayWnd", 42, 1000, FakePlatform.ValidBounds),
                new RawTaskbarHost(101, "Shell_SecondaryTrayWnd", 43, 1001, FakePlatform.ValidBounds),
            ],
        ];

        var evidence = new ModuleSignatureReader(platform).Read();

        AssertWholeSnapshotUnknown(evidence);
        Assert.Equal(EvidenceCollectionStatus.ExplorerSelectionUnknown, evidence.CollectionStatus);
    }

    [Theory]
    [InlineData("Shell_TrayWnd_Near")]
    [InlineData("shell_traywnd")]
    [InlineData("Shell_SecondaryTrayWnd")]
    public void Missing_exact_primary_taskbar_class_makes_snapshot_unknown(string className)
    {
        var platform = FakePlatform.Complete();
        platform.HostSnapshots =
            [[new RawTaskbarHost(100, className, 42, 1000, FakePlatform.ValidBounds)]];

        var evidence = new ModuleSignatureReader(platform).Read();

        AssertWholeSnapshotUnknown(evidence);
        Assert.Equal(EvidenceCollectionStatus.ExplorerSelectionUnknown, evidence.CollectionStatus);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("user")]
    [InlineData("architecture")]
    [InlineData("path")]
    [InlineData("exited")]
    public void Wrong_explorer_identity_is_rejected(string mutation)
    {
        var platform = FakePlatform.Complete();
        var descriptor = platform.ProcessDescriptions[0];
        platform.ProcessDescriptions =
        [
            mutation switch
            {
                "session" => descriptor with { SessionId = descriptor.SessionId + 1 },
                "user" => descriptor with { UserSid = "S-1-5-21-other" },
                "architecture" => descriptor with { Architecture = Architecture.X86 },
                "path" => descriptor with { ImagePath = @"C:\Windows\not-explorer.exe" },
                "exited" => descriptor with { HasExited = true },
                _ => descriptor,
            },
        ];

        var evidence = new ModuleSignatureReader(platform).Read();

        AssertWholeSnapshotUnknown(evidence);
        Assert.Equal(EvidenceCollectionStatus.ExplorerIdentityInvalid, evidence.CollectionStatus);
    }

    [Fact]
    public void Explorer_restart_during_capture_makes_the_whole_snapshot_unknown()
    {
        var platform = FakePlatform.Complete();
        var descriptor = platform.ProcessDescriptions[0];
        platform.ProcessDescriptions =
        [
            descriptor,
            descriptor with { StartTimeUtcTicks = descriptor.StartTimeUtcTicks + 1 },
        ];

        var evidence = new ModuleSignatureReader(platform).Read();

        AssertWholeSnapshotUnknown(evidence);
        Assert.Equal(EvidenceCollectionStatus.ProcessChanged, evidence.CollectionStatus);
    }

    [Fact]
    public void Module_set_change_during_capture_makes_the_whole_snapshot_unknown()
    {
        var platform = FakePlatform.Complete();
        platform.ModuleSnapshots =
        [
            [FakePlatform.ExplorerPath, FakePlatform.TaskbarModulePath, FakePlatform.XamlModulePath],
            [FakePlatform.ExplorerPath, FakePlatform.TaskbarModulePath],
        ];

        var evidence = new ModuleSignatureReader(platform).Read();

        AssertWholeSnapshotUnknown(evidence);
        Assert.Equal(EvidenceCollectionStatus.ModuleInventoryChanged, evidence.CollectionStatus);
    }

    [Fact]
    public void Earlier_file_replacement_before_final_inventory_check_makes_snapshot_unknown()
    {
        var platform = FakePlatform.Complete();
        platform.BeforeFinalModuleSnapshot = () =>
        {
            var capture = platform.FileCaptures[FakePlatform.TaskbarModulePath];
            platform.FileCaptures[FakePlatform.TaskbarModulePath] = capture with
            {
                After = capture.After with { FileId = capture.After.FileId + 1 },
            };
        };

        var evidence = new ModuleSignatureReader(platform).Read();

        AssertWholeSnapshotUnknown(evidence);
        Assert.Equal(EvidenceCollectionStatus.ModuleFileSnapshotInvalid, evidence.CollectionStatus);
        Assert.Equal(EvidenceFailureReason.FileIdentityChanged, evidence.FailureReason);
    }

    [Fact]
    public void Empty_discovery_inventory_keeps_sanitized_explorer_and_display_but_cannot_approve()
    {
        var platform = FakePlatform.Complete();
        platform.ModuleSnapshots = [[FakePlatform.ExplorerPath]];

        var evidence = new ModuleSignatureReader(platform).Read();

        Assert.NotNull(evidence.Explorer);
        Assert.Empty(evidence.Modules);
        Assert.False(evidence.ModuleListKnown);
        Assert.True(evidence.DisplayEvidenceKnown);
        Assert.Equal(EvidenceCollectionStatus.NoDiscoveryModules, evidence.CollectionStatus);
        Assert.Null(evidence.Observation);
        Assert.Equal(
            CompatibilityDecision.ProbeRequired,
            CompatibilityPolicy.EvaluatePreflight(ApprovedIdentity, evidence.Signature).Decision);
    }

    [Fact]
    public void File_identity_change_during_hash_makes_the_whole_snapshot_unknown()
    {
        var platform = FakePlatform.Complete();
        var capture = platform.FileCaptures[FakePlatform.TaskbarModulePath];
        platform.FileCaptures[FakePlatform.TaskbarModulePath] = capture with
        {
            After = capture.After with { LastWriteTimeUtcTicks = capture.After.LastWriteTimeUtcTicks + 1 },
        };

        var evidence = new ModuleSignatureReader(platform).Read();

        AssertWholeSnapshotUnknown(evidence);
        Assert.Equal(EvidenceCollectionStatus.ModuleFileSnapshotInvalid, evidence.CollectionStatus);
        Assert.Equal(EvidenceFailureReason.FileIdentityChanged, evidence.FailureReason);
    }

    [Fact]
    public void Explorer_file_identity_change_is_reported_at_the_explorer_file_boundary()
    {
        var platform = FakePlatform.Complete();
        var capture = platform.FileCaptures[FakePlatform.ExplorerPath];
        platform.FileCaptures[FakePlatform.ExplorerPath] = capture with
        {
            After = capture.After with { FileId = capture.After.FileId + 1 },
        };

        var evidence = new ModuleSignatureReader(platform).Read();

        AssertWholeSnapshotUnknown(evidence);
        Assert.Equal(EvidenceCollectionStatus.ExplorerFileSnapshotInvalid, evidence.CollectionStatus);
        Assert.Equal(EvidenceFailureReason.FileIdentityChanged, evidence.FailureReason);
    }

    [Theory]
    [InlineData(EvidenceFailureReason.FileOpenFailed)]
    [InlineData(EvidenceFailureReason.FinalPathReadFailed)]
    [InlineData(EvidenceFailureReason.FileIdentityReadFailed)]
    [InlineData(EvidenceFailureReason.FileVersionReadFailed)]
    [InlineData(EvidenceFailureReason.FileHashReadFailed)]
    public void Native_file_boundary_failures_are_reported_as_bounded_reason_codes(
        EvidenceFailureReason failureReason)
    {
        var platform = FakePlatform.Complete();
        platform.FileCaptureFailures[FakePlatform.TaskbarModulePath] =
            new FileEvidenceBoundaryException(failureReason);

        var evidence = new ModuleSignatureReader(platform).Read();

        AssertWholeSnapshotUnknown(evidence);
        Assert.Equal(EvidenceCollectionStatus.ModuleFileSnapshotInvalid, evidence.CollectionStatus);
        Assert.Equal(failureReason, evidence.FailureReason);
    }

    [Fact]
    public void Missing_file_version_resource_is_recorded_as_exact_absence_not_a_read_failure()
    {
        var platform = FakePlatform.Complete();
        var capture = platform.FileCaptures[FakePlatform.TaskbarModulePath];
        platform.FileCaptures[FakePlatform.TaskbarModulePath] = capture with { FileVersion = null! };

        var evidence = new ModuleSignatureReader(platform).Read();

        Assert.Equal(EvidenceCollectionStatus.Complete, evidence.CollectionStatus);
        Assert.Equal(EvidenceFailureReason.None, evidence.FailureReason);
        Assert.True(evidence.ModuleListKnown);
        Assert.Null(evidence.Modules.Single(module => module.FileName == "Taskbar.View.dll").FileVersion);
        Assert.NotNull(evidence.Observation);
    }

    [Fact]
    public void Version_path_identity_change_makes_the_whole_snapshot_unknown()
    {
        var platform = FakePlatform.Complete();
        var capture = platform.FileCaptures[FakePlatform.TaskbarModulePath];
        platform.FileCaptures[FakePlatform.TaskbarModulePath] = capture with
        {
            VersionPathIdentity = capture.VersionPathIdentity with
            {
                FileId = capture.VersionPathIdentity.FileId + 1,
            },
        };

        var evidence = new ModuleSignatureReader(platform).Read();

        AssertWholeSnapshotUnknown(evidence);
        Assert.Equal(EvidenceCollectionStatus.ModuleFileSnapshotInvalid, evidence.CollectionStatus);
        Assert.Equal(EvidenceFailureReason.FileIdentityChanged, evidence.FailureReason);
    }

    [Fact]
    public void Native_capture_records_a_versionless_file_from_one_stable_identity()
    {
        var bytes = "versionless fixture"u8.ToArray();
        var path = Path.Combine(
            Path.GetTempPath(),
            $"CodexQuotaTaskbar-{Guid.NewGuid():N}.dll");
        try
        {
            File.WriteAllBytes(path, bytes);

            using var lease = new NativeModuleEvidencePlatform().OpenStableFile(path);
            var capture = lease.Complete();

            Assert.Null(capture.FileVersion);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), capture.Sha256);
            Assert.Equal(capture.Before, capture.VersionPathIdentity);
            Assert.Equal(capture.Before, capture.After);
            Assert.Equal(Path.GetFullPath(path), capture.SourcePath, ignoreCase: true);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Stable_snapshot_exposes_only_sanitized_base_names_versions_and_hashes()
    {
        var evidence = new ModuleSignatureReader(FakePlatform.Complete()).Read();

        var explorer = Assert.IsType<ModuleFileEvidence>(evidence.Explorer);
        Assert.Equal("explorer.exe", explorer.FileName);
        Assert.DoesNotContain("private-user", explorer.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Matches("^[0-9a-f]{64}$", explorer.Sha256);
        Assert.All(evidence.Modules, module =>
        {
            Assert.Equal(Path.GetFileName(module.FileName), module.FileName);
            if (module.FileVersion is not null)
            {
                Assert.DoesNotContain('\r', module.FileVersion);
                Assert.DoesNotContain('\n', module.FileVersion);
                Assert.InRange(module.FileVersion.Length, 1, 128);
            }

            Assert.Matches("^[0-9a-f]{64}$", module.Sha256);
        });
    }

    [Theory]
    [InlineData("control-version")]
    [InlineData("empty-version")]
    [InlineData("blank-version")]
    [InlineData("overlong-version")]
    [InlineData("path-version")]
    [InlineData("path-file-name")]
    [InlineData("control-file-name")]
    [InlineData("bad-hash")]
    public void Invalid_or_unbounded_diagnostic_metadata_makes_snapshot_unknown(string mutation)
    {
        var platform = FakePlatform.Complete();
        var capture = platform.FileCaptures[FakePlatform.TaskbarModulePath];
        platform.FileCaptures[FakePlatform.TaskbarModulePath] = mutation switch
        {
            "control-version" => capture with { FileVersion = "10.0\r\nprivate" },
            "empty-version" => capture with { FileVersion = string.Empty },
            "blank-version" => capture with { FileVersion = "   " },
            "overlong-version" => capture with { FileVersion = new string('1', 129) },
            "path-version" => capture with { FileVersion = @"10.0 C:\Users\private-user" },
            "path-file-name" => capture with { FileName = @"C:\private-user\Taskbar.View.dll" },
            "control-file-name" => capture with { FileName = "Taskbar\nView.dll" },
            "bad-hash" => capture with { Sha256 = "not-a-hash" },
            _ => capture,
        };

        var evidence = new ModuleSignatureReader(platform).Read();

        AssertWholeSnapshotUnknown(evidence);
        Assert.Equal(
            mutation == "bad-hash"
                ? EvidenceFailureReason.HashInvalid
                : mutation.Contains("version", StringComparison.Ordinal)
                    ? EvidenceFailureReason.FileVersionInvalid
                    : EvidenceFailureReason.FileNameInvalid,
            evidence.FailureReason);
    }

    [Fact]
    public void Unicode_format_characters_cannot_escape_through_diagnostic_file_names()
    {
        const string unsafeName = "Taskbar\u202Eevil.dll";
        var unsafePath = $@"C:\Windows\SystemApps\{unsafeName}";
        var platform = FakePlatform.Complete();
        var template = platform.FileCaptures[FakePlatform.TaskbarModulePath];
        platform.ModuleSnapshots =
        [
            [FakePlatform.ExplorerPath, unsafePath, FakePlatform.XamlModulePath],
        ];
        platform.FileCaptures[unsafePath] = template with
        {
            SourcePath = unsafePath,
            FileName = unsafeName,
        };

        var evidence = new ModuleSignatureReader(platform).Read();

        AssertWholeSnapshotUnknown(evidence);
        Assert.Equal(EvidenceFailureReason.FileNameInvalid, evidence.FailureReason);
    }

    [Fact]
    public void Mixed_dpi_is_bound_to_each_monitor_and_taskbar_host()
    {
        var platform = FakePlatform.Complete();
        platform.DpiByWindow[100] = 96;
        platform.DpiByWindow[101] = 144;

        var evidence = new ModuleSignatureReader(platform).Read();

        Assert.True(evidence.DisplayEvidenceKnown);
        Assert.Equal(2, evidence.TaskbarHostCount);
        Assert.Equal(2, evidence.MonitorCount);
        Assert.Equal(
            [new MonitorDpi(0, 96, 96), new MonitorDpi(1, 144, 144)],
            evidence.MonitorDpiValues);
    }

    [Fact]
    public void Multiple_taskbar_hosts_cannot_collapse_onto_one_monitor()
    {
        var platform = FakePlatform.Complete();
        platform.HostSnapshots =
        [
            [
                new RawTaskbarHost(100, "Shell_TrayWnd", 42, 1000, FakePlatform.ValidBounds),
                new RawTaskbarHost(101, "Shell_SecondaryTrayWnd", 42, 1000, FakePlatform.ValidBounds),
            ],
        ];
        platform.DpiByWindow[101] = platform.DpiByWindow[100];

        var evidence = new ModuleSignatureReader(platform).Read();

        Assert.False(evidence.DisplayEvidenceKnown);
        Assert.Equal(EvidenceCollectionStatus.ExplorerSelectionUnknown, evidence.CollectionStatus);
        Assert.Null(evidence.Observation);
    }

    [Fact]
    public void Display_topology_change_during_dpi_capture_is_unknown_and_fails_closed()
    {
        var platform = FakePlatform.Complete();
        platform.HostSnapshots =
        [
            FakePlatform.ValidHosts,
            [new RawTaskbarHost(100, "Shell_TrayWnd", 42, 1000, FakePlatform.ValidBounds)],
        ];

        var evidence = new ModuleSignatureReader(platform).Read();

        Assert.False(evidence.DisplayEvidenceKnown);
        Assert.Equal(EvidenceCollectionStatus.DisplayTopologyChanged, evidence.CollectionStatus);
        Assert.Null(evidence.MonitorCount);
        Assert.Empty(evidence.MonitorDpiValues);
        Assert.Equal(
            CompatibilityDecision.ProbeRequired,
            CompatibilityPolicy.EvaluatePreflight(ApprovedIdentity, evidence.Signature).Decision);
    }

    [Fact]
    public void Taskbar_host_change_after_final_file_process_and_module_checks_fails_closed()
    {
        var platform = FakePlatform.Complete();
        platform.BeforeFinalDisplaySnapshot = () =>
            platform.HostSnapshots =
            [
                [
                    new RawTaskbarHost(
                        100,
                        "Shell_TrayWnd",
                        42,
                        1000,
                        new RawWindowBounds(1, 1000, 1920, 1080)),
                    FakePlatform.ValidHosts[1],
                ],
            ];

        var evidence = new ModuleSignatureReader(platform).Read();

        AssertLateDisplayChangeFailsClosed(evidence);
    }

    [Fact]
    public void Dpi_only_change_after_final_file_process_and_module_checks_fails_closed()
    {
        var platform = FakePlatform.Complete();
        platform.BeforeFinalDisplaySnapshot = () => platform.DpiByWindow[100] = 120;

        var evidence = new ModuleSignatureReader(platform).Read();

        AssertLateDisplayChangeFailsClosed(evidence);
    }

    [Fact]
    public void Independent_monitor_change_after_final_file_process_and_module_checks_fails_closed()
    {
        var platform = FakePlatform.Complete();
        platform.BeforeFinalDisplaySnapshot = () =>
            platform.MonitorSnapshots =
            [
                [
                    new RawDisplayMonitor(
                        1000,
                        new RawWindowBounds(0, 0, 1919, 1080),
                        IsPrimary: true),
                    FakePlatform.ValidMonitors[1],
                ],
            ];

        var evidence = new ModuleSignatureReader(platform).Read();

        AssertLateDisplayChangeFailsClosed(evidence);
    }

    [Fact]
    public void Monitor_count_is_independent_from_taskbar_host_count()
    {
        var platform = FakePlatform.Complete();
        platform.HostSnapshots = [[FakePlatform.ValidHosts[0]]];

        var evidence = new ModuleSignatureReader(platform).Read();

        Assert.True(evidence.DisplayEvidenceKnown);
        Assert.Equal(2, evidence.MonitorCount);
        Assert.Equal(1, evidence.TaskbarHostCount);
        Assert.Equal([new MonitorDpi(0, 96, 96)], evidence.MonitorDpiValues);
        Assert.NotNull(evidence.Observation);
        Assert.Equal(
            CompatibilityDecision.ProbeRequired,
            CompatibilityPolicy.EvaluatePreflight(ApprovedIdentity, evidence.Signature).Decision);
    }

    [Theory]
    [InlineData("dpi")]
    [InlineData("scope")]
    public void Dpi_failure_or_awareness_restore_failure_is_unknown_and_fails_closed(string failure)
    {
        var platform = FakePlatform.Complete();
        if (failure == "dpi")
        {
            platform.DpiFailure = new InvalidOperationException("DPI unavailable");
        }
        else
        {
            platform.DpiScopeRestoreSucceeded = false;
        }

        var evidence = new ModuleSignatureReader(platform).Read();

        Assert.False(evidence.DisplayEvidenceKnown);
        Assert.Equal(
            CompatibilityDecision.ProbeRequired,
            CompatibilityPolicy.EvaluatePreflight(ApprovedIdentity, evidence.Signature).Decision);
    }

    private static void AssertWholeSnapshotUnknown(TaskbarModuleEvidence evidence)
    {
        Assert.Null(evidence.Explorer);
        Assert.Empty(evidence.Modules);
        Assert.False(evidence.ModuleListKnown);
        Assert.False(evidence.DisplayEvidenceKnown);
        Assert.Null(evidence.Observation);
        Assert.Equal(TaskbarSignature.Unknown, evidence.Signature);
    }

    private static void AssertLateDisplayChangeFailsClosed(TaskbarModuleEvidence evidence)
    {
        Assert.False(evidence.DisplayEvidenceKnown);
        Assert.Equal(EvidenceCollectionStatus.DisplayTopologyChanged, evidence.CollectionStatus);
        Assert.Null(evidence.MonitorCount);
        Assert.Empty(evidence.MonitorDpiValues);
        Assert.Null(evidence.Observation);
        Assert.Equal(
            CompatibilityDecision.ProbeRequired,
            CompatibilityPolicy.EvaluatePreflight(ApprovedIdentity, evidence.Signature).Decision);
    }

    private sealed class FakePlatform : IModuleEvidencePlatform
    {
        internal const string ExplorerPath = @"C:\Windows\explorer.exe";
        internal const string TaskbarModulePath = @"C:\Windows\SystemApps\Taskbar.View.dll";
        internal const string XamlModulePath = @"C:\Windows\System32\Windows.UI.Xaml.dll";
        internal static readonly RawWindowBounds ValidBounds = new(0, 1000, 1920, 1080);
        internal static readonly IReadOnlyList<RawTaskbarHost> ValidHosts =
        [
            new RawTaskbarHost(100, "Shell_TrayWnd", 42, 1000, ValidBounds),
            new RawTaskbarHost(101, "Shell_SecondaryTrayWnd", 42, 1001, ValidBounds),
        ];
        internal static readonly IReadOnlyList<RawDisplayMonitor> ValidMonitors =
        [
            new RawDisplayMonitor(
                1000,
                new RawWindowBounds(0, 0, 1920, 1080),
                IsPrimary: true),
            new RawDisplayMonitor(
                1001,
                new RawWindowBounds(1920, 0, 3840, 1080),
                IsPrimary: false),
        ];

        public int CurrentSessionId { get; init; } = 7;

        public string CurrentUserSid { get; init; } = "S-1-5-21-fixture";

        public string ExpectedExplorerPath { get; init; } = ExplorerPath;

        public IReadOnlyList<IReadOnlyList<RawTaskbarHost>> HostSnapshots { get; set; } = [ValidHosts];

        public IReadOnlyList<IReadOnlyList<RawDisplayMonitor>> MonitorSnapshots { get; set; } =
            [ValidMonitors];

        public IReadOnlyList<RawExplorerProcess> ProcessDescriptions { get; set; } =
        [
            new RawExplorerProcess(
                42,
                638000000000000000,
                7,
                "S-1-5-21-fixture",
                Architecture.X64,
                ExplorerPath,
                HasExited: false),
        ];

        public IReadOnlyList<IReadOnlyList<string>> ModuleSnapshots { get; set; } =
        [
            [ExplorerPath, TaskbarModulePath, XamlModulePath],
        ];

        public Dictionary<string, StableFileCapture> FileCaptures { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, Exception> FileCaptureFailures { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<long, int> DpiByWindow { get; } = new()
        {
            [100] = 96,
            [101] = 144,
        };

        public Exception? DpiFailure { get; set; }

        public bool DpiScopeRestoreSucceeded { get; set; } = true;

        public Action? BeforeFinalModuleSnapshot { get; set; }

        public Action? BeforeFinalDisplaySnapshot { get; set; }

        public int ShellWindowOwnerProcessId
        {
            get => ShellWindowOwnerSnapshots[0];
            set => ShellWindowOwnerSnapshots = [value];
        }

        public IReadOnlyList<int> ShellWindowOwnerSnapshots { get; set; } = [42];

        private int hostReadIndex;
        private int monitorReadIndex;
        private int shellOwnerReadIndex;
        private int processReadIndex;
        private int moduleReadIndex;

        private FakePlatform()
        {
            FileCaptures[ExplorerPath] = Capture(
                ExplorerPath,
                "explorer.exe",
                "10.0.26200.1 (WinBuild.160101.0800)",
                new string('a', 64),
                fileId: 1);
            FileCaptures[TaskbarModulePath] = Capture(
                TaskbarModulePath,
                "Taskbar.View.dll",
                "10.0.26200.1",
                new string('b', 64),
                fileId: 2);
            FileCaptures[XamlModulePath] = Capture(
                XamlModulePath,
                "Windows.UI.Xaml.dll",
                "10.0.26200.1",
                new string('c', 64),
                fileId: 3);
        }

        public static FakePlatform Complete() => new();

        public IReadOnlyList<RawTaskbarHost> EnumerateTaskbarHosts()
        {
            if (hostReadIndex == 3)
            {
                BeforeFinalDisplaySnapshot?.Invoke();
            }

            return ReadSequence(HostSnapshots, ref hostReadIndex);
        }

        public IReadOnlyList<RawDisplayMonitor> EnumerateDisplayMonitors() =>
            ReadSequence(MonitorSnapshots, ref monitorReadIndex);

        public int GetShellWindowOwnerProcessId() =>
            ReadSequence(ShellWindowOwnerSnapshots, ref shellOwnerReadIndex);

        public IExplorerProcessLease OpenExplorer(int processId)
        {
            if (processId != 42)
            {
                throw new InvalidOperationException("Unexpected process.");
            }

            return new FakeProcessLease(this);
        }

        public IStableFileCaptureLease OpenStableFile(string path)
        {
            if (FileCaptureFailures.TryGetValue(path, out var failure))
            {
                throw failure;
            }

            return new FakeStableFileCaptureLease(this, path, FileCaptures[path]);
        }

        public IDpiAwarenessScope EnterPerMonitorV2DpiAwareness() =>
            new FakeDpiScope(DpiScopeRestoreSucceeded);

        public int GetDpiForWindow(long windowToken)
        {
            if (DpiFailure is not null)
            {
                throw DpiFailure;
            }

            return DpiByWindow[windowToken];
        }

        private RawExplorerProcess ReadProcessDescription() =>
            ReadSequence(ProcessDescriptions, ref processReadIndex);

        private IReadOnlyList<string> ReadModules()
        {
            if (moduleReadIndex == 1)
            {
                BeforeFinalModuleSnapshot?.Invoke();
            }

            return ReadSequence(ModuleSnapshots, ref moduleReadIndex);
        }

        private static T ReadSequence<T>(IReadOnlyList<T> values, ref int index)
        {
            if (values.Count == 0)
            {
                throw new InvalidOperationException("Sequence is empty.");
            }

            var value = values[Math.Min(index, values.Count - 1)];
            index++;
            return value;
        }

        private static StableFileCapture Capture(
            string path,
            string fileName,
            string fileVersion,
            string sha256,
            ulong fileId)
        {
            var identity = new StableFileIdentity(10, fileId, 1024, 638000000000000000);
            return new StableFileCapture(
                path,
                fileName,
                fileVersion,
                sha256,
                identity,
                identity,
                identity);
        }

        private sealed class FakeProcessLease(FakePlatform owner) : IExplorerProcessLease
        {
            public RawExplorerProcess Describe() => owner.ReadProcessDescription();

            public IReadOnlyList<string> EnumerateModulePaths() => owner.ReadModules();

            public void Dispose()
            {
            }
        }

        private sealed class FakeStableFileCaptureLease(
            FakePlatform owner,
            string path,
            StableFileCapture initialCapture) : IStableFileCaptureLease
        {
            public StableFileCapture Complete() =>
                initialCapture with { After = owner.FileCaptures[path].After };

            public void Dispose()
            {
            }
        }

        private sealed class FakeDpiScope(bool restoreSucceeded) : IDpiAwarenessScope
        {
            public bool RestoreSucceeded { get; private set; } = true;

            public void Dispose()
            {
                RestoreSucceeded = restoreSucceeded;
            }
        }
    }
}
