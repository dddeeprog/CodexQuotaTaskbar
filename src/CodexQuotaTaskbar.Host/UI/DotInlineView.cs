using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CodexQuotaTaskbar.Host.Lifetime;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace CodexQuotaTaskbar.Host.UI;

// One view instance moves between owners. Visibility never owns the draft or a send attempt.
internal sealed class DotInlineView : System.Windows.Controls.UserControl
{
    internal const double ViewHeightDip = 232;
    internal const double MinimumViewHeightDip = 114;
    private const double InputLineHeightDip = 18;
    private const double MinimumInputHeightDip = InputLineHeightDip * 2;
    private const double MaximumInputHeightDip = InputLineHeightDip * 4;
    private const double MaximumMessageHeightDip = 72;
    private readonly Grid layoutRoot;
    private readonly TextBox input;
    private readonly Border inputBorder;
    private readonly TextBlock inputMeasure;
    private readonly TextBlock messageText, countText, statusText;
    private readonly ScrollViewer messageScroller;
    private readonly Button previousButton, nextButton, sendButton, pageButton, earlierButton;
    private readonly Grid reviewActions;
    private readonly Dictionary<string, DateTimeOffset> readRequested = new(StringComparer.Ordinal);
    private DotInlineState? state;
    private DotInlineMessage? displayedMessage;
    private string? selectedMessageId;
    private string? sessionId;
    private long stateSequence = -1, draftRevision;
    private bool applyingDraft, shown, composing, imeEnterDown, hasEdited;
    private bool sendOutstanding, sendObserved;
    private int compositionGeneration, messageVersion;
    private long sendRevision;
    private string? sendRequestId;
    private int earlierReviewBudget;
    private bool earlierPageObserved;
    private double adaptiveWidth = 224, availableHeight = ViewHeightDip;
    private double minimumRequiredHeight = MinimumViewHeightDip;
    private bool updatingAdaptiveLayout, applyingState, adaptiveLayoutPosted;
    private string? measuredMessage, measuredInput;
    private double measuredMessageWidth = -1, measuredInputWidth = -1, naturalMessageHeight, naturalInputHeight;

    internal event Action<DotInlineAction>? ActionRequested;
    internal event EventHandler? CloseRequested;
    internal event EventHandler? InteractionChanged;
    internal event EventHandler? DraftChanged;
    internal event EventHandler? PreferredHeightChanged;
    internal double PreferredHeightDip => Height;
    internal double MinimumRequiredHeightDip => minimumRequiredHeight;
    internal double MessageViewportHeightDip => messageScroller.Height;
    internal double InputViewportHeightDip => input.Height;
    internal bool IsInteracting => IsMouseOver || IsKeyboardFocusWithin || composing;
    internal bool HasLocalDraft => input.Text.Length != 0;
    internal string DraftText => input.Text;
    internal long ClientDraftRevision => draftRevision;
    internal string? SelectedMessageId => selectedMessageId;
    internal TextBox InputControl => input;
    internal ScrollViewer MessageScroller => messageScroller;
    internal Button SendControl => sendButton;
    internal bool IsComposing => composing;

    internal DotInlineView()
    {
        FontFamily = OverlayTypography.Interface;
        Foreground = Brushes.WhiteSmoke;
        Height = ViewHeightDip;
        MinWidth = 160;
        HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch;
        var root = layoutRoot = new Grid { Margin = new Thickness(0, 2, 0, 2), ClipToBounds = true };
        foreach (var height in new[] { 26d, 0, 0, 52, 6, 26, 16 })
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(height) });
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < 4; i++) header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = Label("Dot", 12, FontWeights.SemiBold);
        countText = Label("", 10, FontWeights.Normal);
        countText.Foreground = Brush("#B8B8BE");
        countText.Margin = new Thickness(3, 0, 5, 0);
        previousButton = SmallButton("‹", "上一条消息", () => MoveSelection(-1));
        nextButton = SmallButton("›", "下一条消息", () => MoveSelection(1));
        var close = SmallButton("×", "收起回复框，保留草稿和接收", () => CloseRequested?.Invoke(this, EventArgs.Empty));
        header.Children.Add(label);
        Add(header, countText, 1); Add(header, previousButton, 2); Add(header, nextButton, 3); Add(header, close, 4);
        root.Children.Add(header);

        messageText = new TextBlock
        {
            Text = "正在连接 Dot…", FontSize = 12, LineHeight = 18, TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("#EEEEF2"), Padding = new Thickness(1, 2, 4, 2),
        };
        messageScroller = new ScrollViewer
        {
            Content = messageText, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = true,
            CanContentScroll = false, Height = 0, Visibility = Visibility.Collapsed, PanningMode = PanningMode.VerticalOnly,
        };
        messageScroller.Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), CreateScrollBarStyle());
        AutomationProperties.SetName(messageScroller, "Dot 消息，长消息可滚动阅读");
        messageScroller.ToolTip = "可用滚轮、方向键或触控滚动；读至末尾才标为已读";
        messageScroller.PreviewMouseLeftButtonDown += (_, _) => { if (!composing) messageScroller.Focus(); };
        messageScroller.ScrollChanged += (_, _) => QueueReadAcknowledgement();
        Grid.SetRow(messageScroller, 1); root.Children.Add(messageScroller);

        input = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 8000,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = Brushes.WhiteSmoke, CaretBrush = Brushes.White,
            Padding = new Thickness(0), FontFamily = OverlayTypography.Interface, FontSize = 12, MinHeight = MinimumInputHeightDip,
            Height = MinimumInputHeightDip, MaxHeight = MaximumInputHeightDip, MinLines = 2, MaxLines = 4,
        };
        TextBlock.SetLineHeight(input, InputLineHeightDip);
        TextBlock.SetLineStackingStrategy(input, LineStackingStrategy.BlockLineHeight);
        inputMeasure = new TextBlock { FontFamily = input.FontFamily, FontSize = input.FontSize,
            TextWrapping = TextWrapping.Wrap, LineHeight = InputLineHeightDip,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
        AutomationProperties.SetName(input, "回复 Dot");
        input.ToolTip = "Enter 发送，Shift+Enter 换行；选字时 Enter 只确认输入";
        input.Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), CreateScrollBarStyle());
        inputBorder = new Border
        {
            CornerRadius = new CornerRadius(9), Background = Brush("#18000000"),
            BorderBrush = Brush("#35FFFFFF"), BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 7, 8, 7), Child = input,
        };
        Grid.SetRow(inputBorder, 3); root.Children.Add(inputBorder);

        var actions = new DockPanel();
        sendButton = FlatButton("发送", "发送回复给 Dot", Submit);
        sendButton.MinWidth = 50; sendButton.Background = Brush("#20FFFFFF");
        DockPanel.SetDock(sendButton, Dock.Right); actions.Children.Add(sendButton);
        pageButton = FlatButton("网页登录", "仅在登录、验证或核对时打开官方网页", () => Emit(new("page")));
        DockPanel.SetDock(pageButton, Dock.Left); actions.Children.Add(pageButton);
        var hint = Label("↵ 发送", 10, FontWeights.Normal);
        hint.Foreground = Brush("#A5A5AE"); hint.ToolTip = "Enter 发送，Shift+Enter 换行";
        hint.Margin = new Thickness(3, 0, 0, 0); actions.Children.Add(hint);
        Grid.SetRow(actions, 5); root.Children.Add(actions);
        statusText = Label("正在连接…", 10, FontWeights.Normal);
        statusText.Foreground = Brush("#B8B8C0"); statusText.TextTrimming = TextTrimming.CharacterEllipsis;
        AutomationProperties.SetLiveSetting(statusText, AutomationLiveSetting.Polite);
        Grid.SetRow(statusText, 6); root.Children.Add(statusText);
        reviewActions = new Grid { Visibility = Visibility.Collapsed, ClipToBounds = true };
        reviewActions.ColumnDefinitions.Add(new ColumnDefinition()); reviewActions.ColumnDefinitions.Add(new ColumnDefinition());
        var reviewedSent = FlatButton("已发送，解除", "仅在网页确认已发送后解除，不会再次发送", () => EmitReview("reviewed_sent"));
        var reviewedNotSent = FlatButton("未发送，留草稿", "仅在网页确认未发送后解除，不会自动发送", () => EmitReview("reviewed_not_sent"));
        reviewedSent.FontSize = reviewedNotSent.FontSize = 10;
        reviewedSent.HorizontalAlignment = reviewedNotSent.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        reviewedSent.Padding = reviewedNotSent.Padding = new Thickness(3, 2, 3, 2);
        Add(reviewActions, reviewedSent, 0); Add(reviewActions, reviewedNotSent, 1);
        Grid.SetRow(reviewActions, 6); root.Children.Add(reviewActions);
        earlierButton = FlatButton("", "先在网页核对较早未读，再确认已查看", EarlierMessages);
        earlierButton.FontSize = 10; earlierButton.Visibility = Visibility.Collapsed;
        Grid.SetRow(earlierButton, 6); root.Children.Add(earlierButton);
        Content = root;

        input.TextChanged += (_, _) =>
        {
            if (!applyingDraft) { hasEdited = true; AdvanceDraft(); }
            UpdateSend();
        };
        input.AddHandler(TextCompositionManager.PreviewTextInputStartEvent,
            new TextCompositionEventHandler((_, e) => BeginComposition(e)), true);
        input.AddHandler(TextCompositionManager.PreviewTextInputUpdateEvent,
            new TextCompositionEventHandler((_, e) => BeginComposition(e)), true);
        input.AddHandler(TextCompositionManager.PreviewTextInputEvent, new TextCompositionEventHandler((_, e) =>
        {
            if (e.TextComposition.AutoComplete != TextCompositionAutoComplete.Off || !composing) return;
            var generation = compositionGeneration;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (compositionGeneration != generation) return;
                composing = false; UpdateSend(); NotifyInteraction();
            }));
        }), true);
        input.PreviewKeyDown += async (_, e) => await HandleInputKeyAsync(e, Keyboard.Modifiers);
        input.PreviewKeyUp += (_, e) =>
        {
            if (e.Key == Key.Enter || e.Key == Key.ImeProcessed && e.ImeProcessedKey == Key.Enter)
                imeEnterDown = false;
        };
        input.LostKeyboardFocus += (_, _) =>
        {
            composing = false; compositionGeneration++; imeEnterDown = false; UpdateSend(); NotifyInteraction();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || composing) return;
            e.Handled = true; CloseRequested?.Invoke(this, EventArgs.Empty);
        };
        MouseEnter += (_, _) => NotifyInteraction(); MouseLeave += (_, _) => NotifyInteraction();
        IsKeyboardFocusWithinChanged += (_, _) => NotifyInteraction();
        SizeChanged += (_, args) =>
        {
            if (args.WidthChanged && args.NewSize.Width >= MinWidth) RequestAdaptiveLayout();
        };
        Loaded += (_, _) => { RequestAdaptiveLayout(); QueueReadAcknowledgement(); };
        UpdateSend();
    }

    internal void ApplyState(DotInlineState value)
    {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(value);
        if (sessionId == value.SessionId && value.Sequence <= stateSequence) return;
        applyingState = true;
        try
        {
            var first = state is null;
            var newSession = sessionId is not null && sessionId != value.SessionId;
            if (newSession) { readRequested.Clear(); sendObserved = false; earlierReviewBudget = 0; earlierPageObserved = false; }
            if (!value.Online) readRequested.Clear();
            if (value.PageVisible && state?.PageVisible != true)
            { earlierPageObserved = true; earlierReviewBudget = value.EarlierUnreadCount; }
            if (value.EarlierUnreadCount == 0) { earlierReviewBudget = 0; earlierPageObserved = false; }
            sessionId = value.SessionId; stateSequence = value.Sequence; state = value;
            foreach (var id in readRequested.Keys.Where(id => !value.Messages.Any(message => message.Id == id && message.Unread)).ToArray())
                readRequested.Remove(id);
            if (first && !hasEdited && draftRevision == 0 && input.Text.Length == 0 && !composing)
            {
                draftRevision = value.ClientDraftRevision; SetDraft(value.DraftText);
            }
            else if (!newSession && value.ClientDraftRevision == draftRevision && !composing)
                SetDraft(value.DraftText);
            if (newSession && (hasEdited || HasLocalDraft || sendOutstanding))
            {
                // Recreate only the current draft after a backend restart. A send is never replayed.
                draftRevision = Math.Max(draftRevision, value.ClientDraftRevision) + 1;
                EmitDraft();
            }
            if (sendOutstanding)
            {
                if (value.Busy || value.HasPendingAttempt || value.NeedsReview) sendObserved = true;
                if (value.ClientDraftRevision == sendRevision && value.DraftText.Length == 0
                    || sendObserved && !value.Busy && !value.HasPendingAttempt && !value.NeedsReview
                    || value.LastRequestId == sendRequestId && value.AttemptState == "not_dispatched" && !value.Busy && !value.HasPendingAttempt
                    || value.AttemptState is "reviewed_sent" or "reviewed_not_sent" && !value.Busy && !value.HasPendingAttempt
                    || !value.CanSubmit && !value.Busy && !value.HasPendingAttempt && !value.NeedsReview && !value.RecoveryRequired)
                    sendOutstanding = false;
            }
            var selected = value.Messages.FirstOrDefault(message => message.Id == selectedMessageId);
            if (selected is not null) displayedMessage = selected;
            else if (displayedMessage is null)
            {
                displayedMessage = value.Messages.FirstOrDefault(message => message.Unread) ?? value.Messages.LastOrDefault();
                selectedMessageId = displayedMessage?.Id;
            }
            RenderMessage(); UpdateSend(); QueueReadAcknowledgement();
        }
        finally { applyingState = false; UpdateAdaptiveLayout(); }
    }

    internal void Shown()
    {
        VerifyAccess(); shown = true;
        if (!HasLocalDraft && !composing && state?.HasPendingAttempt != true)
        {
            var unread = state?.Messages.FirstOrDefault(message => message.Unread);
            if (unread is not null && displayedMessage?.Unread != true) Select(unread);
        }
        QueueReadAcknowledgement();
    }

    internal void Hidden()
    {
        VerifyAccess(); shown = false;
        // Do not clear input, alter selection, mark messages read or reset a send here.
        if (IsKeyboardFocusWithin)
        {
            var selectionStart = input.SelectionStart; var selectionLength = input.SelectionLength;
            Keyboard.ClearFocus();
            input.Select(selectionStart, selectionLength);
        }
    }

    internal void ActivateForReply()
    {
        VerifyAccess();
        if (IsVisible) input.Focus();
    }

    internal void SetOffline()
    {
        VerifyAccess();
        if (state is { } current) state = current with { Online = false };
        readRequested.Clear();
        statusText.Text = "连接中断，草稿已保留"; statusText.ToolTip = statusText.Text;
        UpdateSend();
    }

    // The work area is only a ceiling. Short content never expands to fill it.
    // Failure leaves the previous usable layout intact and never changes input.
    internal bool SetAvailableHeight(double maximumHeightDip)
    {
        VerifyAccess();
        if (!double.IsFinite(maximumHeightDip)) return false;
        var plan = MeasureAdaptiveLayout();
        if (maximumHeightDip + .01 < plan.MinimumHeight) return false;
        availableHeight = Math.Min(ViewHeightDip, maximumHeightDip);
        UpdateAdaptiveLayout();
        return true;
    }

    // Owners and offscreen previews can synchronously supply the actual content
    // width before reserving their top extension. Width changes also arrive via
    // a coalesced dispatcher callback after normal WPF arrange.
    internal void RecalculatePreferredHeight(double width)
    {
        VerifyAccess();
        if (!double.IsFinite(width) || width < MinWidth) return;
        adaptiveWidth = width;
        UpdateAdaptiveLayout();
    }

    private void RequestAdaptiveLayout()
    {
        if (adaptiveLayoutPosted) return;
        adaptiveLayoutPosted = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)(() =>
        {
            adaptiveLayoutPosted = false;
            if (ActualWidth >= MinWidth) adaptiveWidth = ActualWidth;
            UpdateAdaptiveLayout();
        }));
    }

    private readonly record struct AdaptivePlan(bool HasMessage, double MessageHeight,
        double InputHeight, double FooterHeight, double FixedHeight, double MinimumHeight);

    private AdaptivePlan MeasureAdaptiveLayout()
    {
        var hasMessage = displayedMessage is not null && !string.IsNullOrEmpty(displayedMessage.Text);
        // Measure the natural no-scroll width. Once a viewport reaches its cap,
        // any scrollbar/wrapping growth scrolls inside that fixed cap instead of
        // feeding back into the owner's size on consecutive layouts.
        var messageWidth = Math.Max(1, adaptiveWidth);
        if (hasMessage && (measuredMessage != messageText.Text || Math.Abs(measuredMessageWidth - messageWidth) > .1))
        {
            messageText.Measure(new System.Windows.Size(messageWidth, MaximumMessageHeightDip));
            naturalMessageHeight = Math.Min(MaximumMessageHeightDip, Math.Ceiling(messageText.DesiredSize.Height));
            measuredMessage = messageText.Text; measuredMessageWidth = messageWidth;
        }
        var inputWidth = Math.Max(1, adaptiveWidth - 18);
        if (measuredInput != input.Text || Math.Abs(measuredInputWidth - inputWidth) > .1)
        {
            inputMeasure.Text = input.Text.Length == 0 ? " " : input.Text;
            inputMeasure.Measure(new System.Windows.Size(inputWidth, MaximumInputHeightDip));
            naturalInputHeight = Math.Clamp(Math.Ceiling(inputMeasure.DesiredSize.Height),
                MinimumInputHeightDip, MaximumInputHeightDip);
            measuredInput = input.Text; measuredInputWidth = inputWidth;
        }
        double footer = 0;
        if (reviewActions.Visibility == Visibility.Visible || earlierButton.Visibility == Visibility.Visible) footer = 26;
        else if (statusText.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(statusText.Text))
        {
            statusText.Measure(new System.Windows.Size(adaptiveWidth, double.PositiveInfinity));
            footer = Math.Ceiling(statusText.DesiredSize.Height);
        }
        // Header, native Send row, root margin and spacing before Send remain
        // visible at every supported height. Message spacing only exists with a message.
        var fixedHeight = 26 + 26 + 4 + 6 + footer;
        // Received text and growing drafts must never raise the physical floor
        // and dismiss a working composer. Only mandatory controls/status count.
        var minimum = fixedHeight + MinimumInputHeightDip + 16;
        return new(hasMessage, hasMessage ? Math.Max(22, naturalMessageHeight) : 0,
            Math.Max(MinimumInputHeightDip, naturalInputHeight), footer, fixedHeight, minimum);
    }

    private void UpdateAdaptiveLayout()
    {
        if (applyingState || updatingAdaptiveLayout) return;
        updatingAdaptiveLayout = true;
        var changed = false;
        try
        {
            var plan = MeasureAdaptiveLayout();
            minimumRequiredHeight = plan.MinimumHeight;
            var cap = Math.Max(plan.MinimumHeight, Math.Min(ViewHeightDip, availableHeight));
            var messageHeight = plan.MessageHeight;
            var inputHeight = plan.InputHeight;
            var excess = plan.FixedHeight + messageHeight + (messageHeight > 0 ? 6 : 0) + inputHeight + 16 - cap;
            if (excess > 0 && plan.HasMessage)
            {
                var reduction = Math.Min(excess, messageHeight - 22);
                messageHeight -= reduction; excess -= reduction;
            }
            if (excess > 0)
            {
                var reduction = Math.Min(excess, inputHeight - MinimumInputHeightDip);
                inputHeight -= reduction; excess -= reduction;
            }
            // With only the mandatory floor available, omit the optional message
            // viewport rather than clipping input/send or claiming the message read.
            if (excess > .01) messageHeight = 0;
            var height = Math.Ceiling(plan.FixedHeight + messageHeight + (messageHeight > 0 ? 6 : 0) + inputHeight + 16);
            changed = Math.Abs(Height - height) > .5;
            messageScroller.Visibility = messageHeight > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (Math.Abs(messageScroller.Height - messageHeight) > .1) messageScroller.Height = messageHeight;
            if (Math.Abs(input.Height - inputHeight) > .1) input.Height = inputHeight;
            SetRowHeight(1, messageHeight); SetRowHeight(2, messageHeight > 0 ? 6 : 0);
            SetRowHeight(3, inputHeight + 16); SetRowHeight(4, 6); SetRowHeight(5, 26); SetRowHeight(6, plan.FooterHeight);
            if (changed) Height = height;
        }
        finally { updatingAdaptiveLayout = false; }
        // Fire after releasing the measurement guard so an owner may synchronously
        // apply a tighter screen ceiling and converge without dropping the update.
        if (changed) PreferredHeightChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetRowHeight(int index, double height)
    {
        if (Math.Abs(layoutRoot.RowDefinitions[index].Height.Value - height) > .1)
            layoutRoot.RowDefinitions[index].Height = new GridLength(height);
    }

    internal Task HandleInputKeyAsync(KeyEventArgs e, ModifierKeys modifiers)
    {
        if (e.Key == Key.ImeProcessed)
        {
            if (e.ImeProcessedKey == Key.Enter) imeEnterDown = true;
            return Task.CompletedTask;
        }
        if (e.Key != Key.Enter) return Task.CompletedTask;
        if (composing) { imeEnterDown = true; return Task.CompletedTask; }
        if (modifiers != ModifierKeys.None) return Task.CompletedTask;
        e.Handled = true;
        if (!imeEnterDown && !e.IsRepeat) Submit();
        return Task.CompletedTask;
    }

    private void BeginComposition(TextCompositionEventArgs e)
    {
        if (e.TextComposition.AutoComplete != TextCompositionAutoComplete.Off) return;
        // Advance even if the IME has not changed Text yet, protecting a newer composing draft.
        composing = true; compositionGeneration++; hasEdited = true; AdvanceDraft(); UpdateSend(); NotifyInteraction();
    }

    private void AdvanceDraft()
    {
        draftRevision++; EmitDraft(); DraftChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EmitDraft() => Emit(new("draft", input.Text, draftRevision));

    private void SetDraft(string text)
    {
        if (input.Text == text) return;
        applyingDraft = true;
        try { input.Text = text; }
        finally { applyingDraft = false; }
    }

    private void Submit()
    {
        if (composing || imeEnterDown || sendOutstanding || state?.CanSubmit != true || string.IsNullOrWhiteSpace(input.Text)) return;
        sendOutstanding = true; sendObserved = false; sendRevision = draftRevision;
        sendRequestId = Guid.NewGuid().ToString("N");
        UpdateSend();
        Emit(new("send", input.Text, draftRevision, RequestId: sendRequestId));
    }

    private void UpdateSend()
    {
        sendButton.IsEnabled = !composing && !sendOutstanding && state?.CanSubmit == true && !string.IsNullOrWhiteSpace(input.Text);
        pageButton.Visibility = state is { Ready: true, NeedsReview: false, WebDraftPresent: false, Online: true, EarlierUnreadCount: 0 }
            ? Visibility.Collapsed : Visibility.Visible;
        if (state is not { } current) { UpdateAdaptiveLayout(); return; }
        statusText.Text = !current.Online ? "连接中断，草稿已保留" : current.Status;
        statusText.ToolTip = statusText.Text;
        reviewActions.Visibility = current.Online && current.RecoveryRequired && current.RecoveryCanReview
            ? Visibility.Visible : Visibility.Collapsed;
        earlierButton.Visibility = current.EarlierUnreadCount > 0 && !current.RecoveryRequired ? Visibility.Visible : Visibility.Collapsed;
        earlierButton.Content = earlierPageObserved && !current.PageVisible && earlierReviewBudget > 0
            ? $"较早 {Math.Min(earlierReviewBudget, current.EarlierUnreadCount)} 条 · 确认已查看"
            : $"较早 {current.EarlierUnreadCount} 条未读 · 网页核对";
        statusText.Visibility = reviewActions.Visibility == Visibility.Visible || earlierButton.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
        UpdateAdaptiveLayout();
    }

    private void EmitReview(string kind)
    {
        if (state is not { Online: true, RecoveryRequired: true, RecoveryCanReview: true } || composing) return;
        Emit(new(kind, input.Text, draftRevision));
    }

    private void EarlierMessages()
    {
        if (state is not { Online: true } current || current.EarlierUnreadCount <= 0) return;
        if (earlierPageObserved && !current.PageVisible && earlierReviewBudget > 0)
        {
            var count = Math.Min(earlierReviewBudget, current.EarlierUnreadCount);
            earlierReviewBudget = 0; earlierPageObserved = false;
            Emit(new("earlier-read", Count: count));
        }
        else Emit(new("page"));
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
        messageVersion++; RenderMessage(); UpdateAdaptiveLayout(); messageScroller.ScrollToTop(); QueueReadAcknowledgement();
    }

    private void RenderMessage()
    {
        var text = displayedMessage?.Text ?? (state?.Online == false ? "Dot 连接中断，已有内容和草稿会保留。" : "有新消息会在这里显示。");
        if (messageText.Text != text) { messageText.Text = text; messageVersion++; }
        var messages = state?.Messages ?? [];
        var index = Array.FindIndex(messages, message => message.Id == selectedMessageId);
        previousButton.IsEnabled = index > 0 || index < 0 && messages.Length > 0;
        nextButton.IsEnabled = index >= 0 && index < messages.Length - 1 || index < 0 && messages.Length > 0;
        countText.Text = state?.UnreadCount > 99 ? "99+ 未读" : state?.UnreadCount > 0 ? $"{state.UnreadCount} 未读"
            : index >= 0 ? $"{index + 1}/{messages.Length}" : "";
        countText.ToolTip = state?.EarlierUnreadCount > 0
            ? $"共 {state.UnreadCount} 条未读，其中 {state.EarlierUnreadCount} 条较早；此处保留最近消息，不代表完整聊天历史"
            : $"共 {state?.UnreadCount ?? 0} 条未读；仅实际打开并阅读的消息才会标为已读";
    }

    private void QueueReadAcknowledgement()
    {
        if (!shown || messageScroller.Visibility != Visibility.Visible || displayedMessage is not { Unread: true }) return;
        var id = displayedMessage.Id; var version = messageVersion;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!shown || !IsVisible || messageScroller.Visibility != Visibility.Visible || state?.Online != true || displayedMessage?.Id != id || messageVersion != version
                || messageScroller.ActualHeight <= 0
                || readRequested.TryGetValue(id, out var requestedAt) && DateTimeOffset.UtcNow - requestedAt < TimeSpan.FromSeconds(3)) return;
            messageScroller.UpdateLayout();
            if (messageScroller.ScrollableHeight > .5 && messageScroller.VerticalOffset < messageScroller.ScrollableHeight - .5) return;
            // A read is idempotent. Retry on later backend snapshots if its acknowledgement was lost.
            readRequested[id] = DateTimeOffset.UtcNow;
            Emit(new("read", MessageId: id));
        }));
    }

    private void Emit(DotInlineAction action) => ActionRequested?.Invoke(action);
    private void NotifyInteraction() => InteractionChanged?.Invoke(this, EventArgs.Empty);

    private static void Add(Grid grid, UIElement child, int column)
    { Grid.SetColumn(child, column); grid.Children.Add(child); }

    private static TextBlock Label(string text, double size, FontWeight weight) => new()
    { Text = text, FontSize = size, FontWeight = weight, VerticalAlignment = VerticalAlignment.Center };

    private static Button SmallButton(string text, string name, Action action)
    {
        var button = FlatButton(text, name, action); button.Width = 24; button.Padding = new Thickness(0); button.FontSize = 17;
        return button;
    }

    private static Button FlatButton(string text, string name, Action action)
    {
        var button = new Button
        {
            Content = text, ToolTip = name, FontSize = 11, Foreground = Brushes.WhiteSmoke,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(6, 3, 6, 3),
            VerticalAlignment = VerticalAlignment.Center, Height = 26, Cursor = System.Windows.Input.Cursors.Hand,
        };
        AutomationProperties.SetName(button, name);
        var border = new FrameworkElementFactory(typeof(Border)) { Name = "ButtonSurface" };
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = new(RelativeSourceMode.TemplatedParent) });
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = new(RelativeSourceMode.TemplatedParent) });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hovered = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hovered.Setters.Add(new Setter(Border.BackgroundProperty, Brush("#15FFFFFF"), "ButtonSurface"));
        var pressed = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty, Brush("#25FFFFFF"), "ButtonSurface"));
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, .4));
        template.Triggers.Add(hovered); template.Triggers.Add(pressed); template.Triggers.Add(disabled);
        button.Template = template;
        button.Click += (_, _) => action();
        return button;
    }

    private static SolidColorBrush Brush(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)); brush.Freeze(); return brush;
    }

    internal static Style CreateScrollBarStyle() => (Style)System.Windows.Markup.XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="{x:Type ScrollBar}">
          <Setter Property="Width" Value="6"/>
          <Setter Property="MinWidth" Value="6"/>
          <Setter Property="Background" Value="Transparent"/>
          <Setter Property="Focusable" Value="False"/>
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="{x:Type ScrollBar}">
                <Grid Width="6" Background="{TemplateBinding Background}">
                  <Track x:Name="PART_Track" Orientation="Vertical" IsDirectionReversed="True"
                         Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}"
                         ViewportSize="{TemplateBinding ViewportSize}"
                         Value="{Binding Value, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}">
                    <Track.DecreaseRepeatButton>
                      <RepeatButton Command="{x:Static ScrollBar.PageUpCommand}" Focusable="False" IsTabStop="False">
                        <RepeatButton.Template><ControlTemplate TargetType="{x:Type RepeatButton}"><Border Background="Transparent"/></ControlTemplate></RepeatButton.Template>
                      </RepeatButton>
                    </Track.DecreaseRepeatButton>
                    <Track.Thumb>
                      <Thumb MinHeight="16" Background="#68FFFFFF" Focusable="False">
                        <Thumb.Template>
                          <ControlTemplate TargetType="{x:Type Thumb}">
                            <Border x:Name="Grip" Margin="1,0" CornerRadius="2" Background="{TemplateBinding Background}"/>
                            <ControlTemplate.Triggers>
                              <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Grip" Property="Background" Value="#A0FFFFFF"/></Trigger>
                              <Trigger Property="IsDragging" Value="True"><Setter TargetName="Grip" Property="Background" Value="#C0FFFFFF"/></Trigger>
                            </ControlTemplate.Triggers>
                          </ControlTemplate>
                        </Thumb.Template>
                      </Thumb>
                    </Track.Thumb>
                    <Track.IncreaseRepeatButton>
                      <RepeatButton Command="{x:Static ScrollBar.PageDownCommand}" Focusable="False" IsTabStop="False">
                        <RepeatButton.Template><ControlTemplate TargetType="{x:Type RepeatButton}"><Border Background="Transparent"/></ControlTemplate></RepeatButton.Template>
                      </RepeatButton>
                    </Track.IncreaseRepeatButton>
                  </Track>
                </Grid>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """);
}
