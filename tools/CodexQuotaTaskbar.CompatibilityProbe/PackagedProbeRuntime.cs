using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using CodexQuotaTaskbar.BridgeControl.Injection;
using CodexQuotaTaskbar.BridgeControl.Safety;
using CodexQuotaTaskbar.CompatibilityProbe.Safety;

namespace CodexQuotaTaskbar.CompatibilityProbe;

internal sealed record BridgePayloadDescriptor(
    string Sha256Hex,
    Func<Stream> OpenPayload);

internal interface IBridgePayloadSource
{
    bool TryGetPayload(out BridgePayloadDescriptor descriptor);
}

internal sealed class EmbeddedBridgePayloadSource : IBridgePayloadSource
{
    internal const string DllResourceName =
        "CodexQuotaTaskbar.CompatibilityProbe.Payload.CodexQuotaTaskbar.Bridge.dll";
    internal const string HashResourceName =
        "CodexQuotaTaskbar.CompatibilityProbe.Payload.CodexQuotaTaskbar.Bridge.sha256";
    private readonly Assembly assembly;

    internal EmbeddedBridgePayloadSource()
        : this(typeof(EmbeddedBridgePayloadSource).Assembly)
    {
    }

    internal EmbeddedBridgePayloadSource(Assembly assembly) =>
        this.assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));

    public bool TryGetPayload(out BridgePayloadDescriptor descriptor)
    {
        descriptor = null!;
        try
        {
            using var hashStream = assembly.GetManifestResourceStream(HashResourceName);
            using var payloadProbe = assembly.GetManifestResourceStream(DllResourceName);
            if (hashStream is null || payloadProbe is null ||
                hashStream.CanSeek && hashStream.Length > 128 ||
                payloadProbe.CanSeek && payloadProbe.Length <= 0)
            {
                return false;
            }

            using var reader = new StreamReader(
                hashStream,
                Encoding.ASCII,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 128,
                leaveOpen: false);
            var digest = reader.ReadToEnd().Trim().ToLowerInvariant();
            if (digest.Length != 64 || !digest.All(IsAsciiHexDigit))
            {
                return false;
            }

            descriptor = new BridgePayloadDescriptor(
                digest,
                () => assembly.GetManifestResourceStream(DllResourceName) ??
                    throw new InvalidOperationException("The embedded bridge payload is unavailable."));
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException or
                ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsAsciiHexDigit(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f';
}

internal sealed class PackagedProbeRuntime : IProbeRuntime
{
    private readonly IBridgePayloadSource payloadSource;
    private readonly IExplorerProcessApi processApi;
    private readonly ActivationJournal activationJournal;
    private readonly Func<ExplorerResponsivenessProbe> responsivenessFactory;

    internal PackagedProbeRuntime()
        : this(
            new EmbeddedBridgePayloadSource(),
            new Win32ExplorerProcessApi(),
            new ActivationJournal(),
            static () => new ExplorerResponsivenessProbe())
    {
    }

    internal PackagedProbeRuntime(
        IBridgePayloadSource payloadSource,
        IExplorerProcessApi processApi,
        ActivationJournal activationJournal,
        Func<ExplorerResponsivenessProbe> responsivenessFactory)
    {
        this.payloadSource = payloadSource ?? throw new ArgumentNullException(nameof(payloadSource));
        this.processApi = processApi ?? throw new ArgumentNullException(nameof(processApi));
        this.activationJournal = activationJournal ??
            throw new ArgumentNullException(nameof(activationJournal));
        this.responsivenessFactory = responsivenessFactory ??
            throw new ArgumentNullException(nameof(responsivenessFactory));
    }

    public ProbeActivationResult Activate(ProbeEvidence evidence, bool explicitRetry)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!TryCreatePermit(evidence, explicitRetry, out var permit, out var identity))
        {
            return ProbeActivationResult.Failed(ProbeActivationStatus.EvidenceUnavailable);
        }

        if (!payloadSource.TryGetPayload(out var payload))
        {
            return ProbeActivationResult.Failed(ProbeActivationStatus.PayloadUnavailable);
        }

        try
        {
            var localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                return ProbeActivationResult.Failed(ProbeActivationStatus.PayloadUnavailable);
            }

            var injector = new ExplorerInjector(
                processApi,
                new BridgeArtifactStore(localAppData, payload.Sha256Hex, payload.OpenPayload),
                new BridgeImageInspector(),
                new ActivationJournalStore(activationJournal));
            var injected = injector.InjectAsync(permit).GetAwaiter().GetResult();
            if (!injected.Succeeded || injected.Session is null)
            {
                return ProbeActivationResult.Failed(ProbeActivationStatus.Rejected);
            }

            return ProbeActivationResult.Ready(
                new PackagedActiveProbeSession(
                    injector,
                    injected.Session,
                    responsivenessFactory(),
                    identity));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException or
                InvalidOperationException or ArgumentException or NotSupportedException or
                OverflowException)
        {
            return ProbeActivationResult.Failed(ProbeActivationStatus.BoundaryFailure);
        }
    }

    public ProbeRecordedDetachStatus DetachRecorded()
    {
        var gate = ReadRecordedActivationGate();
        if (gate == RecordedActivationGate.AlreadyClean)
        {
            return ProbeRecordedDetachStatus.AlreadyClean;
        }

        if (gate != RecordedActivationGate.RecoveryRequired ||
            !payloadSource.TryGetPayload(out var payload))
        {
            return ProbeRecordedDetachStatus.Unsafe;
        }

        try
        {
            var localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                return ProbeRecordedDetachStatus.Unsafe;
            }

            var store = new BridgeArtifactStore(
                localAppData,
                payload.Sha256Hex,
                payload.OpenPayload);
            using var artifact = store.Materialize();
            var contract = new BridgeImageInspector().InspectAndValidate(artifact);
            var recovery = new ExplorerRecoveryController(
                processApi,
                new ActivationRecoveryJournalStore(activationJournal));
            var result = recovery.DetachRecordedAsync(contract).GetAwaiter().GetResult();
            return result.Outcome switch
            {
                ExplorerRecoveryOutcome.Clean => ProbeRecordedDetachStatus.Clean,
                ExplorerRecoveryOutcome.AlreadyClean => ProbeRecordedDetachStatus.AlreadyClean,
                _ => ProbeRecordedDetachStatus.Unsafe,
            };
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException or
                InvalidOperationException or ArgumentException or NotSupportedException or
                OverflowException)
        {
            return ProbeRecordedDetachStatus.Unsafe;
        }
    }

    public bool RestartExplorerGracefully() => false;

    private RecordedActivationGate ReadRecordedActivationGate()
    {
        IActivationSessionLease? lease = null;
        var result = RecordedActivationGate.Unavailable;
        try
        {
            var acquired = activationJournal.TryAcquireSessionLease();
            if (!acquired.Succeeded || acquired.Lease is null)
            {
                return result;
            }

            lease = acquired.Lease;
            var status = activationJournal.ReadStatus();
            if (status.Outcome == ActivationJournalOutcome.Allowed &&
                status.Record is null or { State: ActivationState.Clean })
            {
                result = RecordedActivationGate.AlreadyClean;
            }
            else if (status.Outcome == ActivationJournalOutcome.Blocked)
            {
                result = RecordedActivationGate.RecoveryRequired;
            }
        }
        catch (Exception)
        {
            result = RecordedActivationGate.Unavailable;
        }

        try
        {
            lease?.Dispose();
        }
        catch (Exception)
        {
            return RecordedActivationGate.Unavailable;
        }

        return result;
    }

    private bool TryCreatePermit(
        ProbeEvidence evidence,
        bool explicitRetry,
        out LiveActivationPermit permit,
        out ExplorerResponsivenessIdentity identity)
    {
        permit = null!;
        identity = null!;
        var observation = evidence.Taskbar.Observation;
        var explorerFile = evidence.Taskbar.Explorer;
        var build = evidence.Identity.BuildNumber;
        if (observation is null || explorerFile is null || build is null || build <= 0)
        {
            return false;
        }

        var explorer = observation.ExplorerIdentity;
        try
        {
            var startTime = new DateTime(explorer.StartTimeUtcTicks, DateTimeKind.Utc);
            var creationFileTime = checked((ulong)startTime.ToFileTimeUtc());
            var sessionId = checked((uint)explorer.SessionId);
            identity = new ExplorerResponsivenessIdentity(
                explorer.ProcessId,
                creationFileTime,
                sessionId,
                explorer.UserSid);
            if (sessionId != processApi.CurrentSessionId ||
                !string.Equals(
                    identity.UserSid,
                    processApi.CurrentUserSid,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            permit = LiveActivationPermit.Issue(
                AppVersion(),
                build.Value,
                explorerFile.Sha256,
                CreateTaskbarDigest(evidence.Taskbar),
                explorer.ProcessId,
                creationFileTime,
                explorer.CanonicalImagePath,
                explicitRetry);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or SecurityException or OverflowException)
        {
            return false;
        }
    }

    private static string AppVersion() =>
        typeof(PackagedProbeRuntime).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    private static string CreateTaskbarDigest(
        Windows.TaskbarModuleEvidence evidence)
    {
        var material = new StringBuilder();
        foreach (var module in evidence.Modules
                     .OrderBy(module => module.FileName, StringComparer.Ordinal)
                     .ThenBy(module => module.FileVersion, StringComparer.Ordinal)
                     .ThenBy(module => module.Sha256, StringComparer.Ordinal))
        {
            material.Append(module.FileName)
                .Append('\0')
                .Append(module.FileVersion)
                .Append('\0')
                .Append(module.Sha256)
                .Append('\n');
        }

        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString())))
            .ToLowerInvariant();
    }

    private enum RecordedActivationGate
    {
        AlreadyClean,
        RecoveryRequired,
        Unavailable,
    }

    private sealed class PackagedActiveProbeSession : IActiveProbeSession
    {
        private readonly ExplorerInjector injector;
        private readonly ExplorerInjector.ExplorerProbeSession session;
        private readonly ExplorerResponsivenessProbe responsiveness;
        private readonly ExplorerResponsivenessIdentity identity;
        private readonly object syncRoot = new();
        private bool detachAttempted;
        private bool clean;
        private bool disposed;

        internal PackagedActiveProbeSession(
            ExplorerInjector injector,
            ExplorerInjector.ExplorerProbeSession session,
            ExplorerResponsivenessProbe responsiveness,
            ExplorerResponsivenessIdentity identity)
        {
            this.injector = injector ?? throw new ArgumentNullException(nameof(injector));
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            this.responsiveness = responsiveness ??
                throw new ArgumentNullException(nameof(responsiveness));
            this.identity = identity ?? throw new ArgumentNullException(nameof(identity));
        }

        public ExplorerResponsivenessStatus SampleResponsiveness()
        {
            lock (syncRoot)
            {
                return disposed
                    ? ExplorerResponsivenessStatus.Unsafe
                    : responsiveness.Sample(identity).Status;
            }
        }

        public bool MarkUnsafe()
        {
            lock (syncRoot)
            {
                return !disposed && injector.MarkUnsafe(session);
            }
        }

        public ProbeRecordedDetachStatus Detach()
        {
            lock (syncRoot)
            {
                if (clean)
                {
                    return ProbeRecordedDetachStatus.AlreadyClean;
                }

                if (disposed)
                {
                    return ProbeRecordedDetachStatus.Unsafe;
                }

                detachAttempted = true;
            }

            var result = injector.DetachAsync(session).GetAwaiter().GetResult();
            var status = result.Outcome switch
            {
                ExplorerDetachOutcome.Clean => ProbeRecordedDetachStatus.Clean,
                ExplorerDetachOutcome.AlreadyClean => ProbeRecordedDetachStatus.AlreadyClean,
                _ => ProbeRecordedDetachStatus.Unsafe,
            };
            if (status is ProbeRecordedDetachStatus.Clean or ProbeRecordedDetachStatus.AlreadyClean)
            {
                lock (syncRoot)
                {
                    clean = true;
                }
            }

            return status;
        }

        public void Dispose()
        {
            bool shouldDetach;
            lock (syncRoot)
            {
                if (disposed)
                {
                    return;
                }

                shouldDetach = !detachAttempted && !clean;
            }

            if (shouldDetach)
            {
                try
                {
                    _ = Detach();
                }
                catch (Exception)
                {
                    // The activation journal remains fail-closed.
                }
            }

            lock (syncRoot)
            {
                disposed = true;
            }

            session.ReleaseResources();
        }
    }
}
