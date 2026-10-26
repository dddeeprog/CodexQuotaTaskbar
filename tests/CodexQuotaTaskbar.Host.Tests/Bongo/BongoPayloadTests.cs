using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using CodexQuotaTaskbar.Host.Bongo;

namespace CodexQuotaTaskbar.Host.Tests.Bongo;

public sealed class BongoPayloadJunctionFactAttribute : FactAttribute
{
    public BongoPayloadJunctionFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CQT_BONGO_PAYLOAD_JUNCTION_ROOT")))
            Skip = "Requires an explicit isolated temporary junction fixture; never requests link privileges.";
    }
}

public sealed class BongoPayloadTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "CodexQuotaTaskbar.BongoPayloadTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Extracts_only_verified_fixed_files_and_revalidates_cached_copy()
    {
        var fixture = new Fixture();
        var payload = new BongoPayload(root, fixture.Open);
        var first = await payload.EnsureAsync(CancellationToken.None);
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(first.HelperPath, stamp);
        var second = await payload.EnsureAsync(CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(second.HelperPath));
        Assert.Equal(fixture.Files[BongoPayload.HelperRelativePath], await File.ReadAllBytesAsync(second.HelperPath));
        Assert.True(File.Exists(Path.Combine(second.ModelDirectory, "cat.model3.json")));
        Assert.Matches("^[a-f0-9]{64}$", Path.GetFileName(Path.GetDirectoryName(second.HelperPath))!);
        Assert.Single(Directory.EnumerateDirectories(root));
    }

    [Fact]
    public async Task Concurrent_ensures_publish_one_identical_content_addressed_directory()
    {
        var fixture = new Fixture();
        var first = new BongoPayload(root, fixture.Open);
        var second = new BongoPayload(root, fixture.Open);
        var paths = await Task.WhenAll(first.EnsureAsync(CancellationToken.None), second.EnsureAsync(CancellationToken.None));
        Assert.Equal(paths[0], paths[1]);
        Assert.Single(Directory.EnumerateDirectories(root));
    }

    [Fact]
    public async Task Modified_cached_helper_is_rejected_without_overwriting_it()
    {
        var fixture = new Fixture();
        var payload = new BongoPayload(root, fixture.Open);
        var paths = await payload.EnsureAsync(CancellationToken.None);
        await File.WriteAllTextAsync(paths.HelperPath, "modified");

        await Assert.ThrowsAsync<InvalidDataException>(() => payload.EnsureAsync(CancellationToken.None));
        Assert.Equal("modified", await File.ReadAllTextAsync(paths.HelperPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_unexpected_cached_file_prevents_launch_paths(bool unexpected)
    {
        var fixture = new Fixture();
        var payload = new BongoPayload(root, fixture.Open);
        var paths = await payload.EnsureAsync(CancellationToken.None);
        if (unexpected) await File.WriteAllTextAsync(Path.Combine(paths.ModelDirectory, "surprise.dll"), "extra");
        else File.Delete(Path.Combine(paths.ModelDirectory, "demomodel.moc3"));
        await Assert.ThrowsAsync<InvalidDataException>(() => payload.EnsureAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Wrong_embedded_archive_digest_is_rejected_before_cache_creation()
    {
        var fixture = new Fixture(archiveDigest: new string('0', 64));
        await Assert.ThrowsAsync<InvalidDataException>(() => new BongoPayload(root, fixture.Open).EnsureAsync(CancellationToken.None));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task Missing_resource_is_a_bounded_failure_without_cache_creation()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => new BongoPayload(root, _ => null).EnsureAsync(CancellationToken.None));
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("../escape.exe")]
    [InlineData("standard/../../escape.exe")]
    [InlineData("standard\\escape.exe")]
    [InlineData("C:/escape.exe")]
    [InlineData("standard/CON.txt")]
    [InlineData("standard/file.png:stream")]
    [InlineData("standard//file.png")]
    [InlineData("standard/file.")]
    public async Task Unsafe_manifest_names_never_write_files(string name)
    {
        var fixture = new Fixture(additionalFile: name);
        await Assert.ThrowsAsync<InvalidDataException>(() => new BongoPayload(root, fixture.Open).EnsureAsync(CancellationToken.None));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task Case_aliases_in_manifest_are_rejected_for_Windows_filesystems()
    {
        var fixture = new Fixture(additionalFile: "STANDARD/cat.model3.json");
        await Assert.ThrowsAsync<InvalidDataException>(() => new BongoPayload(root, fixture.Open).EnsureAsync(CancellationToken.None));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task Unexpected_archive_member_cleans_only_its_own_staging()
    {
        Directory.CreateDirectory(root);
        var unrelated = Path.Combine(root, "keep-user-file.txt");
        await File.WriteAllTextAsync(unrelated, "preserve");
        var fixture = new Fixture(replaceZipName: "../escape.exe");
        await Assert.ThrowsAsync<InvalidDataException>(() => new BongoPayload(root, fixture.Open).EnsureAsync(CancellationToken.None));
        Assert.Equal("preserve", await File.ReadAllTextAsync(unrelated));
        Assert.Empty(Directory.EnumerateDirectories(root));
    }

    [Fact]
    public async Task Individual_file_hash_is_checked_in_addition_to_archive_digest()
    {
        var fixture = new Fixture(wrongFileDigest: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => new BongoPayload(root, fixture.Open).EnsureAsync(CancellationToken.None));
        Assert.Empty(Directory.EnumerateDirectories(root));
    }

    [Fact]
    public async Task Non_executable_helper_is_rejected_even_with_matching_hash()
    {
        var fixture = new Fixture(invalidExecutable: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => new BongoPayload(root, fixture.Open).EnsureAsync(CancellationToken.None));
        Assert.Empty(Directory.EnumerateDirectories(root));
    }

    [Fact]
    public async Task Declared_total_size_is_bounded_before_extraction()
    {
        var fixture = new Fixture(oversizeManifest: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => new BongoPayload(root, fixture.Open).EnsureAsync(CancellationToken.None));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task Cancellation_releases_gate_and_never_publishes_partial_cache()
    {
        var fixture = new Fixture();
        using var cancel = new CancellationTokenSource();
        var interrupt = true;
        Stream? Open(string name)
        {
            if (interrupt && name == BongoPayload.ArchiveResourceName)
            {
                interrupt = false;
                return new CancellingStream(cancel);
            }
            return fixture.Open(name);
        }
        var payload = new BongoPayload(root, Open);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => payload.EnsureAsync(cancel.Token));
        Assert.False(Directory.Exists(root));
        Assert.True(File.Exists((await payload.EnsureAsync(CancellationToken.None)).HelperPath));
    }

    [BongoPayloadJunctionFact]
    public async Task Cache_root_junction_is_rejected_and_its_target_is_preserved()
    {
        var link = Path.GetFullPath(Environment.GetEnvironmentVariable("CQT_BONGO_PAYLOAD_JUNCTION_ROOT")!);
        var fixtureRoot = Path.GetDirectoryName(link)!;
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), fixtureRoot, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("diandao-bongo-junction-test-", Path.GetFileName(fixtureRoot), StringComparison.Ordinal);
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
        var sentinel = Path.Combine(fixtureRoot, "foreign", "sentinel.txt");
        var before = await File.ReadAllTextAsync(sentinel);
        var fixture = new Fixture();
        await Assert.ThrowsAsync<InvalidDataException>(() => new BongoPayload(link, fixture.Open).EnsureAsync(CancellationToken.None));
        Assert.Equal(before, await File.ReadAllTextAsync(sentinel));
        Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(sentinel)!));
        Assert.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(sentinel)!));
    }

    [BongoPayloadJunctionFact]
    public async Task Refusing_to_clean_a_staging_link_preserves_target_and_allows_next_ensure()
    {
        var link = Path.GetFullPath(Environment.GetEnvironmentVariable("CQT_BONGO_PAYLOAD_JUNCTION_ROOT")!);
        Assert.StartsWith(".extract-", Path.GetFileName(link), StringComparison.Ordinal);
        var fixtureRoot = Path.GetDirectoryName(link)!;
        Assert.StartsWith("diandao-bongo-junction-test-", Path.GetFileName(fixtureRoot), StringComparison.Ordinal);
        var fixture = new Fixture();
        var payload = new BongoPayload(fixtureRoot, fixture.Open);
        var sentinel = Path.Combine(fixtureRoot, "foreign", "sentinel.txt");
        var before = await File.ReadAllTextAsync(sentinel);
        typeof(BongoPayload).GetMethod("RemoveOwnStagingBestEffort", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(payload, [link]);
        Assert.Equal(before, await File.ReadAllTextAsync(sentinel));
        Assert.True(Directory.Exists(link));
        Assert.True(File.Exists((await payload.EnsureAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2))).HelperPath));
    }

    private sealed class CancellingStream(CancellationTokenSource cancel) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancel.Cancel();
            return ValueTask.FromCanceled<int>(cancel.Token);
        }
    }

    private sealed class Fixture
    {
        internal Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal)
        {
            [BongoPayload.HelperRelativePath] = new byte[2048],
            ["standard/cat.model3.json"] = "{}"u8.ToArray(),
            ["standard/demomodel.moc3"] = "synthetic-test-data"u8.ToArray(),
            ["standard/LICENSE-MIT.txt"] = "MIT License synthetic test fixture"u8.ToArray(),
        };
        private readonly byte[] manifest;
        private readonly byte[] archive;

        internal Fixture(string? archiveDigest = null, string? additionalFile = null, string? replaceZipName = null,
            bool wrongFileDigest = false, bool invalidExecutable = false, bool oversizeManifest = false)
        {
            Files[BongoPayload.HelperRelativePath][0] = invalidExecutable ? (byte)'X' : (byte)'M';
            Files[BongoPayload.HelperRelativePath][1] = (byte)'Z';
            if (additionalFile is not null) Files.Add(additionalFile, [1]);
            using (var bytes = new MemoryStream())
            {
                using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var file in Files)
                    {
                        var name = replaceZipName is not null && file.Key == "standard/LICENSE-MIT.txt" ? replaceZipName : file.Key;
                        using var output = zip.CreateEntry(name).Open();
                        output.Write(file.Value);
                    }
                }
                archive = bytes.ToArray();
            }
            manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = 1, archiveBytes = archive.Length, archiveSha256 = archiveDigest ?? Convert.ToHexString(SHA256.HashData(archive)),
                files = Files.Select(file => new
                {
                    path = file.Key,
                    bytes = oversizeManifest && file.Key == "standard/demomodel.moc3" ? BongoPayload.MaximumPayloadBytes : file.Value.Length,
                    sha256 = wrongFileDigest && file.Key == "standard/demomodel.moc3" ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(file.Value)),
                }),
            });
        }

        internal Stream? Open(string name) => name == BongoPayload.ManifestResourceName ? new MemoryStream(manifest, writable: false)
            : name == BongoPayload.ArchiveResourceName ? new MemoryStream(archive, writable: false) : null;
    }

    public void Dispose()
    {
        var resolved = Path.GetFullPath(root);
        if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(Path.GetDirectoryName(resolved)), "CodexQuotaTaskbar.BongoPayloadTests", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing cleanup outside this test's temporary directory.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
}
