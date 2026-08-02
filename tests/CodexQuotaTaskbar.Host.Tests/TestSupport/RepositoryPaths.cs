namespace CodexQuotaTaskbar.Host.Tests.TestSupport;

internal static class RepositoryPaths
{
    internal static string Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CodexQuotaTaskbar.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
        }
    }

    internal static string HostProject => Path.Combine(Root, "src", "CodexQuotaTaskbar.Host", "CodexQuotaTaskbar.Host.csproj");
}
