using System.Security;

namespace CodexQuotaTaskbar.CompatibilityProbe;

internal enum ProbeMode
{
    CollectOnly,
    Live,
    Detach,
    RestartExplorerTest,
}

internal enum ProbeOptionsError
{
    None,
    InvalidMode,
    OutputRequired,
    SwitchNotAllowed,
    InvalidDisplaySeconds,
    InvalidOutputPath,
    ReparseOutputPath,
    PathInspectionFailure,
    UnknownArgument,
    MissingValue,
}

internal sealed record ProbeOptions(
    ProbeMode Mode,
    string? OutputPath,
    bool ExplicitRetry,
    int DisplaySeconds)
{
    private const int DefaultDisplaySeconds = 5;

    internal static ProbeOptionsParseResult Parse(
        IReadOnlyList<string> arguments,
        string currentDirectory,
        IProbePathInspector? pathInspector = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);

        ProbeMode? mode = null;
        string? output = null;
        var outputSpecified = false;
        var explicitRetry = false;
        var retrySpecified = false;
        var displaySeconds = DefaultDisplaySeconds;
        var displaySpecified = false;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (TryParseMode(argument, out var parsedMode))
            {
                if (mode is not null)
                {
                    return ProbeOptionsParseResult.Fail(ProbeOptionsError.InvalidMode);
                }

                mode = parsedMode;
                continue;
            }

            switch (argument)
            {
                case "--output":
                    if (!TryReadValue(arguments, ref index, out output))
                    {
                        return ProbeOptionsParseResult.Fail(ProbeOptionsError.MissingValue);
                    }

                    if (outputSpecified)
                    {
                        return ProbeOptionsParseResult.Fail(ProbeOptionsError.InvalidOutputPath);
                    }

                    outputSpecified = true;
                    break;

                case "--display-seconds":
                    if (!TryReadValue(arguments, ref index, out var displayValue))
                    {
                        return ProbeOptionsParseResult.Fail(ProbeOptionsError.MissingValue);
                    }

                    if (displaySpecified ||
                        !int.TryParse(displayValue, out displaySeconds) ||
                        displaySeconds is < 1 or > 30)
                    {
                        return ProbeOptionsParseResult.Fail(ProbeOptionsError.InvalidDisplaySeconds);
                    }

                    displaySpecified = true;
                    break;

                case "--explicit-retry":
                    if (retrySpecified)
                    {
                        return ProbeOptionsParseResult.Fail(ProbeOptionsError.SwitchNotAllowed);
                    }

                    explicitRetry = true;
                    retrySpecified = true;
                    break;

                default:
                    return ProbeOptionsParseResult.Fail(ProbeOptionsError.UnknownArgument);
            }
        }

        if (mode is null)
        {
            return ProbeOptionsParseResult.Fail(ProbeOptionsError.InvalidMode);
        }

        if (mode == ProbeMode.Detach)
        {
            if (outputSpecified || retrySpecified || displaySpecified)
            {
                return ProbeOptionsParseResult.Fail(ProbeOptionsError.SwitchNotAllowed);
            }

            return ProbeOptionsParseResult.Success(
                new ProbeOptions(mode.Value, null, false, DefaultDisplaySeconds));
        }

        if (retrySpecified && mode != ProbeMode.Live ||
            displaySpecified && mode != ProbeMode.Live)
        {
            return ProbeOptionsParseResult.Fail(ProbeOptionsError.SwitchNotAllowed);
        }

        if (!outputSpecified)
        {
            return ProbeOptionsParseResult.Fail(ProbeOptionsError.OutputRequired);
        }

        if (!TryValidateOutputPath(
                output!, currentDirectory, pathInspector ?? new FileSystemProbePathInspector(),
                out var normalizedOutput, out var outputError))
        {
            return ProbeOptionsParseResult.Fail(outputError);
        }

        return ProbeOptionsParseResult.Success(
            new ProbeOptions(mode.Value, normalizedOutput, explicitRetry, displaySeconds));
    }

    private static bool TryParseMode(string argument, out ProbeMode mode)
    {
        mode = argument switch
        {
            "--collect-only" => ProbeMode.CollectOnly,
            "--live" => ProbeMode.Live,
            "--detach" => ProbeMode.Detach,
            "--restart-explorer-test" => ProbeMode.RestartExplorerTest,
            _ => default,
        };

        return argument is "--collect-only" or "--live" or "--detach" or
            "--restart-explorer-test";
    }

    private static bool TryReadValue(
        IReadOnlyList<string> arguments,
        ref int index,
        out string? value)
    {
        if (index + 1 >= arguments.Count ||
            arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = null;
            return false;
        }

        value = arguments[++index];
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryValidateOutputPath(
        string output,
        string currentDirectory,
        IProbePathInspector pathInspector,
        out string normalizedOutput,
        out ProbeOptionsError error)
    {
        try
        {
            var baseDirectory = Path.GetFullPath(currentDirectory);
            normalizedOutput = Path.GetFullPath(output, baseDirectory);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            normalizedOutput = string.Empty;
            error = ProbeOptionsError.InvalidOutputPath;
            return false;
        }

        if (!string.Equals(
                Path.GetExtension(normalizedOutput), ".json",
                StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(Path.GetFileName(normalizedOutput)))
        {
            error = ProbeOptionsError.InvalidOutputPath;
            return false;
        }

        var targetInspection = InspectPath(pathInspector, normalizedOutput);
        if (targetInspection.Kind == ProbePathInspectionKind.Failure)
        {
            error = ProbeOptionsError.PathInspectionFailure;
            return false;
        }

        if (targetInspection.Kind == ProbePathInspectionKind.Exists &&
            (targetInspection.Attributes & FileAttributes.Directory) != 0)
        {
            error = ProbeOptionsError.InvalidOutputPath;
            return false;
        }

        var parent = Path.GetDirectoryName(normalizedOutput);
        if (parent is null)
        {
            error = ProbeOptionsError.InvalidOutputPath;
            return false;
        }

        var parentInspection = InspectPath(pathInspector, parent);
        if (parentInspection.Kind == ProbePathInspectionKind.Failure)
        {
            error = ProbeOptionsError.PathInspectionFailure;
            return false;
        }

        if (parentInspection.Kind != ProbePathInspectionKind.Exists ||
            (parentInspection.Attributes & FileAttributes.Directory) == 0)
        {
            error = ProbeOptionsError.InvalidOutputPath;
            return false;
        }

        for (string? candidate = normalizedOutput;
             candidate is not null;
             candidate = Path.GetDirectoryName(candidate))
        {
            var inspection = InspectPath(pathInspector, candidate);
            if (inspection.Kind == ProbePathInspectionKind.Failure)
            {
                error = ProbeOptionsError.PathInspectionFailure;
                return false;
            }

            if (inspection.Kind == ProbePathInspectionKind.Exists &&
                (inspection.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                error = ProbeOptionsError.ReparseOutputPath;
                return false;
            }

            var next = Path.GetDirectoryName(candidate);
            if (string.Equals(next, candidate, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        error = ProbeOptionsError.None;
        return true;
    }

    private static ProbePathInspection InspectPath(
        IProbePathInspector pathInspector,
        string path)
    {
        try
        {
            return pathInspector.TryGetAttributes(path, out var attributes)
                ? new ProbePathInspection(ProbePathInspectionKind.Exists, attributes)
                : new ProbePathInspection(ProbePathInspectionKind.Missing, default);
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                SecurityException or
                ArgumentException or
                NotSupportedException)
        {
            return new ProbePathInspection(ProbePathInspectionKind.Failure, default);
        }
    }

    private enum ProbePathInspectionKind
    {
        Exists,
        Missing,
        Failure,
    }

    private readonly record struct ProbePathInspection(
        ProbePathInspectionKind Kind,
        FileAttributes Attributes);
}

internal sealed record ProbeOptionsParseResult(
    ProbeOptions? Options,
    ProbeOptionsError Error)
{
    internal bool Succeeded => Options is not null && Error == ProbeOptionsError.None;

    internal static ProbeOptionsParseResult Success(ProbeOptions options) =>
        new(options, ProbeOptionsError.None);

    internal static ProbeOptionsParseResult Fail(ProbeOptionsError error) =>
        new(null, error);
}

internal interface IProbePathInspector
{
    bool TryGetAttributes(string path, out FileAttributes attributes);
}

internal sealed class FileSystemProbePathInspector : IProbePathInspector
{
    public bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }
}
