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
    private WhisperModelSize _selected = WhisperModelSize.SmallEn;

    public SetupWizard(SettingsService settings, ModelManager models)
    {
        InitializeComponent();
        _settings = settings;
        _models   = models;
    }

    // ── Card clicks (clicking anywhere on a card selects it) ──────────────────

    private void CardBase_Click(object sender, MouseButtonEventArgs e)
        => RbBase.IsChecked = true;

    private void CardSmall_Click(object sender, MouseButtonEventArgs e)
        => RbSmall.IsChecked = true;

    private void CardMedium_Click(object sender, MouseButtonEventArgs e)
        => RbMedium.IsChecked = true;

    // ── RadioButton changed → update card borders + _selected ────────────────

    private void Radio_Checked(object sender, RoutedEventArgs e)
    {
        // Determine selection
        if (RbBase.IsChecked   == true) _selected = WhisperModelSize.BaseEn;
        else if (RbSmall.IsChecked  == true) _selected = WhisperModelSize.SmallEn;
        else if (RbMedium.IsChecked == true) _selected = WhisperModelSize.MediumEn;

        // Update card borders so the active card is clearly highlighted
        HighlightCard(CardBase,   _selected == WhisperModelSize.BaseEn);
        HighlightCard(CardSmall,  _selected == WhisperModelSize.SmallEn);
        HighlightCard(CardMedium, _selected == WhisperModelSize.MediumEn);
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
        RbBase.IsEnabled = RbSmall.IsEnabled = RbMedium.IsEnabled = false;

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
            TxtStatus.Text  = $"Error: {ex.Message}";
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
        RbBase.IsEnabled = RbSmall.IsEnabled = RbMedium.IsEnabled = true;
    }

    private static string ModelName(WhisperModelSize size) => size switch
    {
        WhisperModelSize.BaseEn   => "Base (142 MB)",
        WhisperModelSize.SmallEn  => "Small (244 MB)",
        WhisperModelSize.MediumEn => "Medium (769 MB)",
        _                         => size.ToString()
    };
}
