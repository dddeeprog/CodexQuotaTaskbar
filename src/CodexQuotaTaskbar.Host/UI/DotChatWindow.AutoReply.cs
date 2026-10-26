using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace CodexQuotaTaskbar.Host.UI;

public partial class DotChatWindow
{
    private readonly DispatcherTimer autoReplyTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private TimeSpan? autoReplyDeadline;
    private bool autoReplyAwaitingPresentation;

    private void InitializeAutoReply()
    {
        autoReplyTimer.Tick += (_, _) => CheckAutoReplyTimeout(opened.Elapsed);
        Composer.MouseEnter += (_, _) => CancelAutoReply();
        Composer.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((_, _) => CancelAutoReply()), true);
        Composer.AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler((_, _) => CancelAutoReply()), true);
        Composer.IsKeyboardFocusWithinChanged += (_, _) => { if (Composer.IsKeyboardFocusWithin) CancelAutoReply(); };
    }

    private void CancelAutoReply()
    {
        autoReplyTimer.Stop();
        autoReplyDeadline = null;
        autoReplyAwaitingPresentation = false;
    }

    private void ExpandReplyForNewMessage()
    {
        if (inlineMode) { QueueInlineState(); return; }
        if (closed) return;
        var manuallyOpen = Composer.Visibility == Visibility.Visible && autoReplyDeadline is null && !autoReplyAwaitingPresentation;
        Composer.Visibility = Visibility.Visible;
        MessageScroller.MaxHeight = 160;
        Present(); UpdateLayout(); Reposition();
        // Automatic presentation never activates a window, focuses input, or changes its contents.
        if (manuallyOpen || Input.Text.Length != 0 || composing || Composer.IsKeyboardFocusWithin || Composer.IsMouseOver)
        {
            CancelAutoReply();
            return;
        }
        autoReplyTimer.Stop();
        if (!IsVisible)
        {
            autoReplyDeadline = null;
            autoReplyAwaitingPresentation = true;
            return;
        }
        autoReplyAwaitingPresentation = false;
        autoReplyDeadline = opened.Elapsed + TimeSpan.FromSeconds(10);
        autoReplyTimer.Interval = TimeSpan.FromSeconds(10);
        autoReplyTimer.Start();
    }

    internal void CheckAutoReplyTimeout(TimeSpan elapsed)
    {
        if (autoReplyDeadline is not { } deadline) return;
        if (elapsed < deadline)
        {
            // An already queued tick from the previous reply cannot close a newer reply's panel.
            autoReplyTimer.Stop();
            autoReplyTimer.Interval = deadline - elapsed;
            autoReplyTimer.Start();
            return;
        }
        CancelAutoReply();
        if (closed || composing || Input.Text.Length != 0 || Composer.IsKeyboardFocusWithin || Composer.IsMouseOver) return;
        Composer.Visibility = Visibility.Collapsed;
        MessageScroller.MaxHeight = 112;
        UpdateLayout(); Reposition();
        // Keep the notification bubble, draft, send attempt, and background connection intact.
        WriteDiagnostics(force: true);
    }
}
