using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Globalization;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;
using FontStyle = System.Windows.FontStyle;
using CodexQuotaTaskbar.Host.Lifetime;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using SystemColors = System.Windows.SystemColors;

namespace CodexQuotaTaskbar.Host.UI;

// The notice is a row in the island's own visual tree. It has no window, input,
// send action. Only an explicit acknowledgement or a deliberate view of a
// fully displayed, non-streaming message can read it; timeout never does.
internal sealed class DotTopNoticeView : System.Windows.Controls.UserControl
{
    internal const double NoticeHeightDip = 36;
    private readonly TextBlock messageText, countText;
    private readonly Button openButton;
    private readonly Border indicator;
    private DotInlineState? acknowledgementState;
    private string? rawText;
    private string normalizedText = "";
    private bool earlierOnly;
    private int? displayedUnreadCount, automationUnreadCount;
    private int displayedEarlierCount;
    private bool automationEarlier;
    private TextWidthKey? measuredText;
    private double naturalTextWidth;
    private readonly record struct TextWidthKey(string Text, CultureInfo Culture, FlowDirection Flow,
        FontFamily Family, FontStyle Style, FontWeight Weight, FontStretch Stretch, double Size, double PixelsPerDip);
    internal event EventHandler? OpenRequested;
    internal event EventHandler? InteractionChanged;
    internal double PreferredHeightDip => NoticeHeightDip;
    internal bool IsInteracting => IsMouseOver || IsKeyboardFocusWithin;
    internal string? DisplayedMessageId { get; private set; }
    internal string SummaryText => messageText.Text;
    internal bool DisplayedTextIsComplete => CanDisplayEntireText(messageText.ActualWidth);
    internal bool MessageLayoutReady => messageText.ActualWidth > 0 && messageText.ActualHeight > 0;

    internal bool IsFullyVisibleWithin(FrameworkElement boundary)
    {
        if (ActualWidth <= 0 || ActualHeight < NoticeHeightDip - .01) return false;
        var full = new Rect(0, 0, ActualWidth, ActualHeight);
        for (DependencyObject? current = this; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement element)
            {
                if (element.Visibility != Visibility.Visible) return false;
                var bounds = ReferenceEquals(element, this) ? full : TransformToAncestor(element).TransformBounds(full);
                if (element.ClipToBounds && (bounds.Left < -.01 || bounds.Top < -.01
                    || bounds.Right > element.RenderSize.Width + .01 || bounds.Bottom > element.RenderSize.Height + .01)) return false;
                if (element.Clip is { } clip && !clip.Bounds.Contains(bounds)) return false;
            }
            if (ReferenceEquals(current, boundary)) return true;
        }
        return false;
    }

    internal DotTopNoticeView()
    {
        Height = NoticeHeightDip;
        MinWidth = 160;
        FontFamily = OverlayTypography.Interface;
        Foreground = Brushes.WhiteSmoke;
        HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch;
        Focusable = false;
        var root = new Grid { Margin = new Thickness(12, 0, 12, 0), ClipToBounds = true };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        indicator = new Border
        {
            Width = 5, Height = 5, CornerRadius = new CornerRadius(2.5),
            Background = new SolidColorBrush(Color.FromRgb(117, 191, 243)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(indicator);
        messageText = new TextBlock
        {
            FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
        };
        Grid.SetColumn(messageText, 1); row.Children.Add(messageText);
        countText = new TextBlock
        {
            FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(190, 190, 199)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(countText, 2); row.Children.Add(countText);
        openButton = new Button
        {
            Content = row, Padding = new Thickness(0), Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Focusable = false,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "点击重显消息；完整短消息会确认本条已读，省略内容不会自动确认。计时收回不算已读。",
        };
        var surface = new FrameworkElementFactory(typeof(Border));
        surface.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Stretch);
        surface.AppendChild(content);
        openButton.Template = new ControlTemplate(typeof(Button)) { VisualTree = surface };
        ApplyHighContrast(SystemParameters.HighContrast);
        openButton.Click += (_, args) => { args.Handled = true; OpenRequested?.Invoke(this, EventArgs.Empty); };
        // The message owns the full notice row; batch marking remains on the cat head.
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.Children.Add(openButton);
        Content = root;
        MouseEnter += (_, _) => InteractionChanged?.Invoke(this, EventArgs.Empty);
        MouseLeave += (_, _) => InteractionChanged?.Invoke(this, EventArgs.Empty);
        IsKeyboardFocusWithinChanged += (_, _) => InteractionChanged?.Invoke(this, EventArgs.Empty);
        messageText.SizeChanged += (_, _) => RefreshReadabilityHint();
    }

    internal void Display(DotInlineMessage message, int unreadCount)
    {
        VerifyAccess(); ArgumentNullException.ThrowIfNull(message);
        acknowledgementState = null;
        earlierOnly = false;
        DisplayedMessageId = message.Id;
        if (!string.Equals(rawText, message.Text, StringComparison.Ordinal))
        {
            rawText = message.Text;
            normalizedText = string.Join(" ", message.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (messageText.Text != normalizedText) messageText.Text = normalizedText;
        }
        if (displayedUnreadCount != unreadCount || countText.Text.Length == 0)
        {
            displayedUnreadCount = unreadCount;
            countText.Text = unreadCount > 1 ? $"{unreadCount} 条" : "Dot";
        }
        RefreshReadabilityHint();
        if (automationEarlier || automationUnreadCount != unreadCount)
        {
            automationEarlier = false; automationUnreadCount = unreadCount;
            AutomationProperties.SetName(openButton, unreadCount > 0 ? $"Dot 新消息，{unreadCount} 条未读，点击查看" : "Dot 消息，点击查看");
        }
    }

    internal void Display(DotInlineMessage message, DotInlineState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Display(message, state.UnreadCount);
        acknowledgementState = state;
    }

    internal DotInlineAction? CreateAcknowledgement(bool singleDisplayedMessage = false)
    {
        var state = acknowledgementState;
        if (state is null || !CanAcknowledge(state) || singleDisplayedMessage && DisplayedMessageId is null) return null;
        return new("ack-notice", MessageId: singleDisplayedMessage ? DisplayedMessageId : null,
            ReadToken: state.ReadToken, ReadSessionId: state.SessionId, ReadSequence: state.ReadSequence);
    }

    internal void DisplayEarlier(DotInlineState state)
    {
        VerifyAccess(); ArgumentNullException.ThrowIfNull(state);
        if (state.EarlierUnreadCount <= 0) throw new ArgumentOutOfRangeException(nameof(state));
        if (earlierOnly && displayedEarlierCount == state.EarlierUnreadCount)
        {
            acknowledgementState = state;
            DisplayedMessageId = null;
            RefreshReadabilityHint();
            return;
        }
        Display(new("local-earlier-summary", $"还有 {state.EarlierUnreadCount} 条更早提醒", null, false), state);
        earlierOnly = true; DisplayedMessageId = null; countText.Text = "";
        displayedEarlierCount = state.EarlierUnreadCount; automationEarlier = true;
        RefreshReadabilityHint();
        AutomationProperties.SetName(openButton, $"还有 {state.EarlierUnreadCount} 条更早的点岛提醒，没有保留正文，不会自动标为已读");
    }

    internal bool CanDisplayEntireText(double availableWidth)
    {
        // The receiver caps retained text at 8000 characters with an ellipsis.
        // Be conservative even if an original short message ends in ellipsis.
        if (earlierOnly || !double.IsFinite(availableWidth) || availableWidth <= 0 || normalizedText.Length == 0
            || normalizedText.EndsWith('…')) return false;
        var key = new TextWidthKey(normalizedText, CultureInfo.CurrentUICulture, messageText.FlowDirection,
            messageText.FontFamily, messageText.FontStyle, messageText.FontWeight, messageText.FontStretch,
            messageText.FontSize, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (measuredText is not { } previous || previous != key)
        {
            var formatted = new FormattedText(key.Text, key.Culture, key.Flow,
                new Typeface(key.Family, key.Style, key.Weight, key.Stretch),
                key.Size, messageText.Foreground, key.PixelsPerDip);
            naturalTextWidth = formatted.WidthIncludingTrailingWhitespace;
            measuredText = key;
        }
        return naturalTextWidth <= availableWidth + .01;
    }

    private static bool CanAcknowledge(DotInlineState state) => state.Online && state.UnreadCount > 0
        && Guid.TryParseExact(state.ReadToken, "N", out _) && Guid.TryParseExact(state.SessionId, "N", out _) && state.ReadSequence > 0;

    private void RefreshReadabilityHint()
    {
        var hint = earlierOnly
        ? "这里只保留更早提醒的数量，没有正文；可点击猫头感叹号清除累计提醒，历史消息保留。"
        : DisplayedTextIsComplete
        ? "点击重显；完整且已停止生成的本条消息显示后会确认已读。计时收回不算已读。"
        : "摘要已省略；可点击猫头感叹号清除累计提醒。计时收回不会清除未读。";
        if (!Equals(openButton.ToolTip, hint)) openButton.ToolTip = hint;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        measuredText = null;
        if (openButton is not null && messageText is not null) RefreshReadabilityHint();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (openButton is not null && messageText is not null &&
            (e.Property == FontFamilyProperty || e.Property == FontStyleProperty || e.Property == FontWeightProperty
             || e.Property == FontStretchProperty || e.Property == FontSizeProperty || e.Property == FlowDirectionProperty))
        {
            measuredText = null;
            RefreshReadabilityHint();
        }
    }

    internal void ApplyHighContrast(bool enabled)
    {
        VerifyAccess();
        openButton.Foreground = messageText.Foreground = enabled ? SystemColors.WindowTextBrush : Brushes.WhiteSmoke;
        countText.Foreground = enabled ? SystemColors.WindowTextBrush : new SolidColorBrush(Color.FromRgb(190, 190, 199));
        indicator.Background = enabled ? SystemColors.HighlightBrush : new SolidColorBrush(Color.FromRgb(117, 191, 243));
        RefreshReadabilityHint();
    }
}
