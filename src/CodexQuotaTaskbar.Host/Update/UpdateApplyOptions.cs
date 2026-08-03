using System.IO;

namespace CodexQuotaTaskbar.Host.Update;

internal sealed record UpdateApplyOptions(int ParentProcessId, string TargetExecutable)
{
    internal static bool TryParse(IReadOnlyList<string> arguments, out UpdateApplyOptions? options)
    {
        options = null;
        if (arguments.Count != 3 ||
            arguments[0] != "--apply-update" ||
            !int.TryParse(arguments[1], out var processId) ||
            processId <= 0)
        {
            return false;
        }

        try
        {
            var target = Path.GetFullPath(arguments[2]);
            if (!Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            options = new UpdateApplyOptions(processId, target);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
