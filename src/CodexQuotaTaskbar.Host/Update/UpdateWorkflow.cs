namespace CodexQuotaTaskbar.Host.Update;

// One execution, injectable for offline verification. Version and security policy
// remain in GitHubUpdateService; the App still owns concurrency and lifetime.
internal sealed class UpdateWorkflow(
    Func<CancellationToken, Task<GitHubRelease?>> check,
    Func<GitHubRelease, CancellationToken, IProgress<UpdateStatus>, Task<string>> stage,
    Func<string, bool> launch,
    Action<UpdateStatus, bool> present,
    Func<Task> shutdown,
    UpdateLaunchState? sharedLaunchState = null,
    Func<string, CancellationToken, Task<bool>>? prepareLaunch = null)
{
    private readonly UpdateLaunchState launchState = sharedLaunchState ?? new();

    internal async Task RunAsync(bool manual, CancellationToken token)
    {
        var running = true;
        try
        {
            token.ThrowIfCancellationRequested();
            if (launchState.PendingTag is not null)
            {
                if (manual) present(launchState.BlockedStatus, manual);
                return;
            }
            if (manual) present(new(UpdateStage.Checking, "正在检查更新", "连接 GitHub 正式发行版…"), manual);
            var release = await check(token);
            if (release is null)
            {
                if (manual) present(new(UpdateStage.Current, "已是最新版本", $"当前为 v{ProductVersion.Text}，暂无更高版本的正式更新。"), manual);
                return;
            }
            present(new(UpdateStage.Downloading, "发现新版本", $"正在下载 {release.Tag}…"), manual);
            // Late posted download events must not overwrite terminal failure/success.
            var progress = new Progress<UpdateStatus>(status => { if (running) present(status, manual); });
            var path = await stage(release, token, progress);
            token.ThrowIfCancellationRequested();
            running = false;
            if (!launchState.TryBegin(release.Tag))
            {
                if (manual) present(launchState.BlockedStatus, manual);
                return;
            }
            if (!await (prepareLaunch?.Invoke(path, token) ?? Task.FromResult(launch(path))))
            {
                launchState.NotStarted();
                throw new InvalidOperationException("无法启动更新程序。");
            }
            present(new(UpdateStage.Installing, "正在安装更新", $"即将重启到 {release.Tag}。"), manual);
            await shutdown();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is System.Net.Http.HttpRequestException or System.IO.IOException or System.IO.InvalidDataException
            or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException
            or System.ComponentModel.Win32Exception or OperationCanceledException or TimeoutException)
        {
            running = false;
            var uncertain = launchState.PendingTag is not null;
            var failure = UpdateStatus.Failure(error, retryAllowed: !uncertain);
            if (uncertain) failure = failure with { Message = launchState.BlockedStatus.Message };
            present(failure, manual);
        }
        finally { running = false; }
    }
}

// Owned by App for its entire lifetime, not by one menu invocation. An exception
// from Process.Start cannot prove that no installer was created. Only an explicit
// "not started" result releases the reservation; no finally block may reset it.
internal sealed class UpdateLaunchState
{
    private string? pendingTag;
    internal string? PendingTag => System.Threading.Volatile.Read(ref pendingTag);
    internal bool TryBegin(string tag) => System.Threading.Interlocked.CompareExchange(ref pendingTag, tag, null) is null;
    internal void NotStarted() => System.Threading.Interlocked.Exchange(ref pendingTag, null);
    internal UpdateStatus BlockedStatus => new(UpdateStage.Failed, "更新状态待确认",
        "更新程序已启动或启动结果尚未确认，本次运行不会重复安装。请先确认更新程序是否完成。", RetryAllowed: false);
}
