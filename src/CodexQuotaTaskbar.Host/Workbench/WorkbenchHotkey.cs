using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace CodexQuotaTaskbar.Host.Workbench;

internal sealed class WorkbenchHotkey : IDisposable
{
    private const int Id = 0x5742;
    private HwndSource? source;
    internal event Action? Pressed;
    internal string? Error { get; private set; }

    internal static bool TryParse(string? text, out uint modifiers, out uint key)
    {
        modifiers = 0; key = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        foreach (var part in parts[..^1])
        {
            var modifier = part.ToUpperInvariant() switch { "CTRL" => 2u, "ALT" => 1u, "SHIFT" => 4u, "WIN" => 8u, _ => 0u };
            if (modifier == 0 || (modifiers & modifier) != 0) return false;
            modifiers |= modifier;
        }
        if ((modifiers & (1 | 2 | 8)) == 0) return false;
        if (!Enum.TryParse<Key>(parts[^1], true, out var parsed)
            || !(parsed is >= Key.A and <= Key.Z or >= Key.F1 and <= Key.F24)) return false;
        key = (uint)KeyInterop.VirtualKeyFromKey(parsed);
        return true;
    }

    internal bool Register(string text)
    {
        if (!TryParse(text, out var modifiers, out var key))
        { Error = "快捷键格式无效，例如 Ctrl+Alt+G。"; return false; }
        source ??= new HwndSource(new HwndSourceParameters("DianDaoWorkbenchHotkey") { ParentWindow = new nint(-3) });
        source.RemoveHook(Hook); source.AddHook(Hook);
        _ = UnregisterHotKey(source.Handle, Id);
        var registered = RegisterHotKey(source.Handle, Id, modifiers | 0x4000, key);
        Error = registered ? null : $"{text} 已被占用或无法注册。可从点岛菜单打开工作台，再更改快捷键。";
        return registered;
    }

    private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0312 && wParam == Id) { handled = true; Pressed?.Invoke(); }
        return 0;
    }

    public void Dispose()
    {
        if (source is null) return;
        _ = UnregisterHotKey(source.Handle, Id); source.RemoveHook(Hook); source.Dispose(); source = null;
    }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);
}
