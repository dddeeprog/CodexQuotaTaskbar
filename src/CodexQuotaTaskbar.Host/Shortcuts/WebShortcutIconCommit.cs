using System.IO;
using System.Security;

namespace CodexQuotaTaskbar.Host.Shortcuts;

internal static class WebShortcutIconCommit
{
    internal static async Task<WebShortcutsSnapshot> SaveAsync(
        Func<IReadOnlyList<WebShortcut>, CancellationToken, Task<WebShortcutsSnapshot>> save,
        WebShortcutManualIconStore icons,
        IReadOnlyList<WebShortcut> previous,
        IReadOnlyList<WebShortcut> replacement,
        string? importedKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(save);
        ArgumentNullException.ThrowIfNull(icons);
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(replacement);
        WebShortcutsSnapshot? result = null;
        try
        {
            result = await save(replacement, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            // The store may have committed immediately before cancellation. Its returned state wins.
            var referenced = (result?.Items ?? previous).Select(item => item.ManualIconKey)
                .Where(WebShortcutManualIconStore.IsValidKey).Select(key => key!).ToHashSet(StringComparer.Ordinal);
            IEnumerable<string?> candidates = result is { ErrorMessage: null }
                ? previous.Select(item => item.ManualIconKey).Append(importedKey)
                : [importedKey];
            foreach (var key in candidates.Where(WebShortcutManualIconStore.IsValidKey).Select(key => key!).Distinct(StringComparer.Ordinal))
            {
                if (referenced.Contains(key)) continue;
                try
                {
                    // Cleanup is best-effort and must neither undo a commit nor mask a save failure.
                    await icons.DeleteAsync(key, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException) { }
            }
        }
    }
}
