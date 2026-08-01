using System.Text.Json;
using System.Security;

namespace CodexQuotaTaskbar.CompatibilityProbe.Reporting;

internal enum CompatibilityReportWriteError
{
    None,
    InvalidOutputPath,
    StorageFailure,
}

internal readonly record struct CompatibilityReportWriteResult(
    CompatibilityReportWriteError Error)
{
    internal bool Succeeded => Error == CompatibilityReportWriteError.None;
}

internal sealed class CompatibilityReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly ICompatibilityReportStorage storage;

    internal CompatibilityReportWriter()
        : this(new FileSystemCompatibilityReportStorage())
    {
    }

    internal CompatibilityReportWriter(ICompatibilityReportStorage storage) =>
        this.storage = storage ?? throw new ArgumentNullException(nameof(storage));

    internal CompatibilityReportWriteResult Write(
        CompatibilityReport report,
        string outputPath)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!IsNormalizedJsonPath(outputPath))
        {
            return new CompatibilityReportWriteResult(
                CompatibilityReportWriteError.InvalidOutputPath);
        }

        try
        {
            var content = JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions);
            storage.WriteAtomically(outputPath, content);
            return new CompatibilityReportWriteResult(CompatibilityReportWriteError.None);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException or JsonException or
                ArgumentException or NotSupportedException)
        {
            return new CompatibilityReportWriteResult(
                CompatibilityReportWriteError.StorageFailure);
        }
    }

    private static bool IsNormalizedJsonPath(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath) ||
            !Path.IsPathFullyQualified(outputPath) ||
            !string.Equals(Path.GetExtension(outputPath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(outputPath),
                outputPath,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

internal interface ICompatibilityReportStorage
{
    void WriteAtomically(string path, ReadOnlyMemory<byte> content);
}

internal sealed class FileSystemCompatibilityReportStorage : ICompatibilityReportStorage
{
    public void WriteAtomically(string path, ReadOnlyMemory<byte> content)
    {
        var directory = Path.GetDirectoryName(path) ??
            throw new ArgumentException("The report path has no parent directory.", nameof(path));
        ValidateDestination(path, directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4_096,
                       FileOptions.WriteThrough))
            {
                stream.Write(content.Span);
                stream.Flush(flushToDisk: true);
            }

            ValidateDestination(path, directory);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or SecurityException)
            {
                // The primary write result remains authoritative.
            }
        }
    }

    private static void ValidateDestination(string path, string directory)
    {
        var directoryAttributes = File.GetAttributes(directory);
        if ((directoryAttributes & FileAttributes.Directory) == 0)
        {
            throw new IOException("The report parent is not a directory.");
        }

        if (File.Exists(path) || Directory.Exists(path))
        {
            var targetAttributes = File.GetAttributes(path);
            if ((targetAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new IOException("The report target is not a regular file.");
            }
        }

        for (string? candidate = directory;
             candidate is not null;
             candidate = Path.GetDirectoryName(candidate))
        {
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                var attributes = File.GetAttributes(candidate);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("The report parent chain contains a reparse point.");
                }
            }

            var next = Path.GetDirectoryName(candidate);
            if (string.Equals(next, candidate, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }
    }
}
