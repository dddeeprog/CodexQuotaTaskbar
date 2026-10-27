using CodexQuotaTaskbar.Host.Update;

namespace CodexQuotaTaskbar.Host.Tests.Update;

public sealed class UpdateWorkflowTests
{
    [Fact]
    public void Dismissed_progress_stays_quiet_but_explicit_restore_shows_latest_without_restarting()
    {
        var state = new UpdatePresentationState(); state.Begin();
        Assert.True(state.Receive(new(UpdateStage.Downloading, "下载", "演示", 20), true));
        state.Dismiss();
        Assert.False(state.Receive(new(UpdateStage.Downloading, "下载", "演示", 48), true));
        Assert.Equal(48, state.Restore().Percent); Assert.False(state.Dismissed);
        Assert.True(state.Receive(UpdateStatus.Failure(new IOException("synthetic path")), true));
        Assert.True(state.Current!.CanRetry);
        state.Begin(); Assert.Null(state.Current); // An explicit retry starts a new presentation.
        Assert.False(state.Receive(new(UpdateStage.Current, "最新", "演示"), false));
    }
    [Theory]
    [InlineData("更新包 SHA-256 校验失败。", "SHA-256")]
    [InlineData("更新包结构无效。", "文件数量")]
    [InlineData("更新包超过允许大小。", "下载大小")]
    [InlineData("无法启动更新程序。", "进程")]
    public void Product_errors_keep_specific_reasons_without_reexecuting_uncertain_install(string message, string expected)
    {
        var status = UpdateStatus.Failure(new InvalidDataException(message), retryAllowed: false);
        Assert.Equal(UpdateStage.Failed, status.Stage); Assert.Contains(expected, status.Detail);
        Assert.False(status.CanRetry);
    }
    [Fact]
    public void Arbitrary_exception_details_do_not_leak_paths_tokens_or_signed_urls()
    {
        var error = new UnauthorizedAccessException("C:\\Users\\private\\secret.json token=synthetic-secret https://example.invalid/?key=secret");
        var status = UpdateStatus.Failure(error);
        Assert.Contains("UnauthorizedAccessException", status.Detail);
        Assert.DoesNotContain("secret", status.Detail); Assert.DoesNotContain("C:\\", status.Detail);
        Assert.DoesNotContain("https://", status.Detail); Assert.True(status.CanRetry);
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Current_version_does_not_download_or_launch(bool manual)
    {
        var shown = new List<UpdateStatus>();
        var flow = new UpdateWorkflow(_ => Task.FromResult<GitHubRelease?>(null),
            (_, _, _) => throw new Exception("must not download"), _ => throw new Exception("must not launch"),
            (s, m) => { Assert.Equal(manual, m); shown.Add(s); }, () => throw new Exception("must not shut down"));
        await flow.RunAsync(manual, CancellationToken.None);
        Assert.Equal(manual ? new[] { UpdateStage.Checking, UpdateStage.Current } : [], shown.Select(s => s.Stage));
    }

    [Theory]
    [InlineData("network")] [InlineData("checksum")] [InlineData("disk")]
    [InlineData("access")] [InlineData("format")] [InlineData("timeout")] [InlineData("launch")]
    public async Task Failure_stops_install_and_has_readable_retry(string failure)
    {
        Exception error = failure switch
        {
            "network" => new HttpRequestException(), "checksum" => new InvalidDataException(),
            "disk" => new IOException(), "access" => new UnauthorizedAccessException(),
            "format" => new System.Text.Json.JsonException(), "timeout" => new OperationCanceledException(),
            _ => new InvalidOperationException(),
        };
        var shown = new List<UpdateStatus>(); var shutdown = false; var launched = false;
        var flow = new UpdateWorkflow(_ => failure is "network" or "format" or "timeout"
                ? Task.FromException<GitHubRelease?>(error) : Task.FromResult<GitHubRelease?>(Release()),
            (_, _, _) => failure == "launch" ? Task.FromResult("synthetic-stage") : Task.FromException<string>(error),
            _ => { launched = true; return false; }, (s, _) => shown.Add(s), () => { shutdown = true; return Task.CompletedTask; });
        await flow.RunAsync(true, CancellationToken.None);
        Assert.Equal(UpdateStage.Failed, shown.Last().Stage); Assert.True(shown.Last().CanRetry);
        Assert.False(shown.Last().Busy); Assert.NotEmpty(shown.Last().Message);
        Assert.False(shutdown); Assert.Equal(failure == "launch", launched);
    }

    [Fact]
    public async Task Staged_success_starts_installer_once_before_shutdown()
    {
        var events = new List<string>();
        var flow = new UpdateWorkflow(_ => Task.FromResult<GitHubRelease?>(Release()),
            (_, _, _) => { events.Add("stage"); return Task.FromResult("verified-file"); },
            path => { Assert.Equal("verified-file", path); events.Add("launch"); return true; },
            (s, _) => events.Add(s.Stage.ToString()), () => { events.Add("shutdown"); return Task.CompletedTask; });
        await flow.RunAsync(true, CancellationToken.None);
        Assert.Equal(new[] { "Checking", "Downloading", "stage", "launch", "Installing", "shutdown" }, events);
    }

    [Fact]
    public async Task Lifetime_cancellation_is_quiet_and_does_not_launch()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var flow = new UpdateWorkflow(_ => throw new Exception(), (_, _, _) => throw new Exception(),
            _ => throw new Exception(), (_, _) => throw new Exception(), () => throw new Exception());
        await flow.RunAsync(true, cts.Token);
    }
    private static GitHubRelease Release() => new(new Version(99, 0, 0), "v99.0.0", new Uri("https://example.invalid/test.zip"), new string('0', 64));
}
