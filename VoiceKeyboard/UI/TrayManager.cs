using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;
using VoiceKeyboard.Core;
using VoiceKeyboard.Models;
using VoiceKeyboard.Services;

namespace VoiceKeyboard.UI;

/// <summary>
/// Manages the system tray icon, context menu, and balloon notifications.
/// Uses Hardcodet.NotifyIcon.Wpf.
/// </summary>
public sealed class TrayManager : IDisposable
{
    private TaskbarIcon? _trayIcon;
    private readonly SettingsService _settings;
    private readonly ModelManager _modelManager;
    private readonly DictationController _dictation;
    private MenuItem? _dictateItem;
    private DictationStatus _status = DictationStatus.Idle;
    private bool _disposed;

    public event EventHandler? OpenSettingsRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? ShowOverlayRequested;

    public TrayManager(SettingsService settings, ModelManager modelManager,
                       DictationController dictation)
    {
        _settings = settings;
        _modelManager = modelManager;
        _dictation = dictation;
    }

    public void Initialize()
    {
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = ReadyToolTip,
            Icon = LoadIcon("microphone.ico"),
            ContextMenu = BuildContextMenu()
        };

        _trayIcon.TrayLeftMouseDown += (_, _) => ShowOverlayRequested?.Invoke(this, EventArgs.Empty);
        _trayIcon.TrayMouseDoubleClick += (_, _) => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private string Hotkey => HotkeyText.Format(_settings.Settings.HotkeyModifiers, _settings.Settings.HotkeyVirtualKey);
    private string ReadyToolTip => $"Akoway — Ready\n{Hotkey} to dictate";

    /// <summary>Call after the hotkey changes in Settings so the menu and tooltip show the new one.</summary>
    public void RefreshHotkey()
    {
        if (_dictateItem != null) _dictateItem.Header = $"_Start Dictation ({Hotkey})";
        if (_trayIcon != null && _status == DictationStatus.Idle) _trayIcon.ToolTipText = ReadyToolTip;
    }

    public void UpdateState(DictationStatus status)
    {
        _status = status;
        if (_trayIcon == null) return;

        switch (status)
        {
            case DictationStatus.Listening:
                _trayIcon.Icon = LoadIcon("microphone_active.ico");
                _trayIcon.ToolTipText = "Akoway — Listening…";
                ShowBalloon("Listening", "Speak now — click the mic to stop.", BalloonIcon.Info, show: _settings.Settings.ShowTrayNotifications);
                break;

            case DictationStatus.Processing:
                _trayIcon.ToolTipText = "Akoway — Processing…";
                break;

            case DictationStatus.Injecting:
                _trayIcon.ToolTipText = "Akoway — Inserting text…";
                break;

            case DictationStatus.Error:
                _trayIcon.Icon = LoadIcon("microphone.ico");
                _trayIcon.ToolTipText = "Akoway — Error (click for details)";
                break;

            case DictationStatus.Idle:
            default:
                _trayIcon.Icon = LoadIcon("microphone.ico");
                _trayIcon.ToolTipText = ReadyToolTip;
                break;
        }
    }

    public void ShowError(string message)
    {
        ShowBalloon("Akoway Error", message, BalloonIcon.Error, show: true);
    }

    // ── Context menu ──────────────────────────────────────────────────────────

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        var header = new MenuItem
        {
            Header = "Akoway",
            IsEnabled = false,
            FontWeight = FontWeights.Bold
        };
        menu.Items.Add(header);
        menu.Items.Add(new Separator());

        var dictateItem = _dictateItem = new MenuItem { Header = $"_Start Dictation ({Hotkey})" };
        dictateItem.Click += (_, _) =>
        {
            ShowOverlayRequested?.Invoke(this, EventArgs.Empty);
            _dictation.ToggleDictation();
        };
        menu.Items.Add(dictateItem);

        menu.Items.Add(new Separator());

        var settingsItem = new MenuItem { Header = "_Settings…" };
        settingsItem.Click += (_, _) => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(settingsItem);

        menu.Items.Add(new Separator());

        var exitItem = new MenuItem { Header = "E_xit" };
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(exitItem);

        return menu;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static System.Drawing.Icon? LoadIcon(string filename)
    {
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var stream = asm.GetManifestResourceStream($"VoiceKeyboard.Resources.Icons.{filename}");
            return stream is not null ? new System.Drawing.Icon(stream) : null;
        }
        catch
        {
            return null;
        }
    }

    private void ShowBalloon(string title, string message, BalloonIcon icon, bool show)
    {
        if (!show || _trayIcon == null) return;
        _trayIcon.ShowBalloonTip(title, message, icon);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _trayIcon?.Dispose();
    }
}
