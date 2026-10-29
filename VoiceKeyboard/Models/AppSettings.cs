using System.IO;

namespace VoiceKeyboard.Models;

/// <summary>
/// All user-configurable settings. Serialized to/from JSON by SettingsService.
/// </summary>
public class AppSettings
{
    // ── Hotkey ────────────────────────────────────────────────────────────────
    /// <summary>Modifier flags (MOD_CONTROL | MOD_ALT etc.)</summary>
    public uint HotkeyModifiers { get; set; } = 0x0002 | 0x0001; // Ctrl + Alt
    /// <summary>Virtual-key code (default: F9 = 0x78, avoids Claude Desktop's Ctrl+Alt+Space)</summary>
    public uint HotkeyVirtualKey { get; set; } = 0x78; // VK_F9

    // ── Dictation Mode ────────────────────────────────────────────────────────
    /// <summary>Toggle = press to start / press to stop. PTT = hold to speak.</summary>
    public DictationMode Mode { get; set; } = DictationMode.Toggle;

    // ── VAD ───────────────────────────────────────────────────────────────────
    /// <summary>Seconds of silence before auto-stopping. Range 0.6 – 2.0.</summary>
    public double SilenceTimeoutSeconds { get; set; } = 1.0;

    // ── Speech Model ──────────────────────────────────────────────────────────
    public SpeechModel ActiveModel { get; set; } = SpeechModel.BarkPro;
    /// <summary>Where to store downloaded model files.</summary>
    public string ModelsDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "Akoway", "Models");

    // ── Text Injection ────────────────────────────────────────────────────────
    public InjectionMethod InjectionMethod { get; set; } = InjectionMethod.Auto;
    /// <summary>Milliseconds between injected keystrokes (lower = faster but less reliable).</summary>
    public int KeystrokeDelayMs { get; set; } = 5;

    // ── System ────────────────────────────────────────────────────────────────
    public bool LaunchOnStartup { get; set; } = false;
    public bool ShowTrayNotifications { get; set; } = true;
}

public enum DictationMode
{
    Toggle,
    PushToTalk
}

public enum SpeechModel
{
    BarkPro     // Underdog Bark 3.0 Pro
}

public enum InjectionMethod
{
    Auto,       // Try SendInput first, fall back to clipboard
    Keystroke,  // Always use SendInput
    Clipboard   // Always use clipboard paste
}
