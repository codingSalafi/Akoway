using Microsoft.Win32;

namespace VoiceKeyboard.Services;

/// <summary>
/// Manages the Windows "Launch on startup" registry key under
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
/// </summary>
public static class StartupService
{
    private const string RegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "Akoway";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, writable: false);
        return key?.GetValue(AppName) != null;
    }

    public static void Enable()
    {
        string exePath = Environment.ProcessPath
            ?? System.Reflection.Assembly.GetExecutingAssembly().Location;

        using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, writable: true);
        key?.SetValue(AppName, $"\"{exePath}\"");
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, writable: true);
        key?.DeleteValue(AppName, throwOnMissingValue: false);
    }

    public static void SetEnabled(bool enabled)
    {
        if (enabled) Enable(); else Disable();
    }
}
