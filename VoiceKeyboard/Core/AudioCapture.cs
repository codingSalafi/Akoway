using NAudio.Wave;

namespace VoiceKeyboard.Core;

/// <summary>
/// Captures microphone audio using NAudio, resamples to 16 kHz mono float[]
/// as required by whisper.cpp.
/// </summary>
public sealed class AudioCapture : IDisposable
{
    // Whisper requires 16 kHz, 16-bit, mono
    private const int SampleRate = 16000;
    private const int BitsPerSample = 16;
    private const int Channels = 1;
    private const int BufferMs = 50; // chunk size

    private WaveInEvent? _waveIn;
    private bool _disposed;

    /// <summary>Raised whenever a new chunk of float PCM samples is available.</summary>
    public event EventHandler<float[]>? AudioDataAvailable;

    /// <summary>Raised on capture error.</summary>
    public event EventHandler<Exception>? CaptureError;

    public bool IsCapturing { get; private set; }

    /// <summary>Start capturing from the default microphone.</summary>
    public void Start(int deviceNumber = 0)
    {
        if (IsCapturing) return;

        _waveIn = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = new WaveFormat(SampleRate, BitsPerSample, Channels),
            BufferMilliseconds = BufferMs
        };

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;

        try
        {
            _waveIn.StartRecording();
            IsCapturing = true;
        }
        catch (Exception ex)
        {
            CaptureError?.Invoke(this, ex);
        }
    }

    public void Stop()
    {
        if (!IsCapturing) return;
        _waveIn?.StopRecording();
        IsCapturing = false;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;

        // Convert 16-bit PCM bytes → float[]  (range -1.0 to 1.0)
        int sampleCount = e.BytesRecorded / 2;
        var samples = new float[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            short raw = BitConverter.ToInt16(e.Buffer, i * 2);
            samples[i] = raw / 32768f;
        }

        AudioDataAvailable?.Invoke(this, samples);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        IsCapturing = false;
        if (e.Exception != null)
            CaptureError?.Invoke(this, e.Exception);
    }

    /// <summary>Returns a list of available input devices.</summary>
    public static IReadOnlyList<string> GetInputDevices()
    {
        var devices = new List<string>();
        for (int i = 0; i < WaveIn.DeviceCount; i++)
        {
            var caps = WaveIn.GetCapabilities(i);
            devices.Add(caps.ProductName);
        }
        return devices;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _waveIn?.Dispose();
    }
}
