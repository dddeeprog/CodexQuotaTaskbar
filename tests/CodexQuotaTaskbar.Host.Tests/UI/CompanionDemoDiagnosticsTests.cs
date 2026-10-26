using System.IO;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

[CollectionDefinition("Isolated companion diagnostics setup", DisableParallelization = true)]
public sealed class CompanionDemoDiagnosticsCollection { }

[Collection("Isolated companion diagnostics setup")]
public sealed class CompanionDemoDiagnosticsTests
{
    [Fact]
    public void Demo_can_disable_the_writer_before_controls_start_without_touching_previous_data()
    {
        var root = Path.Combine(Path.GetTempPath(), "diandao-demo-diagnostics-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "companion.json");
        try
        {
            var start = DateTimeOffset.Parse("2026-10-24T00:00:00Z");
            var director = new CompanionEmotionDirector(start, 42);
            var frame = director.Evaluate(start);
            CompanionDiagnostics.ConfigureDemo(path);
            Assert.True(CompanionDiagnostics.Local.Observe(1, "selected", frame, frame.Emotion, frame.Emotion, true, false));
            var previous = File.ReadAllBytes(path);
            var oldWriter = CompanionDiagnostics.Local;
            CompanionDiagnostics.ConfigureDemo(null);
            Assert.NotSame(oldWriter, CompanionDiagnostics.Local);
            frame = director.Evaluate(start.AddSeconds(6));
            Assert.True(CompanionDiagnostics.Local.Observe(1, "selected", frame, frame.Emotion, frame.Emotion, true, false));
            Assert.Equal(previous, File.ReadAllBytes(path));
            Assert.Single(CompanionDiagnostics.Local.Entries);
        }
        finally
        {
            CompanionDiagnostics.ConfigureDemo(null);
            var resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("diandao-demo-diagnostics-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the diagnostic test directory.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }
}
