namespace CodexQuotaTaskbar.Core.Overlay;

public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => checked(Right - Left);
    public int Height => checked(Bottom - Top);
}
