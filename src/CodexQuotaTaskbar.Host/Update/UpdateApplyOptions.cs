using System.IO;

namespace CodexQuotaTaskbar.Host.Update;

internal sealed record UpdateApplyOptions(int ParentProcessId, string TargetExecutable)
{
    internal long? ParentStartTicks { get; init; }
    internal string? Nonce { get; init; }
    internal static bool TryParse(IReadOnlyList<string> arguments, out UpdateApplyOptions? options)
    {
        options = null;
        if (arguments.Count is not (3 or 5) ||
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
            long? ticks = null;
            string? nonce = null;
            if (arguments.Count == 5)
            {
                if (!long.TryParse(arguments[3], out var parsedTicks) || parsedTicks <= 0
                    || !Guid.TryParseExact(arguments[4], "N", out _)) return false;
                ticks = parsedTicks; nonce = arguments[4];
            }
            options = new UpdateApplyOptions(processId, target) { ParentStartTicks = ticks, Nonce = nonce };
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
