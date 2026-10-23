using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CodexQuotaTaskbar.Host.Provider;

// Only retries observation. The delegate must never prepare or submit a message.
internal static class DotReadPolling
{
    internal static async Task RunAsync(Func<Task> read, Action<Exception> failed, Func<bool> stopped,
        CancellationToken cancellationToken, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        var failures = 0;
        while (!stopped())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { await read(); failures = 0; }
            catch (Exception error) when (IsRecoverable(error)) { failures++; failed(error); }
            if (!stopped()) await delay(TimeSpan.FromSeconds(Math.Min(10, 2 * Math.Pow(2, Math.Min(failures, 3)))), cancellationToken);
        }
    }

    private static bool IsRecoverable(Exception error) => error is InvalidOperationException or COMException or JsonException or IOException or ArgumentException;
    internal static string ErrorKind(Exception error) => error switch
    {
        COMException => "COMException", JsonException => "JsonException", IOException => "IOException",
        ArgumentException => "ArgumentException", InvalidOperationException => "InvalidOperationException", _ => "other"
    };
}
