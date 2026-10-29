using System.Windows;
using CodexQuotaTaskbar.Host.Diagnostics;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace CodexQuotaTaskbar.Host;

public partial class App
{
    private DiagnosticExportRequest? diagnosticExport;
    private async void OnExportDiagnosticsRequested(object? sender, EventArgs args)
    {
        diagnosticExport ??= new DiagnosticExportRequest(() =>
        {
            var dialog = new SaveFileDialog { Title = "导出故障信息", FileName = "点岛故障信息.json",
                Filter = "脱敏故障信息 (*.json)|*.json", DefaultExt = ".json", AddExtension = true, OverwritePrompt = true };
            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }, path => DiagnosticExport.WriteAsync(DiagnosticLogStore.DefaultDirectory, path));
        var result = await diagnosticExport.RunAsync();
        if (result == DiagnosticExportResult.Succeeded)
            System.Windows.MessageBox.Show("故障信息已导出到你选择的位置。内容已按白名单脱敏，请检查后再分享。", "点岛", MessageBoxButton.OK, MessageBoxImage.Information);
        else if (result == DiagnosticExportResult.Failed)
            System.Windows.MessageBox.Show("未能导出故障信息，请选择其他本地 JSON 文件位置后重试。", "点岛", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
