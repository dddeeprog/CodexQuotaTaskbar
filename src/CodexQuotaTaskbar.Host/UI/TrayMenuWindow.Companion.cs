using System.Windows;
using System.Windows.Controls;
using CodexQuotaTaskbar.Host.Settings;
using Button = System.Windows.Controls.Button;

namespace CodexQuotaTaskbar.Host.UI;

public partial class TrayMenuWindow
{
    private Button ballChoice = null!;
    private Button lightChoice = null!;
    private Button motionChoice = null!;
    private Button ballPreview = null!;
    internal event EventHandler? CompanionPreviewRequested;

    private void InitializeCompanionMenu()
    {
        var list = (StackPanel)((ScrollViewer)AppearanceGlass.Child).Content;
        list.Children.Add(new System.Windows.Controls.Border { Height = 1, Background = Freeze("#28FFFFFF"), Margin = new Thickness(10, 5, 10, 5) });
        list.Children.Add(new TextBlock { Text = "右上角状态显示", Tag = "secondary", FontSize = 12, Margin = new Thickness(10, 6, 10, 5) });
        ballChoice = Add("表情小球", () => RequestSettings(settings with { CompanionBall = true }));
        lightChoice = Add("红绿灯", () => RequestSettings(settings with { CompanionBall = false }));
        motionChoice = Add("减少小球动画", () => RequestSettings(settings with { ReduceCompanionMotion = !settings.ReduceCompanionMotion }));
        ballPreview = Add("试玩全部 32 种表情 / 停止", () => InvokeAndHide(CompanionPreviewRequested));

        Button Add(string text, Action action)
        {
            var button = new Button { Content = text, Style = (Style)FindResource("MenuAction"), FontSize = 12 };
            button.Click += (_, _) => action();
            list.Children.Add(button);
            return button;
        }
    }

    private void ApplyCompanionMenu(AppSettings value)
    {
        ballChoice.Content = value.CompanionBallEnabled ? "✓  表情小球" : "    表情小球";
        lightChoice.Content = !value.CompanionBallEnabled ? "✓  红绿灯" : "    红绿灯";
        motionChoice.Content = value.ReduceCompanionMotion ? "✓  减少小球动画" : "    减少小球动画";
        ballPreview.IsEnabled = value.CompanionBallEnabled;
        foreach (var button in new[] { ballChoice, lightChoice, motionChoice, ballPreview })
            button.FontFamily = OverlayTypography.Interface;
    }
}
