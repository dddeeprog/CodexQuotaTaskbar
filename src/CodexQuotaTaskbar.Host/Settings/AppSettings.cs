namespace CodexQuotaTaskbar.Host.Settings;

internal sealed record AppSettings(
    bool ShowAllTaskbars,
    bool StartWithWindows,
    bool LowQuotaNotifications,
    int LowQuotaThreshold)
{
    internal static AppSettings Default { get; } = new(true, false, true, 10);
}
