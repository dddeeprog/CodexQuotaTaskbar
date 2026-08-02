using CodexQuotaTaskbar.Core.Quota;
using CodexQuotaTaskbar.Host.Notifications;

namespace CodexQuotaTaskbar.Host.Tests.Notifications;

public sealed class LowQuotaGateTests
{
    [Fact]
    public void Notifies_once_per_window_and_rearms_after_recovery()
    {
        var gate = new LowQuotaGate(10);
        var low = new QuotaWindowSnapshot(QuotaWindowKind.Primary, 9, 300, DateTimeOffset.UtcNow.AddHours(1));
        var recovered = new QuotaWindowSnapshot(QuotaWindowKind.Primary, 50, 300, DateTimeOffset.UtcNow.AddHours(1));

        Assert.True(gate.ShouldNotify([low]));
        Assert.False(gate.ShouldNotify([low]));
        Assert.False(gate.ShouldNotify([recovered]));
        Assert.True(gate.ShouldNotify([low]));
    }
}
