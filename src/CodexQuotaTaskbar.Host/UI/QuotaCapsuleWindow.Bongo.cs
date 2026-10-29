using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexQuotaTaskbar.Core.Overlay;
using CodexQuotaTaskbar.Host.Bongo;
using Brushes = System.Windows.Media.Brushes;
using Size = System.Windows.Size;

namespace CodexQuotaTaskbar.Host.UI;

public partial class QuotaCapsuleWindow
{
    private bool bongoConfigured;
    private bool bongoWindowClosed;
    private bool bongoWindowClosing;
    private int bongoSizeDip = (int)BongoCompanionPlacement.DefaultWidthDip;
    private BongoIslandSurface? bongoSurface;
    private WriteableBitmap? bongoBitmap;

    internal void HandleHiddenIsland()
    {
        if (IsVisible) return;
        ClearResetStatusPointer();
        CancelPointerGesture("hidden");
        FinishExtensionTransitions();
        // The renderer still stops while hidden. Retain only its last bitmap and
        // visual tree so restoring the owner does not collapse/reinsert the cat.
        // Disable, lock, failure and Close continue to release this cache.
    }

    internal bool BongoHasFrame => bongoBitmap is not null && bongoSurface?.HasModel == true;
    internal bool BongoVisible => IsVisible && BongoHasFrame && bongoSurface?.Visibility == Visibility.Visible;
    internal Size BongoModelSizeDip => new(bongoSizeDip, bongoSizeDip / BongoCompanionPlacement.ModelAspect);
    private double BongoStageHeight => BongoHasFrame ? bongoSurface!.StageHeightDip : 0;

    // Physical geometry is derived from the owner's applied bounds, including when
    // dragging between monitors, instead of another HWND's asynchronously sampled position.
    internal Rect BongoModelBoundsDip => !BongoHasFrame ? Rect.Empty : new Rect(
        GlassBorder.Margin.Left + GlassBorder.BorderThickness.Left + bongoSurface!.ModelBoundsDip.Left,
        GlassBorder.Margin.Top + GlassBorder.BorderThickness.Top + DotTopExtensionHeight + bongoSurface.ModelBoundsDip.Top,
        bongoSurface.ModelBoundsDip.Width, bongoSurface.ModelBoundsDip.Height);

    internal ScreenRect BongoModelBounds
    {
        get
        {
            if (!BongoHasFrame) return default;
            var scale = physicalBounds.Width > 0 ? physicalBounds.Width / LayoutWidth : 1;
            var model = bongoSurface!.ModelBoundsDip;
            var left = physicalBounds.Left + (int)Math.Round((GlassBorder.Margin.Left + GlassBorder.BorderThickness.Left + model.Left) * scale);
            var top = physicalBounds.Top + (int)Math.Round((GlassBorder.Margin.Top + GlassBorder.BorderThickness.Top + DotTopExtensionHeight + model.Top) * scale);
            return new(left, top, left + (int)Math.Round(model.Width * scale), top + (int)Math.Round(model.Height * scale));
        }
    }

    internal void ConfigureBongoSurface(bool enabled, int sizeDip)
    {
        VerifyAccess();
        if (bongoWindowClosed) return;
        var size = Math.Clamp(sizeDip, (int)BongoCompanionPlacement.MinimumWidthDip, (int)BongoCompanionPlacement.MaximumWidthDip);
        var sizeChanged = bongoSizeDip != size;
        bongoConfigured = enabled;
        bongoSizeDip = size;
        if (!enabled) { ClearBongoFrame(); return; }
        if (!sizeChanged || bongoSurface is null) return;
        bongoSurface.SetModelWidth(size);
        RefreshBongoBodyLayout();
        RefreshDotMarkerPlacement();
        PresentationChanged?.Invoke(this, EventArgs.Empty);
    }

    // The coordinator gives this surface one frame owner. The bitmap is reused;
    // pooled native bytes are always returned before this call completes.
    internal void PresentBongoFrame(BongoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        VerifyAccess();
        try
        {
            if (!bongoConfigured || bongoWindowClosed) return;
            var required = (long)frame.Width * frame.Height * 4;
            if (frame.Width is < 1 or > 1024 || frame.Height is < 1 or > 1024
                || frame.ByteCount != required || frame.Pixels.Length < required) { ClearBongoFrame(); return; }
            var hadFrame = BongoHasFrame;
            if (bongoBitmap is null || bongoBitmap.PixelWidth != frame.Width || bongoBitmap.PixelHeight != frame.Height)
                bongoBitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Pbgra32, null);
            bongoBitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Width * 4, 0);
            if (bongoSurface is null)
            {
                var body = DetachQuotaBodyVisual();
                try
                {
                    bongoSurface = new BongoIslandSurface(body, bongoSizeDip, BongoQuotaBodyHeight,
                        LayoutWidth - GlassBorder.Margin.Left - GlassBorder.Margin.Right - GlassBorder.BorderThickness.Left - GlassBorder.BorderThickness.Right)
                    { Background = Brushes.Transparent };
                    SetQuotaBodyVisual(bongoSurface);
                }
                catch
                {
                    if (VisualTreeHelper.GetParent(body) is System.Windows.Controls.Panel parent) parent.Children.Remove(body);
                    SetQuotaBodyVisual(body);
                    throw;
                }
            }
            bongoSurface.Present(bongoBitmap);
            SetExternalCompanion(true);
            UpdateCompanionSpacing();
            RestoreStatusBadge();
            RefreshBongoBodyLayout();
            if (!hadFrame)
            {
                RefreshDotMarkerPlacement();
                PresentationChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            ClearBongoFrame();
        }
        finally { frame.Dispose(); }
    }

    internal void ClearBongoFrame()
    {
        VerifyAccess();
        var hadFrame = BongoHasFrame;
        bongoBitmap = null;
        if (bongoSurface is not null)
        {
            _ = DetachQuotaBodyVisual();
            var body = bongoSurface.DetachQuotaBody();
            SetQuotaBodyVisual(body);
            bongoSurface = null;
        }
        SetExternalCompanion(false);
        UpdateCompanionSpacing();
        RestoreStatusBadge();
        if (bongoWindowClosed || bongoWindowClosing) return;
        RefreshStandaloneWindowHeight();
        RefreshDotMarkerPlacement();
        if (hadFrame) PresentationChanged?.Invoke(this, EventArgs.Empty);
    }

    private double BongoQuotaBodyHeight => Math.Max(48,
        (QuotaBaseHeight + (IsUnifiedPanel ? EffectivePanelBodyHeight + ResetScheduleHeight : 0))
        - GlassBorder.Margin.Top - GlassBorder.Margin.Bottom - GlassBorder.BorderThickness.Top - GlassBorder.BorderThickness.Bottom);

    private void RefreshBongoBodyLayout()
    {
        if (bongoSurface is null) return;
        bongoSurface.SetBodyHeight(BongoQuotaBodyHeight);
        RefreshStandaloneWindowHeight();
    }

    private void CloseBongoSurface()
    {
        bongoWindowClosed = true;
        bongoConfigured = false;
        ClearBongoFrame();
    }
}
