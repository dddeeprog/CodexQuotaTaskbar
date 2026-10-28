using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using Size = System.Windows.Size;

namespace CodexQuotaTaskbar.Host.Workbench;

internal partial class WorkbenchWindow
{
    private Size compactSize;
    private Size expandedSize;
    private string previewKind = "预览";
    private bool fittingCompact;
    internal bool IsPreviewExpanded { get; private set; }
    internal long PreviewRevision { get; private set; }
    internal long BeginPreviewSelection() => ++PreviewRevision;
    internal event Action? PreviewLayoutChanged;

    private void InitializePreviewLayout(WorkbenchSettings settings)
    {
        compactSize = new(settings.CompactWidth, settings.CompactHeight);
        expandedSize = new(settings.Width, settings.Height);
        Width = compactSize.Width; Height = compactSize.Height;
        CollapsePreviewButton.Click += (_, _) => CollapsePreview();
        ReopenPreviewButton.Click += (_, _) => { if (PreviewHost.Children.Count > 0) OpenPreview(previewKind); };
        UpdatePreviewChrome(); FitCompactContent();
    }

    internal void OpenPreview(string kind)
    {
        if (CaptureActive) return;
        previewKind = kind;
        CollapsePreviewButton.Content = "收起" + kind;
        ReopenPreviewButton.Content = "展开" + kind;
        AutomationProperties.SetName(CollapsePreviewButton, "收起文件" + kind);
        AutomationProperties.SetName(ReopenPreviewButton, "重新展开当前文件" + kind);
        if (IsPreviewExpanded) return;
        compactSize = new(Width, Height); IsPreviewExpanded = true;
        SidebarColumn.Width = new GridLength(280);
        SplitterColumn.Width = new GridLength(5);
        PreviewColumn.MinWidth = 300; PreviewColumn.Width = new GridLength(1, GridUnitType.Star);
        PreviewPane.Visibility = PreviewSplitter.Visibility = Visibility.Visible;
        ReopenPreviewButton.Visibility = Visibility.Collapsed;
        UpdatePreviewChrome();
        ApplyPreviewSize(expandedSize);
    }

    internal void CollapsePreview()
    {
        if (CaptureActive) return;
        PreviewRevision++;
        if (IsPreviewExpanded) expandedSize = new(Width, Height); else compactSize = new(Math.Clamp(Width, 360, 420), Height);
        IsPreviewExpanded = false;
        PreviewPane.Visibility = PreviewSplitter.Visibility = Visibility.Collapsed;
        PreviewColumn.MinWidth = 0; PreviewColumn.Width = new GridLength(0);
        SplitterColumn.Width = new GridLength(0); SidebarColumn.Width = new GridLength(1, GridUnitType.Star);
        ReopenPreviewButton.Visibility = PreviewHost.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdatePreviewChrome(); ApplyPreviewSize(compactSize); FitCompactContent();
    }

    internal void ResetPreview()
    {
        if (CaptureActive) return;
        PreviewHost.Children.Clear(); CollapsePreview();
    }

    internal WorkbenchSettings CaptureLayout(WorkbenchSettings settings)
    {
        if (IsPreviewExpanded) expandedSize = new(Width, Height); else compactSize = new(Width, Height);
        return settings with { Width = expandedSize.Width, Height = expandedSize.Height,
            CompactWidth = compactSize.Width, CompactHeight = compactSize.Height, CompactLayoutVersion = 2 };
    }

    private void UpdatePreviewChrome()
    {
        MaxWidth = MaxHeight = double.PositiveInfinity;
        MinWidth = IsPreviewExpanded ? 640 : 360; MinHeight = IsPreviewExpanded ? 480 : 200;
        if (!IsPreviewExpanded) { MaxWidth = 420; MaxHeight = 440; }
        SidebarHost.MaxHeight = IsPreviewExpanded ? double.PositiveInfinity : 240;
        var padding = IsPreviewExpanded ? 7 : 3;
        Resources["WorkbenchRowPadding"] = new Thickness(8, padding, 8, padding);
        Resources["WorkbenchTreeRowHeight"] = IsPreviewExpanded ? 32d : 28d;
        ShellTitle.Text = IsPreviewExpanded ? "点岛 · 项目工作台" : "工作台";
        PinButton.Content = IsPreviewExpanded ? (Pinned ? "置顶：开" : "置顶：关") : (Pinned ? "已置顶" : "置顶");
        PinButton.ToolTip = Pinned ? "置顶已开启；点击后关闭置顶" : "置顶已关闭；点击后保持显示";
        ChooseDirectoryButton.Content = IsPreviewExpanded ? "打开文件夹…" : "目录…";
        ChooseRepositoryButton.Content = IsPreviewExpanded ? "选择 Git 仓库…" : "仓库…";
        PreviewLayoutChanged?.Invoke();
    }

    internal void FitCompactContent()
    {
        if (IsPreviewExpanded || CaptureActive || fittingCompact) return;
        fittingCompact = true;
        try
        {
            var width = Math.Clamp(Width, 360, 420);
            // The sidebar is bounded before measuring: long lists retain scrolling instead of measuring every row.
            for (DependencyObject? node = SidebarHost; node is not null; node = VisualTreeHelper.GetParent(node))
            {
                if (node is UIElement element) element.InvalidateMeasure();
                if (node == LayoutRoot) break;
            }
            LayoutRoot.Measure(new Size(width, double.PositiveInfinity));
            compactSize = new(width, Math.Clamp(Math.Ceiling(LayoutRoot.DesiredSize.Height), 200, 440));
            ApplyPreviewSize(compactSize);
        }
        finally { fittingCompact = false; }
    }

    internal static Rect ConstrainPreviewBounds(Rect bounds, Rect area)
    {
        var width = Math.Min(bounds.Width, area.Width); var height = Math.Min(bounds.Height, area.Height);
        return new(Math.Clamp(bounds.Left, area.Left, Math.Max(area.Left, area.Right - width)),
            Math.Clamp(bounds.Top, area.Top, Math.Max(area.Top, area.Bottom - height)), width, height);
    }

    private void ApplyPreviewSize(Size size)
    {
        var width = size.Width; var height = size.Height;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var area = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
            var left = area.Left / dpi.DpiScaleX; var top = area.Top / dpi.DpiScaleY;
            var right = area.Right / dpi.DpiScaleX; var bottom = area.Bottom / dpi.DpiScaleY;
            MinWidth = Math.Min(IsPreviewExpanded ? 640 : 360, right - left); MinHeight = Math.Min(IsPreviewExpanded ? 480 : 200, bottom - top);
            var fitted = ConstrainPreviewBounds(new Rect(double.IsFinite(Left) ? Left : left, double.IsFinite(Top) ? Top : top, width, height), new Rect(left, top, right - left, bottom - top));
            width = fitted.Width; height = fitted.Height; Left = fitted.Left; Top = fitted.Top;
        }
        Width = width; Height = height;
    }
}
