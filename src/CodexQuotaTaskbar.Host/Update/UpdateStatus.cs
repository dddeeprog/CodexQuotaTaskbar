using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace CodexQuotaTaskbar.Host.Update;

internal enum UpdateStage { Checking, Current, Downloading, Verifying, Installing, Failed }

internal sealed record UpdateStatus(UpdateStage Stage, string Title, string Message, double? Percent = null,
    string? Detail = null, bool RetryAllowed = true)
{
    internal bool Busy => Stage is UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.Verifying or UpdateStage.Installing;
    internal bool CanRetry => Stage == UpdateStage.Failed && RetryAllowed;
    internal static UpdateStatus Failure(Exception error, bool retryAllowed = true) => new(UpdateStage.Failed, "更新未完成", error switch
    {
        UpdateApplyException apply => $"更新安装未完成。{apply.RecoveryOutcome}。请依据安装阶段和错误类别检查。",
        OperationCanceledException => "请求超时或中断。请检查网络后重试。",
        InvalidDataException => "发行信息或更新包校验失败。未执行未通过校验的更新包，请稍后重试。",
        JsonException => "发行信息格式无效。请稍后重试。",
        HttpRequestException => "无法连接 GitHub 或下载更新。请检查网络后重试。",
        UnauthorizedAccessException => "没有访问更新目录的权限。请检查目录权限后重试。",
        IOException => "无法写入或读取更新包。请检查磁盘空间和目录是否被占用。",
        System.ComponentModel.Win32Exception or InvalidOperationException => "无法启动更新程序。当前版本仍可使用，请稍后重试。",
        _ => "更新暂时不可用。当前版本仍可使用，请稍后重试。",
    }, Detail: SafeDetail(error), RetryAllowed: error is not UpdateApplyException && retryAllowed);

    // Product-owned diagnostics are safe verbatim. Arbitrary exception messages
    // may contain local paths, signed URLs or credentials: expose category/code only.
    internal static string SafeDetail(Exception error) => error is UpdateApplyException apply ? apply.SafeDetail : error.Message.Trim() switch
    {
        "更新包 SHA-256 校验失败。" => "具体原因：更新包的 SHA-256 与发行版声明不一致。",
        "更新包超过允许大小。" => "具体原因：更新包超过允许的下载大小。",
        "更新包结构无效。" => "具体原因：压缩包的文件数量、文件名或解压大小不符合要求。",
        "更新程序不是有效的 Windows 可执行文件。" => "具体原因：更新程序没有有效的 Windows 可执行文件标识。",
        "发行信息缺少有效的 Windows x64 更新包或 SHA-256 校验信息。" => "具体原因：发行版缺少 Windows x64 更新包或 SHA-256 校验信息。",
        "更新版本标签无效。" => "具体原因：发行版版本标签不符合要求。",
        "更新目录无效。" => "具体原因：目标目录不在允许的更新目录内。",
        "更新源或目标无效。" => "具体原因：安装源或目标文件不存在、路径相同，或目标不是 EXE。",
        "无法启动更新程序。" => "具体原因：系统未能创建更新程序进程。",
        _ when error is HttpRequestException { StatusCode: { } code } => $"具体原因：服务器返回 HTTP {(int)code}。",
        _ when error is System.ComponentModel.Win32Exception win32 => $"具体原因：Windows 启动失败，错误码 {win32.NativeErrorCode}。",
        _ => $"具体原因：{error.GetType().Name}（0x{error.HResult:X8}）。为保护隐私，不展示原始路径或请求内容。",
    };
}

// Small state-only presenter contract; closing a view never restarts an update.
internal sealed class UpdatePresentationState
{
    internal UpdateStatus? Current { get; private set; }
    internal bool Dismissed { get; private set; }
    internal void Begin() { Current = null; Dismissed = false; }
    internal bool Receive(UpdateStatus status, bool visibleAllowed)
    { Current = status; return visibleAllowed && !Dismissed; }
    internal void Dismiss() => Dismissed = true;
    internal UpdateStatus Restore() { Dismissed = false; return Current ?? new(UpdateStage.Checking, "正在检查更新", "已有更新任务正在进行，请稍候。"); }
}
