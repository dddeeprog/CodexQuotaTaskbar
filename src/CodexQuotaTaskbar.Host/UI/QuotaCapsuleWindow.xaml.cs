using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using CodexQuotaTaskbar.Core.Overlay;
using CodexQuotaTaskbar.Core.Quota;
using CodexQuotaTaskbar.Host.Platform;
using FrostedBlur = BlurredBackground.WPF.BlurredBackground;

namespace CodexQuotaTaskbar.Host.UI;

public partial class QuotaCapsuleWindow : Window
{
    private static readonly System.Windows.Media.Brush Cool = Freeze("#F2F2F7");
    private static readonly System.Windows.Media.Brush Amber = Freeze("#FFB84D");
    private static readonly System.Windows.Media.Brush Critical = Freeze("#FF5C6C");
    private static readonly System.Windows.Media.Brush Neutral = Freeze("#737378");
    private readonly System.Windows.Media.Brush normalLabelForeground;
    private readonly System.Windows.Media.Brush normalValueForeground;
    private readonly System.Windows.Media.Brush normalGlassBackground;
    private readonly System.Windows.Media.Brush normalBackdropBackground;
    private ScreenRect physicalBounds;
    private bool blurReady;
    private ScreenRect dragOriginBounds;
    private int dragOriginX;
    private int dragOriginY;
    private bool pointerDown;
    private bool userDragging;

    internal QuotaCapsuleWindow(string monitorId)
    {
        MonitorId = monitorId;
        InitializeComponent();
        normalLabelForeground = FirstLabel.Foreground;
        normalValueForeground = FirstValue.Foreground;
        normalGlassBackground = GlassBorder.Background;
        normalBackdropBackground = BackdropLayer.Background;
        SourceInitialized += (_, _) => WindowInteropPolicy.AttachNoActivate(this);
        Loaded += (_, _) =>
        {
            blurReady = true;
            ApplySystemAppearance();
        };
        AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnMouseLeftButtonDown), true);
        AddHandler(Mouse.PreviewMouseMoveEvent, new System.Windows.Input.MouseEventHandler(OnMouseMove), true);
        AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(OnMouseLeftButtonUp), true);
        LostMouseCapture += (_, _) => ResetDragState();
        MouseRightButtonUp += (_, _) => ContextInvoked?.Invoke(this, EventArgs.Empty);
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        Closed += (_, _) => SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        ApplySystemAppearance();
    }

    internal string MonitorId { get; }
    internal ScreenRect PhysicalBounds => physicalBounds;
    internal bool IsUserDragging => userDragging;
    internal event EventHandler? PrimaryInvoked;
    internal event EventHandler? ContextInvoked;
    internal event Action<ScreenRect, ScreenRect>? UserMoved;

    internal void Apply(QuotaSnapshot snapshot)
    {
        var projection = QuotaCapsuleProjection.Create(snapshot);
        ApplyRow(projection.Rows[0], FirstLabel, FirstBar, FirstValue);
        var hasSecondRow = projection.Rows.Count > 1;
        System.Windows.Controls.Grid.SetRowSpan(FirstRow, hasSecondRow ? 1 : 2);
        SecondRow.Visibility = hasSecondRow ? Visibility.Visible : Visibility.Collapsed;
        if (hasSecondRow)
        {
            ApplyRow(projection.Rows[1], SecondLabel, SecondBar, SecondValue);
        }

        Opacity = snapshot.Availability == QuotaAvailability.Stale ? 0.78 : 1;
        ToolTip = BuildToolTip(snapshot, projection);
        var rowSummary = string.Join("，", projection.Rows.Select(row => $"{row.Label} {row.Text}"));
        AutomationProperties.SetName(this, $"Codex 额度，{rowSummary}，{projection.StatusText}");
        ApplySystemAppearance();
    }

    internal void Place(ScreenRect bounds)
    {
        physicalBounds = bounds;
        WindowInteropPolicy.Position(this, bounds);
    }

    internal static bool ExceedsDragThreshold(int deltaX, int deltaY) => Math.Abs(deltaX) >= 4 || Math.Abs(deltaY) >= 4;

    internal static ScreenRect TranslateBounds(ScreenRect bounds, int deltaX, int deltaY) => new(
        bounds.Left + deltaX,
        bounds.Top + deltaY,
        bounds.Right + deltaX,
        bounds.Bottom + deltaY);

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ChangedButton != MouseButton.Left || !WindowInteropPolicy.TryGetCursorPosition(out dragOriginX, out dragOriginY))
        {
            return;
        }

        dragOriginBounds = physicalBounds;
        pointerDown = true;
        userDragging = false;
        Mouse.Capture(this);
        eventArgs.Handled = true;
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs eventArgs)
    {
        if (!pointerDown || eventArgs.LeftButton != MouseButtonState.Pressed
            || !WindowInteropPolicy.TryGetCursorPosition(out var cursorX, out var cursorY))
        {
            return;
        }

        var deltaX = cursorX - dragOriginX;
        var deltaY = cursorY - dragOriginY;
        if (!userDragging && !ExceedsDragThreshold(deltaX, deltaY))
        {
            return;
        }

        userDragging = true;
        Cursor = System.Windows.Input.Cursors.SizeAll;
        var moved = TranslateBounds(dragOriginBounds, deltaX, deltaY);
        var previous = physicalBounds;
        Place(moved);
        UserMoved?.Invoke(previous, moved);
        eventArgs.Handled = true;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs eventArgs)
    {
        if (!pointerDown || eventArgs.ChangedButton != MouseButton.Left)
        {
            return;
        }

        var invokePrimary = !userDragging;
        pointerDown = false;
        userDragging = false;
        Cursor = System.Windows.Input.Cursors.Arrow;
        ReleaseMouseCapture();
        eventArgs.Handled = true;
        if (invokePrimary)
        {
            PrimaryInvoked?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ResetDragState()
    {
        pointerDown = false;
        userDragging = false;
        Cursor = System.Windows.Input.Cursors.Arrow;
    }

    private static void ApplyRow(QuotaCapsuleRow row, System.Windows.Controls.TextBlock label, System.Windows.Controls.ProgressBar bar, System.Windows.Controls.TextBlock value)
    {
        label.Text = row.Label;
        value.Text = row.Text;
        bar.Value = row.RemainingPercent ?? 0;
        bar.Foreground = row.ColorToken switch
        {
            "Amber" => Amber,
            "Critical" => Critical,
            "Cool" => Cool,
            _ => Neutral,
        };
    }

    private static string BuildToolTip(QuotaSnapshot snapshot, QuotaCapsuleProjection projection)
    {
        var lines = projection.Rows.Select(row => row.ResetsAt is { } reset
            ? $"{row.Label}：剩余 {row.Text}，{reset.ToLocalTime():M月d日 HH:mm} 重置"
            : $"{row.Label}：{row.Text}");
        return string.Join(Environment.NewLine, lines.Append(snapshot.StatusText));
    }

    private void OnSystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => ApplySystemAppearance();

    private void ApplySystemAppearance()
    {
        if (CapsuleThemePolicy.Resolve(SystemParameters.HighContrast, SystemParameters.IsGlassEnabled) == CapsuleSurfaceMode.OpaqueSystem)
        {
            SetBlurEnabled(false);
            GlassBorder.Background = System.Windows.SystemColors.WindowBrush;
            GlassBorder.BorderBrush = System.Windows.SystemColors.ActiveBorderBrush;
            BackdropLayer.Background = System.Windows.SystemColors.WindowBrush;
            FirstLabel.Foreground = SecondLabel.Foreground = System.Windows.SystemColors.WindowTextBrush;
            FirstValue.Foreground = SecondValue.Foreground = System.Windows.SystemColors.WindowTextBrush;
            return;
        }

        GlassBorder.Background = normalGlassBackground;
        GlassBorder.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(80, 255, 255, 255));
        BackdropLayer.Background = normalBackdropBackground;
        SetBlurEnabled(true);
        FirstLabel.Foreground = SecondLabel.Foreground = normalLabelForeground;
        FirstValue.Foreground = SecondValue.Foreground = normalValueForeground;
    }

    private void SetBlurEnabled(bool enabled)
    {
        if (blurReady && FrostedBlur.GetEnableBlur(GlassBorder) != enabled)
        {
            FrostedBlur.SetBlurRadius(GlassBorder, 18);
            FrostedBlur.SetMerging(GlassBorder, 0.92);
            FrostedBlur.SetDpi(GlassBorder, 48);
            FrostedBlur.SetEnableBlur(GlassBorder, enabled);
        }
    }

    private static System.Windows.Media.Brush Freeze(string color)
    {
        var brush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
