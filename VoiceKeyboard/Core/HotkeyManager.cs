using System.Runtime.InteropServices;
using VoiceKeyboard.Native;

namespace VoiceKeyboard.Core;

/// <summary>
/// Registers a global Windows hotkey and fires <see cref="HotkeyPressed"/>
/// when it is triggered.
///
/// Uses a dedicated background thread with a Win32 message loop and a
/// message-only window (HWND_MESSAGE) — no dependency on the WPF UI thread.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int HotkeyId = 9001;
    private const uint WM_DESTROY_WINDOW = 0x0002;

    private Thread? _thread;
    private IntPtr _hwnd = IntPtr.Zero;
    private uint _modifiers;
    private uint _vk;
    private bool _registered;
    private bool _disposed;

    // Keep delegate alive for the duration of the message loop
    private NativeMethods.WndProcDelegate? _wndProcDelegate;

    public event EventHandler? HotkeyPressed;

    /// <summary>Register a hotkey. Modifiers: MOD_CONTROL | MOD_ALT etc.</summary>
    public void Register(uint modifiers, uint virtualKey)
    {
        _modifiers = modifiers;
        _vk = virtualKey;

        _thread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "VoiceKeyboard-HotkeyThread"
        };
        _thread.Start();
    }

    public void Unregister()
    {
        if (_hwnd != IntPtr.Zero && _registered)
        {
            NativeMethods.UnregisterHotKey(_hwnd, HotkeyId);
            _registered = false;
        }

        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.PostMessage(_hwnd, 0x0012 /*WM_QUIT*/, IntPtr.Zero, IntPtr.Zero);
        }
    }

    // ── Win32 message loop on background thread ───────────────────────────────

    private void MessageLoop()
    {
        // Register a minimal window class
        _wndProcDelegate = WndProc;

        var wndClass = new NativeMethods.WNDCLASS
        {
            lpfnWndProc = _wndProcDelegate,
            lpszClassName = "VoiceKeyboard_HotkeyWindow_" + Guid.NewGuid().ToString("N")[..8]
        };
        NativeMethods.RegisterClass(ref wndClass);

        // Create message-only window (no visible UI)
        _hwnd = NativeMethods.CreateWindowEx(
            0, wndClass.lpszClassName, "",
            0, 0, 0, 0, 0,
            NativeMethods.HWND_MESSAGE,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero) return;

        // Register the hotkey
        bool ok = NativeMethods.RegisterHotKey(
            _hwnd, HotkeyId,
            _modifiers | NativeMethods.MOD_NOREPEAT,
            _vk);

        _registered = ok;

        // Standard Win32 message pump
        while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }

        // Cleanup
        if (_registered)
            NativeMethods.UnregisterHotKey(_hwnd, HotkeyId);

        NativeMethods.DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
            return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unregister();
    }
}
