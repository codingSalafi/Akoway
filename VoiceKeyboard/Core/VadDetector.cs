namespace VoiceKeyboard.Core;

/// <summary>
/// Simple energy-based Voice Activity Detector.
///
/// Algorithm:
///   1. Compute RMS of each incoming audio chunk.
///   2. Compare to a configurable threshold.
///   3. Track consecutive silence to fire SilenceDetected after the timeout.
///
/// For production you can swap this for WebRTC VAD via a native binding,
/// but energy-based works reliably for typical dictation scenarios.
/// </summary>
public sealed class VadDetector
{
    // Energy threshold — tune if mic is noisy / quiet
    private const float DefaultThreshold = 0.01f;

    private readonly float _threshold;
    private double _silenceAccumulatedMs;
    private bool _wasSpeaking;

    /// <summary>Total buffered audio duration in milliseconds (resets on silence).</summary>
    public double BufferedAudioMs { get; private set; }

    /// <summary>Raised when voice (above threshold) is detected.</summary>
    public event EventHandler? VoiceDetected;

    /// <summary>Raised after <see cref="SilenceTimeoutMs"/> ms of continuous silence.</summary>
    public event EventHandler? SilenceDetected;

    /// <summary>Silence duration (ms) before SilenceDetected fires.</summary>
    public double SilenceTimeoutMs { get; set; } = 1000; // 1 s default

    private const int SampleRate = 16000;

    public VadDetector(float threshold = DefaultThreshold)
    {
        _threshold = threshold;
    }

    /// <summary>
    /// Feed a chunk of float PCM samples (16 kHz mono).
    /// Call this from AudioCapture.AudioDataAvailable.
    /// </summary>
    public void Process(float[] samples)
    {
        float rms = ComputeRms(samples);
        double chunkMs = samples.Length / (double)SampleRate * 1000.0;

        if (rms >= _threshold)
        {
            // Voice energy detected
            _silenceAccumulatedMs = 0;
            BufferedAudioMs += chunkMs;

            if (!_wasSpeaking)
            {
                _wasSpeaking = true;
                VoiceDetected?.Invoke(this, EventArgs.Empty);
            }
        }
        else
        {
            // Silence
            if (_wasSpeaking)
            {
                _silenceAccumulatedMs += chunkMs;

                if (_silenceAccumulatedMs >= SilenceTimeoutMs)
                {
                    _wasSpeaking = false;
                    _silenceAccumulatedMs = 0;
                    BufferedAudioMs = 0;
                    SilenceDetected?.Invoke(this, EventArgs.Empty);
                }
            }
        }
    }

    public void Reset()
    {
        _silenceAccumulatedMs = 0;
        _wasSpeaking = false;
        BufferedAudioMs = 0;
    }

    private static float ComputeRms(float[] samples)
    {
        if (samples.Length == 0) return 0f;
        double sum = 0;
        foreach (var s in samples) sum += s * s;
        return (float)Math.Sqrt(sum / samples.Length);
    }
}
