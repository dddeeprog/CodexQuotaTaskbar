namespace CodexQuotaTaskbar.Host.Lifetime;

internal sealed record HostOptions(bool Demo, TimeSpan? ExitAfter, bool SubscriptionLogin = false)
{
    internal static HostOptions Parse(IReadOnlyList<string> arguments)
    {
        var demo = false;
        var subscriptionLogin = false;
        TimeSpan? exitAfter = null;
        for (var index = 0; index < arguments.Count; index++)
        {
            switch (arguments[index])
            {
                case "--demo":
                    demo = true;
                    break;
                case "--subscription-login":
                    subscriptionLogin = true;
                    break;
                case "--exit-after-seconds" when index + 1 < arguments.Count:
                    if (!int.TryParse(arguments[++index], out var seconds) || seconds is < 1 or > 300)
                    {
                        throw new ArgumentException("--exit-after-seconds 必须在 1 到 300 之间。");
                    }
                    exitAfter = TimeSpan.FromSeconds(seconds);
                    break;
                default:
                    throw new ArgumentException($"无法识别的启动参数：{arguments[index]}");
            }
        }

        return new HostOptions(demo, exitAfter, subscriptionLogin);
    }
}
