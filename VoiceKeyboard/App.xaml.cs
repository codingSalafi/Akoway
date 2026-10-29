using System.Windows;
using VoiceKeyboard.Core;
using VoiceKeyboard.Models;
using VoiceKeyboard.Services;
using VoiceKeyboard.UI;

namespace VoiceKeyboard;

public partial class App : Application
{
    private SettingsService? _settingsService;
    private ModelManager? _modelManager;
    private DictationController? _dictationController;
    private HotkeyManager? _hotkeyManager;
    private TrayManager? _trayManager;
    private OverlayWindow? _overlay;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Prevent multiple instances
        var mutex = new System.Threading.Mutex(true, "Akoway_SingleInstance", out bool isNewInstance);
        if (!isNewInstance)
        {
            MessageBox.Show("Akoway is already running.", "Akoway",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        try
        {
            // 1. Load settings
            _settingsService = new SettingsService();
            await _settingsService.LoadAsync();

            // 2. Model manager
            _modelManager = new ModelManager(_settingsService);

            // 3. First-run: show model download wizard if nothing is installed yet
            bool hasModel = _modelManager.GetAllModels().Any(m => m.IsDownloaded);
            if (!hasModel)
            {
                var wizard = new SetupWizard(_settingsService, _modelManager);
                bool downloaded = wizard.ShowDialog() == true;
                if (!downloaded)
                {
                    // User skipped — exit gracefully (they can re-launch when ready)
                    Shutdown();
                    return;
                }
            }

            // 4. Dictation engine (audio → Bark → inject)
            _dictationController = new DictationController(_settingsService, _modelManager);
            _dictationController.StateChanged += OnDictationStateChanged;
            _dictationController.TranscriptReady += OnTranscriptReady;
            await _dictationController.InitializeAsync();

            // 5. Floating overlay
            _overlay = new OverlayWindow();
            _overlay.SetController(_dictationController);

            // 6. Global hotkey listener
            _hotkeyManager = new HotkeyManager();
            _hotkeyManager.HotkeyPressed += OnHotkeyPressed;
            RegisterHotkey();

            // 7. System tray
            _trayManager = new TrayManager(_settingsService, _modelManager, _dictationController);
            _trayManager.OpenSettingsRequested += OnOpenSettings;
            _trayManager.ExitRequested += OnExitRequested;
            _trayManager.ShowOverlayRequested += (_, _) => Dispatcher.Invoke(() => _overlay?.ShowOverlay());
            _trayManager.Initialize();

            // 8. Re-register hotkey after system wake (hibernate/sleep drops it)
            Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;

            // 9. Show overlay immediately on launch
            _overlay?.ShowOverlay();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to start Akoway:\n{ex.Message}",
                "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;
        // Wait 2s for Windows to fully wake before re-registering
        Task.Delay(2000).ContinueWith(_ => Dispatcher.Invoke(() =>
        {
            _hotkeyManager?.Unregister();
            RegisterHotkey();
        }));
    }

    private void RegisterHotkey()
    {
        if (_settingsService == null || _hotkeyManager == null) return;
        var s = _settingsService.Settings;
        _hotkeyManager.Register(s.HotkeyModifiers, s.HotkeyVirtualKey);
    }

    private void OnHotkeyPressed(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            // Hotkey only surfaces the overlay — click the pill to start / stop
            _overlay?.ShowOverlay();
        });
    }

    private void OnDictationStateChanged(object? sender, DictationStateChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            _overlay?.UpdateState(e.NewState, e.Message);
            _trayManager?.UpdateState(e.NewState);
        });
    }

    private void OnTranscriptReady(object? sender, string transcript)
    {
        // Could show transcript preview in overlay — for now nothing extra
    }

    private void OnOpenSettings(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            var win = new SettingsWindow(_settingsService!, _modelManager!, _dictationController!);
            win.HotkeyChanged += (_, _) =>
            {
                _hotkeyManager?.Unregister();
                RegisterHotkey();
            };
            win.Show();
            win.Activate();
        });
    }

    private void OnExitRequested(object? sender, EventArgs e)
    {
        _dictationController?.StopDictation();
        _hotkeyManager?.Unregister();
        _trayManager?.Dispose();
        _overlay?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _dictationController?.Dispose();
        _hotkeyManager?.Dispose();
        _trayManager?.Dispose();
        base.OnExit(e);
    }
}
