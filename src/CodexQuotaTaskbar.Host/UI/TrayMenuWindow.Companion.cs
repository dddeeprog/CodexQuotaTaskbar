using System.Windows;
using System.Windows.Controls;
using CodexQuotaTaskbar.Host.Settings;
using CodexQuotaTaskbar.Host.Bongo;
using Button = System.Windows.Controls.Button;

namespace CodexQuotaTaskbar.Host.UI;

public partial class TrayMenuWindow
{
    private Button ballChoice = null!;
    private Button bongoChoice = null!;
    private Button lightChoice = null!;
    private Button motionChoice = null!;
    private Button typingChoice = null!;
    private Button ballPreview = null!;
    private Button coreTermsChoice = null!;
    private CoreConsentState coreConsentState;
    private bool coreTermsReadable;
    private readonly Dictionary<int, Button> bongoSizeChoices = new();
    internal event EventHandler? CompanionPreviewRequested;
    internal event EventHandler? CoreTermsRequested;

    private void InitializeCompanionMenu()
    {
        ballChoice = Add(CompanionShapeChoices, "小白球", () => RequestSettings(settings with
            { CompanionBall = true, BongoCatVisible = settings.EffectiveBongoCatVisible }), choice: true);
        Name(ballChoice, "CompanionWhiteBallButton", "小白球");
        bongoChoice = Add(BongoVisibilityItems, "显示 BongoCat", () => RequestSettings(settings with
            { BongoCatVisible = !settings.EffectiveBongoCatVisible }));
        Name(bongoChoice, "CompanionBongoCatButton", "显示 BongoCat");
        bongoChoice.ToolTip = "独立显示原版 BongoCat；不会替换状态小白球或开启键鼠互动。";
        lightChoice = Add(CompanionShapeChoices, "红绿灯", () => RequestSettings(settings with
            { CompanionBall = false, BongoCatVisible = settings.EffectiveBongoCatVisible }), choice: true);
        Name(lightChoice, "CompanionSignalLightButton", "红绿灯");
        foreach (var size in new[] { 160, 192, 240 })
        {
            var button = Add(CompanionSizeChoices, size.ToString(), () => RequestSettings(settings with { BongoSizeDip = size }), choice: true);
            Name(button, $"BongoSize{size}Button", $"BongoCat 尺寸 {size}");
            button.ToolTip = $"BongoCat 尺寸 · {size}";
            bongoSizeChoices.Add(size, button);
        }
        motionChoice = Add(CompanionInteractionItems, "减少动态效果", () => RequestSettings(settings with { ReduceCompanionMotion = !settings.ReduceCompanionMotion }));
        Name(motionChoice, "CompanionMotionButton", "减少动态效果");
        typingChoice = Add(CompanionInteractionItems, "跟随键盘敲击", () => RequestSettings(settings with { CompanionTyping = !settings.CompanionTyping }));
        Name(typingChoice, "CompanionTypingButton", "跟随键盘敲击");
        typingChoice.ToolTip = "默认关闭。仅将当前物理键位及鼠标按下/松开用于本地动作，不转换文字、不保存或上传；关闭形象、隐藏或减少动画时暂停。";
        System.Windows.Automation.AutomationProperties.SetName(typingChoice, "跟随键盘敲击");
        System.Windows.Automation.AutomationProperties.SetHelpText(typingChoice, (string)typingChoice.ToolTip);
        ballPreview = Add(CompanionInteractionItems, "试玩全部 32 种表情 / 停止", () => InvokeAndHide(CompanionPreviewRequested));
        Name(ballPreview, "CompanionPreviewButton", "试玩全部 32 种表情 / 停止");
        coreTermsChoice = Add(CompanionInteractionItems, "Core 组件条款 · 待确认", () => InvokeAndHide(CoreTermsRequested));
        coreTermsChoice.Style = (Style)FindResource("MenuAction");
        Name(coreTermsChoice, "CoreComponentTermsButton", "Core 组件条款");
        ToolTipService.SetShowOnDisabled(coreTermsChoice, true);

        Button Add(System.Windows.Controls.Panel list, string text, Action action, bool choice = false)
        {
            var button = new Button { Content = text, Style = (Style)FindResource(choice ? "ChoiceAction" : list == CompanionInteractionItems && text.StartsWith("试玩", StringComparison.Ordinal) ? "MenuAction" : "BinaryAction"),
                FontSize = 13, Margin = choice ? new Thickness(1, 0, 1, 0) : new Thickness(0) };
            button.Click += (_, _) => action();
            list.Children.Add(button);
            return button;
        }

        void Name(Button button, string name, string accessibleName)
        {
            button.Name = name;
            RegisterName(name, button);
            System.Windows.Automation.AutomationProperties.SetName(button, accessibleName);
        }
    }

    private void ApplyCompanionMenu(AppSettings value)
    {
        Select(ballChoice, value.CompanionBallEnabled);
        bongoChoice.Content = "显示 BongoCat";
        bongoChoice.Tag = value.EffectiveBongoCatVisible ? "On" : null;
        System.Windows.Automation.AutomationProperties.SetItemStatus(bongoChoice, value.EffectiveBongoCatVisible ? "已开启" : "已关闭");
        Select(lightChoice, !value.CompanionBallEnabled);
        foreach (var (size, button) in bongoSizeChoices)
        {
            var selected = value.EffectiveBongoSizeDip == size;
            button.IsEnabled = value.EffectiveBongoCatVisible;
            Select(button, selected);
        }
        motionChoice.Content = "减少动态效果";
        motionChoice.Tag = value.ReduceCompanionMotion ? "On" : null;
        System.Windows.Automation.AutomationProperties.SetItemStatus(motionChoice, value.ReduceCompanionMotion ? "已选中" : "未选中");
        typingChoice.Content = "跟随键盘敲击";
        typingChoice.Tag = value.CompanionTyping ? "On" : null;
        System.Windows.Automation.AutomationProperties.SetItemStatus(typingChoice, value.CompanionTyping ? "已选中" : "未选中");
        ballPreview.IsEnabled = value.CompanionBallEnabled;
        foreach (var button in new[] { ballChoice, bongoChoice, lightChoice, motionChoice, typingChoice, ballPreview, coreTermsChoice }.Concat(bongoSizeChoices.Values))
            button.FontFamily = OverlayTypography.Interface;
        RefreshCoreTermsStatus();

        static void Select(Button button, bool selected)
        {
            button.Tag = selected ? "Selected" : null;
            button.Background = System.Windows.Media.Brushes.Transparent;
            System.Windows.Automation.AutomationProperties.SetItemStatus(button, selected ? "已选中" : "未选中");
        }
    }

    internal void ApplyCoreConsentStatus(CoreConsentState state, bool termsReadable)
    {
        coreConsentState = state;
        coreTermsReadable = termsReadable;
        RefreshCoreTermsStatus();
    }

    private void RefreshCoreTermsStatus()
    {
        if (coreTermsChoice is null) return;
        var status = !coreTermsReadable ? "暂不可用" : coreConsentState switch
        {
            CoreConsentState.Accepted => "已确认",
            CoreConsentState.Declined => "暂不使用",
            _ => "待确认",
        };
        coreTermsChoice.Content = $"Core 组件条款 · {status}";
        coreTermsChoice.IsEnabled = coreTermsReadable;
        coreTermsChoice.ToolTip = "仅适用于 Live2D Cubism Core；暂不使用仍保留小白球和其他点岛功能。";
        System.Windows.Automation.AutomationProperties.SetItemStatus(coreTermsChoice, status);
        bongoChoice.ToolTip = coreConsentState == CoreConsentState.Accepted
            ? "独立显示原版 BongoCat；不会替换状态小白球，也不自动开启键鼠互动。"
            : "BongoCat 待确认组件条款；当前以小白球回退显示，已保存的外观与互动选择不变。请主动打开下方 Core 组件条款。";
    }
}
