using System.Windows;
using System.Windows.Media;
using VoiceKeyboard.Core;

namespace VoiceKeyboard.UI;

public partial class BenchmarkDialog : Window
{
    private readonly DictationController _dictation;
    private readonly AudioCapture _audio = new();
    private readonly List<float> _buffer = new();
    private bool _isRecording;
    private CancellationTokenSource? _cts;

    public BenchmarkDialog(DictationController dictation)
    {
        _dictation = dictation;
        InitializeComponent();

        _audio.AudioDataAvailable += (_, samples) =>
        {
            lock (_buffer) _buffer.AddRange(samples);
        };
    }

    private void BtnRecord_Click(object sender, RoutedEventArgs e)
    {
        if (!_isRecording)
            StartRecording();
        else
            StopAndTest();
    }

    private void StartRecording()
    {
        lock (_buffer) _buffer.Clear();
        _isRecording = true;

        BtnRecord.Content = "Stop & Test";
        BtnRecord.Background = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));

        PanelRecording.Visibility = Visibility.Visible;
        PanelResults.Visibility = Visibility.Collapsed;
        TxtIdle.Visibility = Visibility.Collapsed;

        _audio.Start();

        // Auto-stop after 10 seconds
        _cts = new CancellationTokenSource();
        _ = AutoStopAsync(_cts.Token);
    }

    private async Task AutoStopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(10_000, ct); // 10 s max
            if (_isRecording)
                Dispatcher.Invoke(StopAndTest);
        }
        catch (TaskCanceledException) { }
    }

    private async void StopAndTest()
    {
        _cts?.Cancel();
        _audio.Stop();
        _isRecording = false;

        BtnRecord.Content = "Start Recording";
        BtnRecord.Background = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
        BtnRecord.IsEnabled = false;
        PanelRecording.Visibility = Visibility.Collapsed;
        TxtIdle.Text = "Running benchmark…";
        TxtIdle.Visibility = Visibility.Visible;

        float[] samples;
        lock (_buffer) samples = _buffer.ToArray();

        if (samples.Length < 8000) // < 0.5 s
        {
            TxtIdle.Text = "Not enough audio captured. Please record at least 1 second.";
            BtnRecord.IsEnabled = true;
            return;
        }

        try
        {
            var result = await _dictation.RunBenchmarkAsync(samples);
            ShowResults(result);
        }
        catch (Exception ex)
        {
            TxtIdle.Text = $"Benchmark failed: {ex.Message}";
        }
        finally
        {
            BtnRecord.IsEnabled = true;
        }
    }

    private void ShowResults(BenchmarkResult result)
    {
        TxtIdle.Visibility = Visibility.Collapsed;
        PanelResults.Visibility = Visibility.Visible;

        TxtLatency.Text = $"{result.LatencyMs:0}ms";
        TxtRtf.Text = $"{result.RealTimeFactor:0.00}x";
        TxtDevice.Text = "CPU";
        TxtTranscript.Text = string.IsNullOrWhiteSpace(result.Transcript)
            ? "(no speech detected)"
            : $"\"{result.Transcript}\"";

        switch (result.Rating)
        {
            case BenchmarkRating.Recommended:
                TxtRating.Text = "✓ Recommended";
                TxtRating.Foreground = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
                break;
            case BenchmarkRating.Usable:
                TxtRating.Text = "⚠ Usable";
                TxtRating.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x7F, 0x17));
                break;
            case BenchmarkRating.NotRecommended:
                TxtRating.Text = "✗ Not Recommended";
                TxtRating.Foreground = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
                break;
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _audio.Stop();
        _audio.Dispose();
        Close();
    }
}
