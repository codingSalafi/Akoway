using System.Windows;
using System.Windows.Controls;
using VoiceKeyboard.Core;
using VoiceKeyboard.Models;
using VoiceKeyboard.Services;

namespace VoiceKeyboard.UI;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _settings;
    private readonly ModelManager _modelManager;
    private readonly DictationController _dictation;
    private AppSettings _draft = new(); // working copy until Save
    private CancellationTokenSource? _downloadCts;

    public event EventHandler? HotkeyChanged;

    public SettingsWindow(SettingsService settings, ModelManager modelManager,
                          DictationController dictation)
    {
        _settings = settings;
        _modelManager = modelManager;
        _dictation = dictation;

        InitializeComponent();
        LoadDraft();
    }

    // ── Load settings into UI ─────────────────────────────────────────────────

    private void LoadDraft()
    {
        // Deep copy current settings to a draft
        var s = _settings.Settings;
        _draft = new AppSettings
        {
            HotkeyModifiers = s.HotkeyModifiers,
            HotkeyVirtualKey = s.HotkeyVirtualKey,
            Mode = s.Mode,
            SilenceTimeoutSeconds = s.SilenceTimeoutSeconds,
            ActiveModel = s.ActiveModel,
            ModelsDirectory = s.ModelsDirectory,
            InjectionMethod = s.InjectionMethod,
            KeystrokeDelayMs = s.KeystrokeDelayMs,
            LaunchOnStartup = s.LaunchOnStartup,
            ShowTrayNotifications = s.ShowTrayNotifications
        };

        // Hotkey display
        TxtHotkeyDisplay.Text = FormatHotkey(_draft.HotkeyModifiers, _draft.HotkeyVirtualKey);

        // Dictation mode
        RbToggle.IsChecked = _draft.Mode == DictationMode.Toggle;
        RbPtt.IsChecked = _draft.Mode == DictationMode.PushToTalk;

        // Silence
        SliderSilence.Value = _draft.SilenceTimeoutSeconds;
        TxtSilenceValue.Text = $"{_draft.SilenceTimeoutSeconds:0.0}s";

        // Models
        RefreshModelList();
        CmbModel.ItemsSource = Enum.GetValues<SpeechModel>()
            .Select(m => new { Value = m, Name = ModelDisplayName(m) }).ToList();
        CmbModel.DisplayMemberPath = "Name";
        CmbModel.SelectedValuePath = "Value";
        CmbModel.SelectedValue = _draft.ActiveModel;

        // Injection method
        for (int i = 0; i < CmbInjection.Items.Count; i++)
        {
            if (CmbInjection.Items[i] is ComboBoxItem item &&
                item.Tag?.ToString() == _draft.InjectionMethod.ToString())
            {
                CmbInjection.SelectedIndex = i;
                break;
            }
        }

        // System
        ChkStartup.IsChecked = StartupService.IsEnabled();
        ChkNotifications.IsChecked = _draft.ShowTrayNotifications;
    }

    // ── Model management ──────────────────────────────────────────────────────

    private void RefreshModelList()
    {
        var models = _modelManager.GetAllModels()
            .Select(m => new ModelListItem(m))
            .ToList();
        ModelList.ItemsSource = models;
    }

    private async void BtnDownload_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not SpeechModel model) return;

        _downloadCts = new CancellationTokenSource();
        PanelProgress.Visibility = Visibility.Visible;
        btn.IsEnabled = false;
        TxtDownloadStatus.Text = $"Downloading {ModelDisplayName(model)}…";
        PbDownload.Value = 0;

        try
        {
            var progress = new Progress<double>(pct =>
            {
                PbDownload.Value = pct;
                TxtDownloadStatus.Text = $"Downloading {ModelDisplayName(model)}… {pct:0}%";
            });

            await _modelManager.DownloadModelAsync(model, progress, _downloadCts.Token);
            TxtDownloadStatus.Text = $"✓ {ModelDisplayName(model)} downloaded. Loading model…";

            // Auto-select and load the model that was just downloaded
            _draft.ActiveModel = model;
            CmbModel.SelectedValue = model;
            _settings.Settings.ActiveModel = model;
            await _dictation.ReloadModelAsync();
            TxtDownloadStatus.Text = $"✓ {ModelDisplayName(model)} ready to use!";
        }
        catch (OperationCanceledException)
        {
            TxtDownloadStatus.Text = "Download cancelled.";
        }
        catch (Exception ex)
        {
            TxtDownloadStatus.Text = $"Error: {ex.Message}";
        }
        finally
        {
            btn.IsEnabled = true;
            RefreshModelList();

            await Task.Delay(3000);
            PanelProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void BtnDeleteModel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not SpeechModel model) return;

        if (model == _draft.ActiveModel)
        {
            MessageBox.Show("Cannot delete the currently active model.", "Akoway",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = MessageBox.Show($"Delete {ModelDisplayName(model)}?", "Confirm Delete",
            MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            _modelManager.DeleteModel(model);
            RefreshModelList();
        }
    }

    private async void BtnBenchmark_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new BenchmarkDialog(_dictation);
        dlg.Owner = this;
        dlg.ShowDialog();
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    private void BtnChangeHotkey_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new HotkeyDialog(_draft.HotkeyModifiers, _draft.HotkeyVirtualKey) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _draft.HotkeyModifiers = dlg.SelectedModifiers;
            _draft.HotkeyVirtualKey = dlg.SelectedVirtualKey;
            TxtHotkeyDisplay.Text = FormatHotkey(_draft.HotkeyModifiers, _draft.HotkeyVirtualKey);
        }
    }

    private void DictMode_Changed(object sender, RoutedEventArgs e)
    {
        _draft.Mode = RbToggle.IsChecked == true ? DictationMode.Toggle : DictationMode.PushToTalk;
    }

    private void SliderSilence_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        double rounded = Math.Round(e.NewValue, 1);
        _draft.SilenceTimeoutSeconds = rounded;
        if (TxtSilenceValue != null)
            TxtSilenceValue.Text = $"{rounded:0.0}s";
    }

    private void CmbModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbModel.SelectedValue is SpeechModel model)
            _draft.ActiveModel = model;
    }

    private void CmbInjection_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbInjection.SelectedItem is ComboBoxItem item &&
            Enum.TryParse<InjectionMethod>(item.Tag?.ToString(), out var method))
        {
            _draft.InjectionMethod = method;
        }
    }

    private void ChkStartup_Changed(object sender, RoutedEventArgs e)
    {
        StartupService.SetEnabled(ChkStartup.IsChecked == true);
    }

    private void ChkNotifications_Changed(object sender, RoutedEventArgs e)
    {
        _draft.ShowTrayNotifications = ChkNotifications.IsChecked == true;
    }

    private async void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        bool hotkeyChanged =
            _settings.Settings.HotkeyModifiers != _draft.HotkeyModifiers ||
            _settings.Settings.HotkeyVirtualKey != _draft.HotkeyVirtualKey;

        bool modelChanged = _settings.Settings.ActiveModel != _draft.ActiveModel;

        // Apply draft → settings
        ApplyDraft();
        await _settings.SaveAsync();

        if (hotkeyChanged) HotkeyChanged?.Invoke(this, EventArgs.Empty);
        if (modelChanged) await _dictation.ReloadModelAsync();

        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => Close();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void ApplyDraft()
    {
        var s = _settings.Settings;
        s.HotkeyModifiers = _draft.HotkeyModifiers;
        s.HotkeyVirtualKey = _draft.HotkeyVirtualKey;
        s.Mode = _draft.Mode;
        s.SilenceTimeoutSeconds = _draft.SilenceTimeoutSeconds;
        s.ActiveModel = _draft.ActiveModel;
        s.InjectionMethod = _draft.InjectionMethod;
        s.LaunchOnStartup = _draft.LaunchOnStartup;
        s.ShowTrayNotifications = _draft.ShowTrayNotifications;
    }

    private static string FormatHotkey(uint mods, uint vk)
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

    private static string ModelDisplayName(SpeechModel model) => model switch
    {
        SpeechModel.BarkPro   => "Bark 3.0 Pro",
        SpeechModel.BarkClean => "Bark 3.0 Clean",
        _ => model.ToString()
    };
}

// ── View model for model list items ──────────────────────────────────────────

public class ModelListItem
{
    private readonly ModelInfo _model;

    public ModelListItem(ModelInfo model) => _model = model;

    public string DisplayName => _model.DisplayName;
    public SpeechModel Model => _model.Model;
    public string StatusText => _model.IsDownloaded
        ? $"Downloaded — {_model.FileSizeBytes / 1_000_000} MB"
        : "Not downloaded";
    public Visibility DownloadBtnVisibility => _model.IsDownloaded ? Visibility.Collapsed : Visibility.Visible;
    public Visibility DeleteBtnVisibility => _model.IsDownloaded ? Visibility.Visible : Visibility.Collapsed;
}
