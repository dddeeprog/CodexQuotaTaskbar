using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexQuotaTaskbar.Core.Sessions;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Tests.UI;

public sealed class CompanionRuntimeFactAttribute : FactAttribute
{
    public CompanionRuntimeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CQT_COMPANION_RUNTIME_TEST") != "1")
            Skip = "Opt-in desktop rendering check; set CQT_COMPANION_RUNTIME_TEST=1.";
    }
}

public sealed class CompanionRuntimeTests
{
    [CompanionRuntimeFact]
    public void Local_engine_renders_all_emotions_in_both_transparent_layouts_and_disposes()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            if (Environment.GetEnvironmentVariable("CQT_SOFTWARE_RENDERING_TEST") == "1")
                RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                QuotaCapsuleWindow? window = null;
                try
                {
                    foreach (var unified in new[] { false, true })
                    {
                        window = new QuotaCapsuleWindow("companion-live-test", unified) { Left = 100, Top = 100 };
                        var demo = new CodexQuotaTaskbar.Host.Provider.DemoQuotaProvider();
                        await demo.RefreshAsync(CancellationToken.None);
                        window.Apply(demo.Current);
                        window.SetCompanion(true, false);
                        window.ApplySessions(CodexSessionsSnapshot.Create([
                            new("test-a", "演示任务", CodexSessionState.Running, DateTimeOffset.UtcNow),
                        ], DateTimeOffset.UtcNow));
                        window.Show();
                        var control = (CompanionBallControl)typeof(QuotaCapsuleWindow).GetField("companion", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                        for (var i = 0; i < 100 && !control.Ready; i++) await Task.Delay(150);
                        Assert.True(control.Ready, "Native companion did not become ready.");
                        var visual = Assert.IsType<CompanionBallVisual>(control.Visual);
                        Assert.Equal(32, CompanionCatalog.Shared.Value.Emotions.Length);
                        Assert.False(visual.IsHitTestVisible);
                        var timer = (DispatcherTimer)typeof(CompanionBallControl).GetField("timer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(control)!;
                        timer.Stop();
                        foreach (var id in CompanionEmotionDirector.EmotionIds)
                        {
                            visual.SetState(id, true);
                            await Task.Delay(100);
                            Assert.Equal(id, visual.Emotion);
                            Assert.True(visual.AnimationRunning);
                            var pixels = new RenderTargetBitmap(112, 112, 192, 192, PixelFormats.Pbgra32);
                            pixels.Render(visual);
                            var buffer = new byte[112 * 112 * 4];
                            pixels.CopyPixels(buffer, 112 * 4, 0);
                            Assert.Contains(buffer, value => value != 0);
                        }
                        visual.SetState("02", false);
                        await Task.Delay(50);
                        var colorProof = new RenderTargetBitmap(112, 112, 192, 192, PixelFormats.Pbgra32);
                        colorProof.Render(visual);
                        var colorPixels = new byte[112 * 112 * 4];
                        colorProof.CopyPixels(colorPixels, 112 * 4, 0);
                        var opaquePixels = Enumerable.Range(0, 112 * 112).Where(i => colorPixels[i * 4 + 3] > 240).ToArray();
                        Assert.True(opaquePixels.Count(i => colorPixels[i * 4] > 200 && colorPixels[i * 4 + 1] > 200 && colorPixels[i * 4 + 2] > 200) > 1500, "Ball body must be white.");
                        Assert.True(opaquePixels.Count(i => colorPixels[i * 4] < 70 && colorPixels[i * 4 + 1] < 70 && colorPixels[i * 4 + 2] < 70) > 10, "Eyes must remain dark and legible.");
                        control.SetReducedMotion(true);
                        await Task.Delay(250);
                        Assert.False(visual.AnimationRunning);
                        var frameA = visual.FrameCount;
                        await Task.Delay(250);
                        Assert.Equal(frameA, visual.FrameCount);
                        control.SetReducedMotion(false);
                        timer.Start();
                        var resumedFrame = visual.FrameCount;
                        await Task.Delay(500);
                        Assert.True(visual.FrameCount > resumedFrame + 2, "Animation must advance after resuming, including software rendering.");
                        window.UpdateLayout();
                        if (Environment.GetEnvironmentVariable("CQT_COMPANION_PROOF_DIR") is { Length: > 0 } output)
                        {
                            Directory.CreateDirectory(output);
                            var bitmap = new RenderTargetBitmap((int)window.ActualWidth * 2, (int)window.ActualHeight * 2, 192, 192, PixelFormats.Pbgra32);
                            bitmap.Render(window);
                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(bitmap));
                            using var file = File.Create(Path.Combine(output, unified ? "unified.png" : "classic.png"));
                            encoder.Save(file);
                        }
                        window.Hide();
                        await Task.Delay(250);
                        Assert.False(visual.AnimationRunning);
                        var hiddenA = visual.FrameCount;
                        await Task.Delay(250);
                        Assert.Equal(hiddenA, visual.FrameCount);
                        window.Show();
                        typeof(CompanionBallControl).GetMethod("Fail", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(control, null);
                        await Task.Delay(100);
                        Assert.False(control.Ready);
                        Assert.Equal(Visibility.Collapsed, control.Visibility);
                        Assert.Equal(Visibility.Visible, ((System.Windows.Controls.Border)window.FindName("SessionBadge")).Visibility);
                        window.SetCompanion(false, false);
                        Assert.False(visual.AnimationRunning);
                        Assert.Null(typeof(QuotaCapsuleWindow).GetField("companion", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window));
                        window.Close();
                        window = null;
                    }
                }
                catch (Exception ex) { failure = ex; }
                finally { window?.Close(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(55)), "Runtime check timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
