using System.Windows;
using System.Windows.Input;

namespace VoiceKeyboard.UI;

public partial class HotkeyDialog : Window
{
    public uint SelectedModifiers { get; private set; }
    public uint SelectedVirtualKey { get; private set; }

    public HotkeyDialog(uint currentMods, uint currentVk)
    {
        InitializeComponent();
        SelectedModifiers = currentMods;
        SelectedVirtualKey = currentVk;
        TxtCurrent.Text = FormatCurrent(currentMods, currentVk);
        Focusable = true;
        Loaded += (_, _) => Focus();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;

        // Build modifier flags
        uint mods = 0;
        if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
            mods |= 0x0002; // MOD_CONTROL
        if (Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt))
            mods |= 0x0001; // MOD_ALT
        if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
            mods |= 0x0004; // MOD_SHIFT
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin))
            mods |= 0x0008; // MOD_WIN

        // Ignore if only modifier pressed
        var mainKey = e.Key == Key.System ? e.SystemKey : e.Key;
        if (IsModifier(mainKey)) return;

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(mainKey);

        SelectedModifiers = mods;
        SelectedVirtualKey = vk;
        TxtCurrent.Text = FormatCurrent(mods, vk);
        BtnOk.IsEnabled = mods != 0; // require at least one modifier
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static bool IsModifier(Key k) =>
        k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
          or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private static string FormatCurrent(uint mods, uint vk)
    {
        var parts = new List<string>();
        if ((mods & 0x0002) != 0) parts.Add("Ctrl");
        if ((mods & 0x0001) != 0) parts.Add("Alt");
        if ((mods & 0x0004) != 0) parts.Add("Shift");
        if ((mods & 0x0008) != 0) parts.Add("Win");
        if (vk > 0)
            parts.Add(KeyInterop.KeyFromVirtualKey((int)vk).ToString());
        return parts.Count > 0 ? string.Join(" + ", parts) : "—";
    }
}
