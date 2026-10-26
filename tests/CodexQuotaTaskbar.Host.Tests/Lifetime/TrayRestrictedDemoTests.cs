using System.Reflection;
using System.Runtime.ExceptionServices;
using CodexQuotaTaskbar.Host.Settings;
using CodexQuotaTaskbar.Host.Tray;

namespace CodexQuotaTaskbar.Host.Tests.Lifetime;

public sealed class TrayRestrictedDemoTests
{
    [Fact]
    public void Isolated_demo_rejects_startup_changes_before_the_registry_writer()
    {
        OnSta(() =>
        {
            var writes = 0;
            using var tray = new TrayController(AppSettings.Default with { ShowTrayIcon = false },
                (_, _, _) => { }, restrictedDemo: true, setAutostart: _ => writes++);
            Request(tray, tray.Settings with { StartWithWindows = true });
            Assert.Equal(0, writes);
            Assert.False(tray.Settings.StartWithWindows);
            Request(tray, tray.Settings with { ReduceCompanionMotion = true });
            Assert.True(tray.Settings.ReduceCompanionMotion);
            Assert.Equal(0, writes);
        });
    }

    [Fact]
    public void Normal_mode_retains_startup_behavior_using_a_fake_writer_in_this_test()
    {
        OnSta(() =>
        {
            bool? requested = null;
            using var tray = new TrayController(AppSettings.Default with { ShowTrayIcon = false },
                (_, _, _) => { }, setAutostart: value => requested = value);
            Request(tray, tray.Settings with { StartWithWindows = true });
            Assert.True(requested);
            Assert.True(tray.Settings.StartWithWindows);
        });
    }

    private static void Request(TrayController tray, AppSettings value) => typeof(TrayController)
        .GetMethod("ChangeRequested", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tray, [value]);

    private static void OnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        failure?.Throw();
    }
}
