namespace VoiceKeyboard.Core;

/// <summary>Turns a registered hotkey (MOD_* flags + virtual-key code) into text like "Ctrl + Alt + F9".</summary>
public static class HotkeyText
{
    public static string Format(uint mods, uint vk)
    {
        var parts = new List<string>();
        if ((mods & 0x0002) != 0) parts.Add("Ctrl");
        if ((mods & 0x0001) != 0) parts.Add("Alt");
        if ((mods & 0x0004) != 0) parts.Add("Shift");
        if ((mods & 0x0008) != 0) parts.Add("Win");
        parts.Add(VkToString(vk));
        return string.Join(" + ", parts);
    }

    private static string VkToString(uint vk) => vk switch
    {
        0x20 => "Space",
        0xBF => "/",
        0xC0 => "`",
        0xDB => "[",
        0xDD => "]",
        _ when vk >= 0x30 && vk <= 0x39 => ((char)vk).ToString(),
        _ when vk >= 0x41 && vk <= 0x5A => ((char)vk).ToString(),
        _ when vk >= 0x70 && vk <= 0x7B => $"F{vk - 0x6F}",
        _ => $"0x{vk:X2}"
    };
}
