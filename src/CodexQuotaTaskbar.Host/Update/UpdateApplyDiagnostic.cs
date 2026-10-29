using System.IO;
namespace CodexQuotaTaskbar.Host.Update;

internal enum UpdateApplyStage { WaitParent, Validate, Copy, Replace, Cleanup, Start, Rollback, Restart }
internal enum UpdateApplyErrorKind { AccessDenied, Io, InvalidOperation, Win32, Timeout, Cancelled, Argument, Other }
internal sealed record UpdateApplyDiagnostic(UpdateApplyStage Stage, UpdateApplyErrorKind Kind, int HResult)
{
    internal static UpdateApplyDiagnostic Capture(UpdateApplyStage stage, Exception error) => new(stage, error switch
    {
        UnauthorizedAccessException => UpdateApplyErrorKind.AccessDenied,
        IOException => UpdateApplyErrorKind.Io,
        System.ComponentModel.Win32Exception => UpdateApplyErrorKind.Win32,
        TimeoutException => UpdateApplyErrorKind.Timeout,
        OperationCanceledException => UpdateApplyErrorKind.Cancelled,
        ArgumentException => UpdateApplyErrorKind.Argument,
        InvalidOperationException => UpdateApplyErrorKind.InvalidOperation,
        _ => UpdateApplyErrorKind.Other,
    }, error.HResult);

    internal string SafeText => $"{Stage switch
    {
        UpdateApplyStage.WaitParent => "UA-WAIT-PARENT",
        UpdateApplyStage.Validate => "UA-VALIDATE",
        UpdateApplyStage.Copy => "UA-COPY",
        UpdateApplyStage.Replace => "UA-REPLACE",
        UpdateApplyStage.Cleanup => "UA-CLEANUP",
        UpdateApplyStage.Start => "UA-START",
        UpdateApplyStage.Rollback => "UA-ROLLBACK",
        UpdateApplyStage.Restart => "UA-RESTART",
        _ => "UA-UNKNOWN",
    }} / {Kind switch
    {
        UpdateApplyErrorKind.AccessDenied => "AccessDenied",
        UpdateApplyErrorKind.Io => "IO",
        UpdateApplyErrorKind.InvalidOperation => "InvalidOperation",
        UpdateApplyErrorKind.Win32 => "Win32",
        UpdateApplyErrorKind.Timeout => "Timeout",
        UpdateApplyErrorKind.Cancelled => "Cancelled",
        UpdateApplyErrorKind.Argument => "Argument",
        _ => "Other",
    }} / 0x{HResult:X8}";
}
internal sealed class UpdateApplyException : InvalidOperationException
{
    internal UpdateApplyException(UpdateApplyDiagnostic failure, UpdateApplyDiagnostic? cleanupFailure, bool replacementCompleted, int attemptCount)
        : base("更新安装未完成。")
    { Failure = failure; CleanupFailure = cleanupFailure; ReplacementCompleted = replacementCompleted; AttemptCount = attemptCount; HResult = failure.HResult; }
    internal UpdateApplyDiagnostic Failure { get; }
    internal UpdateApplyDiagnostic? CleanupFailure { get; }
    internal bool ReplacementCompleted { get; }
    internal int AttemptCount { get; }
    internal UpdateApplyDiagnostic? RecoveryFailure { get; init; }
    internal string RecoveryOutcome { get; init; } = "待恢复";
    internal string SafeDetail => $"安装诊断：{Failure.SafeText}。替换已完成：{(ReplacementCompleted ? "是" : "否")}；替换尝试：{AttemptCount}。" +
        (CleanupFailure is null ? "" : $"附加清理失败：{CleanupFailure.SafeText}。") +
        (RecoveryFailure is null ? "" : $"附加恢复失败：{RecoveryFailure.SafeText}。") +
        $"恢复结果：{RecoveryOutcome}。不会自动再次执行安装。";
}
