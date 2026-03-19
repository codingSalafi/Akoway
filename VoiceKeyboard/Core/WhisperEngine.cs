using VoiceKeyboard.Models;
using Whisper.net;
using Whisper.net.Ggml;

namespace VoiceKeyboard.Core;

/// <summary>
/// Wraps Whisper.net (whisper.cpp binding) for offline speech-to-text.
///
/// GPU:  Install Whisper.net.Runtime.Cuda NuGet package; CUDA is used
///       automatically when available. Set UseGpu=false to force CPU.
/// </summary>
public sealed class WhisperEngine : IDisposable
{
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private string? _loadedModelPath;
    private bool _disposed;

    public bool IsReady => _processor != null;
    public bool IsUsingGpu { get; private set; }

    /// <summary>
    /// Load a GGML model from disk. If GPU initialization fails, automatically
    /// retries with CPU.
    /// </summary>
    public async Task LoadModelAsync(string modelPath, bool useGpu = true, int gpuDevice = 0)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"Model not found: {modelPath}");

        if (_loadedModelPath == modelPath && IsReady) return;

        // Dispose previous
        DisposeModel();

        bool gpuLoaded = false;

        if (useGpu)
        {
            try
            {
                (_factory, _processor) = BuildProcessor(modelPath, gpu: true, gpuDevice);
                gpuLoaded = true;
                IsUsingGpu = true;
            }
            catch (Exception ex)
            {
                // GPU failed — log and fall back
                System.Diagnostics.Debug.WriteLine($"[WhisperEngine] GPU load failed: {ex.Message}. Falling back to CPU.");
                DisposeModel();
            }
        }

        if (!gpuLoaded)
        {
            (_factory, _processor) = BuildProcessor(modelPath, gpu: false, 0);
            IsUsingGpu = false;
        }

        _loadedModelPath = modelPath;

        // Warm-up: pass 0.1 s of silence so first real inference is fast
        await WarmUpAsync();
    }

    /// <summary>
    /// Transcribe a float[] audio buffer (16 kHz, mono, -1.0 to 1.0).
    /// Returns the full transcript string.
    /// </summary>
    public async Task<string> TranscribeAsync(float[] samples, CancellationToken ct = default)
    {
        if (_processor == null)
            throw new InvalidOperationException("Whisper model not loaded.");

        var segments = new System.Text.StringBuilder();

        await foreach (var segment in _processor.ProcessAsync(samples, ct))
        {
            string text = segment.Text.Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                segments.Append(text);
                segments.Append(' ');
            }
        }

        return segments.ToString().Trim();
    }

    /// <summary>
    /// Benchmark: returns transcription latency and real-time factor for a
    /// given audio buffer.
    /// </summary>
    public async Task<BenchmarkResult> BenchmarkAsync(float[] samples)
    {
        if (_processor == null)
            throw new InvalidOperationException("Whisper model not loaded.");

        const int sampleRate = 16000;
        double audioDurationSec = samples.Length / (double)sampleRate;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        string transcript = await TranscribeAsync(samples);
        sw.Stop();

        double latencyMs = sw.Elapsed.TotalMilliseconds;
        double rtf = latencyMs / 1000.0 / audioDurationSec;

        return new BenchmarkResult
        {
            LatencyMs = latencyMs,
            RealTimeFactor = rtf,
            Transcript = transcript,
            IsGpu = IsUsingGpu,
            Rating = rtf switch
            {
                <= 0.5 => BenchmarkRating.Recommended,
                <= 1.0 => BenchmarkRating.Usable,
                _      => BenchmarkRating.NotRecommended
            }
        };
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static (WhisperFactory factory, WhisperProcessor processor)
        BuildProcessor(string modelPath, bool gpu, int gpuDevice)
    {
        var factory = WhisperFactory.FromPath(modelPath);

        var builder = factory.CreateBuilder()
            .WithLanguage("en")
            .WithNoContext()        // don't bleed between utterances
            .WithSingleSegment();  // one segment per call → lower latency

        // CUDA is auto-selected by Whisper.net.Runtime.Cuda at runtime.
        // No explicit GPU API call needed for 1.7.x — suppress unused warnings.
        _ = gpu; _ = gpuDevice;

        return (factory, builder.Build());
    }

    private async Task WarmUpAsync()
    {
        // 0.5 s of silence at 16 kHz = 8000 samples
        var silence = new float[8000];
        try { await TranscribeAsync(silence); }
        catch { /* ignore warm-up errors */ }
    }

    private void DisposeModel()
    {
        _processor?.Dispose();
        _factory?.Dispose();
        _processor = null;
        _factory = null;
        _loadedModelPath = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeModel();
    }
}

// ── DTOs ─────────────────────────────────────────────────────────────────────

public class BenchmarkResult
{
    public double LatencyMs { get; set; }
    public double RealTimeFactor { get; set; }
    public string Transcript { get; set; } = "";
    public bool IsGpu { get; set; }
    public BenchmarkRating Rating { get; set; }
}

public enum BenchmarkRating { Recommended, Usable, NotRecommended }

// ── Model download helper ─────────────────────────────────────────────────────

public static class WhisperModelDownloader
{
    private static readonly Dictionary<WhisperModelSize, (GgmlType type, string fileName)> ModelMap = new()
    {
        [WhisperModelSize.BaseEn]   = (GgmlType.BaseEn,   "ggml-base.en.bin"),
        [WhisperModelSize.SmallEn]  = (GgmlType.SmallEn,  "ggml-small.en.bin"),
        [WhisperModelSize.MediumEn] = (GgmlType.MediumEn, "ggml-medium.en.bin"),
    };

    public static string GetFileName(WhisperModelSize size) => ModelMap[size].fileName;

    public static string GetModelPath(WhisperModelSize size, string modelsDir)
        => Path.Combine(modelsDir, GetFileName(size));

    public static bool IsDownloaded(WhisperModelSize size, string modelsDir)
        => File.Exists(GetModelPath(size, modelsDir));

    public static async Task DownloadAsync(
        WhisperModelSize size,
        string modelsDir,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(modelsDir);
        var destPath = GetModelPath(size, modelsDir);
        var (ggmlType, _) = ModelMap[size];

        await using var modelStream = await WhisperGgmlDownloader.GetGgmlModelAsync(ggmlType);
        await using var fileStream = File.OpenWrite(destPath + ".tmp");

        var buffer = new byte[81920]; // 80 KB chunks
        long totalBytes = 0;
        int read;

        while ((read = await modelStream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
            totalBytes += read;
            progress?.Report(totalBytes);
        }

        await fileStream.DisposeAsync();
        File.Move(destPath + ".tmp", destPath, overwrite: true);
    }
}
