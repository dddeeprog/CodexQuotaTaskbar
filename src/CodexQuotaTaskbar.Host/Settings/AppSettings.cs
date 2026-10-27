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
    bool DotRealtime = false,
    bool CompanionTyping = false,
    string CompanionAppearance = "white-ball",
    int BongoSizeDip = 192,
    bool? BongoCatVisible = null)
{
    internal static AppSettings Default { get; } = new(true, false, true, 10, true, true);
    // Public appearance template only. Core acceptance never opts into input, Dot, or autostart.
    internal static AppSettings InstallerDefault { get; } = new(true, false, false, 10, true, true,
        MaterialTransparencyPercent: 3, UnifiedPanel: true, CompanionBall: true,
        ReduceCompanionMotion: false, ShowTrayIcon: false, DotRealtime: false, CompanionTyping: false,
        CompanionAppearance: "bongo-cat", BongoSizeDip: 192, BongoCatVisible: true);
    internal bool AutomaticUpdatesEnabled => AutomaticUpdates is not false;
    internal bool SubscriptionDetailsEnabled => SubscriptionDetails is not false;
    internal bool CompanionBallEnabled => CompanionBall is not false;
    internal string EffectiveCompanionAppearance => CompanionAppearance == "bongo-cat" ? "bongo-cat" : "white-ball";
    internal bool BongoAppearanceSelected => EffectiveCompanionAppearance == "bongo-cat";
    // A missing value is the old exclusive setting. Preserve that cat choice,
    // while the ball now remains the independent Codex status display.
    internal bool EffectiveBongoCatVisible => BongoCatVisible ?? (CompanionBallEnabled && BongoAppearanceSelected);
    internal bool CompanionBongoEnabled => EffectiveBongoCatVisible;
    internal int EffectiveBongoSizeDip => Math.Clamp(BongoSizeDip, 140, 240);
    internal bool TrayIconEnabled => ShowTrayIcon is not false;
    internal int EffectiveMaterialTransparencyPercent => Math.Clamp(MaterialTransparencyPercent, 0, 100);
}
