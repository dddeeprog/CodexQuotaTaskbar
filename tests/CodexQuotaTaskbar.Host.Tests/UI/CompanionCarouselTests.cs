using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexQuotaTaskbar.Core.Sessions;
using CodexQuotaTaskbar.Host.Platform;
using CodexQuotaTaskbar.Host.Resets;
using CodexQuotaTaskbar.Host.UI;
using Xunit.Abstractions;

namespace CodexQuotaTaskbar.Host.Tests.UI;

[Collection("Native UI isolation")]
public sealed class CompanionCarouselTests(ITestOutputHelper output)
{
    private static readonly MethodInfo OnFrame = typeof(CompanionBallVisual).GetMethod("OnFrame", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo OnRender = typeof(CompanionBallVisual).GetMethod("OnRender", BindingFlags.NonPublic | BindingFlags.Instance)!;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Full_bags_are_actually_presented_without_effective_idle_borrowing_even_with_input(bool animated)
    {
        var start = DateTimeOffset.Parse("2026-10-27T00:00:00Z");
        var director = new CompanionEmotionDirector(start, 42);
        var engine = new CompanionPoseEngine(42);
        var seen = new List<string>();
        for (var item = 0; item < 96; item++)
        {
            var at = item * 12000d;
            var first = director.Evaluate(start.AddMilliseconds(at));
            seen.Add(first.Emotion);
            engine.SetEmotion(first.Emotion, at, animated);
            var staticPose = engine.Pose.ToArray();
            for (var ms = 0; ms < 12000; ms += 100)
            {
                var now = start.AddMilliseconds(at + ms);
                director.Interact(now, true); director.Apply(CodexSessionsSnapshot.Empty, now);
                Assert.Equal(first.Emotion, director.Evaluate(now).Emotion);
                engine.Gaze(.5, -.5); engine.Sample(at + ms, animated);
                Assert.Equal(first.Emotion, engine.Definition.Id);
                Assert.Equal(first.Emotion, engine.RequestedEmotion);
                if (!animated) Assert.Equal(staticPose, engine.Pose);
            }
        }
        foreach (var bag in seen.Chunk(32))
        {
            Assert.Equal(32, bag.Distinct().Count());
            Assert.Single(bag, id => id == "02");
        }
        Assert.NotEqual(seen[31], seen[32]); Assert.NotEqual(seen[63], seen[64]);
    }

    [Fact]
    public void Every_multi_shape_item_varies_within_dwell_and_legacy_wake_tails_keep_their_pose()
    {
        foreach (var definition in CompanionCatalog.Shared.Value.Emotions)
        {
            var scale = CompanionPoseEngine.PoolIntervalScale(definition);
            Assert.InRange(scale, .375, 1);
            Assert.True(definition.PoolMs[1] * scale <= 6000);
            if (definition.PoolMs[1] <= 6000) Assert.Equal(1, scale);
            for (var seed = 0; seed < 8; seed++)
            {
                var engine = new CompanionPoseEngine(seed); engine.SetEmotion(definition.Id, 0, true);
                var positions = new HashSet<int>();
                for (var ms = 0; ms < 12000; ms += 33)
                {
                    engine.Sample(ms, true); positions.Add(engine.PoolPosition);
                    Assert.Equal(definition.Id, engine.Definition.Id);
                    Assert.All(engine.Pose, value => Assert.True(double.IsFinite(value)));
                }
                if (definition.Pool.Length > 1) Assert.True(positions.Count > 1, definition.Id);
                if (definition.Id is "01" or "07")
                {
                    var tail = definition.Sequence!.Frames[^1].Pose;
                    // The selected tail still has its own authored breathing;
                    // retaining that motion is not a fallback to another ID.
                    Assert.Equal(tail[0], engine.Pose[0]); Assert.Equal(tail[3], engine.Pose[3]);
                    Assert.InRange(Math.Abs(engine.Pose[1] - tail[1]), 0, Math.Abs(tail[4] * 55) + .001);
                    Assert.InRange(Math.Abs(engine.Pose[2] - tail[2]), 0, Math.Abs(tail[4]) + .001);
                }
            }
            output.WriteLine($"{definition.Id} {definition.Name}: 12s; eye interval {definition.PoolMs[0]}–{definition.PoolMs[1]} => {definition.PoolMs[0]*scale}–{definition.PoolMs[1]*scale} ms");
        }
    }

    [Fact]
    public void Hidden_control_apply_tap_and_typing_do_not_consume_its_bag_or_reset_visual() => Sta(() =>
    {
        using var control = new CompanionBallControl();
        control.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        var director = (CompanionEmotionDirector)typeof(CompanionBallControl).GetField("director", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(control)!;
        var first = director.Evaluate(DateTimeOffset.UtcNow);
        for (var i = 0; i < 100; i++)
        {
            control.Apply(CodexSessionsSnapshot.Empty); control.Tap(); control.Hover();
            control.KeyboardActivity(new(CompanionInputKind.Key, 30, true));
            var frame = director.Evaluate(DateTimeOffset.UtcNow.AddDays(1));
            Assert.Equal(first.Draw, frame.Draw); Assert.Equal(first.Emotion, frame.Emotion);
            Assert.Equal(first.Emotion, control.Visual!.Emotion);
        }
        Assert.False(control.Visual!.AnimationRunning); Assert.Null(PresentationSource.FromVisual(control));
    });

    [Theory]
    [InlineData("01")]
    [InlineData("07")]
    public void Wake_tail_opens_actual_eye_contours_without_borrowing_idle_identity(string id)
    {
        var engine = new CompanionPoseEngine(42);
        engine.SetEmotion(id, 0, true);
        var end = engine.Definition.Sequence!.Frames[^1].At;
        // Exercise a skipped sequence, then allow the existing contour spring to settle.
        for (var ms = end + 500; ms <= 11000; ms += 33) engine.Sample(ms, true);
        Assert.Equal(id, engine.RequestedEmotion); Assert.Equal(id, engine.Definition.Id);
        var open = CompanionCatalog.Shared.Value.Expressions[0];
        for (var eye = 0; eye < 2; eye++)
        {
            for (var point = 0; point < open[eye].Length; point++)
                for (var axis = 0; axis < 2; axis++)
                    Assert.InRange(Math.Abs(open[eye][point][axis] - engine.Rings[eye][point][axis]), 0, .001);
            Assert.True(engine.Rings[eye].Max(p => p[1]) - engine.Rings[eye].Min(p => p[1]) > 35);
        }
        engine.SetEmotion("00", 11001, true);
        for (var ms = 11001; ms < 14000; ms += 33) engine.Sample(ms, true);
        Assert.True(engine.Pose[CompanionPoseEngine.Left + 5] < .1);
        // Re-entering starts the authored wake sequence again, not the previous open tail.
        engine.SetEmotion(id, 14000, true);
        for (var ms = 14000; ms < 14500; ms += 33) engine.Sample(ms, true);
        Assert.True(engine.Rings[0].Max(p => p[1]) - engine.Rings[0].Min(p => p[1]) < 30);
    }

    [Fact]
    public void Reset_hint_observes_whole_ball_without_an_extra_hit_layer_or_clock() => Sta(() =>
    {
        using var control = new CompanionBallControl();
        Assert.Single(control.Children.Cast<UIElement>()); // Existing count badge only, before visual loading.
        Assert.Equal(56, control.Width); Assert.Equal(56, control.Height);
        var hover = new List<bool>();
        control.ResetHintHoverChanged += hover.Add;
        control.SetResetHintHovered(true);
        control.SetResetHintHovered(true);
        Assert.False(System.Windows.Controls.ToolTipService.GetIsEnabled(control));
        foreach (var state in Enum.GetValues<ResetVisualState>())
        {
            control.SetResetState(state);
            Assert.Null(control.ToolTip);
        }
        control.Measure(new Size(56, 56)); control.Arrange(new Rect(0, 0, 56, 56));
        // Unshown WPF roots are not IsVisible and InputHitTest returns null.
        // Check the arranged surface bounds without claiming native hit delivery.
        var hintBounds = new Rect(control.RenderSize);
        Assert.True(hintBounds.Contains(new Point(28, 48)));
        Assert.True(hintBounds.Contains(new Point(28, 25)));
        Assert.False(hintBounds.Contains(new Point(57, 48)));
        Assert.Null(PresentationSource.FromVisual(control));
        control.Dispose(); Assert.Null(control.ToolTip);
        Assert.Equal(new[] { true, false }, hover);
        Assert.True(System.Windows.Controls.ToolTipService.GetIsEnabled(control));
    });

    [Theory]
    [InlineData("01")]
    [InlineData("07")]
    public void Wake_can_toggle_reduced_motion_without_advancing_to_a_different_identity(string id)
    {
        var engine = new CompanionPoseEngine(42);
        engine.SetEmotion(id, 0, false);
        var originalPose = engine.Pose.ToArray();
        for (var ms = 0; ms < 1200; ms += 33) engine.Sample(ms, false);
        Assert.Equal(originalPose, engine.Pose);
        Assert.True(engine.Rings[0].Max(p => p[1]) - engine.Rings[0].Min(p => p[1]) < 30);
        for (var ms = 1200; ms < 6000; ms += 33) engine.Sample(ms, true);
        Assert.True(engine.Rings[0].Max(p => p[1]) - engine.Rings[0].Min(p => p[1]) > 35);
        engine.Sample(6000, false);
        var reducedPose = engine.Pose.ToArray();
        for (var ms = 6000; ms < 11000; ms += 33) engine.Sample(ms, false);
        Assert.Equal(reducedPose, engine.Pose); Assert.Equal(id, engine.Definition.Id);
        Assert.Equal(id, engine.RequestedEmotion);
    }

    [Fact]
    public void Complete_phase_cycle_is_multicolor_continuous_and_has_constant_spatial_coverage()
    {
        for (var ms = 0; ms <= 36000; ms += 25)
        {
            var f = CompanionBallVisual.SampleNormalRing(ms, true);
            var hues = Enumerable.Range(0, 24).Select(i => Hue(CompanionBallVisual.SpectrumColor(f.Phase, i / 24d))).ToHashSet();
            Assert.Equal(6, hues.Count);
            Assert.Equal(CompanionBallVisual.SpectrumColor(f.Phase, .25), f.Color);
            var a = CompanionBallVisual.SpectrumColor(f.Phase, .13);
            var b = CompanionBallVisual.SpectrumColor(CompanionBallVisual.SampleNormalRing(ms + 1, true).Phase, .13);
            Assert.InRange(Math.Abs(a.R-b.R) + Math.Abs(a.G-b.G) + Math.Abs(a.B-b.B), 0, 3);
        }
        Assert.Equal(CompanionBallVisual.SampleNormalRing(0, true), CompanionBallVisual.SampleNormalRing(36000, true));
    }

    [Theory]
    [InlineData(52d)]
    [InlineData(56d)]
    public void Actual_default_pixels_keep_multiple_hues_at_all_phases_and_specials_are_unambiguous(double size) => Sta(() =>
    {
        var ms = 0d; using var visual = new CompanionBallVisual(() => ms); visual.SetState("35", true);
        var min = 6; var frames = new HashSet<string>();
        for (var step = 0; step <= 144; step++)
        {
            ms = step * 250; Frame(visual);
            var pixels = Pixels(Record(visual, size), (int)size, (int)size);
            var hues = RingHues(pixels, (int)size);
            min = Math.Min(min, hues.Count);
            Assert.True(hues.Count >= 4, $"{size} DIP / {ms} ms: only {hues.Count} hues");
            frames.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels)));
        }
        Assert.True(frames.Count > 100); output.WriteLine($"{size} DIP: minimum {min} hue sectors over 145 rasterized phases");
        foreach (var state in new[] { ResetVisualState.Upcoming, ResetVisualState.Confirmed, ResetVisualState.Failed })
        {
            visual.SetResetState(state); var retained = visual.NormalFrame;
            for (var step = 0; step < 5; step++)
            {
                ms += 1000; Frame(visual);
                Assert.Equal(retained, visual.NormalFrame);
                Assert.InRange(RingHues(Pixels(Record(visual, size), (int)size, (int)size), (int)size).Count, 1, 2);
            }
            visual.SetResetState(ResetVisualState.None); ms += 34; Frame(visual);
            Assert.True(RingHues(Pixels(Record(visual, size), (int)size, (int)size), (int)size).Count >= 4);
        }
        visual.SetState("35", false);
        var still = Pixels(Record(visual, size), (int)size, (int)size);
        ms += 60000; Frame(visual);
        Assert.Equal(still, Pixels(Record(visual, size), (int)size, (int)size));
        Assert.True(RingHues(still, (int)size).Count >= 4);
    });

    [Fact]
    public void Export_carousel_and_spatial_spectrum_only_on_explicit_request() => Sta(() =>
    {
        var dir = Environment.GetEnvironmentVariable("CQT_CAROUSEL_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return;
        var ffmpeg = Environment.GetEnvironmentVariable("CQT_PREVIEW_FFMPEG"); Assert.True(File.Exists(ffmpeg));
        Directory.CreateDirectory(dir); var target = Path.Combine(dir, "diandao-carousel-spectrum-preview.mp4");
        Assert.False(File.Exists(target));
        const int width=1280, height=720, fps=30, frames=1200;
        var ms=0d; var sizes = new[] {52d,56d,56d};
        var visuals=sizes.Select(_=>new CompanionBallVisual(()=>ms)).ToArray();
        var process = new ProcessStartInfo(ffmpeg!) { UseShellExecute=false, CreateNoWindow=true, RedirectStandardInput=true, RedirectStandardError=true };
        foreach(var arg in new[]{"-hide_banner","-loglevel","error","-n","-f","rawvideo","-pixel_format","bgra","-video_size","1280x720","-framerate","30","-i","pipe:0","-an","-c:v","libx264","-preset","fast","-crf","18","-pix_fmt","yuv420p","-movflags","+faststart",target}) process.ArgumentList.Add(arg);
        using var encoder=Process.Start(process)!; var errors=encoder.StandardError.ReadToEndAsync();
        var canvas=new DrawingVisual(); var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32); var pixels=new byte[width*height*4];
        var background=new SolidColorBrush(Color.FromRgb(23,25,28)); background.Freeze();
        var proofs=new HashSet<int>{0,90,330,360,690,720,900,1050,1080,1110,1140,1170};
        try
        {
            for(var frame=0;frame<frames;frame++)
            {
                var seconds=frame/(double)fps; ms=seconds*1000;
                var id=seconds<12?"01":seconds<24?"07":"02";
                var state=seconds<36||seconds>=39?ResetVisualState.None:seconds<37?ResetVisualState.Upcoming:seconds<38?ResetVisualState.Confirmed:ResetVisualState.Failed;
                for(var i=0;i<3;i++){visuals[i].SetState(id,i<2);visuals[i].SetResetState(state);Frame(visuals[i]);}
                using(var dc=canvas.RenderOpen())
                {
                    dc.DrawRectangle(background,null,new Rect(0,0,width,height));
                    Text(dc,"32 表情公平轮播 · 同圈多段 RGB 流光",27,40,26);
                    Text(dc,"未安装候选 · 合成数据 / 实际 WPF 控件 · 1× 原速（非现场录屏）",15,42,74,Brushes.Silver);
                    Text(dc,$"当前 {id} · {CompanionCatalog.Shared.Value.Get(id).Name}   /   {seconds:F1}s",20,42,113);
                    for(var i=0;i<3;i++)
                    {
                        var x=48+i*410; Text(dc,i==2?"减少动态 · 静止对照":$"默认 {sizes[i]:0} DIP · 4×对照",18,x+28,167);
                        var drawing=Record(visuals[i],sizes[i]);
                        dc.PushTransform(new TranslateTransform(x+40,205));dc.PushTransform(new ScaleTransform(4,4));dc.DrawDrawing(drawing);dc.Pop();dc.Pop();
                        dc.PushTransform(new TranslateTransform(x+117,490));dc.DrawDrawing(drawing);dc.Pop();
                        Text(dc,$"原尺寸 {sizes[i]:0} DIP（未放大）",13,x+63,560);
                        Text(dc,$"选中 {visuals[i].Emotion} / 实际 {visuals[i].EffectiveEmotion}",14,x+65,595);
                    }
                    Text(dc,seconds<36?"每项 12 秒：01 / 07 不再强制回 02；02 也能看到内部眼型变化。":"特殊状态示例：金 → 绿 → 红 → 普通多色；不模拟真实额度到账。",15,42,641,Brushes.Silver);
                    Text(dc,"演示为固定示例顺序，实际运行用 32 项洗牌袋；无点击 / 真实消息 / 安装操作。",13,42,679,Brushes.Silver);
                }
                bitmap.Clear();bitmap.Render(canvas);bitmap.CopyPixels(pixels,width*4,0);encoder.StandardInput.BaseStream.Write(pixels);
                if(proofs.Contains(frame)) Save(dir,$"proof-{frame:D4}.png",bitmap);
            }
            encoder.StandardInput.Close(); Assert.True(encoder.WaitForExit(60000)); Assert.Equal(0,encoder.ExitCode); Assert.True(string.IsNullOrWhiteSpace(errors.GetAwaiter().GetResult()));
            Assert.All(visuals,v=>{Assert.False(v.IsLoaded);Assert.False(v.AnimationRunning);Assert.Null(PresentationSource.FromVisual(v));});
        }
        finally{foreach(var v in visuals)v.Dispose();}
    },180);

    private static void Frame(CompanionBallVisual v)=>OnFrame.Invoke(v,[null,EventArgs.Empty]);
    private static Drawing Record(CompanionBallVisual v,double size)
    {v.Measure(new Size(size,size));v.Arrange(new Rect(0,0,size,size));var d=new DrawingGroup();using var dc=d.Open();OnRender.Invoke(v,[dc]);return d;}
    private static byte[] Pixels(Drawing drawing,int width,int height)
    {var v=new DrawingVisual();using(var dc=v.RenderOpen())dc.DrawDrawing(drawing);var b=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);b.Render(v);var p=new byte[width*height*4];b.CopyPixels(p,width*4,0);return p;}
    private static HashSet<int> RingHues(byte[] pixels,int size)
    {
        var hues=new HashSet<int>();
        for(var y=(int)(size*.79);y<size;y++)for(var x=0;x<size;x++)
        {var p=(y*size+x)*4;var b=pixels[p];var g=pixels[p+1];var r=pixels[p+2];if(Math.Max(r,Math.Max(g,b))-Math.Min(r,Math.Min(g,b))>25 && pixels[p+3]>70)hues.Add(Hue(Color.FromRgb(r,g,b)));}
        return hues;
    }
    private static int Hue(Color c)
    {
        double max=Math.Max(c.R,Math.Max(c.G,c.B)),min=Math.Min(c.R,Math.Min(c.G,c.B)),d=max-min;
        if(d==0)return -1;var h=max==c.R?(c.G-c.B)/d:max==c.G?2+(c.B-c.R)/d:4+(c.R-c.G)/d;
        return (int)Math.Floor((h+6)%6);
    }
    private static void Save(string dir,string name,BitmapSource bitmap)
    {var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(Path.Combine(dir,name));png.Save(stream);}
    private static void Text(DrawingContext dc,string text,double size,double x,double y,Brush? brush=null)=>dc.DrawText(new FormattedText(text,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Microsoft YaHei UI"),size,brush??Brushes.WhiteSmoke,1),new Point(x,y));
    private static void Sta(Action action,int seconds=60)
    {Exception? failure=null;var thread=new Thread(()=>{try{action();}catch(Exception e){failure=e;}finally{Dispatcher.CurrentDispatcher.InvokeShutdown();}}){IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromSeconds(seconds)));if(failure is not null)ExceptionDispatchInfo.Capture(failure).Throw();}
}
