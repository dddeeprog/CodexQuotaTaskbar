namespace CodexQuotaTaskbar.Host.Settings;

internal sealed record AppSettings(
    bool ShowAllTaskbars,
    bool StartWithWindows,
    bool LowQuotaNotifications,
    int LowQuotaThreshold,
    bool? AutomaticUpdates = true)
{
    internal static AppSettings Default { get; } = new(true, false, true, 10, true);
    internal bool AutomaticUpdatesEnabled => AutomaticUpdates is not false;
}
