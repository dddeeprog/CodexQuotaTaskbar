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
    internal bool IsPreviewExpanded { get; private set; }
    internal long PreviewRevision { get; private set; }
    internal long BeginPreviewSelection() => ++PreviewRevision;

    private void InitializePreviewLayout(WorkbenchSettings settings)
    {
        compactSize = new(settings.CompactWidth, settings.CompactHeight);
        expandedSize = new(settings.Width, settings.Height);
        Width = compactSize.Width; Height = compactSize.Height;
        CollapsePreviewButton.Click += (_, _) => CollapsePreview();
        ReopenPreviewButton.Click += (_, _) => { if (PreviewHost.Children.Count > 0) OpenPreview(previewKind); };
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
        ApplyPreviewSize(expandedSize);
    }

    internal void CollapsePreview()
    {
        if (CaptureActive) return;
        PreviewRevision++;
        if (IsPreviewExpanded) expandedSize = new(Width, Height); else compactSize = new(Width, Height);
        IsPreviewExpanded = false;
        PreviewPane.Visibility = PreviewSplitter.Visibility = Visibility.Collapsed;
        PreviewColumn.MinWidth = 0; PreviewColumn.Width = new GridLength(0);
        SplitterColumn.Width = new GridLength(0); SidebarColumn.Width = new GridLength(1, GridUnitType.Star);
        ReopenPreviewButton.Visibility = PreviewHost.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ApplyPreviewSize(compactSize);
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
            CompactWidth = compactSize.Width, CompactHeight = compactSize.Height };
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
            MinWidth = Math.Min(640, right - left); MinHeight = Math.Min(480, bottom - top);
            width = Math.Min(width, right - left); height = Math.Min(height, bottom - top);
            Left = Math.Clamp(Left, left, Math.Max(left, right - width));
            Top = Math.Clamp(Top, top, Math.Max(top, bottom - height));
        }
        Width = width; Height = height;
    }
}
