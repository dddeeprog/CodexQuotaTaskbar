namespace CodexQuotaTaskbar.Host.Settings;

internal sealed record AppSettings(
    bool ShowAllTaskbars,
    bool StartWithWindows,
    bool LowQuotaNotifications,
    int LowQuotaThreshold,
    bool? AutomaticUpdates = true,
    bool? SubscriptionDetails = true,
    int MaterialTransparencyPercent = 0,
    bool UnifiedPanel = false,
    bool? CompanionBall = true,
    bool ReduceCompanionMotion = false,
    bool? ShowTrayIcon = true,
    bool DotRealtime = false)
{
    internal static AppSettings Default { get; } = new(true, false, true, 10, true, true);
    internal bool AutomaticUpdatesEnabled => AutomaticUpdates is not false;
    internal bool SubscriptionDetailsEnabled => SubscriptionDetails is not false;
    internal bool CompanionBallEnabled => CompanionBall is not false;
    internal bool TrayIconEnabled => ShowTrayIcon is not false;
    internal int EffectiveMaterialTransparencyPercent => Math.Clamp(MaterialTransparencyPercent, 0, 100);
}
