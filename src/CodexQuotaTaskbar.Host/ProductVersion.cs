namespace CodexQuotaTaskbar.Host;

internal static class ProductVersion
{
    internal static Version Value { get; } = ReadActual();
    internal static string Text => Value.ToString(3);
    private static Version ReadActual()
    {
        var version = typeof(ProductVersion).Assembly.GetName().Version
            ?? throw new InvalidOperationException("程序版本不可用。");
        return new(version.Major, version.Minor, version.Build);
    }
}
