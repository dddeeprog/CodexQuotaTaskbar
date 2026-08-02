using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CodexQuotaTaskbar.Core.Tests.Compatibility;

public sealed class RecoveryScriptTests
{
    private const string ScriptRelativePath =
        "tools/CodexQuotaTaskbar.CompatibilityProbe/recover-probe.ps1";

    [Fact]
    public async Task Script_parses_under_windows_powershell_without_being_executed()
    {
        var scriptPath = FindRepositoryFile(ScriptRelativePath);
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(
            "$tokens = $null; $errors = $null; " +
            "[System.Management.Automation.Language.Parser]::ParseFile(" +
            "$env:CQTB_RECOVERY_SCRIPT_PATH, [ref]$tokens, [ref]$errors) | Out-Null; " +
            "if ($errors.Count -ne 0) { " +
            "$errors | ForEach-Object { [Console]::Error.WriteLine($_.Message) }; exit 1 }");
        startInfo.Environment["CQTB_RECOVERY_SCRIPT_PATH"] = scriptPath;

        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Windows PowerShell could not be started.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var output = await standardOutput;
        var error = await standardError;
        Assert.True(
            process.ExitCode == 0,
            $"PowerShell parser failed. stdout: {output} stderr: {error}");
    }

    [Fact]
    public void Activation_hex_uses_a_windows_powershell_5_1_compatible_api()
    {
        var source = ReadScript();

        Assert.DoesNotContain("Convert]::ToHexString", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[System.BitConverter]::ToString", source, StringComparison.Ordinal);
        Assert.Contains(".Replace('-', '')", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Unexpected_failures_have_one_top_level_nonzero_exit_instead_of_silent_catches()
    {
        var source = ReadScript();
        var untypedCatches = Regex.Matches(source, @"(?m)^\s*catch\s*\{\s*$");

        Assert.DoesNotContain("-ErrorAction SilentlyContinue", source, StringComparison.Ordinal);
        Assert.Contains(
            "[System.Diagnostics.Process]::GetProcessById",
            source,
            StringComparison.Ordinal);
        Assert.Single(untypedCatches.Cast<Match>());
        var failureTail = source[untypedCatches[0].Index..];
        Assert.Contains("Write-Error", failureTail, StringComparison.Ordinal);
        Assert.Contains("-ErrorAction Continue", failureTail, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"(?m)^\s*exit\s+1\s*$"), failureTail);
    }

    [Fact]
    public void Shutdown_is_signaled_before_waiting_for_the_matching_quiesced_event()
    {
        var source = ReadScript();
        var shutdownOpen = source.IndexOf(
            "OpenExisting($shutdownEventName)", StringComparison.Ordinal);
        var quiescedOpen = source.IndexOf(
            "OpenExisting($quiescedEventName)", StringComparison.Ordinal);
        var shutdownSet = source.IndexOf("$shutdown.Set()", StringComparison.Ordinal);
        var quiescedWait = source.IndexOf("$quiesced.WaitOne(10000)", StringComparison.Ordinal);

        Assert.True(shutdownOpen >= 0, "The matching Shutdown event must be opened.");
        Assert.True(quiescedOpen > shutdownOpen, "Quiesced must be opened after Shutdown.");
        Assert.True(shutdownSet > quiescedOpen, "Shutdown must be signaled after both handles open.");
        Assert.True(quiescedWait > shutdownSet, "Quiesced must be awaited after Shutdown is signaled.");
    }

    [Fact]
    public void Script_never_kills_or_starts_explorer()
    {
        var source = ReadScript();
        var forbidden = new Regex(
            @"(?i)(\bStop-Process\b|\bStart-Process\b|\btaskkill(?:\.exe)?\b|\bTerminateProcess\b|\.Kill\s*\()",
            RegexOptions.CultureInvariant);

        Assert.DoesNotMatch(forbidden, source);
        Assert.DoesNotContain("Get-CurrentSessionExplorer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Start-ExplorerIfExited", source, StringComparison.Ordinal);
    }

    private static string ReadScript() =>
        File.ReadAllText(FindRepositoryFile(ScriptRelativePath));

    private static string FindRepositoryFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "CodexQuotaTaskbar.slnx")))
            {
                continue;
            }

            var candidate = Path.Combine(
                directory.FullName,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"Repository file was not found: {relativePath}",
            relativePath);
    }
}
