namespace CodexQuotaTaskbar.Host.Lifetime;

internal sealed record HostOptions(bool Demo, TimeSpan? ExitAfter, bool SubscriptionLogin = false, bool SoftwareRendering = true, bool DotRealtime = false)
{
    internal static HostOptions Parse(IReadOnlyList<string> arguments)
    {
        var demo = false;
        var dotRealtime = false;
        var subscriptionLogin = false;
        var softwareRendering = true;
        TimeSpan? exitAfter = null;
        for (var index = 0; index < arguments.Count; index++)
        {
            switch (arguments[index])
            {
                case "--dot-realtime":
                    dotRealtime = true;
                    break;
                case "--demo":
                    demo = true;
                    break;
                case "--subscription-login":
                    subscriptionLogin = true;
                    break;
                case "--software-rendering":
                    softwareRendering = true;
                    break;
                case "--hardware-rendering":
                    softwareRendering = false;
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

        return new HostOptions(demo, exitAfter, subscriptionLogin, softwareRendering, dotRealtime);
    }
}
