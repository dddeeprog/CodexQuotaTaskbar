using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Lifetime;

internal sealed record DotViewport(int Left, int Top, int Right, int Bottom);
internal sealed record DotAnchor(int X, int Y, int Bottom, int Left, int Top, int Right, int WorkBottom, double Dpi, bool Visible, int Transparency,
    DotViewport? Lane = null);
internal sealed record DotCommand(string Kind, DotAnchor? Anchor = null, DotInlineAction? Action = null);

// Same binary, isolated browser lifecycle. Inline data uses inherited local pipes;
// it is never sent to diagnostics, a socket, or a third-party service.
internal sealed class DotChatProcess : IDisposable
{
    private readonly bool inline;
    private readonly Dispatcher owner = Dispatcher.CurrentDispatcher;
    private readonly DotInlineSendGuard? sendGuard;
    private readonly Func<DotInlineSendCheckpoint, Task>? saveCheckpoint;
    private Process? process;
    private Process? retiringProcess;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly DispatcherTimer placement = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DotAnchorSendPump anchorPump;
    private DotAnchor? anchor;
    private long generation, publishedSequence;
    private bool replyAllowed;
    private long replyPermissionGeneration;
    private Task guardLoaded = Task.CompletedTask;
    private bool guardLoadStarted, uncertainAfterDisconnect, guardStorageFailed;
    private string? guardedRequestId;
    private long guardedDraftRevision;
    private readonly string draftEpoch = Guid.NewGuid().ToString("N");
    private string? guardedDraftEpoch, recoveryPendingRequestId;
    private bool recoveryPageOpened;
    private DotInlineState? latestState;
    private readonly SemaphoreSlim guardGate = new(1, 1);
    internal event Action<DotInlineState>? StateChanged;
    internal event EventHandler? OpenInlineRequested;
    internal DotInlineState? LatestState => latestState;
    internal bool ReplyAllowed => replyAllowed;
    internal bool IsRunning
    {
        get { try { return process is { HasExited: false }; } catch (InvalidOperationException) { return false; } }
    }

    internal DotChatProcess(bool inline = false, string? sendGuardPath = null,
        Func<DotInlineSendCheckpoint, Task>? persistCheckpoint = null)
    {
        this.inline = inline;
        if (inline) sendGuard = sendGuardPath is null ? DotInlineSendGuard.CreateDefault() : new(sendGuardPath);
        if (inline) saveCheckpoint = persistCheckpoint ?? (checkpoint => sendGuard!.SaveAsync(checkpoint));
        anchorPump = new(value => SendAsync(new("anchor", value)), enabled =>
        {
            if (enabled) placement.Start(); else placement.Stop();
        });
        placement.Tick += async (_, _) =>
        {
            if (!IsRunning) { anchorPump.Stop(); return; }
            await anchorPump.FlushAsync();
        };
    }

    internal void UpdateAnchor(DotAnchor value)
    {
        var previous = anchor; anchor = value;
        // Inline content has no child placement; only visibility is relevant to
        // the optional manual login host. Do not stream drag coordinates.
        if (!inline || previous?.Visible != value.Visible) anchorPump.Update(value);
    }

    internal void Start()
    {
        if (IsRunning) return;
        if (retiringProcess is { } retiring)
        {
            if (!retiring.HasExited) throw new InvalidOperationException("Dot 正在正常退出，请稍后再开启。");
            retiring.Dispose(); retiringProcess = null;
        }
        if (inline && !guardLoadStarted) { guardLoadStarted = true; guardLoaded = LoadGuardAsync(); }
        process?.Dispose();
        var version = ++generation;
        using var parent = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = inline,
            StandardInputEncoding = new UTF8Encoding(false), CreateNoWindow = true,
        };
        if (inline) start.StandardOutputEncoding = new UTF8Encoding(false);
        start.ArgumentList.Add("--dot-chat");
        start.ArgumentList.Add(parent.Id.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(parent.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
        if (inline) start.ArgumentList.Add("--inline");
        process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 Dot 接收。");
        if (inline) _ = ReadStatesAsync(process, version);
        anchorPump.Start();
    }

    internal void Show()
    {
        Start();
        if (inline) { OpenInlineRequested?.Invoke(this, EventArgs.Empty); return; }
        _ = SendAsync(new("reply", anchor));
    }

    // The selected BongoCat mode is read-only even when its renderer falls back.
    // A permission change affects future sends, never an attempt already across the pipe.
    internal void SetReplyAllowed(bool allowed)
    {
        allowed = false; // All island appearances are now notification-only.
        if (replyAllowed == allowed) return;
        replyAllowed = allowed;
        replyPermissionGeneration++;
        if (latestState is not { } state) return;
        state = state with { Sequence = ++publishedSequence, ReplyAllowed = allowed };
        latestState = state;
        StateChanged?.Invoke(state);
    }

    internal async Task<bool> SendInlineActionAsync(DotInlineAction action)
    {
        if (!inline || !DotInlineWire.ValidAction(action)) return false;
        if (!replyAllowed && action.Kind != "ack-notice") return false;
        var actionTarget = process; var actionGeneration = generation;
        var actionPermissionGeneration = replyPermissionGeneration;
        if (action.Kind == "send" && !replyAllowed) { PublishModeNotDispatched(action); return false; }
        await guardLoaded;
        if (action.Kind == "send" && (!replyAllowed || replyPermissionGeneration != actionPermissionGeneration))
        { PublishModeNotDispatched(action); return false; }
        if (actionTarget is null || !ReferenceEquals(process, actionTarget) || generation != actionGeneration) return false;
        if (action.Kind is "reviewed_sent" or "reviewed_not_sent")
        {
            if (!uncertainAfterDisconnect || guardStorageFailed || !recoveryPageOpened || latestState?.PageVisible != false
                || guardedRequestId is null) return false;
            recoveryPendingRequestId = guardedRequestId;
            action = action with { RequestId = guardedRequestId,
                OriginalDraftRevision = guardedDraftEpoch == draftEpoch ? guardedDraftRevision : null };
        }
        if (action.Kind == "earlier-read" && (!recoveryPageOpened || latestState?.PageVisible != false)) return false;
        if (action.Kind == "send")
        {
            if (uncertainAfterDisconnect || guardStorageFailed || !IsRunning) return false;
            if (latestState is { HasPendingAttempt: true } or { Busy: true } or { NeedsReview: true }) return false;
            // The tiny no-content checkpoint must reach disk before Send crosses the pipe.
            await guardGate.WaitAsync();
            try
            {
                if (uncertainAfterDisconnect || guardStorageFailed || !IsRunning
                    || generation != actionGeneration || !ReferenceEquals(process, actionTarget)) return false;
                if (!replyAllowed || replyPermissionGeneration != actionPermissionGeneration)
                { PublishModeNotDispatched(action); return false; }
                if (guardedRequestId is not null) return false;
                guardedRequestId = action.RequestId;
                guardedDraftRevision = action.DraftRevision; guardedDraftEpoch = draftEpoch;
                try { await saveCheckpoint!(new(true, guardedRequestId, DateTimeOffset.UtcNow, guardedDraftRevision, draftEpoch)); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
                {
                    guardStorageFailed = true; uncertainAfterDisconnect = true; PublishOfflineState(); return false;
                }
                if (!replyAllowed || replyPermissionGeneration != actionPermissionGeneration)
                { await CancelUnsentForModeAsync(action, gateHeld: true); return false; }
                if (uncertainAfterDisconnect || guardStorageFailed || !IsRunning
                    || generation != actionGeneration || !ReferenceEquals(process, actionTarget))
                { uncertainAfterDisconnect = true; PublishOfflineState(); return false; }
            }
            finally { guardGate.Release(); }
        }
        var modeRejectedBeforeWrite = false;
        var sent = await SendAsync(new("inline", Action: action), actionTarget, actionGeneration,
            action.Kind == "send" ? actionPermissionGeneration : null, () => modeRejectedBeforeWrite = true);
        if (!sent && action.Kind == "send")
        {
            if (modeRejectedBeforeWrite) await CancelUnsentForModeAsync(action);
            else { uncertainAfterDisconnect = true; PublishOfflineState(); }
        }
        return sent;
    }

    private void PublishModeNotDispatched(DotInlineAction action)
    {
        if (latestState is not { } state || guardedRequestId is not null || state.HasPendingAttempt || state.Busy) return;
        state = state with { Sequence = ++publishedSequence, ReplyAllowed = replyAllowed,
            AttemptState = "not_dispatched", LastRequestId = action.RequestId,
            Status = "尚未发送：BongoCat 模式仅阅读消息，草稿已保留" };
        latestState = state; StateChanged?.Invoke(state);
    }

    // Only called on a branch that has definitely not written this request to the pipe.
    // This cannot clear an earlier, actually dispatched or unknown attempt.
    private async Task CancelUnsentForModeAsync(DotInlineAction action, bool gateHeld = false)
    {
        if (!gateHeld) await guardGate.WaitAsync();
        try
        {
            if (guardedRequestId != action.RequestId) return;
            await saveCheckpoint!(new(false, null, DateTimeOffset.UtcNow));
            guardedRequestId = null;
            uncertainAfterDisconnect = false;
            if (latestState is { } state)
            {
                state = state with { Sequence = ++publishedSequence, ReplyAllowed = replyAllowed,
                    HasPendingAttempt = false, NeedsReview = false, Busy = false,
                    AttemptState = "not_dispatched", LastRequestId = action.RequestId,
                    Status = "尚未发送：模式已切换，草稿已保留" };
                latestState = state; StateChanged?.Invoke(state);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { guardStorageFailed = true; uncertainAfterDisconnect = true; PublishOfflineState(); }
        finally { if (!gateHeld) guardGate.Release(); }
    }

    private async Task LoadGuardAsync()
    {
        try
        {
            var checkpoint = await sendGuard!.LoadAsync();
            await owner.InvokeAsync(() => { uncertainAfterDisconnect = checkpoint.Uncertain; guardedRequestId = checkpoint.RequestId;
                guardedDraftRevision = checkpoint.DraftRevision; guardedDraftEpoch = checkpoint.DraftEpoch; });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { await owner.InvokeAsync(() => { uncertainAfterDisconnect = true; guardStorageFailed = true; }); }
    }

    private async Task ReadStatesAsync(Process target, long version)
    {
        string? session = null;
        long sequence = 0;
        try
        {
            await guardLoaded.ConfigureAwait(false);
            var reader = new DotInlineLineReader(target.StandardOutput, DotInlineWire.MaximumStateCharacters);
            while (await reader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
            {
                var state = DotInlineWire.Deserialize<DotInlineState>(line);
                if (state is null || !DotInlineWire.ValidState(state)) throw new InvalidDataException("Invalid Dot state protocol.");
                session ??= state.SessionId;
                if (state.SessionId != session || state.Sequence <= sequence) continue;
                sequence = state.Sequence;
                await owner.InvokeAsync(() => { if (generation == version && ReferenceEquals(process, target)) AcceptState(state); });
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
        finally
        {
            await owner.InvokeAsync(() =>
            {
                if (generation != version || !ReferenceEquals(process, target)) return;
                anchorPump.Stop();
                uncertainAfterDisconnect |= guardedRequestId is not null || latestState is { HasPendingAttempt: true };
                PublishOfflineState();
            });
        }
    }

    private void AcceptState(DotInlineState state)
    {
        if (state.PageVisible) recoveryPageOpened = true;
        if (recoveryPendingRequestId is { } review && state.LastRequestId == review
            && state.AttemptState is "reviewed_sent" or "reviewed_not_sent")
        {
            recoveryPendingRequestId = null; uncertainAfterDisconnect = false; guardedRequestId = null;
            _ = ClearGuardAsync(review);
        }
        if (!uncertainAfterDisconnect && guardedRequestId is { } request && state.LastRequestId == request
            && !state.HasPendingAttempt && !state.Busy && state.AttemptState is "confirmed" or "not_dispatched")
        {
            guardedRequestId = null;
            _ = ClearGuardAsync(request);
        }
        if (uncertainAfterDisconnect || guardStorageFailed)
            state = state with { NeedsReview = true, HasPendingAttempt = true,
                RecoveryRequired = true, RecoveryCanReview = !guardStorageFailed && recoveryPageOpened && !state.PageVisible,
                Status = guardStorageFailed ? "发送保护记录暂不可用 · 草稿已保留，未发送" : "上次发送结果待核对 · 不会自动重发，请先用网页登录核验" };
        state = state with { Sequence = ++publishedSequence, ReplyAllowed = replyAllowed };
        latestState = state;
        StateChanged?.Invoke(state);
    }

    private async Task ClearGuardAsync(string request)
    {
        await guardGate.WaitAsync();
        try
        {
            // A newer request must never be cleared by an older receipt's disk write.
            if (guardedRequestId is not null) return;
            await saveCheckpoint!(new(false, null, DateTimeOffset.UtcNow));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { guardStorageFailed = true; uncertainAfterDisconnect = true; PublishOfflineState(); }
        finally { guardGate.Release(); }
    }

    private void PublishOfflineState()
    {
        if (latestState is not { } state) return;
        state = state with { Sequence = ++publishedSequence, ReplyAllowed = replyAllowed, Online = false, NeedsReview = state.NeedsReview || uncertainAfterDisconnect,
            HasPendingAttempt = state.HasPendingAttempt || guardedRequestId is not null || uncertainAfterDisconnect,
            Status = uncertainAfterDisconnect ? "上次发送结果待核对 · 草稿已保留，不会自动重发" : "Dot 连接已中断 · 草稿已保留" };
        latestState = state; StateChanged?.Invoke(state);
    }

    private async Task<bool> SendAsync(DotCommand command, Process? expectedTarget = null, long? expectedGeneration = null,
        long? expectedReplyPermissionGeneration = null, Action? modeRejectedBeforeWrite = null)
    {
        var target = process;
        if (target is null) return false;
        // Coalesce placement before entering the pipe; user actions are rare and are not dropped.
        if (command.Kind == "anchor" && writeGate.CurrentCount == 0) return false;
        await writeGate.WaitAsync();
        try
        {
            if (expectedReplyPermissionGeneration is { } permission
                && (!replyAllowed || replyPermissionGeneration != permission))
            { modeRejectedBeforeWrite?.Invoke(); return false; }
            if (expectedTarget is not null && (!ReferenceEquals(target, expectedTarget)
                || !ReferenceEquals(process, target) || generation != expectedGeneration)) return false;
            if (!target.HasExited)
            {
                await target.StandardInput.WriteLineAsync(inline ? DotInlineWire.Serialize(command) : JsonSerializer.Serialize(command));
                await target.StandardInput.FlushAsync();
                return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        { if (ReferenceEquals(process, target)) anchorPump.Stop(); return false; }
        finally { writeGate.Release(); }
    }

    internal static bool TryOpen(string[] args, System.Windows.Application app)
    {
        if (args.Length == 0 || args[0] != "--dot-chat") return false;
        Process? parent = null;
        var inline = args.Length == 4 && args[3] == "--inline";
        if ((args.Length == 3 || inline) && int.TryParse(args[1], out var id) && long.TryParse(args[2], out var ticks))
        {
            parent = Process.GetProcessById(id);
            if (parent.StartTime.ToUniversalTime().Ticks != ticks) { parent.Dispose(); throw new ArgumentException("所属点岛已退出。"); }
        }
        else if (args.Length != 1) throw new ArgumentException("Dot 接收启动参数无效。");
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        if (inline) Console.OutputEncoding = new UTF8Encoding(false);
        var window = new DotChatWindow(parent, inline);
        app.MainWindow = window;
        app.ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Start();
        return true;
    }

    public void Dispose()
    {
        anchorPump.Stop();
        if (inline) { uncertainAfterDisconnect |= guardedRequestId is not null || latestState is { HasPendingAttempt: true }; PublishOfflineState(); }
        generation++;
        if (process is null) return;
        try
        {
            // EOF is a graceful stop even when the bubble is hidden and there is no main HWND to close.
            process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { }
        // Keep a retiring child's identity until it has exited. A fast off/on
        // must not open the same browser profile in two receivers concurrently.
        retiringProcess = process; process = null;
    }
}
