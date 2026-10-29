using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using CodexQuotaTaskbar.Core.Quota;
using CodexQuotaTaskbar.Host.Diagnostics;
using CodexQuotaTaskbar.Host.Resets;
using CodexQuotaTaskbar.Host.Settings;
using CodexQuotaTaskbar.Host.UI;
using CodexQuotaTaskbar.Host.Update;
using Button = System.Windows.Controls.Button;

namespace CodexQuotaTaskbar.Host.Tests.Diagnostics;

public sealed class ReliabilityDiagnosticsTests
{
    [Theory]
    [InlineData("待恢复", UpdateApplyStage.Validate, false, false, DiagnosticUpdateOutcome.PendingRecovery)]
    [InlineData("已有更新事务待恢复", UpdateApplyStage.Validate, false, false, DiagnosticUpdateOutcome.PendingRecovery)]
    [InlineData("待恢复", UpdateApplyStage.Cleanup, true, false, DiagnosticUpdateOutcome.PendingRecovery)]
    [InlineData("已恢复 v0.1.15；该失败更新已阻止重复安装", UpdateApplyStage.Validate, false, false, DiagnosticUpdateOutcome.Restored)]
    [InlineData("已恢复 v0.1.15，已尝试正常启动", UpdateApplyStage.Start, false, false, DiagnosticUpdateOutcome.Restored)]
    [InlineData("已恢复 v0.1.15", UpdateApplyStage.Start, false, false, DiagnosticUpdateOutcome.Restored)]
    [InlineData("新版已就绪", UpdateApplyStage.Cleanup, true, false, DiagnosticUpdateOutcome.Ready)]
    [InlineData("新版已就绪", UpdateApplyStage.Validate, false, false, DiagnosticUpdateOutcome.Ready)]
    [InlineData("未替换", UpdateApplyStage.Copy, true, false, DiagnosticUpdateOutcome.Failed)]
    [InlineData("保留 v0.1.15，已尝试正常启动", UpdateApplyStage.Start, false, false, DiagnosticUpdateOutcome.Failed)]
    [InlineData("当前 v0.1.16，失败版本 v0.1.17 已阻止重复安装", UpdateApplyStage.Validate, false, false, DiagnosticUpdateOutcome.Failed)]
    [InlineData("新版已就绪", UpdateApplyStage.Cleanup, true, true, DiagnosticUpdateOutcome.PendingRecovery)]
    [InlineData("PRIVATE-UNKNOWN-OUTCOME", UpdateApplyStage.Cleanup, true, false, DiagnosticUpdateOutcome.PendingRecovery)]
    [InlineData("已恢复 vSECRET-TOKEN", UpdateApplyStage.Start, true, false, DiagnosticUpdateOutcome.PendingRecovery)]
    internal void Update_outcome_uses_controlled_transaction_report_without_inventing_recovery_from_replacement(
        string outcome, UpdateApplyStage stage, bool replaced, bool recoveryFailure, DiagnosticUpdateOutcome expected)
    {
        var primary = UpdateApplyDiagnostic.Capture(stage, new IOException("PRIVATE-PRIMARY-PATH"));
        var error = new UpdateApplyException(primary, null, replaced, 1)
        {
            RecoveryOutcome = outcome,
            RecoveryFailure = recoveryFailure ? UpdateApplyDiagnostic.Capture(UpdateApplyStage.Restart, new IOException("PRIVATE-RECOVERY-PATH")) : null,
        };
        Assert.Equal(expected, ReliabilityDiagnostics.ClassifyUpdateOutcome(error));
        var log = new ReliabilityDiagnostics(); log.Update(error.Failure, ReliabilityDiagnostics.ClassifyUpdateOutcome(error));
        Assert.Equal(expected, Assert.Single(log.Entries).Outcome);
        Assert.DoesNotContain("PRIVATE", System.Text.Json.JsonSerializer.Serialize(log.Entries));
        Assert.DoesNotContain("TOKEN", System.Text.Json.JsonSerializer.Serialize(log.Entries));
    }
    [Fact]
    public void Undefined_typed_values_are_rejected_before_they_enter_history()
    {
        var log = new ReliabilityDiagnostics();
        log.Quota((QuotaAvailability)999); log.Query((ResetQueryPhase)999);
        log.Footer(ResetVisualState.Failed, (FooterSuppression)999);
        log.Update(new((UpdateApplyStage)999, UpdateApplyErrorKind.Io, -1), DiagnosticUpdateOutcome.Failed);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public void Business_transitions_are_deduplicated_bounded_and_retain_actual_version()
    {
        var log = new ReliabilityDiagnostics();
        for (var i = 0; i < 1000; i++) log.Footer(ResetVisualState.Failed, FooterSuppression.OwnerHidden);
        Assert.Single(log.Entries);
        for (var i = 0; i < 250; i++) log.Quota(i % 2 == 0 ? QuotaAvailability.Available : QuotaAvailability.Stale);
        Assert.Equal(192, log.Entries.Count);
        Assert.All(log.Entries, e => Assert.Equal(ProductVersion.Text, e.Version));
        var safe = UpdateApplyDiagnostic.Capture(UpdateApplyStage.Replace, new IOException("SECRET-PATH-TOKEN"));
        log.Update(safe, DiagnosticUpdateOutcome.PendingRecovery);
        var last = log.Entries.Last(); Assert.Equal(UpdateApplyStage.Replace, last.Stage); Assert.Equal(safe.HResult, last.HResult);
    }

    [Fact]
    public void Classic_layout_failure_records_footer_disabled_instead_of_claiming_it_was_shown() => Sta(() =>
    {
        var window = new QuotaCapsuleWindow("synthetic-classic-diagnostic", unifiedPanel: false);
        try
        {
            window.ApplyResetQueryState(new(1, ResetQueryPhase.Failed));
            Assert.Equal(ResetFooterKind.None, window.ResetFooter);
            var footer = ReliabilityDiagnostics.Local.Entries.Last(e => e.Kind == ReliabilityEvent.Footer);
            Assert.Equal(ResetVisualState.Failed, footer.Visual); Assert.Equal(FooterSuppression.Disabled, footer.Reason);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Older_query_revision_cannot_publish_a_misleading_diagnostic() => Sta(() =>
    {
        var window = new QuotaCapsuleWindow("synthetic-query-diagnostic");
        try
        {
            window.ApplyResetQueryState(new(2, ResetQueryPhase.Failed));
            var before = ReliabilityDiagnostics.Local.Entries.Last(e => e.Kind == ReliabilityEvent.Query);
            window.ApplyResetQueryState(new(1, ResetQueryPhase.Idle));
            Assert.Same(before, ReliabilityDiagnostics.Local.Entries.Last(e => e.Kind == ReliabilityEvent.Query));
            Assert.Equal(ResetQueryPhase.Failed, before.Query);
        }
        finally { window.Close(); }
    });

    [Fact]
    public async Task Only_accepted_quota_generation_publishes_diagnostics()
    {
        await using var provider = new CodexQuotaTaskbar.Host.Provider.CodexRateLimitProvider(false);
        var publish = provider.GetType().GetMethod("SetForGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!;
        publish.Invoke(provider, [QuotaSnapshot.Stale([], DateTimeOffset.UtcNow, "PRIVATE-ACCOUNT"), 0L]);
        var before = ReliabilityDiagnostics.Local.Entries.Last(e => e.Kind == ReliabilityEvent.Quota);
        publish.Invoke(provider, [QuotaSnapshot.Available([], DateTimeOffset.UtcNow), -1L]);
        Assert.Same(before, ReliabilityDiagnostics.Local.Entries.Last(e => e.Kind == ReliabilityEvent.Quota));
        publish.Invoke(provider, [QuotaSnapshot.Available([], DateTimeOffset.UtcNow), 0L]);
        Assert.Equal(QuotaAvailability.Available, ReliabilityDiagnostics.Local.Entries.Last(e => e.Kind == ReliabilityEvent.Quota).Availability);
    }

    [Theory]
    [InlineData(true, true, FooterSuppression.Eligible)]
    [InlineData(false, true, FooterSuppression.CompactEligible)]
    [InlineData(false, false, FooterSuppression.NoSpace)]
    internal void Footer_space_decisions_explain_eligible_compact_and_suppressed_status(bool full, bool compact, FooterSuppression expected) => Sta(() =>
    {
        var window = new QuotaCapsuleWindow("synthetic-space-diagnostic");
        try
        {
            window.SetPointerObservationAllowed(false);
            window.ApplyResetQueryState(new(1, ResetQueryPhase.Failed));
            window.SetResetSchedulePresentationAllowed(full, compact);
            OwnerVisible(window, true);
            var entry = ReliabilityDiagnostics.Local.Entries.Last(e => e.Kind == ReliabilityEvent.Footer);
            Assert.Equal(ResetFooterKind.Status, window.ResetFooter); Assert.Equal(ResetVisualState.Failed, entry.Visual); Assert.Equal(expected, entry.Reason);
            OwnerVisible(window, false);
        }
        finally { window.Close(); }
    });

    private static void OwnerVisible(QuotaCapsuleWindow window, bool visible)
    {
        // Sourceless WPF cache fixture, same as the existing popup tests; no HWND or physical input.
        var write = typeof(UIElement).GetMethod("WriteFlag", BindingFlags.Instance | BindingFlags.NonPublic)!;
        write.Invoke(window, [Enum.Parse(write.GetParameters()[0].ParameterType, "IsVisibleCache"), visible]);
        var key = (DependencyPropertyKey)typeof(UIElement).GetField("IsVisiblePropertyKey", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        window.SetValue(key, visible); window.HandleResetOwnerVisibilityChanged();
    }
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Feature_page_export_action_is_reachable_with_both_tray_settings_without_settings_changes(bool trayEnabled)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            TrayMenuWindow? menu = null;
            try
            {
                menu = new TrayMenuWindow(AppSettings.Default with { ShowTrayIcon = trayEnabled }, activateOnShow: false);
                var button = Assert.IsType<Button>(menu.FindName("ExportDiagnosticsButton"));
                Assert.True(Assert.IsAssignableFrom<FrameworkElement>(menu.FindName("PreferencesRoot")).IsAncestorOf(button));
                var changed = 0; var requested = 0;
                menu.SettingsChangeRequested += (_, _) => changed++;
                menu.ExportDiagnosticsRequested += (_, _) => requested++;
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(1, requested); Assert.Equal(0, changed); Assert.False(menu.IsVisible);
            }
            catch (Exception error) { failure = error; }
            finally { menu?.Close(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
