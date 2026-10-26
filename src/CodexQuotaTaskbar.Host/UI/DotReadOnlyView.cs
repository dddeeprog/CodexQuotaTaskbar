using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CodexQuotaTaskbar.Host.Lifetime;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace CodexQuotaTaskbar.Host.UI;

// Cat mode has a genuinely read-only view: no TextBox, send action, login page
// or keyboard submission path is constructed here.
internal sealed class DotReadOnlyView : System.Windows.Controls.UserControl
{
    internal const double ViewHeightDip = 104;
    internal const double MinimumViewHeightDip = 72;
    private readonly Grid layoutRoot;
    private readonly TextBlock messageText, countText;
    private readonly ScrollViewer messageScroller;
    private readonly Button previousButton, nextButton;
    private readonly Dictionary<string, DateTimeOffset> readRequested = new(StringComparer.Ordinal);
    private DotInlineState? state;
    private DotInlineMessage? displayedMessage;
    private string? selectedMessageId, sessionId;
    private long stateSequence = -1;
    private bool shown;
    private int messageVersion;
    internal event Action<DotInlineAction>? ActionRequested;
    internal event EventHandler? CloseRequested;
    internal event EventHandler? InteractionChanged;
    internal double PreferredHeightDip => Height;
    internal bool IsInteracting => IsMouseOver || IsKeyboardFocusWithin;
    internal string? SelectedMessageId => selectedMessageId;
    internal ScrollViewer MessageScroller => messageScroller;

    internal DotReadOnlyView()
    {
        Height = ViewHeightDip; MinWidth = 160;
        FontFamily = OverlayTypography.Interface; Foreground = Brushes.WhiteSmoke;
        HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch;
        Focusable = false;
        layoutRoot = new Grid { Margin = new Thickness(12, 4, 12, 4), ClipToBounds = true };
        layoutRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
        layoutRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(72) });
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < 3; i++) header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        countText = new TextBlock
        {
            Text = "Dot · 只读", FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(190, 190, 199)),
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        header.Children.Add(countText);
        previousButton = SmallButton("‹", "上一条消息", () => MoveSelection(-1));
        nextButton = SmallButton("›", "下一条消息", () => MoveSelection(1));
        var close = SmallButton("×", "收起消息，保留未读提醒", () => CloseRequested?.Invoke(this, EventArgs.Empty));
        Add(header, previousButton, 1); Add(header, nextButton, 2); Add(header, close, 3);
        layoutRoot.Children.Add(header);
        messageText = new TextBlock
        {
            FontSize = 12, LineHeight = 18, TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.WhiteSmoke, Padding = new Thickness(0, 2, 4, 2),
        };
        messageScroller = new ScrollViewer
        {
            Content = messageText, Height = 72, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false, PanningMode = PanningMode.VerticalOnly, Focusable = true,
            ToolTip = "只读消息 · 可滚轮、方向键或触控滚动；读至末尾才标为已读",
        };
        messageScroller.Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), DotInlineView.CreateScrollBarStyle());
        AutomationProperties.SetName(messageScroller, "Dot 只读消息");
        messageScroller.PreviewMouseLeftButtonDown += (_, _) => messageScroller.Focus();
        messageScroller.ScrollChanged += (_, _) => QueueReadAcknowledgement();
        Grid.SetRow(messageScroller, 1); layoutRoot.Children.Add(messageScroller);
        Content = layoutRoot;
        MouseEnter += (_, _) => InteractionChanged?.Invoke(this, EventArgs.Empty);
        MouseLeave += (_, _) => InteractionChanged?.Invoke(this, EventArgs.Empty);
        IsKeyboardFocusWithinChanged += (_, _) => InteractionChanged?.Invoke(this, EventArgs.Empty);
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            args.Handled = true; CloseRequested?.Invoke(this, EventArgs.Empty);
        };
        Loaded += (_, _) => QueueReadAcknowledgement();
    }

    internal bool SetAvailableHeight(double maximumHeightDip)
    {
        VerifyAccess();
        if (!double.IsFinite(maximumHeightDip) || maximumHeightDip < MinimumViewHeightDip) return false;
        var height = Math.Min(ViewHeightDip, maximumHeightDip);
        Height = height;
        messageScroller.Height = height - 32;
        layoutRoot.RowDefinitions[1].Height = new GridLength(height - 32);
        return true;
    }

    internal void ApplyState(DotInlineState value)
    {
        VerifyAccess(); ArgumentNullException.ThrowIfNull(value);
        if (sessionId == value.SessionId && value.Sequence <= stateSequence) return;
        if (sessionId != value.SessionId || !value.Online) readRequested.Clear();
        sessionId = value.SessionId; stateSequence = value.Sequence; state = value;
        foreach (var id in readRequested.Keys.Where(id => !value.Messages.Any(message => message.Id == id && message.Unread)).ToArray())
            readRequested.Remove(id);
        var selected = value.Messages.FirstOrDefault(message => message.Id == selectedMessageId);
        if (selected is not null) displayedMessage = selected;
        else if (displayedMessage is null)
        {
            displayedMessage = value.Messages.FirstOrDefault(message => message.Unread) ?? value.Messages.LastOrDefault();
            selectedMessageId = displayedMessage?.Id;
        }
        RenderMessage(); QueueReadAcknowledgement();
    }

    // Only the explicit marker/notice click calls Shown. Automatic rows never do.
    internal void Shown()
    {
        VerifyAccess(); shown = true;
        if (displayedMessage?.Unread != true && state?.Messages.FirstOrDefault(message => message.Unread) is { } unread)
            Select(unread);
        QueueReadAcknowledgement();
    }

    internal void Hidden()
    {
        VerifyAccess(); shown = false;
        if (IsKeyboardFocusWithin) Keyboard.ClearFocus();
    }

    internal void SetOffline()
    {
        VerifyAccess();
        if (state is { } current) state = current with { Online = false };
        readRequested.Clear(); RenderMessage();
    }

    private void MoveSelection(int direction)
    {
        if (state is null || state.Messages.Length == 0) return;
        var index = Array.FindIndex(state.Messages, message => message.Id == selectedMessageId);
        if (index < 0) index = direction > 0 ? -1 : state.Messages.Length;
        var target = index + direction;
        if (target >= 0 && target < state.Messages.Length) Select(state.Messages[target]);
    }

    private void Select(DotInlineMessage message)
    {
        displayedMessage = message; selectedMessageId = message.Id;
        messageVersion++; RenderMessage(); messageScroller.ScrollToTop(); QueueReadAcknowledgement();
    }

    private void RenderMessage()
    {
        var text = displayedMessage?.Text ?? (state?.Online == false ? "Dot 连接中断，未读记录仍保留。" : "暂时没有消息。");
        if (messageText.Text != text) { messageText.Text = text; messageVersion++; }
        var messages = state?.Messages ?? [];
        var index = Array.FindIndex(messages, message => message.Id == selectedMessageId);
        previousButton.IsEnabled = index > 0 || index < 0 && messages.Length > 0;
        nextButton.IsEnabled = index >= 0 && index < messages.Length - 1 || index < 0 && messages.Length > 0;
        countText.Text = state?.UnreadCount > 0 ? $"Dot · {state.UnreadCount} 未读 · 只读" : "Dot · 只读";
        var hint = state?.EarlierUnreadCount > 0
            ? $"保留 {state.EarlierUnreadCount} 条较早未读计数；此处只显示最近消息，不会自动清除较早未读。"
            : "只读模式不提供回复；只有实际阅读的消息才标为已读。";
        if (state is { } current && (current.DraftText.Length != 0 || current.HasPendingAttempt || current.Busy || current.NeedsReview))
            hint += " 草稿和发送状态已保留，切换为小白球可继续处理。";
        countText.ToolTip = hint;
    }

    private void QueueReadAcknowledgement()
    {
        if (!shown || displayedMessage is not { Unread: true }) return;
        var id = displayedMessage.Id; var version = messageVersion;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!shown || !IsVisible || state?.Online != true || displayedMessage?.Id != id || messageVersion != version
                || messageScroller.ActualHeight <= 0
                || readRequested.TryGetValue(id, out var requestedAt) && DateTimeOffset.UtcNow - requestedAt < TimeSpan.FromSeconds(3)) return;
            messageScroller.UpdateLayout();
            if (messageScroller.ScrollableHeight > .5 && messageScroller.VerticalOffset < messageScroller.ScrollableHeight - .5) return;
            readRequested[id] = DateTimeOffset.UtcNow;
            ActionRequested?.Invoke(new("read", MessageId: id));
        }));
    }

    private static void Add(Grid grid, UIElement child, int column)
    { Grid.SetColumn(child, column); grid.Children.Add(child); }

    private static Button SmallButton(string text, string name, Action action)
    {
        var button = new Button
        {
            Content = text, ToolTip = name, Width = 24, Height = 24, FontSize = 16,
            Padding = new Thickness(0), Background = Brushes.Transparent, Foreground = Brushes.WhiteSmoke,
            BorderThickness = new Thickness(0), Focusable = false, Cursor = System.Windows.Input.Cursors.Hand,
        };
        AutomationProperties.SetName(button, name);
        var surface = new FrameworkElementFactory(typeof(Border));
        surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        surface.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        surface.AppendChild(presenter);
        button.Template = new ControlTemplate(typeof(Button)) { VisualTree = surface };
        button.Click += (_, args) => { args.Handled = true; action(); };
        return button;
    }
}
