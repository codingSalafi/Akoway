using VoiceKeyboard.Models;
using VoiceKeyboard.Services;

namespace VoiceKeyboard.Core;

/// <summary>
/// Orchestrates the full dictation pipeline:
///
///   Hotkey → AudioCapture → VAD → BarkEngine → VoiceCommandProcessor
///                                              → TextInjector
///
/// State machine:
///   Idle ──(toggle/PTT press)──► Listening
///   Listening ──(VAD silence)──► Processing
///   Processing ──(transcript)──► Injecting → Idle
///   Any state ──(error)──► Error → Idle
/// </summary>
public sealed class DictationController : IDisposable
{
    private readonly SettingsService _settings;
    private readonly ModelManager _modelManager;

    private readonly AudioCapture _audio = new();
    private readonly VadDetector _vad = new();
    private readonly BarkEngine _engine = new();
    private readonly VoiceCommandProcessor _commandProcessor = new();
    private TextInjector _injector;

    private readonly List<float> _audioBuffer = new();
    private readonly object _bufferLock = new();

    private DictationStatus _status = DictationStatus.Idle;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public event EventHandler<DictationStateChangedEventArgs>? StateChanged;
    public event EventHandler<string>? TranscriptReady;
    public event EventHandler<string>? ErrorOccurred;

    public DictationStatus Status => _status;

    public DictationController(SettingsService settings, ModelManager modelManager)
    {
        _settings = settings;
        _modelManager = modelManager;
        _injector = new TextInjector(settings.Settings.KeystrokeDelayMs);

        // Wire audio → VAD → buffer
        _audio.AudioDataAvailable += OnAudioData;
        _audio.CaptureError += (_, ex) => HandleError($"Microphone error: {ex.Message}");

        _vad.VoiceDetected += OnVoiceDetected;
        _vad.SilenceDetected += OnSilenceDetected;
    }

    // ── Initialization ────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        var s = _settings.Settings;
        _vad.SilenceTimeoutMs = s.SilenceTimeoutSeconds * 1000;

        string modelPath = _modelManager.GetModelPath(s.ActiveModel);
        if (!_modelManager.IsDownloaded(s.ActiveModel))
        {
            SetStatus(DictationStatus.Error, $"Model not found: {Path.GetFileName(modelPath)}");
            return;
        }

        try
        {
            await _engine.LoadModelAsync(BarkModels.For(s.ActiveModel), modelPath);
        }
        catch (Exception ex)
        {
            HandleError($"Failed to load model: {ex.Message}");
        }
    }

    // ── Toggle / PTT ──────────────────────────────────────────────────────────

    public void ToggleDictation()
    {
        if (_status == DictationStatus.Listening)
            StopDictation();
        else if (_status == DictationStatus.Idle)
            StartDictation();
    }

    public void StartDictation()
    {
        if (_status != DictationStatus.Idle) return;
        if (!_engine.IsReady)
        {
            HandleError("Model not loaded. Check Settings → Models.");
            return;
        }
        if (AudioCapture.GetInputDevices().Count == 0)
        {
            HandleError("No microphone found. Plug one in or check Windows sound settings.");
            return;
        }

        lock (_bufferLock) _audioBuffer.Clear();
        _vad.Reset();
        _cts = new CancellationTokenSource();

        _audio.Start();
        if (!_audio.IsCapturing) return; // CaptureError has already reported the problem
        SetStatus(DictationStatus.Listening);
    }

    /// <summary>
    /// Stop without transcribing or typing anything (the overlay's close button).
    /// Also abandons a transcription that is still running.
    /// </summary>
    public void CancelDictation()
    {
        _cts?.Cancel();
        _audio.Stop();
        lock (_bufferLock) _audioBuffer.Clear();

        if (_status is DictationStatus.Listening or DictationStatus.Processing)
            SetStatus(DictationStatus.Idle);
    }

    public void StopDictation()
    {
        if (_status == DictationStatus.Idle) return;
        _audio.Stop();

        // If we have buffered audio, transcribe it now
        float[]? pending;
        lock (_bufferLock)
        {
            pending = _audioBuffer.Count > 0 ? _audioBuffer.ToArray() : null;
            _audioBuffer.Clear();
        }

        if (pending != null)
            _ = TranscribeAndInjectAsync(pending);
        else
            SetStatus(DictationStatus.Idle);
    }

    // ── Audio pipeline ────────────────────────────────────────────────────────

    private void OnAudioData(object? sender, float[] samples)
    {
        if (_status != DictationStatus.Listening) return;

        lock (_bufferLock) _audioBuffer.AddRange(samples);
        _vad.Process(samples);
    }

    private void OnVoiceDetected(object? sender, EventArgs e)
    {
        // Could light up a visual indicator here
    }

    private void OnSilenceDetected(object? sender, EventArgs e)
    {
        // VAD silence is intentionally ignored.
        // Processing only begins when the user clicks the overlay to stop.
    }

    // ── Transcription & injection ─────────────────────────────────────────────

    private async Task TranscribeAndInjectAsync(float[] samples)
    {
        SetStatus(DictationStatus.Processing);

        try
        {
            var ct = _cts?.Token ?? CancellationToken.None;
            string raw = await _engine.TranscribeAsync(samples, ct);
            ct.ThrowIfCancellationRequested(); // closed while the last frames were decoding

            if (string.IsNullOrWhiteSpace(raw))
            {
                SetStatus(DictationStatus.Idle); // nothing heard, go back to idle
                return;
            }

            TranscriptReady?.Invoke(this, raw);

            // Post-process: commands, punctuation, caps
            var processed = _commandProcessor.Process(raw);

            SetStatus(DictationStatus.Injecting);

            // Give the target window time to confirm it still has focus
            await Task.Delay(150, ct);

            await _injector.InjectAsync(
                processed.TextToInsert,
                _settings.Settings.InjectionMethod,
                processed.Actions.ToArray());
        }
        catch (OperationCanceledException) { /* dictation was stopped */ }
        catch (Exception ex)
        {
            HandleError($"Transcription error: {ex.Message}");
            return;
        }

        // Return to idle — user must click again to start a new recording
        if (_status == DictationStatus.Injecting)
            SetStatus(DictationStatus.Idle);
    }

    // ── Model reload ──────────────────────────────────────────────────────────

    public async Task ReloadModelAsync()
    {
        bool wasListening = _status == DictationStatus.Listening;
        StopDictation();
        await InitializeAsync();
        if (wasListening) StartDictation();
    }

    // ── Benchmark ─────────────────────────────────────────────────────────────

    public async Task<BenchmarkResult> RunBenchmarkAsync(float[] audioSamples)
    {
        if (!_engine.IsReady)
            throw new InvalidOperationException("Model not loaded.");
        return await _engine.BenchmarkAsync(audioSamples);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetStatus(DictationStatus status, string? message = null)
    {
        _status = status;
        StateChanged?.Invoke(this, new DictationStateChangedEventArgs(status, message));
    }

    private void HandleError(string message)
    {
        _audio.Stop();
        ErrorOccurred?.Invoke(this, message);
        SetStatus(DictationStatus.Error, message);
        // Auto-recover to Idle once there's been time to read the message
        Task.Delay(6000).ContinueWith(_ =>
        {
            if (_status == DictationStatus.Error)
                SetStatus(DictationStatus.Idle);
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _audio.Dispose();
        _engine.Dispose();
    }
}
