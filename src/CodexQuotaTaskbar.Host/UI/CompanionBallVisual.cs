using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CodexQuotaTaskbar.Host.Platform;
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
    private static readonly Brush GreenBody = MakeGreenBody();
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
    private bool resetNoticeActive;
    private double lastFrame = -100, nextOrbit, previousTime;
    private int spinVersion, burstVersion;
    internal bool AnimationRunning => subscribed;
    internal bool ResetNoticeActive => resetNoticeActive;
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

    internal void SetResetNoticeActive(bool active)
    {
        if (disposed || resetNoticeActive == active) return;
        resetNoticeActive = active;
        InvalidateVisual();
    }

    internal void SetState(string id, bool animate)
    {
        if (disposed) return;
        var changed = engine.RequestedEmotion != id || motion != animate;
        motion = animate;
        engine.SetEmotion(id, Now, animate);
        if (!animate) typing.Cancel();
        if (changed) { engine.Sample(Now, animate); if (!animate) { trails.Clear(); pieces.Clear(); } InvalidateVisual(); }
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
        var now = Now;
        // 30 fps ceiling; quiet sleeping states need only 15 fps. Hidden/reduced states have no frame callback.
        var interval = engine.Definition.Id is "00" or "06" ? 1000.0 / 15 : 1000.0 / 30;
        if (now - lastFrame + .1 < interval) return;
        lastFrame = now;
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
        DrawTrails(dc, false);
        var p = engine.Pose;
        var tap = TypingFrame;
        var body = Matrix.Identity;
        body.Translate(-Center, -Center); body.Scale(p[2] * tap.ScaleX, p[2] * tap.ScaleY); body.Rotate(p[3]); body.Translate(Center + p[0], Center + p[1] + tap.BodyY);
        dc.PushTransform(new MatrixTransform(body));
        dc.DrawGeometry(resetNoticeActive ? GreenBody : Body, null, Head.Value);
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
    private static Brush MakeGreenBody()
    {
        var brush = new RadialGradientBrush { Center = new Point(.38, .32), GradientOrigin = new Point(.38, .32), RadiusX = .75, RadiusY = .75 };
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(232, 255, 238), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(166, 226, 185), .62));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(105, 177, 139), 1));
        brush.Freeze(); return brush;
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
        disposed = true; resetNoticeActive = false; typing.SetEnabled(false); StopClock(); trails.Clear(); pieces.Clear();
        Loaded -= OnLoaded; Unloaded -= OnUnloaded; IsVisibleChanged -= OnVisibility;
    }
    private sealed class Trail { internal double Phase, Radius, Tilt, Roll, Speed, Age, Retraction; internal int Color; internal bool Orbit; }
    private sealed class Piece { internal double X, Y, VX, VY, Age, MaxAge, Size, Rotation, Spin; internal int Color, Shape; }
}
