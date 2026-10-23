using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using CodexQuotaTaskbar.Host.UI;

namespace CodexQuotaTaskbar.Host.Lifetime;

internal sealed record DotAnchor(int X, int Y, int Bottom, int Left, int Top, int Right, int WorkBottom, double Dpi, bool Visible, int Transparency);
internal sealed record DotCommand(string Kind, DotAnchor? Anchor = null);

// Same binary, isolated browser lifecycle. Only placement/control crosses inherited stdin, never chat text.
internal sealed class DotChatProcess : IDisposable
{
    private Process? process;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly DispatcherTimer placement = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DotAnchorSendPump anchorPump;
    private DotAnchor? anchor;
    internal bool IsRunning
    {
        get { try { return process is { HasExited: false }; } catch (InvalidOperationException) { return false; } }
    }

    internal DotChatProcess()
    {
        anchorPump = new(value => SendAsync(new("anchor", value)), enabled =>
        {
            if (enabled) placement.Start(); else placement.Stop();
        });
        placement.Tick += async (_, _) =>
        {
            if (!IsRunning) { anchorPump.Stop(); return; }
            await anchorPump.FlushAsync();
        };
    }

    internal void UpdateAnchor(DotAnchor value) { anchor = value; anchorPump.Update(value); }

    internal void Start()
    {
        if (IsRunning) return;
        process?.Dispose();
        using var parent = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, RedirectStandardInput = true,
            StandardInputEncoding = new UTF8Encoding(false), CreateNoWindow = true,
        };
        start.ArgumentList.Add("--dot-chat");
        start.ArgumentList.Add(parent.Id.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(parent.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
        process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 Dot 接收。");
        anchorPump.Start();
    }

    internal void Show()
    {
        Start();
        _ = SendAsync(new("reply", anchor));
    }

    private async Task<bool> SendAsync(DotCommand command)
    {
        var target = process;
        if (target is null) return false;
        // Coalesce placement before entering the pipe; user actions are rare and are not dropped.
        if (command.Kind == "anchor" && writeGate.CurrentCount == 0) return false;
        await writeGate.WaitAsync();
        try
        {
            if (!target.HasExited)
            {
                await target.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command));
                await target.StandardInput.FlushAsync();
                return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        { if (ReferenceEquals(process, target)) anchorPump.Stop(); return false; }
        finally { writeGate.Release(); }
    }

    internal static bool TryOpen(string[] args, System.Windows.Application app)
    {
        if (args.Length == 0 || args[0] != "--dot-chat") return false;
        Process? parent = null;
        if (args.Length == 3 && int.TryParse(args[1], out var id) && long.TryParse(args[2], out var ticks))
        {
            parent = Process.GetProcessById(id);
            if (parent.StartTime.ToUniversalTime().Ticks != ticks) { parent.Dispose(); throw new ArgumentException("所属点岛已退出。"); }
        }
        else if (args.Length != 1) throw new ArgumentException("Dot 接收启动参数无效。");
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var window = new DotChatWindow(parent);
        app.MainWindow = window;
        app.ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Start();
        return true;
    }

    public void Dispose()
    {
        anchorPump.Stop();
        if (process is null) return;
        try
        {
            // EOF is a graceful stop even when the bubble is hidden and there is no main HWND to close.
            process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { }
        process.Dispose(); process = null;
    }
}
