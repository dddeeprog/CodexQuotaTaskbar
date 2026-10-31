using CodexQuotaTaskbar.Core.Quota;

namespace CodexQuotaTaskbar.Host.Resets;

internal sealed record ResetCreditUseContext(string AccountFingerprint, string ConnectionEpoch, string DisplayName,
    QuotaResetCreditsSnapshot Cards, bool Fresh, string? WorkspaceBinding = null);
internal interface IResetCreditUseTransport
{
    Task<ResetCreditUseContext?> ReadContextAsync(CancellationToken cancellationToken);
    Task<string> ConsumeAsync(ResetCreditUseContext context, string requestKey, CancellationToken cancellationToken);
    Task RefreshAsync(CancellationToken cancellationToken);
}
internal enum ResetCreditUseState { Idle, Checking, Confirming, Sending, Unknown, Completed, Blocked }
internal sealed record ResetCreditUseConfirmation(string Token, string DisplayName, bool IsRetry, bool SelectionMayVary);
internal sealed record ResetCreditUseView(ResetCreditUseState State, string Message, bool CanRetry, bool IsBusy)
{
    public ResetCreditUseConfirmation? Confirmation { get; init; }
}

// One App-owned instance. Windows may cancel a confirmation, never an already sent operation.
internal sealed class ResetCreditUseService : IDisposable
{
    private readonly IResetCreditUseTransport transport;
    private readonly ResetCreditPendingStore store;
    private readonly SemaphoreSlim operation = new(1, 1);
    private readonly object gate = new();
    private ResetCreditUseContext? confirmedContext;
    private string? confirmedKey;
    private string? knownOutcome;
    private long revision;
    private bool disposeRequested;
    internal event Action? Changed;
    internal ResetCreditUseView View { get; private set; }

    internal ResetCreditUseService(IResetCreditUseTransport transport, ResetCreditPendingStore store)
    {
        this.transport = transport;
        this.store = store;
        View = store.IsBlocked ? new(ResetCreditUseState.Blocked, "本地请求记录不可用，已禁止使用", false, false)
            : store.Current is { State: "pending" } ? new(ResetCreditUseState.Unknown, "此前请求结果尚未确认", true, false)
            : store.Current is { State: "completed" } receipt ? new(ResetCreditUseState.Completed, ResultMessage(receipt.Outcome!), false, false)
            : new(ResetCreditUseState.Idle, string.Empty, false, false);
    }

    internal async Task<ResetCreditUseConfirmation?> PrepareConfirmationAsync(bool retry, CancellationToken cancellationToken = default)
    {
        if (!await operation.WaitAsync(0, cancellationToken)) return null;
        try
        {
            long version;
            lock (gate)
            {
                if (disposeRequested || store.IsBlocked || store.Current is { State: "pending" } && !retry) return null;
                version = ++revision;
                confirmedContext = null;
                Set(new(ResetCreditUseState.Checking, "正在核对账户与重置卡…", false, true));
            }
            var context = await transport.ReadContextAsync(cancellationToken);
            lock (gate)
            {
                if (version != revision || !Reliable(context) || !context!.Fresh
                    || retry && (store.Current is not { State: "pending" } pending || pending.AccountFingerprint != context.AccountFingerprint)
                    || !retry && (!CanUse(context) || store.Current is { State: "completed" } completed && context.Cards.CapturedAt <= completed.SentAt))
                { Restore("账户或重置卡信息已变化，请重新核对"); return null; }
                var confirmation = new ResetCreditUseConfirmation(Guid.NewGuid().ToString("N"), context.DisplayName, retry, !HasCompleteExpiries(context));
                confirmedContext = context;
                confirmedKey = retry ? store.Current!.RequestKey : Guid.NewGuid().ToString("N");
                Set(new(ResetCreditUseState.Confirming, retry ? "重试上次使用，仍可能消耗1张" : "请确认使用当前账户的重置卡", false, false) { Confirmation = confirmation });
                return confirmation;
            }
        }
        catch (Exception) { lock (gate) Restore("核对失败，未发送使用请求"); return null; }
        finally { operation.Release(); FinishDispose(); }
    }

    internal async Task ConfirmAsync(string token, CancellationToken cancellationToken = default)
    {
        if (!await operation.WaitAsync(0, cancellationToken)) return;
        try
        {
            ResetCreditUseContext expected;
            ResetCreditUseConfirmation confirmation;
            string key;
            long version;
            lock (gate)
            {
                if (disposeRequested || store.IsBlocked || View.Confirmation is not { } candidate || candidate.Token != token || confirmedContext is null) return;
                confirmation = candidate; expected = confirmedContext; key = confirmedKey!; version = revision;
                Set(new(ResetCreditUseState.Checking, "正在进行最后核对…", false, true));
            }
            var context = await transport.ReadContextAsync(cancellationToken);
            ResetCreditPending pending;
            lock (gate)
            {
                if (version != revision || !Reliable(context) || !context!.Fresh || !SameIdentity(context, expected)
                    || !confirmation.IsRetry && !CanUse(context))
                { Restore("账户或连接发生变化，未发送使用请求"); return; }
                pending = confirmation.IsRetry ? store.Current! : new(context.AccountFingerprint, key, "pending", null, DateTimeOffset.UtcNow);
                knownOutcome = null;
                if (!store.Save(pending)) { Block(); return; }
                confirmedContext = null;
                confirmedKey = null;
                Set(new(ResetCreditUseState.Sending, "正在提交，请勿重复操作…", false, true));
            }
            string outcome;
            try { outcome = await transport.ConsumeAsync(context!, key, CancellationToken.None); }
            catch (Exception) { lock (gate) Restore("请求结果未知；可先只读刷新，继续上次使用仍可能消耗1张且必须沿用原请求编号"); return; }
            lock (gate)
            {
                if (outcome is not ("reset" or "alreadyRedeemed" or "noCredit" or "nothingToReset"))
                { Restore("请求结果未知；可先只读刷新，继续上次使用仍可能消耗1张且必须沿用原请求编号"); return; }
                knownOutcome = outcome;
                if (!store.Save(pending with { State = "completed", Outcome = outcome })) { Block(); return; }
                Set(new(ResetCreditUseState.Completed, ResultMessage(outcome), false, false));
            }
            // The durable receipt wins even when this best-effort refresh fails.
            try { await transport.RefreshAsync(CancellationToken.None); }
            catch (Exception)
            {
                lock (gate) Set(new(ResetCreditUseState.Completed,
                    ResultMessage(outcome) + "；最新卡/额度读取失败，请刷新；不要重复使用", false, false));
            }
        }
        catch (Exception) { lock (gate) Restore("核对失败，未发送新的请求"); }
        finally { operation.Release(); FinishDispose(); }
    }

    internal void CancelConfirmation() { lock (gate) { ++revision; if (View.State != ResetCreditUseState.Sending) Restore(string.Empty); } }
    internal async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!await operation.WaitAsync(0, cancellationToken)) return;
        try
        {
            lock (gate)
            {
                if (disposeRequested || store.IsBlocked) return;
                ++revision;
                confirmedContext = null;
                confirmedKey = null;
                Set(new(ResetCreditUseState.Checking, "正在只读刷新…", false, true));
            }
            // ReadContext refreshes the existing provider. Counts are not receipt evidence.
            if (await transport.ReadContextAsync(cancellationToken) is null)
                throw new InvalidOperationException("Latest context unavailable");
            lock (gate) FinishCheck(store.Current is { State: "pending" }
                ? "已只读刷新；此前请求结果仍未知，卡数变化不能确认结果"
                : "已只读刷新最新卡与额度信息");
        }
        catch (Exception)
        {
            lock (gate) FinishCheck(store.Current is { State: "pending" }
                ? "只读刷新失败；此前请求结果仍未确认"
                : "只读刷新失败，请稍后重试");
        }
        finally { operation.Release(); FinishDispose(); }
    }
    internal void Invalidate() => CancelConfirmation();
    internal bool CanStartNewRequest(QuotaResetCreditsSnapshot cards)
    {
        lock (gate) return !disposeRequested && !store.IsBlocked && store.Current is not { State: "pending" }
            && (store.Current is not { State: "completed" } receipt
                || cards.CapturedAt is { } captured && captured > receipt.SentAt);
    }
    private void FinishCheck(string message)
    {
        if (store.Current is { State: "completed" } receipt)
            Set(new(ResetCreditUseState.Completed, ResultMessage(receipt.Outcome!) + "；" + message, false, false));
        else Restore(message);
    }
    internal void ValidateCurrent(ResetCreditUseContext? context)
    { lock (gate) if (confirmedContext is { } expected && (context is null || !SameIdentity(context, expected))) CancelConfirmation(); }
    private static bool SameIdentity(ResetCreditUseContext a, ResetCreditUseContext b) => a.AccountFingerprint == b.AccountFingerprint
        && a.ConnectionEpoch == b.ConnectionEpoch && a.WorkspaceBinding == b.WorkspaceBinding;
    private static bool Reliable(ResetCreditUseContext? context) => context is not null && ResetCreditPendingStore.IsFingerprint(context.AccountFingerprint) && !string.IsNullOrWhiteSpace(context.ConnectionEpoch);
    private static bool CanUse(ResetCreditUseContext context)
    {
        if (!context.Fresh || context.Cards.IsStale || context.Cards.AvailableCount is not > 0 || context.Cards.CapturedAt is null) return false;
        var available = context.Cards.Credits?.Where(card => card.Status == "available" && card.ResetType == "codexRateLimits").ToArray();
        // The count remains authoritative; an expired advertised card is a reason to refresh,
        // never a reason to infer which unlisted card the backend would choose.
        return available is null || !available.Any(card => card.ExpiresAt <= DateTimeOffset.UtcNow);
    }
    private static bool HasCompleteExpiries(ResetCreditUseContext context) => context.Cards.Credits is { } cards
        && cards.Count >= context.Cards.AvailableCount && cards.All(card => card.HasExpiryInformation && card.ExpiresAt is not null);
    private void Restore(string message)
    {
        confirmedContext = null; confirmedKey = null;
        if (store.IsBlocked) { Block(); return; }
        Set(store.Current is { State: "pending" } ? new(ResetCreditUseState.Unknown,
                string.IsNullOrWhiteSpace(message) ? "此前请求结果尚未确认" : message, true, false)
            : store.Current is { State: "completed" } done ? new(ResetCreditUseState.Completed, ResultMessage(done.Outcome!), false, false)
            : new(ResetCreditUseState.Idle, message, false, false));
    }
    private void Block() => Set(new(ResetCreditUseState.Blocked,
        knownOutcome is not null ? ResultMessage(knownOutcome) + "；结果记录保存失败，已禁止继续使用"
            : "本地请求记录无法可靠保存，已禁止继续使用", false, false));
    private void Set(ResetCreditUseView view) { View = view; Changed?.Invoke(); }
    private static string ResultMessage(string outcome) => outcome switch
    { "reset" => "已使用1张，符合条件的额度窗口已重置", "alreadyRedeemed" => "此前请求已使用，无需重复", "noCredit" => "当前没有可用重置卡", _ => "没有符合条件窗口，未执行重置" };
    public void Dispose() { lock (gate) { disposeRequested = true; ++revision; } FinishDispose(); }
    private void FinishDispose() { lock (gate) if (disposeRequested && operation.CurrentCount > 0) store.Dispose(); }
}
