namespace WinfoMain.Common;

using System.Runtime.InteropServices;

/// <summary>
/// Process-wide singleton for low-level mouse + keyboard hooks.
/// Subscribers register via OnInput event. Hooks are installed once
/// when the first subscriber attaches and removed when the last detaches.
/// Must be created on an STA thread with a message pump.
/// </summary>
public sealed class GlobalInputHook : IDisposable
{
    public static GlobalInputHook Instance { get; } = new();

    /// <summary>Fires on any mouse or keyboard event. Runs on the hook thread — keep handlers fast.</summary>
    public event Action? OnInput;

    private IntPtr _mouseHook, _keyboardHook;
    private LowLevelProc? _mouseProc, _keyboardProc; // prevent GC

    private GlobalInputHook() { }

    /// <summary>Install hooks if not already active. Safe to call multiple times.</summary>
    public void Install()
    {
        if (_mouseHook != IntPtr.Zero) return;

        _mouseProc = MouseProc;
        _keyboardProc = KeyboardProc;

        var module = GetModuleHandle(null);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);
    }

    /// <summary>Remove hooks. Safe to call multiple times.</summary>
    public void Uninstall()
    {
        if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        if (_keyboardHook != IntPtr.Zero) { UnhookWindowsHookEx(_keyboardHook); _keyboardHook = IntPtr.Zero; }
        _mouseProc = null;
        _keyboardProc = null;
    }

    public bool IsInstalled => _mouseHook != IntPtr.Zero;

    public void Dispose() => Uninstall();

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0) OnInput?.Invoke();
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0) OnInput?.Invoke();
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    // P/Invoke

    private delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    private const int WH_MOUSE_LL = 14;
    private const int WH_KEYBOARD_LL = 13;

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
