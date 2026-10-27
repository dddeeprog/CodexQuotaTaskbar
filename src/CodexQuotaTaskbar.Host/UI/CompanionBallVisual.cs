using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CodexQuotaTaskbar.Host.Platform;
using CodexQuotaTaskbar.Host.Resets;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;
using Pen = System.Windows.Media.Pen;

namespace CodexQuotaTaskbar.Host.UI;

// WPF vector renderer for Grok Ball's MIT-licensed contour/pose data.
// FX keep bounded native geometry (ribbons, confetti and sleeping z's), not video frames.
internal sealed class CompanionBallVisual : FrameworkElement, IDisposable
{
    private const double Center = 114.2705;
    private static readonly Lazy<Geometry> Head = new(() => Polygon(CompanionCatalog.Shared.Value.Head));
    private static readonly Brush Body = MakeBody();
    private static readonly Lazy<Drawing> ResetRing = new(MakeResetRing);
    private static readonly Lazy<Drawing> ResetReflection = new(MakeResetReflection);
    private static readonly Lazy<Drawing> GoldResetRing = new(() => MakeGold(ResetRing.Value));
    private static readonly Lazy<Drawing> GoldResetReflection = new(() => MakeGold(ResetReflection.Value));
    private static readonly Lazy<Drawing> RedResetRing = new(MakeFailureRing);
    private static readonly Brush Eye = Frozen(Color.FromRgb(35, 35, 38));
    private static readonly Pen PawEdge = MakePawEdge();
    private static readonly Lazy<Drawing> TypingKeyboard = new(MakeTypingKeyboard);
    private static readonly Brush TypingKeyLight = Frozen(Color.FromRgb(75, 231, 233));
    private static readonly Brush HandBallShadow = Frozen(Color.FromArgb(34, 20, 24, 32));
    private static readonly Brush MouseBody = Frozen(Color.FromRgb(253, 253, 253));
    private static readonly Lazy<Drawing> MousePad = new(MakeMousePad);
    private static readonly Geometry MouseContour = MakeMouseContour();
    private static readonly Pen MouseInk = MakeMouseInk();
    private static readonly Brush[] Colors = [Frozen(Color.FromRgb(249,112,92)), Frozen(Color.FromRgb(91,149,240)),
        Frozen(Color.FromRgb(63,190,134)), Frozen(Color.FromRgb(245,177,63)), Frozen(Color.FromRgb(154,114,238)), Frozen(Color.FromRgb(53,195,189))];
    private static readonly Typeface SleepTypeface = new(new System.Windows.Media.FontFamily("Segoe UI"), FontStyles.Italic, FontWeights.Bold, FontStretches.Normal);
    private readonly CompanionPoseEngine engine = new();
    private readonly Stopwatch clock = new();
    private readonly Func<double>? clockMilliseconds;
    private readonly CompanionTypingMotion typing = new();
    private readonly Random random = new();
    private readonly List<Trail> trails = [];
    private readonly List<Piece> pieces = [];
    private readonly (double Lo, double Hi)[] silhouette;
    private readonly double minY, maxY;
    private bool motion = true, subscribed, disposed, typingInteractionEnabled;
    private ResetVisualState resetState;
    private double resetFloatOrigin;
    private double floatTransitionOrigin = double.NegativeInfinity;
    private ResetFloatFrame floatTransitionFrom = ResetFloatFrame.Rest;
    private readonly MatrixTransform resetGlowTransform = new();
    private readonly NormalRingSurface normalRing = new();
    internal Drawing NormalRingDrawing => normalRing.Drawing;
    internal Drawing NormalReflectionDrawing => normalRing.Reflection;
    internal double NormalReflectionOpacity => NormalFrame.Opacity * (.85 - .57 * Math.Clamp(-FloatFrame.BodyY / 28, 0, 1));
    internal NormalRingFrame NormalFrame { get; private set; } = SampleNormalRing(0, true);
    internal readonly record struct NormalRingFrame(Color Color, double Opacity, double Phase);
    internal ResetFloatFrame FloatFrame { get; private set; } = ResetFloatFrame.Rest;
    internal readonly record struct ResetFloatFrame(double BodyY, double GlowScaleX, double GlowScaleY, double GlowOpacity)
    {
        internal static readonly ResetFloatFrame Rest = new(0, 1, 1, 1);
    }
    private double lastFrame = -100, nextOrbit, previousTime;
    private int spinVersion, burstVersion;
    internal bool AnimationRunning => subscribed;
    internal bool ResetNoticeActive => resetState != ResetVisualState.None;
    internal ResetVisualState ResetState => resetState;
    internal int FrameCount { get; private set; }
    internal string Emotion => engine.RequestedEmotion;
    internal string EffectiveEmotion => engine.Definition.Id;
    internal CompanionGazeSample GazeSample => engine.GazeSample;
    internal CompanionTypingFrame TypingFrame => motion && Visibility == Visibility.Visible && !disposed
        ? typing.Sample(Now) : CompanionTypingFrame.Rest;
    internal event Action? Presented;

    internal CompanionBallVisual(Func<double>? clockMilliseconds = null)
    {
        this.clockMilliseconds = clockMilliseconds;
        IsHitTestVisible = false;
        var head = CompanionCatalog.Shared.Value.Head;
        minY = head.Min(p => p[1]); maxY = head.Max(p => p[1]);
        silhouette = new (double, double)[(int)Math.Ceiling((maxY - minY) / 2) + 1];
        for (var row = 0; row < silhouette.Length; row++)
        {
            var y = minY + row * 2; var lo = double.PositiveInfinity; var hi = double.NegativeInfinity;
            for (var i = 0; i < head.Length; i++)
            {
                var a = head[i]; var b = head[(i + 1) % head.Length];
                if ((a[1] <= y && b[1] >= y) || (b[1] <= y && a[1] >= y))
                { var t = b[1] == a[1] ? 0 : (y - a[1]) / (b[1] - a[1]); var x = a[0] + (b[0] - a[0]) * t; lo = Math.Min(lo, x); hi = Math.Max(hi, x); }
            }
            silhouette[row] = lo <= hi ? (lo, hi) : (Center - 4, Center + 4);
        }
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnVisibility;
    }

    internal void SetResetNoticeActive(bool active) => SetResetState(active ? ResetVisualState.Upcoming : ResetVisualState.None);

    internal void SetResetState(ResetVisualState state)
    {
        if (disposed) return;
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        // Defensive normalization for old/replayed visual projections. Transport
        // still reports Checking, but it shares the ordinary RGB appearance.
        if (state == ResetVisualState.Checking) state = ResetVisualState.None;
        if (resetState == state) return;
        if ((resetState == ResetVisualState.None) != (state == ResetVisualState.None))
        {
            floatTransitionFrom = FloatFrame;
            floatTransitionOrigin = Now;
        }
        // A new special episode starts at rest. Color changes and repeated
        // projections preserve phase; neither polling nor errors restart a hop.
        if (resetState == ResetVisualState.None) resetFloatOrigin = Now;
        resetState = state;
        if (state == ResetVisualState.None) UpdateNormalRing();
        InvalidateVisual();
    }

    internal void SetState(string id, bool animate)
    {
        if (disposed) return;
        var changed = engine.RequestedEmotion != id || motion != animate;
        motion = animate;
        engine.SetEmotion(id, Now, animate);
        if (!animate) { typing.Cancel(); FloatFrame = ResetFloatFrame.Rest; floatTransitionOrigin = double.NegativeInfinity; }
        if (changed) { engine.Sample(Now, animate); UpdateNormalRing(); if (!animate) { trails.Clear(); pieces.Clear(); } InvalidateVisual(); }
        UpdateClock();
    }
    internal void Gaze(double x, double y)
    {
        if (!motion || disposed) return;
        engine.Gaze(x, y);
        typing.Pointer(x, y);
    }
    internal void SetTypingInteraction(bool enabled)
    {
        if (disposed || typingInteractionEnabled == enabled) return;
        typingInteractionEnabled = enabled;
        typing.SetEnabled(enabled);
        InvalidateVisual();
    }
    internal void Input(CompanionInputEvent input)
    {
        if (disposed) return;
        if (input.Kind == CompanionInputKind.Reset) { typing.Cancel(); return; }
        if (!motion || Visibility != Visibility.Visible) { typing.Cancel(); return; }
        // State edges are never throttled or queued. The existing bounded render loop
        // reads the latest state; fast input cannot create a queue of drawing work.
        typing.Input(input);
    }
    private double Now => clockMilliseconds?.Invoke() ?? clock.Elapsed.TotalMilliseconds;
    private void OnLoaded(object sender, RoutedEventArgs e) => UpdateClock();
    private void OnUnloaded(object sender, RoutedEventArgs e) { typing.Cancel(); StopClock(); }
    private void OnVisibility(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible) typing.Cancel();
        UpdateClock();
    }
    private void UpdateClock()
    {
        if (disposed || !motion || !IsVisible || !IsLoaded) { StopClock(); return; }
        if (subscribed) return;
        clock.Start(); subscribed = true; lastFrame = -100;
        CompositionTarget.Rendering += OnFrame;
    }
    private void StopClock()
    {
        clock.Stop();
        if (!subscribed) return;
        CompositionTarget.Rendering -= OnFrame; subscribed = false;
    }
    private void OnFrame(object? sender, EventArgs e)
    {
        if (disposed || !motion || Visibility != Visibility.Visible) return;
        // An injected clock is the explicit offline sampling seam. Production
        // ignores even a queued render callback after hiding or unloading.
        if (clockMilliseconds is null && (!IsLoaded || !IsVisible)) return;
        var now = Now;
        // 30 fps ceiling; quiet sleeping states need only 15 fps. Hidden/reduced states have no frame callback.
        var interval = engine.Definition.Id is "00" or "06" ? 1000.0 / 15 : 1000.0 / 30;
        if (now - lastFrame + .1 < interval) return;
        lastFrame = now;
        if (resetState == ResetVisualState.None) UpdateNormalRing();
        var targetFloat = SampleResetFloat(resetState, resetState == ResetVisualState.None ? now : now - resetFloatOrigin, motion);
        FloatFrame = BlendFloat(floatTransitionFrom, targetFloat, (now - floatTransitionOrigin) / 220);
        if (FloatFrame.BodyY != 0)
            resetGlowTransform.Matrix = new Matrix(FloatFrame.GlowScaleX, 0, 0, FloatFrame.GlowScaleY,
                Center * (1 - FloatFrame.GlowScaleX), 246 * (1 - FloatFrame.GlowScaleY));
        engine.Sample(now, true);
        UpdateEffects(now);
        FrameCount++;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (disposed || ActualWidth <= 0 || ActualHeight <= 0) return;
        // Same 18% inset and -15..244 viewbox as the former SVG renderer.
        var scale = Math.Min(ActualWidth, ActualHeight) * .64 / 259;
        dc.PushTransform(new TranslateTransform(ActualWidth / 2 - Center * scale, ActualHeight / 2 - Center * scale));
        dc.PushTransform(new ScaleTransform(scale, scale));
        DrawResetRing(dc);
        DrawTrails(dc, false);
        var p = engine.Pose;
        var tap = TypingFrame;
        var body = Matrix.Identity;
        body.Translate(-Center, -Center); body.Scale(p[2] * tap.ScaleX, p[2] * tap.ScaleY); body.Rotate(p[3]); body.Translate(Center + p[0], Center + p[1] + tap.BodyY + FloatFrame.BodyY);
        dc.PushTransform(new MatrixTransform(body));
        dc.DrawGeometry(Body, null, Head.Value);
        if (resetState == ResetVisualState.None)
        {
            normalRing.UpdateReflection(body);
            dc.PushOpacity(NormalReflectionOpacity);
            dc.DrawDrawing(normalRing.Reflection);
            dc.Pop();
        }
        if (resetState is ResetVisualState.Upcoming or ResetVisualState.Confirmed)
            dc.DrawDrawing(resetState == ResetVisualState.Upcoming ? GoldResetReflection.Value : ResetReflection.Value);
        DrawEye(dc, 0, CompanionPoseEngine.Left);
        DrawEye(dc, 1, CompanionPoseEngine.Right);
        dc.Pop();
        DrawTrails(dc, true);
        DrawTypingHands(dc, tap);
        DrawParticles(dc);
        if (p[8] > 0) DrawSleep(dc, Now, p[8]);
        dc.Pop(); dc.Pop();
        // The observer deduplicates identities; ordinary animation frames do not write a log.
        Presented?.Invoke();
    }

    // The former Checking angle stays zero; only real failures retain the soft pulse.
    internal static (double Angle, double Opacity) QueryRingMotion(ResetVisualState state, double milliseconds, bool animate) =>
        (0,
         state == ResetVisualState.Failed && animate ? .82 + .18 * Math.Cos(milliseconds / 2800 * Math.Tau) : 1);

    // A full spectrum exists around the ring at EVERY phase. Rotation is spatial,
    // never a uniform temporal tint. The front arc supplies the local bounce light.
    internal static Color SpectrumColor(double phase, double angle)
    {
        ReadOnlySpan<Color> palette = [Color.FromRgb(255, 112, 115), Color.FromRgb(255, 219, 107),
            Color.FromRgb(109, 229, 157), Color.FromRgb(103, 219, 239),
            Color.FromRgb(128, 159, 255), Color.FromRgb(211, 133, 247), Color.FromRgb(255, 112, 115)];
        var position = ((angle - phase) % 1 + 1) % 1 * 6;
        var index = Math.Min(5, (int)position); var t = position - index;
        var a = palette[index]; var b = palette[index + 1];
        return Color.FromRgb((byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));
    }

    internal static NormalRingFrame SampleNormalRing(double milliseconds, bool animate)
    {
        var time = animate ? Math.Max(0, milliseconds) : 0;
        var phase = time % 36000 / 36000;
        return new(SpectrumColor(phase, .25), animate ? .77 + .09 * Math.Cos(time % 6000 / 6000 * Math.Tau) : .78, phase);
    }

    private void UpdateNormalRing()
    {
        if (resetState != ResetVisualState.None) return;
        NormalFrame = SampleNormalRing(Now, motion);
        normalRing.Update(NormalFrame);
    }

    // Freeze the 24-piece vector spectrum once across all monitors. Per frame only
    // one shared brush transform and the tiny lower reflection stops are updated.
    private sealed class NormalRingSurface
    {
        private static readonly Lazy<Drawing> Spectrum = new(MakeSpectrum);
        internal DrawingGroup Drawing { get; } = new();
        internal GeometryDrawing Reflection { get; }
        private readonly RadialGradientBrush reflectionBrush;
        private readonly MatrixTransform reflectionTransform = new();
        private readonly MatrixTransform spectrumTransform = new();
        private Color? previous;

        internal NormalRingSurface()
        {
            var layers = ((DrawingGroup)ResetRing.Value).Children.Cast<GeometryDrawing>().ToArray();
            for (var i = 0; i < layers.Length; i++)
            {
                if (i == 1)
                {
                    var shadow = ((GradientBrush)layers[i].Brush).Clone();
                    foreach (var stop in shadow.GradientStops)
                        stop.Color = Color.FromArgb(stop.Color.A, 12, 12, 15);
                    shadow.Freeze();
                    Drawing.Children.Add(new GeometryDrawing(shadow, null, layers[i].Geometry));
                    continue;
                }
                var brush = new DrawingBrush(Spectrum.Value)
                {
                    ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(-1, -1, 2, 2),
                    ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(-1, -1, 2, 2),
                    Stretch = Stretch.Fill, TileMode = TileMode.None, Transform = spectrumTransform,
                    Opacity = i == 3 ? .45 : i == 2 ? .78 : 1,
                };
                var surface = new GeometryDrawing(brush, null, layers[i].Geometry);
                if (i == 0)
                {
                    var mask = ((GradientBrush)layers[i].Brush).Clone();
                    foreach (var stop in mask.GradientStops) stop.Color = Color.FromArgb(stop.Color.A, 255, 255, 255);
                    mask.Freeze();
                    var halo = new DrawingGroup { OpacityMask = mask };
                    halo.Children.Add(surface); Drawing.Children.Add(halo);
                }
                else Drawing.Children.Add(surface);
            }
            reflectionBrush = ((RadialGradientBrush)((GeometryDrawing)ResetReflection.Value).Brush).Clone();
            reflectionBrush.MappingMode = BrushMappingMode.Absolute;
            reflectionBrush.Transform = reflectionTransform;
            byte[] alphas = [96, 42, 0, 0];
            for (var i = 0; i < reflectionBrush.GradientStops.Count; i++)
                reflectionBrush.GradientStops[i].Color = Color.FromArgb(alphas[i], 0, 0, 0);
            Reflection = new GeometryDrawing(reflectionBrush, null, Head.Value);
            UpdateReflection(Matrix.Identity);
            Update(SampleNormalRing(0, true));
        }

        private static Drawing MakeSpectrum()
        {
            var result = new DrawingGroup();
            const int count = 24;
            // Slight overlap removes antialias seams. Both endpoints have continuous
            // neighboring hues; the closed spectrum has no hard wrap at red.
            for (var i = 0; i < count; i++)
            {
                var a = (double)i / count; var b = (i + 1d) / count;
                var first = new Point(Math.Cos(a * Math.Tau), Math.Sin(a * Math.Tau));
                var last = new Point(Math.Cos(b * Math.Tau), Math.Sin(b * Math.Tau));
                var end = new Point(Math.Cos((b + .0003) * Math.Tau), Math.Sin((b + .0003) * Math.Tau));
                var shape = new StreamGeometry();
                using (var path = shape.Open())
                {
                    path.BeginFigure(new Point(0, 0), true, true);
                    path.LineTo(first, true, false);
                    path.ArcTo(end, new System.Windows.Size(1, 1), 0, false, SweepDirection.Clockwise, true, false);
                }
                var brush = new LinearGradientBrush(SpectrumColor(0, a), SpectrumColor(0, b), first, last)
                    { MappingMode = BrushMappingMode.Absolute };
                result.Children.Add(new GeometryDrawing(brush, null, shape));
            }
            result.Freeze(); return result;
        }

        internal void UpdateReflection(Matrix body)
        {
            var bounds = Rect.Transform(Head.Value.Bounds, body);
            var center = new Point(bounds.X + bounds.Width * .5, bounds.Bottom + bounds.Height * .06);
            reflectionBrush.Center = center; reflectionBrush.GradientOrigin = center;
            reflectionBrush.RadiusX = bounds.Width * .65; reflectionBrush.RadiusY = bounds.Height * .34;
            if (body.HasInverse) body.Invert();
            reflectionTransform.Matrix = body;
        }

        internal void Update(NormalRingFrame frame)
        {
            var transform = Matrix.Identity;
            transform.Rotate(frame.Phase * 360);
            transform.Scale(176, 62);
            transform.Translate(Center, 246);
            if (spectrumTransform.Matrix != transform) spectrumTransform.Matrix = transform;
            if (previous == frame.Color) return;
            previous = frame.Color;
            foreach (var stop in reflectionBrush.GradientStops)
                stop.Color = Color.FromArgb(stop.Color.A, frame.Color.R, frame.Color.G, frame.Color.B);
        }
    }

    // Ordinary: 28 model units = 3.60/3.87 DIP at 52/56 DIP, six seconds aligned
    // with RGB breathing. Specials retain their approved 12-unit / 3.2s motion.
    // Only drawing transforms move, never layout or the input hit region.
    internal static ResetFloatFrame SampleResetFloat(ResetVisualState state, double milliseconds, bool animate)
    {
        if (!animate) return ResetFloatFrame.Rest;
        var normal = state is ResetVisualState.None or ResetVisualState.Checking;
        var period = normal ? 6000 : 3200;
        var height = .5 * (1 - Math.Cos(Math.Max(0, milliseconds) % period / period * Math.Tau));
        return new(-(normal ? 28 : 12) * height, 1 + .12 * height, 1 + .08 * height, 1 - .18 * height);
    }

    // Retarget from the last drawn pose when ordinary/special modes change;
    // repeated projections never restart the 220ms ease-out or ambient clocks.
    internal static ResetFloatFrame BlendFloat(ResetFloatFrame from, ResetFloatFrame to, double progress)
    {
        if (progress >= 1) return to;
        var t = 1 - Math.Pow(1 - Math.Clamp(progress, 0, 1), 3);
        return new(from.BodyY + (to.BodyY - from.BodyY) * t,
            from.GlowScaleX + (to.GlowScaleX - from.GlowScaleX) * t,
            from.GlowScaleY + (to.GlowScaleY - from.GlowScaleY) * t,
            from.GlowOpacity + (to.GlowOpacity - from.GlowOpacity) * t);
    }

    private void DrawSupportedRing(DrawingContext dc, Drawing ring)
    {
        if (FloatFrame.BodyY == 0) { dc.DrawDrawing(ring); return; }
        var layers = ((DrawingGroup)ring).Children;
        // Only the first cached surface is the diffuse halo. The opening, inner
        // wall and solid base retain their exact position and geometry.
        dc.PushOpacity(FloatFrame.GlowOpacity);
        dc.PushTransform(resetGlowTransform);
        dc.DrawDrawing(layers[0]);
        dc.Pop(); dc.Pop();
        for (var i = 1; i < layers.Count; i++) dc.DrawDrawing(layers[i]);
    }

    private void DrawResetRing(DrawingContext dc)
    {
        switch (resetState)
        {
            case ResetVisualState.Upcoming: DrawSupportedRing(dc, GoldResetRing.Value); break;
            case ResetVisualState.Confirmed: DrawSupportedRing(dc, ResetRing.Value); break;
            case ResetVisualState.None:
                dc.PushOpacity(NormalFrame.Opacity);
                DrawSupportedRing(dc, normalRing.Drawing);
                dc.Pop();
                break;
            case ResetVisualState.Failed:
                dc.PushOpacity(QueryRingMotion(resetState, Now, motion).Opacity);
                DrawSupportedRing(dc, RedResetRing.Value);
                dc.Pop();
                break;
        }
    }

    private static Drawing MakeFailureRing()
    {
        var drawing = ResetRing.Value.Clone();
        Tint(drawing);
        drawing.Freeze(); return drawing;
        void Tint(Drawing item)
        {
            if (item is DrawingGroup group) foreach (var child in group.Children) Tint(child);
            if (item is GeometryDrawing { Brush: GradientBrush brush })
                foreach (var stop in brush.GradientStops)
                {
                    var color = stop.Color;
                    stop.Color = Color.FromArgb(color.A, color.G, (byte)(color.G * .25 + color.R * .25), (byte)(color.B * .65));
                }
        }
    }

    private static void DrawTypingHands(DrawingContext dc, CompanionTypingFrame tap)
    {
        if (!tap.DecorationsVisible) return;
        // Screen-left is the mouse paw; screen-right is the keyboard paw, as in
        // BongoCat's standard mode. These vectors are original, not model assets.
        dc.DrawDrawing(MousePad.Value);
        dc.DrawDrawing(TypingKeyboard.Value);
        var keyDown = CompanionTypingKeyboard.TryGet(tap.Key, out var cap);
        Point keyboardTarget;
        if (keyDown)
        {
            dc.DrawGeometry(TypingKeyLight, null, KeyShape(cap));
            keyboardTarget = CompanionTypingKeyboard.Project(cap.X + cap.Width * .5, cap.Y + .45);
            keyboardTarget.Y -= 18; // The compressed ball leaves the struck cap's cyan front visible.
            DrawHandBall(dc, keyboardTarget, true);
        }
        else DrawHandBall(dc, new Point(228, 197), false);

        var mouse = new Point(4 + tap.MouseX * 12, 234 + tap.MouseY * 8);
        // A tilted white pebble with an asymmetrical ink contour; the ball grips
        // its back while the broader front remains readable beneath it.
        dc.PushTransform(new TranslateTransform(mouse.X - 4, mouse.Y + 8));
        dc.PushTransform(new RotateTransform(28));
        dc.DrawGeometry(MouseBody, MouseInk, MouseContour);
        if (tap.MouseLeftDown) dc.DrawGeometry(TypingKeyLight, null,
            Quad(new Point(-16, 12), new Point(-3, 7), new Point(-3, 25), new Point(-12, 25)));
        if (tap.MouseRightDown) dc.DrawGeometry(TypingKeyLight, null,
            Quad(new Point(1, 6), new Point(17, 0), new Point(15, 18), new Point(1, 24)));
        dc.DrawLine(MouseInk, new Point(-14, 25), new Point(2, 11));
        dc.Pop(); dc.Pop();
        var mouseDown = tap.MouseLeftDown || tap.MouseRightDown;
        DrawHandBall(dc, new Point(mouse.X, mouse.Y - 7 + (mouseDown ? 4 : 0)), mouseDown);
    }

    private static Drawing MakeMousePad()
    {
        var drawing = new DrawingGroup();
        var surface = new LinearGradientBrush(Color.FromRgb(216, 216, 216), Color.FromRgb(167, 168, 170),
            new Point(.1, 0), new Point(.95, 1));
        surface.Freeze();
        var underside = Frozen(Color.FromRgb(113, 114, 116));
        using (var dc = drawing.Open())
        {
            // Transform a rounded square instead of giving the perspective pad sharp corners.
            dc.PushTransform(new MatrixTransform(.93, .20, -.49, .69, 0, 200));
            dc.DrawRoundedRectangle(underside, null, new Rect(0, 0, 88, 88), 8, 8);
            dc.Pop();
            dc.PushTransform(new MatrixTransform(.93, .20, -.49, .69, 0, 198));
            dc.DrawRoundedRectangle(surface, null, new Rect(0, 0, 88, 88), 8, 8);
            dc.Pop();
        }
        drawing.Freeze();
        return drawing;
    }

    private static Geometry MakeMouseContour()
    {
        var contour = new StreamGeometry();
        using (var path = contour.Open())
        {
            path.BeginFigure(new Point(0, -34), true, true);
            path.BezierTo(new Point(17, -35), new Point(25, -14), new Point(23, 6), true, false);
            path.BezierTo(new Point(21, 27), new Point(12, 35), new Point(-3, 34), true, false);
            path.BezierTo(new Point(-20, 34), new Point(-25, 20), new Point(-23, 1), true, false);
            path.BezierTo(new Point(-22, -21), new Point(-16, -36), new Point(0, -34), true, false);
        }
        contour.Freeze();
        return contour;
    }

    private static Pen MakeMouseInk()
    {
        var ink = new Pen(Frozen(Color.FromRgb(29, 29, 30)), 3.6)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        ink.Freeze();
        return ink;
    }

    private static Drawing MakeTypingKeyboard()
    {
        var drawing = new DrawingGroup();
        var shell = Frozen(Color.FromRgb(90, 94, 104));
        var keys = Frozen(Color.FromRgb(224, 226, 231));
        var edge = new Pen(Frozen(Color.FromArgb(150, 62, 66, 76)), .65);
        edge.Freeze();
        using (var dc = drawing.Open())
        {
            dc.DrawGeometry(shell, PawEdge, Quad(CompanionTypingKeyboard.Project(-.2, -.18),
                CompanionTypingKeyboard.Project(15.2, -.18), CompanionTypingKeyboard.Project(15.2, 5.18), CompanionTypingKeyboard.Project(-.2, 5.18)));
            foreach (var cap in CompanionTypingKeyboard.Keys) dc.DrawGeometry(keys, edge, KeyShape(cap));
        }
        drawing.Freeze();
        return drawing;
    }

    private static Geometry KeyShape(CompanionTypingKeyCap cap) => Quad(
        CompanionTypingKeyboard.Project(cap.X + .08, cap.Y + .09),
        CompanionTypingKeyboard.Project(cap.X + cap.Width - .08, cap.Y + .09),
        CompanionTypingKeyboard.Project(cap.X + cap.Width - .08, cap.Y + .88),
        CompanionTypingKeyboard.Project(cap.X + .08, cap.Y + .88));

    private static Geometry Quad(Point a, Point b, Point c, Point d)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open()) { g.BeginFigure(a, true, true); g.LineTo(b, true, false); g.LineTo(c, true, false); g.LineTo(d, true, false); }
        geometry.Freeze(); return geometry;
    }

    private static void DrawHandBall(DrawingContext dc, Point center, bool pressed)
    {
        // Detached little spheres share the body's material, without connecting arms.
        // A held input changes only their pressure pose; release is immediately round again.
        var radiusX = pressed ? 24.5 : 22;
        var radiusY = pressed ? 18 : 22;
        dc.DrawEllipse(HandBallShadow, null, new Point(center.X, center.Y + radiusY - 1), radiusX * .88, 4);
        dc.DrawEllipse(Body, PawEdge, center, radiusX, radiusY);
    }

    private void DrawEye(DrawingContext dc, int side, int offset)
    {
        var p = engine.Pose; var ring = engine.Rings[side];
        var centerX = 0.0; var centerY = 0.0;
        foreach (var xy in ring) { centerX += xy[0]; centerY += xy[1]; }
        centerX /= ring.Length; centerY /= ring.Length;
        var sy = Math.Clamp(p[offset + 3] * Math.Clamp(p[offset + 5], .02, 2.4), .02, 2.4);
        var halfHeight = 21 * sy + 2;
        var ey = Math.Clamp(centerY + p[offset + 1] + p[offset + 7], minY + halfHeight, maxY - halfHeight);
        var sil = silhouette[Math.Clamp((int)Math.Round((ey - minY) / 2), 0, silhouette.Length - 1)];
        var halfWidth = Math.Max((sil.Hi - sil.Lo) / 2, 12);
        var theta = Math.Clamp((centerX - Center + p[offset] + p[offset + 6]) / halfWidth, -1.15, 1.15) + p[10];
        var cosine = Math.Cos(theta);
        if (cosine <= .02) return;
        var ex = (sil.Lo + sil.Hi) / 2 + halfWidth * Math.Sin(theta) * .985;
        var dy = (ey - Center) / 130;
        var transform = Matrix.Identity;
        transform.Translate(-centerX, -centerY); transform.Scale(p[offset + 2] * cosine, sy * Math.Sqrt(1 - dy * dy * .22));
        transform.Rotate(p[offset + 4]); transform.Translate(ex, ey);
        dc.PushTransform(new MatrixTransform(transform));
        dc.DrawGeometry(Eye, null, Polygon(ring));
        dc.Pop();
    }

    private void UpdateEffects(double now)
    {
        var dt = Math.Clamp((now - previousTime) / 1000, .001, .08); previousTime = now;
        if (spinVersion != engine.SpinVersion)
        {
            spinVersion = engine.SpinVersion;
            for (var i = 0; i < 4 && trails.Count < 8; i++) trails.Add(new Trail { Phase = i * .6, Radius = 116 + i * 12,
                Tilt = .3, Roll = -.3 + random.NextDouble() * .6, Speed = 5.5, Color = i % Colors.Length });
        }
        var orbit = engine.Pose[9] > 0;
        if (orbit && now >= nextOrbit && trails.Count(t => t.Orbit) < 2)
        { trails.Add(new Trail { Orbit = true, Phase = random.NextDouble() * Math.Tau, Radius = 124 + 16 * trails.Count(t => t.Orbit), Tilt = .16, Speed = 2, Color = random.Next(Colors.Length) }); nextOrbit = now + 700; }
        for (var i = trails.Count - 1; i >= 0; i--)
        {
            var tr = trails[i]; tr.Age += dt;
            if (tr.Orbit ? !orbit : !engine.Spinning || tr.Age > 5) tr.Retraction += dt / .5;
            if (tr.Retraction >= 1) { trails.RemoveAt(i); continue; }
            tr.Phase += dt * tr.Speed;
        }
        if (burstVersion != engine.BurstVersion)
        {
            burstVersion = engine.BurstVersion;
            for (var i = 0; i < 20 && pieces.Count < 60; i++)
            {
                var angle = i * Math.Tau / 20 + random.NextDouble() * .7 - .35;
                var speed = 170 + random.NextDouble() * 190;
                pieces.Add(new Piece { X = Center + Math.Cos(angle) * 106, Y = Center + Math.Sin(angle) * 106,
                    VX = Math.Cos(angle) * speed, VY = Math.Sin(angle) * speed - 45, MaxAge = .45 + random.NextDouble() * .4,
                    Size = 3.5 + random.NextDouble() * 4.5, Rotation = random.NextDouble() * 360,
                    Spin = random.NextDouble() * 520 - 260, Color = random.Next(Colors.Length), Shape = random.Next(10) });
            }
        }
        for (var i = pieces.Count - 1; i >= 0; i--)
        {
            var pc = pieces[i]; pc.Age += dt;
            if (pc.Age >= pc.MaxAge) { pieces.RemoveAt(i); continue; }
            pc.X += pc.VX * dt; pc.Y += pc.VY * dt;
            var drag = Math.Pow(.94, 60 * dt); pc.VX *= drag; pc.VY = pc.VY * drag + 40 * dt; pc.Rotation += pc.Spin * dt;
        }
    }

    private void DrawTrails(DrawingContext dc, bool front)
    {
        foreach (var tr in trails)
        {
            var length = 2.8 * (1 - tr.Retraction * tr.Retraction * (3 - 2 * tr.Retraction));
            var grow = Math.Min(tr.Age / .34, 1);
            var width = 10 * grow * (1 - .72 * tr.Retraction * tr.Retraction);
            if (width < .5) continue;
            // Bounded 32-segment tapered ribbons; split by depth so the face is never covered by back segments.
            dc.PushOpacity(Math.Min(tr.Age / .26, 1));
            for (var i = 0; i < 32; i++)
            {
                var a = tr.Phase - length + length * i / 32;
                var b = tr.Phase - length + length * (i + 1) / 32;
                if ((Math.Cos((a + b) / 2) >= 0) != front) continue;
                var pen = new Pen(Colors[(tr.Color + (int)(tr.Age / 2)) % Colors.Length], width * (.5 + .5 * i / 32)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                dc.DrawLine(pen, Orbit(tr, a), Orbit(tr, b));
            }
            dc.Pop();
        }
    }
    private static Point Orbit(Trail tr, double angle)
    {
        var x = tr.Radius * Math.Sin(angle); var y = -tr.Radius * Math.Cos(angle) * Math.Sin(tr.Tilt);
        return new Point(Center + x * Math.Cos(tr.Roll) - y * Math.Sin(tr.Roll), Center + x * Math.Sin(tr.Roll) + y * Math.Cos(tr.Roll));
    }
    private void DrawParticles(DrawingContext dc)
    {
        foreach (var pc in pieces)
        {
            var u = pc.Age / pc.MaxAge; var size = Math.Max(.5, pc.Size * (1 - .4 * u));
            dc.PushOpacity(u < .1 ? u / .1 : Math.Pow(1 - (u - .1) / .9, 1.7));
            dc.PushTransform(new TranslateTransform(pc.X, pc.Y)); dc.PushTransform(new RotateTransform(pc.Rotation));
            if (pc.Shape < 2)
            {
                var star = new StreamGeometry();
                using (var ctx = star.Open()) for (var i = 0; i < 10; i++)
                { var a = -Math.PI / 2 + i * Math.PI / 5; var r = size * (i % 2 == 0 ? 1 : .42); var point = new Point(Math.Cos(a) * r, Math.Sin(a) * r); if (i == 0) ctx.BeginFigure(point, true, true); else ctx.LineTo(point, true, false); }
                dc.DrawGeometry(Colors[3], null, star);
            }
            else if (pc.Shape < 5) dc.DrawEllipse(Colors[pc.Color], null, new Point(), size, size);
            else dc.DrawRoundedRectangle(Colors[pc.Color], null, new Rect(-size / 2, -size, size, size * 1.9), size * .24, size * .24);
            dc.Pop(); dc.Pop(); dc.Pop();
        }
    }
    private void DrawSleep(DrawingContext dc, double now, double strength)
    {
        for (var z = 0; z < 3; z++)
        {
            var p = (now * .00033 + z / 3.0) % 1;
            var opacity = (p < .18 ? p / .18 : 1 - (p - .18) / .82) * .8 * strength;
            dc.PushOpacity(opacity);
            var text = new FormattedText("z", CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight, SleepTypeface, 12 + p * 11, Brushes.DarkGray, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(180 + p * 34 + 4 * Math.Sin(p * 9), 30 - p * 42));
            dc.Pop();
        }
    }
    private static Geometry Polygon(double[][] points)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        { ctx.BeginFigure(new Point(points[0][0], points[0][1]), true, true); for (var i = 1; i < points.Length; i++) ctx.LineTo(new Point(points[i][0], points[i][1]), true, false); }
        geometry.Freeze(); return geometry;
    }
    private static Brush MakeBody()
    {
        var brush = new RadialGradientBrush { Center = new Point(.38, .32), GradientOrigin = new Point(.38, .32), RadiusX = .75, RadiusY = .75 };
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(255, 255, 255), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(245, 245, 247), .62));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(216, 216, 222), 1));
        brush.Freeze(); return brush;
    }
    private static Drawing MakeResetRing()
    {
        // Shared static surfaces give the ring perspective and thickness without
        // an offscreen blur surface. The ball keeps its size and original pose.
        var center = new Point(Center, 244);
        var opening = new EllipseGeometry(new Point(Center, 240), 78, 26);
        var glow = new RadialGradientBrush
        {
            GradientStops = new GradientStopCollection
            {
                new(Color.FromArgb(0, 94, 246, 76), 0),
                new(Color.FromArgb(0, 94, 246, 76), .28),
                new(Color.FromArgb(126, 94, 246, 76), .63),
                new(Color.FromArgb(37, 94, 246, 76), .83),
                new(Color.FromArgb(0, 94, 246, 76), 1),
            },
        };
        var shadow = new RadialGradientBrush
        {
            GradientStops = new GradientStopCollection
            {
                new(Color.FromArgb(215, 8, 24, 12), 0),
                new(Color.FromArgb(175, 8, 24, 12), .55),
                new(Color.FromArgb(80, 8, 24, 12), .82),
                new(Color.FromArgb(0, 8, 24, 12), 1),
            },
        };
        var top = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
            GradientStops = new GradientStopCollection
            {
                new(Color.FromRgb(87, 211, 62), 0),
                new(Color.FromRgb(185, 255, 122), .66),
                new(Color.FromRgb(132, 242, 65), 1),
            },
        };
        var innerWall = new LinearGradientBrush(Color.FromRgb(18, 66, 27), Color.FromRgb(69, 144, 49),
            new Point(0, 0), new Point(0, 1));
        var front = new LinearGradientBrush(Color.FromRgb(78, 196, 55), Color.FromRgb(19, 82, 30),
            new Point(0, 0), new Point(0, 1));
        var frontWall = new StreamGeometry();
        const double rx = 119, ry = 42, depth = 16, k = .55228475;
        using (var path = frontWall.Open())
        {
            path.BeginFigure(new Point(Center - rx, center.Y), true, true);
            path.BezierTo(new Point(Center - rx, center.Y + ry * k), new Point(Center - rx * k, center.Y + ry),
                new Point(Center, center.Y + ry), true, true);
            path.BezierTo(new Point(Center + rx * k, center.Y + ry), new Point(Center + rx, center.Y + ry * k),
                new Point(Center + rx, center.Y), true, true);
            path.LineTo(new Point(Center + rx, center.Y + depth), true, false);
            path.BezierTo(new Point(Center + rx, center.Y + depth + ry * k), new Point(Center + rx * k, center.Y + depth + ry),
                new Point(Center, center.Y + depth + ry), true, true);
            path.BezierTo(new Point(Center - rx * k, center.Y + depth + ry), new Point(Center - rx, center.Y + depth + ry * k),
                new Point(Center - rx, center.Y + depth), true, true);
        }
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.DrawEllipse(glow, null, new Point(Center, 246), 176, 62);
            dc.DrawEllipse(shadow, null, new Point(Center, 247), 84, 29);
            dc.DrawGeometry(front, null, frontWall);
            dc.DrawGeometry(innerWall, null, new CombinedGeometry(GeometryCombineMode.Exclude,
                opening, new EllipseGeometry(new Point(Center, 249), 75, 23)));
            dc.DrawGeometry(top, null, new CombinedGeometry(GeometryCombineMode.Exclude,
                new EllipseGeometry(center, rx, ry), opening));
        }
        drawing.Freeze();
        return drawing;
    }

    private static Drawing MakeResetReflection()
    {
        // A weak bounce light clipped by the original head, transformed with the
        // existing body pose. The upper face and all eye shapes stay untouched.
        var light = new RadialGradientBrush
        {
            Center = new Point(.5, 1.06), GradientOrigin = new Point(.5, 1.06), RadiusX = .65, RadiusY = .42,
            GradientStops = new GradientStopCollection
            {
                new(Color.FromArgb(60, 92, 228, 92), 0),
                new(Color.FromArgb(27, 92, 228, 92), .48),
                new(Color.FromArgb(0, 92, 228, 92), .9),
                new(Color.FromArgb(0, 92, 228, 92), 1),
            },
        };
        var drawing = new GeometryDrawing(light, null, Head.Value);
        drawing.Freeze();
        return drawing;
    }
    private static Drawing MakeGold(Drawing original)
    {
        var drawing = original.Clone();
        Tint(drawing);
        drawing.Freeze();
        return drawing;

        static void Tint(Drawing item)
        {
            if (item is DrawingGroup group) foreach (var child in group.Children) Tint(child);
            if (item is GeometryDrawing { Brush: GradientBrush brush })
                foreach (var stop in brush.GradientStops)
                {
                    var color = stop.Color;
                    stop.Color = Color.FromArgb(color.A, color.G, (byte)Math.Round(color.G * .68 + color.R * .2),
                        (byte)Math.Round(color.B * .65));
                }
        }
    }
    private static Brush Frozen(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
    private static Pen MakePawEdge()
    {
        var pen = new Pen(Frozen(Color.FromArgb(92, 116, 116, 124)), 1.8);
        pen.Freeze(); return pen;
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; resetState = ResetVisualState.None; typing.SetEnabled(false); StopClock(); trails.Clear(); pieces.Clear();
        Loaded -= OnLoaded; Unloaded -= OnUnloaded; IsVisibleChanged -= OnVisibility;
    }
    private sealed class Trail { internal double Phase, Radius, Tilt, Roll, Speed, Age, Retraction; internal int Color; internal bool Orbit; }
    private sealed class Piece { internal double X, Y, VX, VY, Age, MaxAge, Size, Rotation, Spin; internal int Color, Shape; }
}
