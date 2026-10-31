using System.Windows;
using Button = System.Windows.Controls.Button;
using CodexQuotaTaskbar.Core.Quota;
using CodexQuotaTaskbar.Host.Resets;

namespace CodexQuotaTaskbar.Host.UI;

public partial class QuotaPopoverWindow
{
    private ResetCreditUseService? resetCreditUseService;
    private string? resetCreditUseToken;
    private readonly List<Button> resetCreditUseButtons = [];

    private void InitializeResetCreditUse()
    {
        RetryResetCreditUseButton.Click += async (_, _) => await PrepareResetCreditUseAsync(true);
        ConfirmResetCreditUseButton.Click += async (_, _) =>
        {
            if (closingAnimated || popupClosed) return;
            if (resetCreditUseService is { } service && resetCreditUseToken is { } token)
                await service.ConfirmAsync(token);
        };
        CancelResetCreditUseButton.Click += (_, _) => { if (!closingAnimated && !popupClosed) resetCreditUseService?.CancelConfirmation(); };
        Closing += (_, _) => CancelResetCreditUseForClose();
        Closed += (_, _) =>
        {
            if (resetCreditUseService is { } service)
            {
                service.Changed -= OnResetCreditUseChanged;
                // This only cancels an unsent confirmation. An in-flight attempt
                // belongs to the App service and survives closing this window.
                service.CancelConfirmation();
            }
        };
        RenderResetCreditUse();
    }

    internal void SetResetCreditUseService(ResetCreditUseService? service)
    {
        if (resetCreditUseService is not null) resetCreditUseService.Changed -= OnResetCreditUseChanged;
        resetCreditUseService = service;
        if (service is not null) service.Changed += OnResetCreditUseChanged;
        RenderResetCreditUse();
    }

    internal async Task PrepareResetCreditUseAsync(bool retry)
    {
        if (closingAnimated || popupClosed || resetCreditUseService is not { } service) return;
        await service.PrepareConfirmationAsync(retry);
        if (!popupClosed) RenderResetCreditUse();
    }

    private void CancelResetCreditUseForClose()
    {
        resetCreditUseToken = null;
        resetCreditUseService?.CancelConfirmation();
        foreach (var button in resetCreditUseButtons) button.IsEnabled = false;
        RetryResetCreditUseButton.IsEnabled = ConfirmResetCreditUseButton.IsEnabled = false;
        CancelResetCreditUseButton.IsEnabled = false;
    }

    private void OnResetCreditUseChanged()
    {
        if (Dispatcher.CheckAccess()) { if (!popupClosed) RenderResetCreditUse(); }
        else if (!Dispatcher.HasShutdownStarted)
            _ = Dispatcher.InvokeAsync(() => { if (!popupClosed) RenderResetCreditUse(); });
    }

    private void RenderResetCreditUse()
    {
        var view = resetCreditUseService?.View;
        var confirmation = view?.Confirmation;
        resetCreditUseToken = confirmation?.Token;
        var cards = quotaSnapshot.ResetCredits;
        var eligible = quotaSnapshot.Availability == QuotaAvailability.Available && !cards.IsStale
            && cards.AvailableCount is > 0 && cards.Credits?.Any(card => card.Status == "available"
                && card.ResetType == "codexRateLimits" && card.ExpiresAt <= DateTimeOffset.UtcNow) != true
            && resetCreditUseService?.CanStartNewRequest(cards) == true;
        var canUse = view is { IsBusy: false, Confirmation: null }
            && view.State is not (ResetCreditUseState.Unknown or ResetCreditUseState.Blocked) && eligible;
        foreach (var button in resetCreditUseButtons) button.IsEnabled = canUse;
        RetryResetCreditUseButton.Visibility = view?.CanRetry == true ? Visibility.Visible : Visibility.Collapsed;
        RetryResetCreditUseButton.IsEnabled = view is { CanRetry: true, IsBusy: false, Confirmation: null };
        // Keep actual operation results/errors, without an idle instruction row.
        ResetCreditUseStatusText.Text = view?.Message ?? string.Empty;
        ResetCreditUseStatusText.Visibility = ResetCreditUseStatusText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ResetCreditUseConfirmationPanel.Visibility = confirmation is null ? Visibility.Collapsed : Visibility.Visible;
        ResetCreditUseActions.Visibility = confirmation is not null || view?.CanRetry == true
            || ResetCreditUseStatusText.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        if (confirmation is not null)
        {
            ResetCreditUseConfirmationText.Text = $"当前账号：{confirmation.DisplayName}\n"
                + (confirmation.IsRetry ? "继续上次使用，仍可能消耗1张；复用原请求编号，不新建请求。\n" : "本次将消耗1张重置卡。\n")
                + "仅重置符合条件的额度窗口，不保证所有额度恢复。未见受支持撤销，请按不可撤销处理。\n"
                + "由服务端选择卡，不保证使用已展示的最早到期卡。\n"
                + "取消或确认前关闭不会发送；提交后关闭窗口不代表撤销。";
            ConfirmResetCreditUseButton.Content = confirmation.IsRetry ? "确认继续上次使用" : "确认使用1张";
        }
        ApplySystemAppearance();
        if (closingAnimated || popupClosed)
        {
            resetCreditUseToken = null;
            foreach (var button in resetCreditUseButtons) button.IsEnabled = false;
            RetryResetCreditUseButton.IsEnabled = ConfirmResetCreditUseButton.IsEnabled = false;
            CancelResetCreditUseButton.IsEnabled = false;
        }
    }
}
