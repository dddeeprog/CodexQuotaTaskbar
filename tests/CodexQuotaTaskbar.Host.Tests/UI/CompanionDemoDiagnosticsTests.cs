using System.IO;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

[CollectionDefinition("Isolated companion diagnostics setup", DisableParallelization = true)]
public sealed class CompanionDemoDiagnosticsCollection { }

[Collection("Isolated companion diagnostics setup")]
public sealed class CompanionDemoDiagnosticsTests
{
    [Fact]
    public void Disposing_an_actual_control_twice_releases_both_kinds_without_forgetting_another_live_control()
    {
        CompanionDiagnostics.ConfigureDemo(null);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var ball = new CompanionBallControl();
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var id = (int)typeof(CompanionBallControl).GetField("diagnosticControlId", flags)!.GetValue(ball)!;
                var diagnostics = (CompanionDiagnostics)typeof(CompanionBallControl).GetField("diagnostics", flags)!.GetValue(ball)!;
                var frame = new CompanionEmotionDirector(DateTimeOffset.UnixEpoch, 42).Evaluate(DateTimeOffset.UnixEpoch);
                bool Observe(int control, string kind) => diagnostics.Observe(control, kind, frame, frame.Emotion, frame.Emotion, true, false);
                Assert.True(Observe(id, "selected")); Assert.True(Observe(id, "presented"));
                Assert.True(Observe(int.MaxValue, "selected")); Assert.True(Observe(int.MaxValue, "presented"));
                ball.Dispose(); ball.Dispose();
                Assert.True(Observe(id, "selected")); Assert.True(Observe(id, "presented"));
                Assert.False(Observe(int.MaxValue, "selected")); Assert.False(Observe(int.MaxValue, "presented"));
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Isolated control disposal timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        CompanionDiagnostics.ConfigureDemo(null);
    }

    [Fact]
    public void Demo_can_disable_the_writer_before_controls_start_without_touching_previous_data()
    {
        var fixtureRoot = Path.GetFullPath(Path.Combine(CodexQuotaTaskbar.Host.Tests.TestSupport.RepositoryPaths.Root, "artifacts/reliability/perf/fixtures"));
        var root = Path.Combine(fixtureRoot, "demo-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "companion.json");
        try
        {
            var start = DateTimeOffset.Parse("2026-10-24T00:00:00Z");
            var director = new CompanionEmotionDirector(start, 42);
            var frame = director.Evaluate(start);
            CompanionDiagnostics.ConfigureDemo(path);
            Assert.True(CompanionDiagnostics.Local.Observe(1, "selected", frame, frame.Emotion, frame.Emotion, true, false));
            var oldWriter = CompanionDiagnostics.Local;
            CompanionDiagnostics.ConfigureDemo(null);
            var previous = File.ReadAllBytes(path); // Replacement must flush the old producer's final event.
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
            if (!resolved.StartsWith(fixtureRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("demo-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the diagnostic test directory.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }
}
