using System.IO;

namespace CodexQuotaTaskbar.Host.Lifetime;

// Optional demo diagnostics must not turn an ordinary file-sharing failure
// into an abandoned shutdown task. Keep only non-content error metadata.
internal sealed record DemoProofFileFailure(string Type, int HResult);

internal static class DemoProofFile
{
    internal static bool TryWrite(string path, string content, out DemoProofFileFailure? failure)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(content);
        failure = null;
        try
        {
            File.WriteAllText(path + ".tmp", content);
            File.Move(path + ".tmp", path, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Do not retry, alter sharing/permissions or delete the old target.
            // A caller may stop its optional recording loop and report this.
            failure = new(error.GetType().FullName ?? error.GetType().Name, error.HResult);
            return false;
        }
    }
}
