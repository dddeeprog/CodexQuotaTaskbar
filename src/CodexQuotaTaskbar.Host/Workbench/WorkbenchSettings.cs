namespace CodexQuotaTaskbar.Host.Workbench;

internal sealed record WorkbenchSettings(
    string? Directory = null, string? Repository = null, string Hotkey = "Ctrl+Alt+G",
    bool Pinned = false, double? Left = null, double? Top = null,
    double Width = 1100, double Height = 720)
{
    internal WorkbenchSettings Normalize() => this with
    {
        Hotkey = WorkbenchHotkey.TryParse(Hotkey, out _, out _) ? Hotkey : "Ctrl+Alt+G",
        Width = double.IsFinite(Width) ? Math.Clamp(Width, 640, 3000) : 1100,
        Height = double.IsFinite(Height) ? Math.Clamp(Height, 400, 2400) : 720,
        Left = Left is { } x && double.IsFinite(x) ? x : null,
        Top = Top is { } y && double.IsFinite(y) ? y : null,
    };
}
