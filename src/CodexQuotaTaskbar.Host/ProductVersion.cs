namespace CodexQuotaTaskbar.Host;

internal static class ProductVersion
{
    internal static Version Value { get; } = ReadActual();
    internal static string Text => Value.ToString(3);
    internal static string BuildIdentity => typeof(ProductVersion).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().SingleOrDefault()?.InformationalVersion ?? Text;
    internal static string DisplayText => FormatDisplayText(Text, BuildIdentity);

    // Trial labels are presentation only. Numeric update comparison stays on Value.
    internal static string FormatDisplayText(string numericVersion, string? identity)
    {
        var prefix = numericVersion + "+trial.";
        if (identity is null || identity.Length > 160 || !identity.StartsWith(prefix, StringComparison.Ordinal)) return numericVersion;
        var suffix = identity[prefix.Length..];
        var separator = suffix.LastIndexOf('.');
        if (separator <= 0) return numericVersion;
        var task = suffix[..separator];
        var revision = suffix[(separator + 1)..];
        return task.Length <= 64 && task.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.')
            && revision.Length == 40 && revision.All(char.IsAsciiHexDigit)
            ? numericVersion + " · 本机试用 · " + revision[..7] : numericVersion;
    }
    private static Version ReadActual()
    {
        var version = typeof(ProductVersion).Assembly.GetName().Version
            ?? throw new InvalidOperationException("程序版本不可用。");
        return new(version.Major, version.Minor, version.Build);
    }
}
