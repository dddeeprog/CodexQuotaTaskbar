namespace CodexQuotaTaskbar.Core.Overlay;

public enum TaskbarEdge
{
    Left,
    Top,
    Right,
    Bottom,
}

public sealed record TaskbarAnchor(
    string MonitorId,
    ScreenRect MonitorBounds,
    ScreenRect TaskbarBounds,
    TaskbarEdge Edge,
    int Dpi,
    bool IsPrimary,
    bool IsVisible);
