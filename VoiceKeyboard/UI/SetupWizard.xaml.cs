using System.Windows;
using System.Windows.Input;
using VoiceKeyboard.Models;
using VoiceKeyboard.Services;

namespace VoiceKeyboard.UI;

public partial class SetupWizard : Window
{
    private readonly SettingsService _settings;
    private readonly ModelManager   _models;
    private CancellationTokenSource? _cts;

    private const SpeechModel Model = SpeechModel.BarkPro;

    public SetupWizard(SettingsService settings, ModelManager models)
    {
        InitializeComponent();
        _settings = settings;
        _models   = models;
    }

    // ── Download ──────────────────────────────────────────────────────────────

    private async void BtnDownload_Click(object sender, RoutedEventArgs e)
    {
        BtnDownload.IsEnabled = false;
        TxtSkip.IsEnabled     = false;

        ProgressPanel.Visibility = Visibility.Visible;
        TxtStatus.Text = $"Downloading {ModelName}…";
        TxtPercent.Text = "0%";

        _cts = new CancellationTokenSource();

        var progress = new Progress<double>(pct =>
        {
            PbDownload.Value  = pct;
            TxtPercent.Text   = $"{pct:0}%";
        });

        try
        {
            await _models.DownloadModelAsync(Model, progress, _cts.Token);

            // Persist as active model
            _settings.Settings.ActiveModel = Model;
            await _settings.SaveAsync();

            TxtStatus.Text  = $"✓ {ModelName} downloaded successfully!";
            TxtPercent.Text = "100%";
            PbDownload.Value = 100;

            await Task.Delay(600); // brief pause so user sees 100%
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            TxtStatus.Text  = "Download cancelled.";
            TxtPercent.Text = "";
            ResetControls();
        }
        catch (Exception ex)
        {
            TxtStatus.Text  = $"Error: {ex.Message} Click Download to resume.";
            TxtPercent.Text = "";
            ResetControls();
        }
    }

    private void TxtSkip_Click(object sender, MouseButtonEventArgs e)
    {
        _cts?.Cancel();
        DialogResult = false;
        Close();
    }

    private void ResetControls()
    {
        BtnDownload.IsEnabled = true;
        TxtSkip.IsEnabled     = true;
    }

    private const string ModelName = "Bark 3.0 Pro (910 MB)";
}
