using System.Windows.Threading;
using CodexQuotaTaskbar.Core.Overlay;
using CodexQuotaTaskbar.Host.Bongo;
using CodexQuotaTaskbar.Host.Platform;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Overlay;

internal sealed partial class OverlayCoordinator
{
    private readonly Func<BongoRuntime> createBongoRuntime;
    private readonly SemaphoreSlim bongoTransition = new(1, 1);
    private readonly object bongoFrameGate = new();
    private readonly HashSet<string> bongoFits = new(StringComparer.Ordinal);
    private BongoRuntime? bongoRuntime;
    private BongoFrame? pendingBongoFrame;
    private long pendingBongoGeneration;
    private bool bongoFramePosted;
    private bool bongoSelected;
    private bool bongoFailed;
    private bool bongoStopping;
    private string? bongoFailureReason;
    private int bongoSize = 192;
    private long bongoGeneration;
    private BongoTarget? desiredBongoTarget;
    private Task bongoTransitionTask = Task.CompletedTask;
    private long bongoFramesPresented;
    private long lastBongoFramesProduced;
    private bool lastBongoExitConfirmed = true;
    private sealed record BongoTarget(int Width, int Height, bool Static);

    internal void SetCompanionAppearance(string appearance, int sizeDip)
        => SetBongoCatVisibility(string.Equals(appearance, "bongo-cat", StringComparison.Ordinal), sizeDip);

    internal void SetBongoCatVisibility(bool visible, int sizeDip)
    {
        var selected = visible;
        var size = Math.Clamp(sizeDip, 140, 240);
        if (bongoSelected == selected && bongoSize == size) return;
        bongoSelected = selected; bongoSize = size; bongoFailed = false; bongoFailureReason = null;
        RefreshBongoSurfaces();
        PublishDotReplyAvailability(); RefreshInlineDotPresentation();
    }

    private bool BongoEligible(QuotaCapsuleWindow owner) => !disposed && !bongoStopping && bongoSelected
        && !bongoFailed && !sessionLocked && owner.IsVisible && bongoFits.Contains(owner.MonitorId);

    private void RefreshBongoSurfaces()
    {
        var eligible = new List<QuotaCapsuleWindow>();
        bongoFits.Clear();
        foreach (var owner in windows.Values)
        {
            var dpi = DpiFor(owner); var scale = dpi / 96d;
            var extraHeight = 8 + bongoSize / BongoCompanionPlacement.ModelAspect;
            var baseReserved = owner.CoreBodyReservedHeight + FooterReservation(owner)
                - (owner.BongoHasFrame ? 8 + owner.BongoModelSizeDip.Height : 0);
            var work = WorkAreaFor(owner);
            var stackHeight = !owner.IsUnifiedPanel && sessions.Sessions.Count > 0 ? ReservedSessionHeight(sessions.Sessions.Count) : 0;
            var fits = BongoGroupFits(work, dpi, owner.LayoutWidth, baseReserved, extraHeight,
                stackHeight, stackHeight > 0 ? SessionStackWindow.StackWidth : 0);
            if (fits) bongoFits.Add(owner.MonitorId);
            owner.ConfigureBongoSurface(bongoSelected && !bongoFailed && !bongoStopping && fits, bongoSize);
            owner.SetCompanionFailure(bongoSelected && bongoFailed ? bongoFailureReason : null);
            if (BongoEligible(owner)) eligible.Add(owner);
            else owner.ClearBongoFrame();
        }
        BongoTarget? target = null;
        if (eligible.Count > 0)
        {
            var width = eligible.Max(owner =>
            {
                var dpi = NativeMethods.GetDpiForWindow(new System.Windows.Interop.WindowInteropHelper(owner).Handle);
                return (int)Math.Ceiling(owner.BongoModelSizeDip.Width * (dpi > 0 ? dpi : 96) / 96d);
            });
            width = Math.Clamp(width, 64, 1024);
            target = new(width, Math.Clamp((int)Math.Ceiling(width / BongoCompanionPlacement.ModelAspect), 64, 1024),
                companionReduced || !System.Windows.SystemParameters.ClientAreaAnimation);
        }
        var needsStaticFrame = target?.Static == true && eligible.Any(owner => !owner.BongoHasFrame)
            && bongoTransitionTask.IsCompleted && bongoRuntime?.ExitConfirmed == true;
        if (target != desiredBongoTarget || needsStaticFrame)
        {
            desiredBongoTarget = target;
            var version = Interlocked.Increment(ref bongoGeneration);
            ClearPendingBongoFrame();
            bongoTransitionTask = TransitionBongoAsync(version);
        }
        PublishTypingAvailability();
    }

    internal static bool BongoGroupFits(ScreenRect work, int dpi, double ownerWidthDip, double baseHeightDip,
        double stageHeightDip, double classicStackHeightDip = 0, double classicStackWidthDip = 0)
    {
        var values = new[] { ownerWidthDip, baseHeightDip, stageHeightDip, classicStackHeightDip, classicStackWidthDip };
        if (values.Any(value => !double.IsFinite(value) || value < 0) || ownerWidthDip == 0 || baseHeightDip == 0
            || work.Width <= 0 || work.Height <= 8) return false;
        var scale = dpi is >= 48 and <= 768 ? dpi / 96d : 1;
        var fullHeight = baseHeightDip + stageHeightDip + (classicStackHeightDip > 0 ? Math.Max(0, classicStackHeightDip - 16) : 0);
        return Math.Max(ownerWidthDip, classicStackWidthDip) * scale <= work.Width && fullHeight * scale <= work.Height - 8;
    }

    // The model lives in the owner's visual tree. There is no secondary HWND.
    private void UpdateBongoPlacement(QuotaCapsuleWindow owner, TaskbarAnchor? knownAnchor = null) { }

    private async Task TransitionBongoAsync(long version)
    {
        await bongoTransition.WaitAsync();
        try
        {
            if (disposed || version != Volatile.Read(ref bongoGeneration)) return;
            if (bongoRuntime is not null)
            {
                await bongoRuntime.DisposeAsync();
                lastBongoFramesProduced = bongoRuntime.FramesProduced;
                lastBongoExitConfirmed = bongoRuntime.ExitConfirmed;
                if (!bongoRuntime.ExitConfirmed)
                {
                    bongoFailed = true; bongoFailureReason = "BongoCat 退出尚未确认，请稍后重新选择外观";
                    OnOwnerThread(RefreshBongoSurfaces); return;
                }
                bongoRuntime = null;
            }
            if (disposed || version != Volatile.Read(ref bongoGeneration) || desiredBongoTarget is not { } target) return;
            var runtime = createBongoRuntime(); bongoRuntime = runtime;
            runtime.FrameAvailable += frame => QueueBongoFrame(frame, version);
            runtime.Failed += reason => OnOwnerThread(() =>
            {
                if (version != Volatile.Read(ref bongoGeneration)) return;
                bongoFailed = true; bongoFailureReason = reason; RefreshBongoSurfaces(); PublishDotAnchor();
            });
            _ = runtime.StartAsync(target.Width, target.Height, target.Static);
        }
        finally { bongoTransition.Release(); }
    }

    private void QueueBongoFrame(BongoFrame frame, long generation)
    {
        bool schedule;
        lock (bongoFrameGate)
        {
            if (disposed || generation != Volatile.Read(ref bongoGeneration)) { frame.Dispose(); return; }
            pendingBongoFrame?.Dispose(); pendingBongoFrame = frame; pendingBongoGeneration = generation;
            schedule = !bongoFramePosted; bongoFramePosted = true;
        }
        if (!schedule) return;
        try { ownerDispatcher.BeginInvoke(DispatcherPriority.Render, (Action)PresentBongoFrame); }
        catch (InvalidOperationException) { ClearPendingBongoFrame(); }
    }

    private void PresentBongoFrame()
    {
        BongoFrame? frame;
        long generation;
        lock (bongoFrameGate) { frame = pendingBongoFrame; generation = pendingBongoGeneration; pendingBongoFrame = null; bongoFramePosted = false; }
        if (frame is null) return;
        try
        {
            if (disposed || generation != Volatile.Read(ref bongoGeneration)) return;
            foreach (var owner in windows.Values.Where(BongoEligible)) owner.PresentBongoFrame(frame.Clone());
            bongoFramesPresented++;
        }
        finally { frame.Dispose(); }
    }

    private void ClearPendingBongoFrame()
    {
        lock (bongoFrameGate) { pendingBongoFrame?.Dispose(); pendingBongoFrame = null; bongoFramePosted = false; }
    }

    private void InvokeCompanion(QuotaCapsuleWindow owner)
    {
        dotOwnerId = owner.MonitorId; CloseContextRequested?.Invoke(this, EventArgs.Empty);
        popover?.Close(); PublishDotAnchor(); OpenDotChatRequested?.Invoke(owner, EventArgs.Empty);
    }

    internal async Task StopBongoAsync()
    {
        bongoStopping = true; PublishTypingAvailability();
        desiredBongoTarget = null; Interlocked.Increment(ref bongoGeneration); ClearPendingBongoFrame();
        await bongoTransitionTask; await bongoTransition.WaitAsync();
        try
        {
            if (bongoRuntime is not null)
            {
                await bongoRuntime.DisposeAsync();
                lastBongoFramesProduced = bongoRuntime.FramesProduced;
                lastBongoExitConfirmed = bongoRuntime.ExitConfirmed;
                if (bongoRuntime.ExitConfirmed) bongoRuntime = null;
            }
        }
        finally { bongoTransition.Release(); }
    }

    private void DisposeBongo() { CloseBongoViews(); _ = StopBongoAsync(); }
    private void CloseBongoViews()
    {
        foreach (var owner in windows.Values) { owner.ClearBongoFrame(); owner.ConfigureBongoSurface(false, bongoSize); }
    }
    private void RemoveBongoView(string monitorId)
    {
        if (windows.TryGetValue(monitorId, out var owner)) { owner.ClearBongoFrame(); owner.ConfigureBongoSurface(false, bongoSize); }
    }

    internal object BongoProofState() => new
    {
        selected = bongoSelected, failed = bongoFailed, typingEnabled = companionTyping,
        viewCount = windows.Values.Count(owner => owner.BongoHasFrame),
        visibleViews = windows.Values.Count(owner => owner.BongoVisible),
        readyViews = windows.Values.Count(owner => owner.BongoHasFrame), framesPresented = bongoFramesPresented,
        framesProduced = bongoRuntime?.FramesProduced ?? lastBongoFramesProduced,
        activeHelperPid = bongoRuntime?.ActiveProcessId, helperExitConfirmed = bongoRuntime?.ExitConfirmed ?? lastBongoExitConfirmed,
        sizes = windows.Values.Where(owner => owner.BongoHasFrame).Select(owner => owner.BongoModelBounds).ToArray(),
        reduced = companionReduced, pointerObservationEnabled = observePointer, sharedOwnerWindow = true,
        surfaces = windows.Values.Where(owner => owner.BongoHasFrame).Select(owner => new
        {
            visible = owner.IsVisible, transparent = owner.AllowsTransparency, showActivated = owner.ShowActivated,
            bounds = owner.PhysicalBounds, modelBounds = owner.BongoModelBounds,
            extendedStyle = owner.IsVisible ? NativeMethods.GetWindowLongPtr(
                new System.Windows.Interop.WindowInteropHelper(owner).Handle, NativeMethods.GwlExStyle).ToInt64() : 0,
        }).ToArray(),
    };
}
