using System.Runtime.InteropServices;
using System.Text.Json;
using CodexQuotaTaskbar.Host.Provider;

namespace CodexQuotaTaskbar.Host.Tests.Provider;

public sealed class DotReadPollingTests
{
    [Fact]
    public async Task Two_read_failures_do_not_stop_observation_and_a_success_resets_the_backoff()
    {
        var reads = 0;
        var stopped = false;
        var failures = new List<string>();
        var delays = new List<double>();
        await DotReadPolling.RunAsync(() =>
        {
            reads++;
            if (reads == 1) throw new COMException("Synthetic private exception detail must not be logged.");
            if (reads == 2) throw new JsonException("Synthetic malformed observation.");
            if (reads == 4) stopped = true;
            return Task.CompletedTask;
        }, error => failures.Add(DotReadPolling.ErrorKind(error)), () => stopped, CancellationToken.None,
        (duration, _) => { delays.Add(duration.TotalSeconds); return Task.CompletedTask; });

        Assert.Equal(4, reads);
        Assert.Equal(new[] { "COMException", "JsonException" }, failures);
        Assert.Equal(new[] { 4d, 8d, 2d }, delays);
    }

    [Theory]
    [InlineData("invalid-operation")]
    [InlineData("com")]
    [InlineData("json")]
    [InlineData("io")]
    [InlineData("argument")]
    public async Task Each_supported_transient_read_failure_allows_a_later_observation(string kind)
    {
        var reads = 0;
        var failures = 0;
        var stopped = false;
        await DotReadPolling.RunAsync(() =>
        {
            if (++reads == 1) throw kind switch
            {
                "invalid-operation" => new InvalidOperationException(), "com" => new COMException(),
                "json" => new JsonException(), "io" => new IOException(), "argument" => new ArgumentException(),
                _ => new Exception("Unreachable")
            };
            stopped = true;
            return Task.CompletedTask;
        }, _ => failures++, () => stopped, CancellationToken.None, (_, _) => Task.CompletedTask);
        Assert.Equal(2, reads);
        Assert.Equal(1, failures);
    }

    [Fact]
    public async Task Repeated_failures_have_bounded_backoff_and_never_invoke_a_submission_delegate()
    {
        var reads = 0;
        var stopped = false;
        var delays = new List<double>();
        await DotReadPolling.RunAsync(() =>
        {
            if (++reads <= 6) throw new InvalidOperationException();
            stopped = true;
            return Task.CompletedTask;
        }, _ => { }, () => stopped, CancellationToken.None,
            (duration, _) => { delays.Add(duration.TotalSeconds); return Task.CompletedTask; });
        Assert.Equal(7, reads);
        Assert.Equal(new[] { 4d, 8d, 10d, 10d, 10d, 10d }, delays);
    }

    [Fact]
    public async Task Cancellation_before_first_read_is_not_swallowed_as_a_transient_failure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var reads = 0;
        var failures = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DotReadPolling.RunAsync(
            () => { reads++; return Task.CompletedTask; }, _ => failures++, () => false, cancellation.Token));
        Assert.Equal(0, reads);
        Assert.Equal(0, failures);
    }

    [Fact]
    public async Task Cancellation_during_backoff_stops_without_another_read()
    {
        using var cancellation = new CancellationTokenSource();
        var reads = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DotReadPolling.RunAsync(
            () => { reads++; throw new COMException(); }, _ => { }, () => false, cancellation.Token,
            (_, token) => { cancellation.Cancel(); return Task.FromCanceled(token); }));
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task Unexpected_programming_errors_are_not_hidden_by_the_recovery_loop()
    {
        var errors = 0;
        await Assert.ThrowsAsync<FormatException>(() => DotReadPolling.RunAsync(
            () => throw new FormatException("Synthetic programming error"), _ => errors++, () => false,
            CancellationToken.None, (_, _) => Task.CompletedTask));
        Assert.Equal(0, errors);
    }
}
