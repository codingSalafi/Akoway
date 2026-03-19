using System.Runtime.InteropServices;
using VoiceKeyboard.Models;
using VoiceKeyboard.Native;

namespace VoiceKeyboard.Core;

/// <summary>
/// Injects text into the currently focused Windows application.
///
/// Strategy (Auto mode):
///   1. Try SendInput (Unicode keystrokes) — fast, no clipboard side effects.
///   2. On any failure, fall back to Clipboard paste (Ctrl+V):
///      • Save clipboard
///      • Set clipboard = transcript
///      • Send Ctrl+V
///      • Restore original clipboard
///
/// Clipboard restoration is ALWAYS attempted, even on error.
/// </summary>
public sealed class TextInjector
{
    private readonly int _keystrokeDelayMs;

    public TextInjector(int keystrokeDelayMs = 5)
    {
        _keystrokeDelayMs = keystrokeDelayMs;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task InjectAsync(string text, InjectionMethod method, TextAction[] actions)
    {
        if (actions.Length > 0)
        {
            ExecuteActions(actions);
            await Task.Delay(20);
        }

        if (string.IsNullOrEmpty(text)) return;

        switch (method)
        {
            case InjectionMethod.Keystroke:
                SendInputText(text);
                break;

            case InjectionMethod.Clipboard:
                await ClipboardPasteAsync(text);
                break;

            case InjectionMethod.Auto:
            default:
                try
                {
                    SendInputText(text);
                }
                catch
                {
                    await ClipboardPasteAsync(text);
                }
                break;
        }
    }

    // ── SendInput injection ───────────────────────────────────────────────────

    private void SendInputText(string text)
    {
        foreach (char c in text)
        {
            if (c == '\n')
            {
                SendVirtualKey(NativeMethods.VK_RETURN);
            }
            else if (c == '\t')
            {
                SendVirtualKey(0x09); // VK_TAB
            }
            else
            {
                SendUnicodeChar(c);
            }

            if (_keystrokeDelayMs > 0)
                Thread.Sleep(_keystrokeDelayMs);
        }
    }

    private static void SendUnicodeChar(char c)
    {
        var inputs = new NativeMethods.INPUT[2];

        // Key down
        inputs[0].type = NativeMethods.INPUT_KEYBOARD;
        inputs[0].data.ki = new NativeMethods.KEYBDINPUT
        {
            wVk = 0,
            wScan = c,
            dwFlags = NativeMethods.KEYEVENTF_UNICODE,
            dwExtraInfo = IntPtr.Zero
        };

        // Key up
        inputs[1].type = NativeMethods.INPUT_KEYBOARD;
        inputs[1].data.ki = new NativeMethods.KEYBDINPUT
        {
            wVk = 0,
            wScan = c,
            dwFlags = NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP,
            dwExtraInfo = IntPtr.Zero
        };

        uint result = NativeMethods.SendInput(2, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        if (result == 0)
            throw new InvalidOperationException($"SendInput failed for char '{c}': {Marshal.GetLastWin32Error()}");
    }

    private static void SendVirtualKey(ushort vk)
    {
        var inputs = new NativeMethods.INPUT[2];

        inputs[0].type = NativeMethods.INPUT_KEYBOARD;
        inputs[0].data.ki = new NativeMethods.KEYBDINPUT { wVk = vk };

        inputs[1].type = NativeMethods.INPUT_KEYBOARD;
        inputs[1].data.ki = new NativeMethods.KEYBDINPUT
        {
            wVk = vk,
            dwFlags = NativeMethods.KEYEVENTF_KEYUP
        };

        NativeMethods.SendInput(2, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    // ── Clipboard paste injection ─────────────────────────────────────────────

    private async Task ClipboardPasteAsync(string text)
    {
        string? saved = null;
        bool clipboardOpened = false;

        try
        {
            // Save existing clipboard contents (retry up to 5 times)
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (NativeMethods.OpenClipboard(IntPtr.Zero))
                {
                    clipboardOpened = true;
                    var hData = NativeMethods.GetClipboardData(NativeMethods.CF_UNICODETEXT);
                    if (hData != IntPtr.Zero)
                    {
                        var ptr = NativeMethods.GlobalLock(hData);
                        if (ptr != IntPtr.Zero)
                        {
                            saved = Marshal.PtrToStringUni(ptr);
                            NativeMethods.GlobalUnlock(hData);
                        }
                    }
                    NativeMethods.CloseClipboard();
                    clipboardOpened = false;
                    break;
                }
                await Task.Delay(20);
            }

            // Set clipboard to our text
            SetClipboardText(text);

            // Give the target app focus time
            await Task.Delay(30);

            // Send Ctrl+V
            SendKeyCombo(NativeMethods.VK_CONTROL, NativeMethods.VK_V);

            // Wait for paste to complete before restoring
            await Task.Delay(80);
        }
        finally
        {
            if (clipboardOpened)
                NativeMethods.CloseClipboard();

            // Always restore clipboard, even if paste failed
            await Task.Delay(30);
            if (saved != null)
                SetClipboardText(saved);
            else
                ClearClipboard();
        }
    }

    private static void SetClipboardText(string text)
    {
        // Retry up to 5 times (other apps may hold clipboard)
        for (int i = 0; i < 5; i++)
        {
            if (NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                NativeMethods.EmptyClipboard();

                int byteCount = (text.Length + 1) * 2; // UTF-16 + null terminator
                IntPtr hMem = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (UIntPtr)byteCount);
                IntPtr ptr = NativeMethods.GlobalLock(hMem);

                Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
                Marshal.WriteInt16(ptr, text.Length * 2, 0); // null terminator

                NativeMethods.GlobalUnlock(hMem);
                NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, hMem);
                NativeMethods.CloseClipboard();
                return;
            }
            Thread.Sleep(20);
        }
    }

    private static void ClearClipboard()
    {
        for (int i = 0; i < 5; i++)
        {
            if (NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                NativeMethods.EmptyClipboard();
                NativeMethods.CloseClipboard();
                return;
            }
            Thread.Sleep(20);
        }
    }

    // ── Editing actions ───────────────────────────────────────────────────────

    private static void ExecuteActions(TextAction[] actions)
    {
        foreach (var action in actions)
        {
            switch (action)
            {
                case TextAction.Undo:
                    SendKeyCombo(NativeMethods.VK_CONTROL, NativeMethods.VK_Z);
                    break;

                case TextAction.SelectAll:
                    SendKeyCombo(NativeMethods.VK_CONTROL, NativeMethods.VK_A);
                    break;

                case TextAction.DeleteLastWord:
                    // Ctrl+Shift+Left (select word left), then Delete
                    SendKeyCombo3(NativeMethods.VK_CONTROL, 0x10 /*VK_SHIFT*/, 0x25 /*VK_LEFT*/);
                    SendVirtualKey(NativeMethods.VK_DELETE);
                    break;

                case TextAction.DeleteLastSentence:
                    // Shift+Home (select to line start), then Delete
                    SendKeyCombo(0x10 /*VK_SHIFT*/, 0x24 /*VK_HOME*/);
                    SendVirtualKey(NativeMethods.VK_DELETE);
                    break;

                case TextAction.ClearLine:
                    SendKeyCombo(NativeMethods.VK_CONTROL, NativeMethods.VK_A);
                    Thread.Sleep(20);
                    SendVirtualKey(NativeMethods.VK_DELETE);
                    break;
            }
            Thread.Sleep(30);
        }
    }

    private static void SendKeyCombo(ushort mod, ushort key)
    {
        var inputs = new NativeMethods.INPUT[4];

        inputs[0].type = NativeMethods.INPUT_KEYBOARD;
        inputs[0].data.ki = new NativeMethods.KEYBDINPUT { wVk = mod };

        inputs[1].type = NativeMethods.INPUT_KEYBOARD;
        inputs[1].data.ki = new NativeMethods.KEYBDINPUT { wVk = key };

        inputs[2].type = NativeMethods.INPUT_KEYBOARD;
        inputs[2].data.ki = new NativeMethods.KEYBDINPUT
            { wVk = key, dwFlags = NativeMethods.KEYEVENTF_KEYUP };

        inputs[3].type = NativeMethods.INPUT_KEYBOARD;
        inputs[3].data.ki = new NativeMethods.KEYBDINPUT
            { wVk = mod, dwFlags = NativeMethods.KEYEVENTF_KEYUP };

        NativeMethods.SendInput(4, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static void SendKeyCombo3(ushort mod1, ushort mod2, ushort key)
    {
        var inputs = new NativeMethods.INPUT[6];
        inputs[0].type = NativeMethods.INPUT_KEYBOARD;
        inputs[0].data.ki = new NativeMethods.KEYBDINPUT { wVk = mod1 };
        inputs[1].type = NativeMethods.INPUT_KEYBOARD;
        inputs[1].data.ki = new NativeMethods.KEYBDINPUT { wVk = mod2 };
        inputs[2].type = NativeMethods.INPUT_KEYBOARD;
        inputs[2].data.ki = new NativeMethods.KEYBDINPUT { wVk = key };
        inputs[3].type = NativeMethods.INPUT_KEYBOARD;
        inputs[3].data.ki = new NativeMethods.KEYBDINPUT { wVk = key, dwFlags = NativeMethods.KEYEVENTF_KEYUP };
        inputs[4].type = NativeMethods.INPUT_KEYBOARD;
        inputs[4].data.ki = new NativeMethods.KEYBDINPUT { wVk = mod2, dwFlags = NativeMethods.KEYEVENTF_KEYUP };
        inputs[5].type = NativeMethods.INPUT_KEYBOARD;
        inputs[5].data.ki = new NativeMethods.KEYBDINPUT { wVk = mod1, dwFlags = NativeMethods.KEYEVENTF_KEYUP };
        NativeMethods.SendInput(6, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }
}
