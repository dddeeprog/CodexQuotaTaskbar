using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CodexQuotaTaskbar.Host.Provider;

internal sealed class CodexAppServerClient : IAsyncDisposable
{
    private const int MaximumMessageCharacters = 262_144;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new();
    private readonly SemaphoreSlim requestLock = new(1, 1);
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private Process? process;
    private StreamWriter? input;
    private Task? readerTask;
    private long nextId;

    internal event EventHandler<string>? Notification;

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        if (process is not null)
        {
            return;
        }

        process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = CodexLauncher.FindExecutable(),
                ArgumentList = { "app-server", "--listen", "stdio://" },
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };
        if (!process.Start())
        {
            throw new InvalidOperationException("无法启动 Codex App Server。");
        }

        input = process.StandardInput;
        readerTask = ReadLoopAsync(process.StandardOutput, lifetime.Token);
        _ = DrainErrorsAsync(process.StandardError, lifetime.Token);

        await RequestAsync("initialize", new
        {
            clientInfo = new { name = "codex_quota_taskbar", title = "Codex Quota Taskbar", version = ProductVersion.Text },
        }, cancellationToken);
        await SendAsync(new { method = "initialized", @params = new { } }, cancellationToken);
    }

    internal Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException("请求编号冲突。");
        }

        return CompleteRequestAsync(id, method, parameters, completion, cancellationToken);
    }

    private async Task<JsonElement> CompleteRequestAsync(long id, string method, object? parameters, TaskCompletionSource<JsonElement> completion, CancellationToken cancellationToken)
    {
        var acquired = false;
        try
        {
            await requestLock.WaitAsync(cancellationToken);
            acquired = true;
            await SendAsync(new { method, id, @params = parameters ?? new { } }, cancellationToken);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }
        finally
        {
            pending.TryRemove(id, out _);
            if (acquired)
            {
                requestLock.Release();
            }
        }
    }

    private async Task SendAsync(object message, CancellationToken cancellationToken)
    {
        var writer = input ?? throw new InvalidOperationException("Codex App Server 尚未启动。");
        var json = JsonSerializer.Serialize(message);
        if (json.Length > MaximumMessageCharacters)
        {
            throw new InvalidOperationException("App Server 请求过大。");
        }

        await writeLock.WaitAsync(cancellationToken);
        try
        {
            await writer.WriteLineAsync(json.AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                if (line.Length > MaximumMessageCharacters)
                {
                    throw new InvalidDataException("App Server 响应超过安全上限。");
                }

                foreach (var message in ParseMessages(line))
                {
                    Dispatch(message);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            foreach (var request in pending.Values)
            {
                request.TrySetException(new InvalidOperationException("Codex App Server 连接已中断。", exception));
            }
        }
    }

    internal static IReadOnlyList<JsonElement> ParseMessages(string line)
    {
        var messages = new List<JsonElement>();
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(line), new JsonReaderOptions
        {
            AllowMultipleValues = true,
            MaxDepth = 64,
        });
        try
        {
            while (reader.Read())
            {
                using var document = JsonDocument.ParseValue(ref reader);
                messages.Add(document.RootElement.Clone());
            }
        }
        catch (JsonException)
        {
            // A malformed diagnostic line must not tear down the long-lived App Server connection.
        }
        return messages;
    }

    private void Dispatch(JsonElement root)
    {
        if (root.TryGetProperty("id", out var idNode) && idNode.TryGetInt64(out var id) && pending.TryGetValue(id, out var completion))
        {
            if (root.TryGetProperty("error", out var error))
            {
                var message = error.TryGetProperty("message", out var messageNode) ? messageNode.GetString() : "App Server 请求失败。";
                completion.TrySetException(new InvalidOperationException(message));
            }
            else if (root.TryGetProperty("result", out var result))
            {
                completion.TrySetResult(result.Clone());
            }
            else
            {
                completion.TrySetException(new InvalidDataException("App Server 响应缺少 result。"));
            }
        }
        else if (root.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String)
        {
            Notification?.Invoke(this, method.GetString() ?? string.Empty);
        }
    }

    private static async Task DrainErrorsAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.ReadLineAsync(cancellationToken) is not null)
            {
                // App Server stderr may contain private local details; intentionally discard it.
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        if (input is not null)
        {
            input.Close();
        }

        if (process is not null)
        {
            try
            {
                if (!process.HasExited && !process.WaitForExit(2000))
                {
                    process.Kill(true);
                }
            }
            catch (InvalidOperationException)
            {
            }
            process.Dispose();
        }

        if (readerTask is not null)
        {
            try
            {
                await readerTask.ConfigureAwait(false);
            }
            catch (Exception) when (lifetime.IsCancellationRequested)
            {
            }
        }

        writeLock.Dispose();
        requestLock.Dispose();
        lifetime.Dispose();
    }
}
