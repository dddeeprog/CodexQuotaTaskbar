using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using CodexQuotaTaskbar.Host.Platform;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Bongo;

// A single shared renderer for the app's companion surfaces. Only ephemeral
// physical state crosses its private pipes; no native input listener is used.
internal sealed class BongoRuntime : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<BongoPayloadPaths>> resolve;
    private readonly Action? beforeStart;
    private readonly BongoInputBuffer input = new();
    private readonly object gate = new();
    private CancellationTokenSource? stop;
    private Task? running;
    private Task? disposal;
    private Process? uncertainProcess;
    private double pointerX, pointerY;
    private int motionRequested;
    private bool disposed;

    internal BongoRuntime() : this(new BongoPayload().EnsureAsync) { }
    internal BongoRuntime(Func<CancellationToken, Task<BongoPayloadPaths>> resolve, Action? beforeStart = null)
    {
        this.resolve = resolve;
        this.beforeStart = beforeStart;
    }
    internal event Action<BongoFrame>? FrameAvailable;
    internal event Action<string>? Failed;
    internal long FramesProduced { get; private set; }
    internal int? ActiveProcessId { get; private set; }
    internal bool IsRunning { get { lock (gate) return running is { IsCompleted: false }; } }
    internal bool ExitConfirmed
    {
        get
        {
            lock (gate)
            {
                if (uncertainProcess is not null && uncertainProcess.HasExited)
                {
                    uncertainProcess.Dispose(); uncertainProcess = null; ActiveProcessId = null;
                }
                return ActiveProcessId is null;
            }
        }
    }

    internal void Accept(CompanionInputEvent value) => input.Accept(value);
    internal void ResetInput() { input.Reset(); Volatile.Write(ref pointerX, 0); Volatile.Write(ref pointerY, 0); }
    internal void SetPointer(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) return;
        Volatile.Write(ref pointerX, Math.Clamp(x, -1, 1));
        Volatile.Write(ref pointerY, Math.Clamp(y, -1, 1));
    }
    internal void PreviewMotion() => Interlocked.Exchange(ref motionRequested, 1);

    internal Task StartAsync(int width, int height, bool singleFrame = false)
    {
        if (width is < 64 or > 1024 || height is < 64 or > 1024) throw new ArgumentOutOfRangeException(nameof(width));
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (running is { IsCompleted: false }) throw new InvalidOperationException("Bongo renderer already running.");
            if (uncertainProcess is not null)
            {
                if (!uncertainProcess.HasExited) throw new InvalidOperationException("Previous Bongo renderer exit is unconfirmed.");
                uncertainProcess.Dispose(); uncertainProcess = null;
            }
            stop?.Dispose(); stop = new CancellationTokenSource();
            var runToken = stop.Token;
            ResetInput(); FramesProduced = 0;
            running = Task.Run(() => RenderLoopAsync(width, height, singleFrame, runToken));
            return running;
        }
    }

    internal async Task StopAsync()
    {
        Task? current;
        lock (gate) { stop?.Cancel(); current = running; }
        if (current is not null) await current.ConfigureAwait(false);
        ResetInput();
    }

    private async Task RenderLoopAsync(int width, int height, bool singleFrame, CancellationToken token)
    {
        Process? process = null;
        Task? diagnostics = null;
        var uncertain = false;
        var processStarted = false;
        try
        {
            var paths = await resolve(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var info = new ProcessStartInfo(paths.HelperPath)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), WorkingDirectory = Path.GetDirectoryName(paths.HelperPath)!,
            };
            foreach (var value in new[] { "--stdio", "--model", paths.ModelDirectory, "--width", width.ToString(), "--height", height.ToString() })
                info.ArgumentList.Add(value);
            // Runtime opt-ins for test tools do not become production child opt-ins.
            foreach (var key in info.Environment.Keys.Where(key => key.StartsWith("CQT_", StringComparison.Ordinal)
                || key == "CODEX_QUOTA_UI_PROOFS").ToArray()) info.Environment.Remove(key);
            process = new Process { StartInfo = info };
            beforeStart?.Invoke();
            if (!process.Start()) throw new InvalidOperationException("Bongo renderer did not start.");
            processStarted = true;
            ActiveProcessId = process.Id;
            OverlayLifecycleDiagnostics.Local.Record(OverlayLifecycleEvent.RendererStarted, relatedPid: process.Id);
            process.StandardInput.AutoFlush = true;
            diagnostics = DrainDiagnosticsAsync(process.StandardError);
            var clock = Stopwatch.StartNew();
            var header = new byte[16];
            var first = true;
            var nextFrame = 0.0;
            var expressionResetAt = double.PositiveInfinity;
            while (!token.IsCancellationRequested)
            {
                var batch = input.Drain();
                object? motion = null;
                string? expression = null;
                if (Interlocked.Exchange(ref motionRequested, 0) != 0)
                {
                    motion = new { group = "CAT_motion", index = 0 };
                    expression = "live2d_expression1.exp3.json";
                    expressionResetAt = clock.Elapsed.TotalMilliseconds + 2500;
                }
                else if (clock.Elapsed.TotalMilliseconds >= expressionResetAt)
                {
                    expression = "live2d_expression0.exp3.json";
                    expressionResetAt = double.PositiveInfinity;
                }
                var command = new
                {
                    time_ms = clock.ElapsedMilliseconds, pointer_x = Volatile.Read(ref pointerX), pointer_y = Volatile.Read(ref pointerY),
                    key_events = batch.Keys.Select(key => new { hid = key.Hid, down = key.Down }),
                    mouse_left = batch.MouseLeft, mouse_right = batch.MouseRight, reset = batch.Reset,
                    motion, expression,
                };
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(first ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(5));
                using var interrupt = deadline.Token.Register(() =>
                {
                    // Interrupt the actual owned IO, not just the awaiting task.
                    // The read finishes before its pooled buffer can be returned.
                    try { process.StandardInput.BaseStream.Dispose(); } catch (Exception error) when (error is IOException or InvalidOperationException) { }
                    try { process.StandardOutput.BaseStream.Dispose(); } catch (Exception error) when (error is IOException or InvalidOperationException) { }
                });
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command).AsMemory(), deadline.Token).ConfigureAwait(false);
                await ReadExactlyAsync(process.StandardOutput.BaseStream, header, deadline.Token).ConfigureAwait(false);
                var bytes = DecodeHeader(header, width, height);
                var pixels = ArrayPool<byte>.Shared.Rent(bytes);
                BongoFrame? frame = null;
                var bufferOwned = true;
                try
                {
                    await ReadExactlyAsync(process.StandardOutput.BaseStream, pixels.AsMemory(0, bytes), deadline.Token).ConfigureAwait(false);
                    frame = new(width, height, pixels, bytes, ArrayPool<byte>.Shared);
                    bufferOwned = false;
                    FramesProduced++;
                    if (first) OverlayLifecycleDiagnostics.Local.Record(OverlayLifecycleEvent.RendererFirstFrame, relatedPid: process.Id);
                    if (FrameAvailable is { } handler) { handler(frame); frame = null; }
                }
                finally
                {
                    if (frame is not null) frame.Dispose();
                    if (bufferOwned) ArrayPool<byte>.Shared.Return(pixels);
                }
                first = false;
                if (singleFrame) break;
                nextFrame += 50;
                var delay = nextFrame - clock.Elapsed.TotalMilliseconds;
                if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), token).ConfigureAwait(false);
                else if (delay < -100) nextFrame = clock.Elapsed.TotalMilliseconds;
            }
        }
        catch (Exception error) when (token.IsCancellationRequested
            && error is OperationCanceledException or IOException or ObjectDisposedException) { }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException or System.ComponentModel.Win32Exception or OperationCanceledException or JsonException
            or ArgumentException or NotSupportedException or System.Net.Http.HttpRequestException)
        {
            // Fixed categories only: never echo pipe content or physical input.
            OverlayLifecycleDiagnostics.Local.Record(OverlayLifecycleEvent.RendererFailed, relatedPid: ActiveProcessId ?? 0);
            Failed?.Invoke(error is OperationCanceledException ? "BongoCat 渲染超时，已回退小白球" : "BongoCat 暂不可用，已回退小白球");
        }
        finally
        {
            ResetInput();
            if (process is not null && !processStarted)
            {
                // A rejected startup guard or failed Process.Start has no child
                // exit to await and must not create an uncertain-process lock.
                process.Dispose();
            }
            else if (process is not null)
            {
                try { process.StandardInput.Close(); } catch (Exception error) when (error is IOException or InvalidOperationException) { }
                // A rejected/cancelled frame can leave the producer blocked on its
                // payload; close only our pipe before waiting, never kill it.
                try { process.StandardOutput.Close(); } catch (Exception error) when (error is IOException or InvalidOperationException) { }
                try
                {
                    using var exitDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(exitDeadline.Token).ConfigureAwait(false);
                    if (diagnostics is not null) await diagnostics.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                    if (!token.IsCancellationRequested && process.ExitCode != 0)
                        Failed?.Invoke("BongoCat 渲染异常结束，已回退小白球");
                }
                catch (Exception error) when (error is OperationCanceledException or TimeoutException or InvalidOperationException or IOException)
                {
                    uncertain = true;
                    lock (gate) uncertainProcess = process;
                    Failed?.Invoke("BongoCat 退出尚未确认，已停用自动重启");
                }
                OverlayLifecycleDiagnostics.Local.Record(uncertain ? OverlayLifecycleEvent.RendererExitUnconfirmed
                    : OverlayLifecycleEvent.RendererExited, relatedPid: ActiveProcessId ?? 0);
                if (!uncertain) { process.Dispose(); ActiveProcessId = null; }
            }
        }
    }

    internal static int DecodeHeader(ReadOnlySpan<byte> header, int width, int height)
    {
        if (header.Length != 16 || !header[..4].SequenceEqual("BCF1"u8)) throw new InvalidDataException("Invalid frame header.");
        var w = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
        var h = BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(header[12..16]);
        if (w != width || h != height || w is < 64 or > 1024 || h is < 64 or > 1024 || count != (long)w * h * 4)
            throw new InvalidDataException("Invalid bounded frame dimensions.");
        return (int)count;
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        while (!buffer.IsEmpty)
        {
            var count = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("Bongo frame ended early.");
            buffer = buffer[count..];
        }
    }

    private static async Task DrainDiagnosticsAsync(StreamReader reader)
    {
        // Consume without storing: native diagnostics contain counters/version,
        // but no renderer output is written into user logs.
        var chars = new char[512];
        try { while (await reader.ReadAsync(chars).ConfigureAwait(false) != 0) { } }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposal is null) { disposed = true; disposal = DisposeCoreAsync(); }
            return new ValueTask(disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await StopAsync().ConfigureAwait(false);
        stop?.Dispose();
    }
}
