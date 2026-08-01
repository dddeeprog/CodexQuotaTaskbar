using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexQuotaTaskbar.BridgeControl.Safety;

public enum ActivationState
{
    Pending,
    Stable,
    Clean,
    Unsafe,
}

public enum ActivationJournalOutcome
{
    Allowed,
    PendingPersisted,
    TransitionPersisted,
    Blocked,
    InvalidRecord,
    InvalidRequest,
    InvalidTransition,
    ActivationMismatch,
    StorageFailure,
}

public sealed record ActivationStartRequest(
    string AppVersion,
    int WindowsBuild,
    string ExplorerSignatureSha256,
    string TaskbarSignatureSha256,
    int ExplorerProcessId,
    ulong ExplorerCreationTimeFileTime100ns);

public sealed record ActivationJournalRecord(
    string AppVersion,
    int WindowsBuild,
    string ExplorerSignatureSha256,
    string TaskbarSignatureSha256,
    int ExplorerProcessId,
    ulong ExplorerCreationTimeFileTime100ns,
    Guid ActivationId,
    DateTimeOffset StartedUtc,
    ActivationState State);

public sealed record ActivationJournalResult
{
    internal ActivationJournalResult(
        ActivationJournalOutcome outcome,
        ActivationJournalRecord? record)
    {
        Outcome = outcome;
        Record = record;
    }

    public ActivationJournalOutcome Outcome { get; }

    public ActivationJournalRecord? Record { get; }

    public bool Succeeded => Outcome is
        ActivationJournalOutcome.Allowed or
        ActivationJournalOutcome.PendingPersisted or
        ActivationJournalOutcome.TransitionPersisted;
}

internal enum ActivationSessionLeaseOutcome
{
    Acquired,
    Busy,
    StorageFailure,
}

internal interface IActivationSessionLease : IDisposable
{
}

internal sealed record ActivationSessionLeaseResult(
    ActivationSessionLeaseOutcome Outcome,
    IActivationSessionLease? Lease)
{
    internal bool Succeeded =>
        Outcome == ActivationSessionLeaseOutcome.Acquired && Lease is not null;
}

internal interface IActivationJournalMutexGate
{
    IDisposable? TryEnter(string mutexName, TimeSpan timeout);
}

internal interface IActivationJournalPathInspector
{
    bool TryGetAttributes(
        string path,
        out FileAttributes attributes,
        out bool exists);
}

internal sealed class FileSystemActivationJournalPathInspector :
    IActivationJournalPathInspector
{
    internal static FileSystemActivationJournalPathInspector Instance { get; } = new();

    private FileSystemActivationJournalPathInspector()
    {
    }

    public bool TryGetAttributes(
        string path,
        out FileAttributes attributes,
        out bool exists)
    {
        try
        {
            attributes = File.GetAttributes(path);
            exists = true;
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            exists = false;
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            exists = false;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                SecurityException or
                ArgumentException or
                NotSupportedException)
        {
            attributes = default;
            exists = false;
            return false;
        }
    }
}

internal sealed class NamedActivationJournalMutexGate : IActivationJournalMutexGate
{
    internal static NamedActivationJournalMutexGate Instance { get; } = new();

    private NamedActivationJournalMutexGate()
    {
    }

    public IDisposable? TryEnter(string mutexName, TimeSpan timeout)
    {
        var mutex = new Mutex(initiallyOwned: false, mutexName);
        try
        {
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(timeout);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                mutex.Dispose();
                return null;
            }

            return new OwnedMutex(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    private sealed class OwnedMutex(Mutex mutex) : IDisposable
    {
        private Mutex? mutex = mutex;

        public void Dispose()
        {
            var ownedMutex = Interlocked.Exchange(ref mutex, null);
            if (ownedMutex is null)
            {
                return;
            }

            try
            {
                ownedMutex.ReleaseMutex();
            }
            finally
            {
                ownedMutex.Dispose();
            }
        }
    }
}

public sealed class ActivationJournal
{
    private const int CurrentSchemaVersion = 1;
    private const int MaximumAppVersionLength = 128;
    private static readonly TimeSpan DefaultMutexTimeout = TimeSpan.FromSeconds(5);
    internal const int MaximumJournalBytes = 16 * 1024;

    private static readonly HashSet<string> ExpectedPropertyNames = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "appVersion",
        "windowsBuild",
        "explorerSignatureSha256",
        "taskbarSignatureSha256",
        "explorerProcessId",
        "explorerCreationTimeFileTime100ns",
        "activationId",
        "startedUtc",
        "state",
    };

    private readonly object syncRoot = new();
    private readonly IActivationJournalMutexGate mutexGate;
    private readonly TimeSpan mutexTimeout;
    private readonly IActivationJournalPathInspector pathInspector;
    private readonly Func<DateTimeOffset> utcNow;

    public ActivationJournal()
        : this(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            static () => DateTimeOffset.UtcNow)
    {
    }

    internal ActivationJournal(
        string localAppDataRoot,
        Func<DateTimeOffset> utcNow)
        : this(
            localAppDataRoot,
            utcNow,
            NamedActivationJournalMutexGate.Instance,
            DefaultMutexTimeout,
            FileSystemActivationJournalPathInspector.Instance)
    {
    }

    internal ActivationJournal(
        string localAppDataRoot,
        Func<DateTimeOffset> utcNow,
        IActivationJournalMutexGate mutexGate,
        TimeSpan mutexTimeout)
        : this(
            localAppDataRoot,
            utcNow,
            mutexGate,
            mutexTimeout,
            FileSystemActivationJournalPathInspector.Instance)
    {
    }

    internal ActivationJournal(
        string localAppDataRoot,
        Func<DateTimeOffset> utcNow,
        IActivationJournalMutexGate mutexGate,
        TimeSpan mutexTimeout,
        IActivationJournalPathInspector pathInspector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppDataRoot);
        this.utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        this.mutexGate = mutexGate ?? throw new ArgumentNullException(nameof(mutexGate));
        this.pathInspector = pathInspector ?? throw new ArgumentNullException(nameof(pathInspector));
        if (mutexTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(mutexTimeout));
        }

        this.mutexTimeout = mutexTimeout;
        JournalPath = Path.Combine(
            Path.GetFullPath(localAppDataRoot),
            "CodexQuotaTaskbar",
            "Probe",
            "activation.json");
        SessionLeasePath = Path.Combine(
            Path.GetDirectoryName(JournalPath)!,
            "activation.session.lock");
        MutexName = CreateMutexName(JournalPath);
    }

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexQuotaTaskbar",
        "Probe",
        "activation.json");

    public string JournalPath { get; }

    internal string SessionLeasePath { get; }

    internal string MutexName { get; }

    internal ActivationSessionLeaseResult TryAcquireSessionLease()
    {
        lock (syncRoot)
        {
            IDisposable? journalLease;
            try
            {
                journalLease = mutexGate.TryEnter(MutexName, mutexTimeout);
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                return SessionLeaseResult(ActivationSessionLeaseOutcome.StorageFailure);
            }

            if (journalLease is null)
            {
                return SessionLeaseResult(ActivationSessionLeaseOutcome.StorageFailure);
            }

            ActivationSessionLeaseResult result;
            try
            {
                result = OpenSessionLease();
            }
            catch
            {
                TryDisposeLease(journalLease);
                throw;
            }

            try
            {
                journalLease.Dispose();
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                result.Lease?.Dispose();
                return SessionLeaseResult(ActivationSessionLeaseOutcome.StorageFailure);
            }

            return result;
        }
    }

    public ActivationJournalResult ReadStatus() =>
        ExecuteWithExclusiveAccess(
            () =>
            {
                if (!TryGetUtcNow(out var now))
                {
                    return Result(ActivationJournalOutcome.StorageFailure);
                }

                var read = ReadRecord(now);
                return read.Kind switch
                {
                    JournalReadKind.Missing => Result(ActivationJournalOutcome.Allowed),
                    JournalReadKind.Invalid => Result(ActivationJournalOutcome.InvalidRecord),
                    JournalReadKind.StorageFailure => Result(ActivationJournalOutcome.StorageFailure),
                    JournalReadKind.Valid when read.Record!.State == ActivationState.Clean =>
                        Result(ActivationJournalOutcome.Allowed, read.Record),
                    JournalReadKind.Valid => Result(ActivationJournalOutcome.Blocked, read.Record),
                    _ => Result(ActivationJournalOutcome.StorageFailure),
                };
            });

    public ActivationJournalResult TryBegin(
        ActivationStartRequest request,
        bool explicitRetry = false)
    {
        if (!TryNormalizeRequest(request, out var normalizedRequest))
        {
            return Result(ActivationJournalOutcome.InvalidRequest);
        }

        return ExecuteWithExclusiveAccess(
            () =>
            {
                if (!TryGetUtcNow(out var now))
                {
                    return Result(ActivationJournalOutcome.StorageFailure);
                }

                var read = ReadRecord(now);
                if (read.Kind == JournalReadKind.Invalid)
                {
                    return Result(ActivationJournalOutcome.InvalidRecord);
                }

                if (read.Kind == JournalReadKind.StorageFailure)
                {
                    return Result(ActivationJournalOutcome.StorageFailure);
                }

                if (read.Kind == JournalReadKind.Valid &&
                    read.Record!.State != ActivationState.Clean &&
                    !explicitRetry)
                {
                    return Result(ActivationJournalOutcome.Blocked, read.Record);
                }

                var pending = new ActivationJournalRecord(
                    normalizedRequest.AppVersion,
                    normalizedRequest.WindowsBuild,
                    normalizedRequest.ExplorerSignatureSha256,
                    normalizedRequest.TaskbarSignatureSha256,
                    normalizedRequest.ExplorerProcessId,
                    normalizedRequest.ExplorerCreationTimeFileTime100ns,
                    Guid.NewGuid(),
                    now,
                    ActivationState.Pending);

                return WriteRecord(pending)
                    ? Result(ActivationJournalOutcome.PendingPersisted, pending)
                    : Result(ActivationJournalOutcome.StorageFailure, read.Record);
            });
    }

    public ActivationJournalResult TryTransition(
        Guid activationId,
        ActivationState nextState)
    {
        if (activationId == Guid.Empty)
        {
            return Result(ActivationJournalOutcome.InvalidRequest);
        }

        return ExecuteWithExclusiveAccess(
            () =>
            {
                if (!TryGetUtcNow(out var now))
                {
                    return Result(ActivationJournalOutcome.StorageFailure);
                }

                var read = ReadRecord(now);
                if (read.Kind == JournalReadKind.Invalid)
                {
                    return Result(ActivationJournalOutcome.InvalidRecord);
                }

                if (read.Kind == JournalReadKind.StorageFailure)
                {
                    return Result(ActivationJournalOutcome.StorageFailure);
                }

                if (read.Kind == JournalReadKind.Missing ||
                    read.Record!.ActivationId != activationId)
                {
                    return Result(ActivationJournalOutcome.ActivationMismatch, read.Record);
                }

                if (!IsLegalTransition(read.Record.State, nextState))
                {
                    return Result(ActivationJournalOutcome.InvalidTransition, read.Record);
                }

                var updated = read.Record with { State = nextState };
                return WriteRecord(updated)
                    ? Result(ActivationJournalOutcome.TransitionPersisted, updated)
                    : Result(ActivationJournalOutcome.StorageFailure, read.Record);
            });
    }

    internal ActivationJournalResult TryRecoverToClean(
        Guid activationId,
        int explorerProcessId,
        ulong explorerCreationTimeFileTime100ns)
    {
        if (activationId == Guid.Empty ||
            explorerProcessId <= 0 ||
            explorerCreationTimeFileTime100ns == 0)
        {
            return Result(ActivationJournalOutcome.InvalidRequest);
        }

        return ExecuteWithExclusiveAccess(
            () =>
            {
                if (!TryGetUtcNow(out var now))
                {
                    return Result(ActivationJournalOutcome.StorageFailure);
                }

                var read = ReadRecord(now);
                if (read.Kind == JournalReadKind.Invalid)
                {
                    return Result(ActivationJournalOutcome.InvalidRecord);
                }

                if (read.Kind == JournalReadKind.StorageFailure)
                {
                    return Result(ActivationJournalOutcome.StorageFailure);
                }

                if (read.Kind == JournalReadKind.Missing ||
                    read.Record!.ActivationId != activationId ||
                    read.Record.ExplorerProcessId != explorerProcessId ||
                    read.Record.ExplorerCreationTimeFileTime100ns !=
                        explorerCreationTimeFileTime100ns)
                {
                    return Result(ActivationJournalOutcome.ActivationMismatch, read.Record);
                }

                if (read.Record.State is not (
                    ActivationState.Pending or
                    ActivationState.Stable or
                    ActivationState.Unsafe))
                {
                    return Result(ActivationJournalOutcome.InvalidTransition, read.Record);
                }

                var updated = read.Record with { State = ActivationState.Clean };
                return WriteRecord(updated)
                    ? Result(ActivationJournalOutcome.TransitionPersisted, updated)
                    : Result(ActivationJournalOutcome.StorageFailure, read.Record);
            });
    }

    private ActivationJournalResult ExecuteWithExclusiveAccess(
        Func<ActivationJournalResult> operation)
    {
        lock (syncRoot)
        {
            IDisposable? lease;
            try
            {
                lease = mutexGate.TryEnter(MutexName, mutexTimeout);
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                return Result(ActivationJournalOutcome.StorageFailure);
            }

            if (lease is null)
            {
                return Result(ActivationJournalOutcome.StorageFailure);
            }

            ActivationJournalResult result;
            try
            {
                result = operation();
            }
            catch
            {
                TryDisposeLease(lease);
                throw;
            }

            try
            {
                lease.Dispose();
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                return Result(ActivationJournalOutcome.StorageFailure, result.Record);
            }

            return result;
        }
    }

    private static void TryDisposeLease(IDisposable lease)
    {
        try
        {
            lease.Dispose();
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // Preserve the original unexpected exception while making a best-effort release.
        }
    }

    private static string CreateMutexName(string journalPath)
    {
        var canonicalPath = Path.GetFullPath(journalPath).ToUpperInvariant();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPath));
        return $@"Local\CQTB.ActivationJournal.v1.{Convert.ToHexString(digest)}";
    }

    private static ActivationJournalResult Result(
        ActivationJournalOutcome outcome,
        ActivationJournalRecord? record = null) =>
        new(outcome, record);

    private ActivationSessionLeaseResult OpenSessionLease()
    {
        FileStream? stream = null;
        try
        {
            if (!IsPathSafe(SessionLeasePath))
            {
                return SessionLeaseResult(ActivationSessionLeaseOutcome.StorageFailure);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(SessionLeasePath)!);
            if (!IsPathSafe(SessionLeasePath))
            {
                return SessionLeaseResult(ActivationSessionLeaseOutcome.StorageFailure);
            }

            try
            {
                stream = new FileStream(
                    SessionLeasePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                return SessionLeaseResult(ActivationSessionLeaseOutcome.Busy);
            }

            if (!IsPathSafe(SessionLeasePath))
            {
                stream.Dispose();
                stream = null;
                return SessionLeaseResult(ActivationSessionLeaseOutcome.StorageFailure);
            }

            var owned = new FileActivationSessionLease(stream);
            stream = null;
            return SessionLeaseResult(ActivationSessionLeaseOutcome.Acquired, owned);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            stream?.Dispose();
            return SessionLeaseResult(ActivationSessionLeaseOutcome.StorageFailure);
        }
    }

    private static ActivationSessionLeaseResult SessionLeaseResult(
        ActivationSessionLeaseOutcome outcome,
        IActivationSessionLease? lease = null) =>
        new(outcome, lease);

    private static bool IsLegalTransition(
        ActivationState currentState,
        ActivationState nextState) =>
        (currentState, nextState) is
            (ActivationState.Pending, ActivationState.Stable) or
            (ActivationState.Pending, ActivationState.Clean) or
            (ActivationState.Pending, ActivationState.Unsafe) or
            (ActivationState.Stable, ActivationState.Clean) or
            (ActivationState.Stable, ActivationState.Unsafe);

    private static bool TryNormalizeRequest(
        ActivationStartRequest? request,
        out ActivationStartRequest normalized)
    {
        normalized = null!;
        if (request is null ||
            !IsSafeAppVersion(request.AppVersion) ||
            request.WindowsBuild <= 0 ||
            request.ExplorerProcessId <= 0 ||
            request.ExplorerCreationTimeFileTime100ns <= 0 ||
            !TryNormalizeDigest(request.ExplorerSignatureSha256, out var explorerDigest) ||
            !TryNormalizeDigest(request.TaskbarSignatureSha256, out var taskbarDigest))
        {
            return false;
        }

        normalized = request with
        {
            ExplorerSignatureSha256 = explorerDigest,
            TaskbarSignatureSha256 = taskbarDigest,
        };
        return true;
    }

    private static bool IsSafeAppVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > MaximumAppVersionLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!IsAsciiLetterOrDigit(character) &&
                character is not '.' and not '-' and not '_' and not '+')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiLetterOrDigit(char value) =>
        value is >= '0' and <= '9' or
            >= 'A' and <= 'Z' or
            >= 'a' and <= 'z';

    private static bool TryNormalizeDigest(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and
                not (>= 'A' and <= 'F') and
                not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        normalized = value.ToLowerInvariant();
        return true;
    }

    private static bool TryReadNormalizedDigest(JsonElement element, out string digest)
    {
        digest = string.Empty;
        if (element.ValueKind != JsonValueKind.String ||
            !TryNormalizeDigest(element.GetString(), out var normalized) ||
            !string.Equals(element.GetString(), normalized, StringComparison.Ordinal))
        {
            return false;
        }

        digest = normalized;
        return true;
    }

    private bool TryGetUtcNow(out DateTimeOffset now)
    {
        try
        {
            now = utcNow().ToUniversalTime();
            return now > DateTimeOffset.UnixEpoch;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            now = default;
            return false;
        }
    }

    private JournalReadResult ReadRecord(DateTimeOffset now)
    {
        if (!IsJournalPathSafe())
        {
            return new JournalReadResult(JournalReadKind.StorageFailure, null);
        }

        byte[] bytes;
        try
        {
            using var stream = new FileStream(
                JournalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            if (stream.Length is <= 0 or > MaximumJournalBytes)
            {
                return new JournalReadResult(JournalReadKind.Invalid, null);
            }

            var length = checked((int)stream.Length);
            bytes = new byte[length];
            stream.ReadExactly(bytes);
            if (stream.Length != length)
            {
                return new JournalReadResult(JournalReadKind.Invalid, null);
            }
        }
        catch (FileNotFoundException)
        {
            return new JournalReadResult(JournalReadKind.Missing, null);
        }
        catch (DirectoryNotFoundException)
        {
            return new JournalReadResult(JournalReadKind.Missing, null);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return new JournalReadResult(JournalReadKind.StorageFailure, null);
        }

        try
        {
            using var document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 4,
                });
            return TryParseRecord(document.RootElement, now, out var record)
                ? new JournalReadResult(JournalReadKind.Valid, record)
                : new JournalReadResult(JournalReadKind.Invalid, null);
        }
        catch (JsonException)
        {
            return new JournalReadResult(JournalReadKind.Invalid, null);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return new JournalReadResult(JournalReadKind.StorageFailure, null);
        }
    }

    private static bool TryParseRecord(
        JsonElement root,
        DateTimeOffset now,
        out ActivationJournalRecord? record)
    {
        record = null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!ExpectedPropertyNames.Contains(property.Name) ||
                !values.TryAdd(property.Name, property.Value))
            {
                return false;
            }
        }

        if (values.Count != ExpectedPropertyNames.Count ||
            !values["schemaVersion"].TryGetInt32(out var schemaVersion) ||
            schemaVersion != CurrentSchemaVersion ||
            values["appVersion"].ValueKind != JsonValueKind.String ||
            !IsSafeAppVersion(values["appVersion"].GetString()) ||
            !values["windowsBuild"].TryGetInt32(out var windowsBuild) ||
            windowsBuild <= 0 ||
            !TryReadNormalizedDigest(values["explorerSignatureSha256"], out var explorerDigest) ||
            !TryReadNormalizedDigest(values["taskbarSignatureSha256"], out var taskbarDigest) ||
            !values["explorerProcessId"].TryGetInt32(out var explorerProcessId) ||
            explorerProcessId <= 0 ||
            !values["explorerCreationTimeFileTime100ns"].TryGetUInt64(out var explorerCreationTime) ||
            explorerCreationTime <= 0 ||
            !TryReadGuid(values["activationId"], out var activationId) ||
            !TryReadUtcStart(values["startedUtc"], now, out var startedUtc) ||
            !TryReadState(values["state"], out var state))
        {
            return false;
        }

        record = new ActivationJournalRecord(
            values["appVersion"].GetString()!,
            windowsBuild,
            explorerDigest,
            taskbarDigest,
            explorerProcessId,
            explorerCreationTime,
            activationId,
            startedUtc,
            state);
        return true;
    }

    private static bool TryReadGuid(JsonElement element, out Guid activationId)
    {
        activationId = Guid.Empty;
        return element.ValueKind == JsonValueKind.String &&
            Guid.TryParseExact(element.GetString(), "D", out activationId) &&
            activationId != Guid.Empty;
    }

    private static bool TryReadUtcStart(
        JsonElement element,
        DateTimeOffset now,
        out DateTimeOffset startedUtc)
    {
        startedUtc = default;
        return element.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(
                element.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out startedUtc) &&
            startedUtc.Offset == TimeSpan.Zero &&
            startedUtc > DateTimeOffset.UnixEpoch &&
            startedUtc <= now;
    }

    private static bool TryReadState(JsonElement element, out ActivationState state)
    {
        state = default;
        var value = element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
        return value is not null &&
            Enum.TryParse(value, ignoreCase: false, out state) &&
            Enum.IsDefined(state) &&
            string.Equals(value, state.ToString(), StringComparison.Ordinal);
    }

    private bool WriteRecord(ActivationJournalRecord record)
    {
        string? temporaryPath = null;
        try
        {
            if (!IsJournalPathSafe())
            {
                return false;
            }

            var directory = Path.GetDirectoryName(JournalPath)!;
            Directory.CreateDirectory(directory);
            if (!IsJournalPathSafe())
            {
                return false;
            }

            temporaryPath = Path.Combine(
                directory,
                $"activation.{Guid.NewGuid():N}.tmp");
            var bytes = Serialize(record);
            if (bytes.Length > MaximumJournalBytes)
            {
                return false;
            }

            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (!IsJournalPathSafe())
            {
                return false;
            }

            if (File.Exists(JournalPath))
            {
                File.Replace(
                    temporaryPath,
                    JournalPath,
                    destinationBackupFileName: null,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, JournalPath);
            }

            temporaryPath = null;
            return true;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return false;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                TryDeleteTemporaryFile(temporaryPath);
            }
        }
    }

    private bool IsJournalPathSafe() => IsPathSafe(JournalPath);

    private bool IsPathSafe(string path)
    {
        try
        {
            foreach (var prefix in EnumeratePathPrefixes(path))
            {
                if (!pathInspector.TryGetAttributes(
                        prefix,
                        out var attributes,
                        out var exists) ||
                    exists && (attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return false;
        }
    }

    private static IEnumerable<string> EnumeratePathPrefixes(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            yield break;
        }

        yield return root;
        var current = root;
        var remainder = fullPath[root.Length..];
        foreach (var segment in remainder.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            yield return current;
        }
    }

    private static byte[] Serialize(ActivationJournalRecord record)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", CurrentSchemaVersion);
            writer.WriteString("appVersion", record.AppVersion);
            writer.WriteNumber("windowsBuild", record.WindowsBuild);
            writer.WriteString("explorerSignatureSha256", record.ExplorerSignatureSha256);
            writer.WriteString("taskbarSignatureSha256", record.TaskbarSignatureSha256);
            writer.WriteNumber("explorerProcessId", record.ExplorerProcessId);
            writer.WriteNumber(
                "explorerCreationTimeFileTime100ns",
                record.ExplorerCreationTimeFileTime100ns);
            writer.WriteString("activationId", record.ActivationId);
            writer.WriteString(
                "startedUtc",
                record.StartedUtc.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("state", record.State.ToString());
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static void TryDeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            File.Delete(temporaryPath);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // The operation has already been converted to StorageFailure. Never touch the
            // destination while attempting best-effort cleanup of an uncommitted temp file.
        }
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is IOException or
            UnauthorizedAccessException or
            SecurityException or
            WaitHandleCannotBeOpenedException or
            ArgumentException or
            NotSupportedException or
            InvalidOperationException or
            OverflowException;

    private static bool IsSharingViolation(IOException exception)
    {
        var errorCode = exception.HResult & 0xffff;
        return errorCode is 32 or 33;
    }

    private sealed class FileActivationSessionLease(FileStream stream) :
        IActivationSessionLease
    {
        private FileStream? stream = stream;

        public void Dispose() => Interlocked.Exchange(ref stream, null)?.Dispose();
    }

    private enum JournalReadKind
    {
        Missing,
        Valid,
        Invalid,
        StorageFailure,
    }

    private readonly record struct JournalReadResult(
        JournalReadKind Kind,
        ActivationJournalRecord? Record);
}
