using System.Text.Json;

namespace CodexQuotaTaskbar.UpdateSandbox;

internal static class Program
{
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    internal static async Task<int> Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Windows only.");
            if (args.Length == 0 || args is ["--help"])
            {
                Console.WriteLine("TEST FIXTURE ONLY. Default is read-only dry-run. Required: --sandbox-root <absolute fresh GUID root> --package-root <absolute built package> --package-sha256 <manifest SHA256> [--scenario all|success|pending|recover] [--execute]. See README. No official App/helper is launched.");
                return 0;
            }
            var options = Options.Parse(args);
            var boundary = new Boundary(options.SandboxRoot);
            boundary.CheckRoot(requireFresh: !options.Child);
            var package = Package.Load(options.PackageRoot, options.PackageSha256);
            if (options.Child)
            {
                if (!options.Execute) throw new InvalidOperationException("Fixture child requires --execute.");
                await FixtureChild.RunAsync(options, boundary, package);
                return 0;
            }
            var scenarios = options.Scenario == "all" ? new[] { "success", "pending", "recover" } : [options.Scenario];
            var plan = new
            {
                Mode = options.Execute ? "execute" : "dry-run",
                Boundary = "TEST FIXTURE: no SystemUpdateRuntime, official trust, App mutex or UI-ready coverage",
                Root = boundary.Root,
                PackageManifestSha256 = package.ManifestHash,
                Scenarios = scenarios.Select(name => Scenario.Plan(boundary, name)),
                ExcludedPaths = boundary.Excluded,
                Cleanup = "No automatic directory deletion; pending receipts/backups and all evidence are retained. Children receive only their owned stop file, never a name-based kill."
            };
            // The dry-run branch must stay ahead of every directory creation, file write, transaction and Start.
            if (!options.Execute) { Console.WriteLine(JsonSerializer.Serialize(plan, Json)); return 0; }
            boundary.CreateFreshRoot();
            using var lease = new FileStream(boundary.InRoot("run.owner.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            Files.WriteJson(boundary, boundary.InRoot("plan.json"), plan);
            var outcomes = new List<object>();
            var passed = true;
            foreach (var scenario in scenarios)
            {
                try { outcomes.Add(await Scenario.RunAsync(boundary, package, scenario)); }
                catch (Exception error)
                {
                    passed = false;
                    outcomes.Add(new { Scenario = scenario, Passed = false, ErrorType = error.GetType().Name, error.Message });
                    // Preserve the failing tree and stop starting more processes after an unexpected failure.
                    break;
                }
            }
            Files.WriteJson(boundary, boundary.InRoot("summary.json"), new { Passed = passed, Results = outcomes });
            Console.WriteLine(JsonSerializer.Serialize(new { Passed = passed, boundary.Root }, Json));
            return passed ? 0 : 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Sandbox refused or failed: {error.GetType().Name}: {error.Message}");
            return 2;
        }
    }
}

internal sealed record Options(bool Execute, bool Child, string SandboxRoot, string PackageRoot, string PackageSha256, string Scenario, string? Ticket)
{
    internal static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var execute = false;
        var child = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--execute") { if (execute) throw new ArgumentException("Duplicate --execute."); execute = true; continue; }
            if (args[i] == "--fixture-child") { if (child) throw new ArgumentException("Duplicate child switch."); child = true; continue; }
            var name = args[i];
            if (name is not ("--sandbox-root" or "--package-root" or "--package-sha256" or "--scenario" or "--ticket")
                || ++i >= args.Length || !values.TryAdd(name, args[i])) throw new ArgumentException("Unknown, missing or duplicate argument.");
        }
        string Required(string key) => values.TryGetValue(key, out var value) ? value : throw new ArgumentException($"Missing {key}.");
        var scenario = values.GetValueOrDefault("--scenario", "all");
        if (scenario is not ("all" or "success" or "pending" or "recover")) throw new ArgumentException("Invalid scenario.");
        var hash = Required("--package-sha256").ToUpperInvariant();
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new ArgumentException("Manifest SHA256 must be explicit.");
        var ticket = values.GetValueOrDefault("--ticket");
        if (child != (ticket is not null)) throw new ArgumentException("A ticket is only valid for fixture child mode.");
        return new(execute, child, Boundary.Absolute(Required("--sandbox-root")), Boundary.Absolute(Required("--package-root")), hash, scenario, ticket);
    }
}
