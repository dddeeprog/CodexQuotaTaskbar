using System.Security.Cryptography;
using System.Security;
using System.Text;
using CodexQuotaTaskbar.BridgeControl.Injection;

namespace CodexQuotaTaskbar.BridgeControl.Tests;

public sealed class BridgeArtifactStoreTests
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("fake bridge payload");
    private static readonly string Digest = Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant();

    [Fact]
    public void Materializes_to_the_full_lowercase_sha256_directory_and_holds_a_stable_lease()
    {
        using var temporary = new TemporaryDirectory();
        var store = CreateStore(temporary.Path, Payload, Digest.ToUpperInvariant());

        using var artifact = store.Materialize();

        var expected = Path.Combine(
            temporary.Path,
            "CodexQuotaTaskbar",
            "Probe",
            "runtime",
            Digest,
            "CodexQuotaTaskbar.Bridge.dll");
        Assert.Equal(expected, artifact.CanonicalPath);
        Assert.Equal(Digest, artifact.Sha256Hex);
        Assert.Equal(Payload, File.ReadAllBytes(expected));
        Assert.ThrowsAny<IOException>(() => File.Open(expected, FileMode.Open, FileAccess.Write, FileShare.None));
    }

    [Fact]
    public void Rehashes_and_reuses_an_existing_correct_artifact_without_opening_the_payload()
    {
        using var temporary = new TemporaryDirectory();
        var first = CreateStore(temporary.Path, Payload, Digest);
        string path;
        using (var artifact = first.Materialize())
        {
            path = artifact.CanonicalPath;
        }

        var second = new BridgeArtifactStore(
            temporary.Path,
            Digest,
            () => throw new InvalidOperationException("payload should not be opened"));

        using var reused = second.Materialize();

        Assert.Equal(path, reused.CanonicalPath);
        Assert.Equal(Payload, File.ReadAllBytes(path));
    }

    [Fact]
    public void Atomically_repairs_a_corrupt_existing_artifact_from_the_verified_payload()
    {
        using var temporary = new TemporaryDirectory();
        var store = CreateStore(temporary.Path, Payload, Digest);
        string path;
        using (var artifact = store.Materialize())
        {
            path = artifact.CanonicalPath;
        }

        File.WriteAllText(path, "corrupt");

        using var repaired = store.Materialize();

        Assert.Equal(Payload, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public void Payload_digest_mismatch_is_typed_and_never_publishes_the_file()
    {
        using var temporary = new TemporaryDirectory();
        var store = CreateStore(temporary.Path, Encoding.UTF8.GetBytes("wrong"), Digest);

        var exception = Assert.Throws<BridgeArtifactException>(() => store.Materialize());

        Assert.Equal(BridgeArtifactFailureCode.PayloadHashMismatch, exception.Code);
        Assert.Empty(Directory.GetFiles(temporary.Path, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void Invalid_expected_digest_is_rejected_before_filesystem_access(string expectedDigest)
    {
        using var temporary = new TemporaryDirectory();

        var exception = Assert.Throws<BridgeArtifactException>(() =>
            new BridgeArtifactStore(temporary.Path, expectedDigest, () => new MemoryStream(Payload)));

        Assert.Equal(BridgeArtifactFailureCode.InvalidExpectedHash, exception.Code);
        Assert.Empty(Directory.GetFileSystemEntries(temporary.Path));
    }

    [Fact]
    public void Payload_security_failure_is_converted_to_a_typed_io_failure()
    {
        using var temporary = new TemporaryDirectory();
        var store = new BridgeArtifactStore(
            temporary.Path,
            Digest,
            () => throw new SecurityException("fake payload access failure"));

        var exception = Assert.Throws<BridgeArtifactException>(() => store.Materialize());

        Assert.Equal(BridgeArtifactFailureCode.IoFailure, exception.Code);
    }

    private static BridgeArtifactStore CreateStore(
        string root,
        byte[] payload,
        string digest) =>
        new(root, digest, () => new MemoryStream(payload, writable: false));

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"cqtb-artifact-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
