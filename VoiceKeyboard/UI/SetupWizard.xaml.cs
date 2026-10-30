using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VoiceKeyboard.Models;
using VoiceKeyboard.Services;

namespace VoiceKeyboard.UI;

public partial class SetupWizard : Window
{
    private readonly SettingsService _settings;
    private readonly ModelManager   _models;
    private CancellationTokenSource? _cts;

    // Which model the user has selected
    private SpeechModel _selected = SpeechModel.BarkPro;

    public SetupWizard(SettingsService settings, ModelManager models)
    {
        InitializeComponent();
        _settings = settings;
        _models   = models;
    }

    // ── Card clicks (clicking anywhere on a card selects it) ──────────────────

    private void CardPro_Click(object sender, MouseButtonEventArgs e)
        => RbPro.IsChecked = true;

    private void CardClean_Click(object sender, MouseButtonEventArgs e)
        => RbClean.IsChecked = true;

    // ── RadioButton changed → update card borders + _selected ────────────────

    private void Radio_Checked(object sender, RoutedEventArgs e)
    {
        // Fires while InitializeComponent is still building the window (RbPro starts checked),
        // before RbClean and the cards exist, so go by the sender rather than the fields
        _selected = ReferenceEquals(sender, RbClean) ? SpeechModel.BarkClean : SpeechModel.BarkPro;

        // Update card borders so the active card is clearly highlighted
        HighlightCard(CardPro,   _selected == SpeechModel.BarkPro);
        HighlightCard(CardClean, _selected == SpeechModel.BarkClean);
    }

    private static void HighlightCard(System.Windows.Controls.Border? card, bool active)
    {
        if (card == null) return;
        card.BorderBrush     = active
            ? new SolidColorBrush(Color.FromRgb(0x8E, 0xD0, 0x20))
            : new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));
        card.BorderThickness = active
            ? new Thickness(2)
            : new Thickness(1.5);
    }

    // ── Download ──────────────────────────────────────────────────────────────

    private async void BtnDownload_Click(object sender, RoutedEventArgs e)
    {
        BtnDownload.IsEnabled = false;
        TxtSkip.IsEnabled     = false;
        RbPro.IsEnabled = RbClean.IsEnabled = false;

        ProgressPanel.Visibility = Visibility.Visible;
        TxtStatus.Text = $"Downloading {ModelName(_selected)}…";
        TxtPercent.Text = "0%";

        _cts = new CancellationTokenSource();

        var progress = new Progress<double>(pct =>
        {
            PbDownload.Value  = pct;
            TxtPercent.Text   = $"{pct:0}%";
        });

        try
        {
            await _models.DownloadModelAsync(_selected, progress, _cts.Token);

            // Persist as active model
            _settings.Settings.ActiveModel = _selected;
            await _settings.SaveAsync();

            TxtStatus.Text  = $"✓ {ModelName(_selected)} downloaded successfully!";
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
        RbPro.IsEnabled = RbClean.IsEnabled = true;
    }

    private static string ModelName(SpeechModel model) => model switch
    {
        SpeechModel.BarkClean => "Bark 3.0 Clean (240 MB)",
        _                     => "Bark 3.0 Pro (910 MB)"
    };
}
