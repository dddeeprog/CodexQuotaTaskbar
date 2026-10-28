namespace CodexQuotaTaskbar.Host.Workbench;

internal sealed record WorkbenchSettings(
    string? Directory = null, string? Repository = null, string Hotkey = "Ctrl+Alt+G",
    bool Pinned = false, double? Left = null, double? Top = null,
    double Width = 1100, double Height = 720, double CompactWidth = 400, double CompactHeight = 280,
    int CompactLayoutVersion = 0)
{
    internal WorkbenchSettings Normalize() => this with
    {
        Hotkey = WorkbenchHotkey.TryParse(Hotkey, out _, out _) ? Hotkey : "Ctrl+Alt+G",
        Width = double.IsFinite(Width) ? Math.Clamp(Width, 640, 3000) : 1100,
        Height = double.IsFinite(Height) ? Math.Clamp(Height, 480, 2400) : 720,
        CompactWidth = CompactLayoutVersion >= 2 && double.IsFinite(CompactWidth) ? Math.Clamp(CompactWidth, 360, 420) : 400,
        CompactHeight = CompactLayoutVersion >= 2 && double.IsFinite(CompactHeight) ? Math.Clamp(CompactHeight, 200, 440) : 280,
        CompactLayoutVersion = 2,
        Left = Left is { } x && double.IsFinite(x) ? x : null,
        Top = Top is { } y && double.IsFinite(y) ? y : null,
    };
}
