namespace CodexQuotaTaskbar.Host.Update;

// One execution, injectable for offline verification. Version and security policy
// remain in GitHubUpdateService; the App still owns concurrency and lifetime.
internal sealed class UpdateWorkflow(
    Func<CancellationToken, Task<GitHubRelease?>> check,
    Func<GitHubRelease, CancellationToken, IProgress<UpdateStatus>, Task<string>> stage,
    Func<string, bool> launch,
    Action<UpdateStatus, bool> present,
    Func<Task> shutdown)
{
    internal async Task RunAsync(bool manual, CancellationToken token)
    {
        var running = true;
        try
        {
            token.ThrowIfCancellationRequested();
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
            if (!launch(path)) throw new InvalidOperationException("无法启动更新程序。");
            present(new(UpdateStage.Installing, "正在安装更新", $"即将重启到 {release.Tag}。"), manual);
            await shutdown();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is System.Net.Http.HttpRequestException or System.IO.IOException or System.IO.InvalidDataException
            or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException
            or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            running = false;
            present(UpdateStatus.Failure(error), manual);
        }
        finally { running = false; }
    }
}
