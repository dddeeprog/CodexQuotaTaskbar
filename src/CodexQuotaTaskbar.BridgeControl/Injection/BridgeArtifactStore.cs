using System.Security.Cryptography;
using System.Security;

namespace CodexQuotaTaskbar.BridgeControl.Injection;

internal enum BridgeArtifactFailureCode
{
    InvalidExpectedHash,
    UnsafePath,
    PayloadHashMismatch,
    PayloadTooLarge,
    IoFailure,
}

internal sealed class BridgeArtifactException : Exception
{
    internal BridgeArtifactException(
        BridgeArtifactFailureCode code,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    internal BridgeArtifactFailureCode Code { get; }
}

internal interface IBridgeArtifactStore
{
    IBridgeArtifactLease Materialize();
}

internal interface IBridgeArtifactLease : IDisposable
{
    string CanonicalPath { get; }

    string Sha256Hex { get; }

    Stream OpenRead();
}

internal sealed class BridgeArtifactStore : IBridgeArtifactStore
{
    private const long MaximumPayloadBytes = 64L * 1024 * 1024;
    private const string ArtifactFileName = "CodexQuotaTaskbar.Bridge.dll";
    private readonly string localAppDataRoot;
    private readonly string expectedSha256;
    private readonly Func<Stream> openPayload;

    internal BridgeArtifactStore(
        string localAppDataRoot,
        string expectedSha256,
        Func<Stream> openPayload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppDataRoot);
        this.expectedSha256 = NormalizeDigest(expectedSha256);
        this.localAppDataRoot = Path.GetFullPath(localAppDataRoot);
        this.openPayload = openPayload ?? throw new ArgumentNullException(nameof(openPayload));
    }

    public IBridgeArtifactLease Materialize()
    {
        var artifactDirectory = Path.Combine(
            localAppDataRoot,
            "CodexQuotaTaskbar",
            "Probe",
            "runtime",
            expectedSha256);
        var artifactPath = Path.Combine(artifactDirectory, ArtifactFileName);
        EnsureSafeDirectory(artifactDirectory);

        if (File.Exists(artifactPath))
        {
            RejectUnsafeTarget(artifactPath);
            var existing = TryOpenVerifiedLease(artifactPath);
            if (existing is not null)
            {
                return existing;
            }
        }
        else if (Directory.Exists(artifactPath))
        {
            throw Failure(
                BridgeArtifactFailureCode.UnsafePath,
                "The bridge artifact target is not a regular file.");
        }

        var temporaryPath = Path.Combine(
            artifactDirectory,
            $".{ArtifactFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            WriteVerifiedPayload(temporaryPath);
            EnsureSafeDirectory(artifactDirectory);
            if (File.Exists(artifactPath))
            {
                RejectUnsafeTarget(artifactPath);
            }

            File.Move(temporaryPath, artifactPath, overwrite: true);
            temporaryPath = string.Empty;
            return TryOpenVerifiedLease(artifactPath) ??
                throw Failure(
                    BridgeArtifactFailureCode.IoFailure,
                    "The published bridge artifact could not be verified.");
        }
        catch (BridgeArtifactException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw Failure(
                BridgeArtifactFailureCode.IoFailure,
                "The bridge artifact could not be materialized.",
                exception);
        }
        finally
        {
            if (!string.IsNullOrEmpty(temporaryPath))
            {
                TryDelete(temporaryPath);
            }
        }
    }

    private static string NormalizeDigest(string digest)
    {
        if (string.IsNullOrWhiteSpace(digest) ||
            digest.Length != 64 ||
            !digest.All(IsAsciiHexDigit))
        {
            throw Failure(
                BridgeArtifactFailureCode.InvalidExpectedHash,
                "The expected bridge SHA-256 digest is invalid.");
        }

        return digest.ToLowerInvariant();
    }

    private void EnsureSafeDirectory(string artifactDirectory)
    {
        try
        {
            if (!Directory.Exists(localAppDataRoot))
            {
                Directory.CreateDirectory(localAppDataRoot);
            }

            RejectReparseDirectory(localAppDataRoot);
            var current = localAppDataRoot;
            var relative = Path.GetRelativePath(localAppDataRoot, artifactDirectory);
            if (relative == ".." ||
                relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                throw Failure(
                    BridgeArtifactFailureCode.UnsafePath,
                    "The bridge artifact path escapes its root.");
            }

            foreach (var component in relative.Split(
                         Path.DirectorySeparatorChar,
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, component);
                Directory.CreateDirectory(current);
                RejectReparseDirectory(current);
            }
        }
        catch (BridgeArtifactException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw Failure(
                BridgeArtifactFailureCode.UnsafePath,
                "The bridge artifact directory is unavailable.",
                exception);
        }
    }

    private static void RejectReparseDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw Failure(
                BridgeArtifactFailureCode.UnsafePath,
                "The bridge artifact directory chain is unsafe.");
        }
    }

    private static void RejectUnsafeTarget(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw Failure(
                BridgeArtifactFailureCode.UnsafePath,
                "The bridge artifact target is unsafe.");
        }
    }

    private IBridgeArtifactLease? TryOpenVerifiedLease(string artifactPath)
    {
        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                artifactPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            if (stream.Length is <= 0 or > MaximumPayloadBytes)
            {
                stream.Dispose();
                return null;
            }

            var digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!string.Equals(digest, expectedSha256, StringComparison.Ordinal))
            {
                stream.Dispose();
                return null;
            }

            stream.Position = 0;
            return new BridgeArtifactLease(
                Path.GetFullPath(artifactPath),
                expectedSha256,
                stream);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            stream?.Dispose();
            throw Failure(
                BridgeArtifactFailureCode.IoFailure,
                "The bridge artifact could not be verified.",
                exception);
        }
    }

    private void WriteVerifiedPayload(string temporaryPath)
    {
        try
        {
            using var source = openPayload();
            if (source is null || !source.CanRead)
            {
                throw Failure(
                    BridgeArtifactFailureCode.IoFailure,
                    "The embedded bridge payload is unreadable.");
            }

            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var destination = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 64 * 1024,
                       FileOptions.WriteThrough))
            {
                var buffer = new byte[64 * 1024];
                long total = 0;
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total = checked(total + read);
                    if (total > MaximumPayloadBytes)
                    {
                        throw Failure(
                            BridgeArtifactFailureCode.PayloadTooLarge,
                            "The embedded bridge payload is oversized.");
                    }

                    digest.AppendData(buffer, 0, read);
                    destination.Write(buffer, 0, read);
                }

                destination.Flush(flushToDisk: true);
            }

            var actual = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
            if (!string.Equals(actual, expectedSha256, StringComparison.Ordinal))
            {
                throw Failure(
                    BridgeArtifactFailureCode.PayloadHashMismatch,
                    "The embedded bridge payload digest does not match its manifest.");
            }
        }
        catch (BridgeArtifactException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw Failure(
                BridgeArtifactFailureCode.IoFailure,
                "The embedded bridge payload could not be written.",
                exception);
        }
    }

    private static bool IsAsciiHexDigit(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static bool IsRecoverable(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or
            NotSupportedException or InvalidOperationException or OverflowException;

    private static BridgeArtifactException Failure(
        BridgeArtifactFailureCode code,
        string message,
        Exception? innerException = null) =>
        new(code, message, innerException);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // Preserve the primary typed materialization failure.
        }
    }

    private sealed class BridgeArtifactLease : IBridgeArtifactLease
    {
        private readonly FileStream stableStream;
        private bool disposed;

        internal BridgeArtifactLease(
            string canonicalPath,
            string sha256Hex,
            FileStream stableStream)
        {
            CanonicalPath = canonicalPath;
            Sha256Hex = sha256Hex;
            this.stableStream = stableStream;
        }

        public string CanonicalPath { get; }

        public string Sha256Hex { get; }

        public Stream OpenRead()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return new FileStream(
                CanonicalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            stableStream.Dispose();
            disposed = true;
        }
    }
}
