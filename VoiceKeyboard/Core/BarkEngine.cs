using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VoiceKeyboard.Core;

/// <summary>
/// Offline speech-to-text with Underdog Bark 3.0 Pro (Parakeet TDT 0.6B
/// fine-tune), run through ONNX Runtime.
///
/// Model folder (produced by tools/bark-onnx/export_bark_onnx.py):
///   preprocessor.onnx  waveform → normalized 128-bin log-mel features
///   encoder.int8.onnx  features → acoustic frames
///   decoder.onnx       LSTM prediction network (one token per call)
///   joiner.onnx        frame + prediction → token logits + duration logits
///   tokens.txt         SentencePiece vocabulary, blank last
///
/// Runs on CPU only: about 0.06× real time on a laptop CPU. DirectML was
/// measured slower (0.11×) because it can't run the int8 encoder ops natively.
/// </summary>
public sealed class BarkEngine : IDisposable
{
    private const int SampleRate = 16000;
    // Underdog's reference script prepends 0.25 s of silence before decoding
    private const int LeadInSamples = SampleRate / 4;
    private const int MaxSymbolsPerFrame = 10;

    private InferenceSession? _preprocessor;
    private InferenceSession? _encoder;
    private InferenceSession? _decoder;
    private InferenceSession? _joiner;
    private string[] _tokens = Array.Empty<string>();
    private int _blankId;
    private int _decoderLayers;
    private int _decoderHidden;
    private string? _loadedModelDir;
    private bool _disposed;

    public bool IsReady => _joiner != null;

    /// <summary>
    /// Load the model from its folder.
    /// </summary>
    public async Task LoadModelAsync(string modelDir)
    {
        if (!BarkModelDownloader.IsDownloaded(modelDir))
            throw new FileNotFoundException($"Model not found: {modelDir}");

        if (_loadedModelDir == modelDir && IsReady) return;

        // Dispose previous
        DisposeModel();

        await Task.Run(() =>
        {
            using var options = new SessionOptions();
            _preprocessor = new InferenceSession(Path.Combine(modelDir, "preprocessor.onnx"), options);
            _encoder = new InferenceSession(Path.Combine(modelDir, "encoder.int8.onnx"), options);
            _decoder = new InferenceSession(Path.Combine(modelDir, "decoder.onnx"), options);
            _joiner = new InferenceSession(Path.Combine(modelDir, "joiner.onnx"), options);

            _tokens = File.ReadLines(Path.Combine(modelDir, "tokens.txt"))
                .Select(line => line[..line.LastIndexOf(' ')])
                .ToArray();
            _blankId = _tokens.Length - 1;

            var stateDims = _decoder.InputMetadata["states_h"].Dimensions; // (layers, 1, hidden)
            _decoderLayers = stateDims[0];
            _decoderHidden = stateDims[2];
        });

        _loadedModelDir = modelDir;

        // Warm-up: pass 0.5 s of silence so first real inference is fast
        await WarmUpAsync();
    }

    /// <summary>
    /// Transcribe a float[] audio buffer (16 kHz, mono, -1.0 to 1.0).
    /// Returns the full transcript string.
    /// </summary>
    public Task<string> TranscribeAsync(float[] samples, CancellationToken ct = default)
    {
        if (!IsReady)
            throw new InvalidOperationException("Speech model not loaded.");

        return Task.Run(() => Transcribe(samples, ct), ct);
    }

    /// <summary>
    /// Benchmark: returns transcription latency and real-time factor for a
    /// given audio buffer.
    /// </summary>
    public async Task<BenchmarkResult> BenchmarkAsync(float[] samples)
    {
        if (!IsReady)
            throw new InvalidOperationException("Speech model not loaded.");

        double audioDurationSec = samples.Length / (double)SampleRate;

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
            Rating = rtf switch
            {
                <= 0.5 => BenchmarkRating.Recommended,
                <= 1.0 => BenchmarkRating.Usable,
                _      => BenchmarkRating.NotRecommended
            }
        };
    }

    // ── Inference ─────────────────────────────────────────────────────────────

    private string Transcribe(float[] samples, CancellationToken ct)
    {
        var wave = new float[LeadInSamples + samples.Length];
        samples.CopyTo(wave, LeadInSamples);

        // 1. Waveform → log-mel features
        using var features = _preprocessor!.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("waveforms", new DenseTensor<float>(wave, new[] { 1, wave.Length })),
            NamedOnnxValue.CreateFromTensor("lengths", new DenseTensor<long>(new[] { (long)wave.Length }, new[] { 1 }))
        });

        // 2. Features → acoustic frames, shape (1, hidden, frames)
        using var encoded = _encoder!.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("features", features.First(v => v.Name == "features").AsTensor<float>()),
            NamedOnnxValue.CreateFromTensor("feature_lengths", features.First(v => v.Name == "feature_lengths").AsTensor<long>())
        });
        var encodedTensor = encoded.First(v => v.Name == "encoded").AsTensor<float>();
        int hidden = encodedTensor.Dimensions[1];
        int frameCount = (int)encoded.First(v => v.Name == "encoded_lengths").AsTensor<long>()[0];

        // Re-lay out as one contiguous vector per frame
        var frames = new float[frameCount * hidden];
        for (int d = 0; d < hidden; d++)
            for (int t = 0; t < frameCount; t++)
                frames[t * hidden + d] = encodedTensor[0, d, t];

        // 3. Greedy TDT decoding: each step predicts a token and how many frames to skip
        var stateH = new float[_decoderLayers * _decoderHidden];
        var stateC = new float[_decoderLayers * _decoderHidden];
        var prediction = RunDecoder(_blankId, ref stateH, ref stateC);

        var ids = new List<int>();
        int frame = 0, emitted = 0;
        while (frame < frameCount)
        {
            ct.ThrowIfCancellationRequested();

            var logits = RunJoiner(new Memory<float>(frames, frame * hidden, hidden), prediction);
            int token = ArgMax(logits, 0, _blankId + 1);
            int skip = ArgMax(logits, _blankId + 1, logits.Length - _blankId - 1);

            if (token != _blankId)
            {
                ids.Add(token);
                prediction = RunDecoder(token, ref stateH, ref stateC);
                emitted++;
            }

            if (skip == 0 && (token == _blankId || emitted >= MaxSymbolsPerFrame))
                skip = 1;
            if (skip > 0)
                emitted = 0;
            frame += skip;
        }

        return DecodeTokens(ids);
    }

    private float[] RunDecoder(int token, ref float[] stateH, ref float[] stateC)
    {
        var stateShape = new[] { _decoderLayers, 1, _decoderHidden };
        using var outputs = _decoder!.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("targets", new DenseTensor<long>(new[] { (long)token }, new[] { 1, 1 })),
            NamedOnnxValue.CreateFromTensor("states_h", new DenseTensor<float>(stateH, stateShape)),
            NamedOnnxValue.CreateFromTensor("states_c", new DenseTensor<float>(stateC, stateShape))
        });
        stateH = outputs.First(v => v.Name == "next_states_h").AsTensor<float>().ToArray();
        stateC = outputs.First(v => v.Name == "next_states_c").AsTensor<float>().ToArray();
        return outputs.First(v => v.Name == "decoded").AsTensor<float>().ToArray();
    }

    private float[] RunJoiner(Memory<float> frame, float[] prediction)
    {
        using var outputs = _joiner!.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("encoder_outputs", new DenseTensor<float>(frame, new[] { 1, frame.Length, 1 })),
            NamedOnnxValue.CreateFromTensor("decoder_outputs", new DenseTensor<float>(prediction, new[] { 1, prediction.Length, 1 }))
        });
        return outputs.First().AsTensor<float>().ToArray();
    }

    private static int ArgMax(float[] values, int start, int count)
    {
        int best = start;
        for (int i = start + 1; i < start + count; i++)
            if (values[i] > values[best]) best = i;
        return best - start;
    }

    private string DecodeTokens(List<int> ids)
    {
        var sb = new System.Text.StringBuilder();
        foreach (int id in ids)
        {
            string piece = _tokens[id];
            // Skip control tokens such as <unk> and <|endoftext|>
            if (piece.StartsWith('<') && piece.EndsWith('>')) continue;
            sb.Append(piece);
        }
        // SentencePiece marks word starts with U+2581
        return sb.Replace('▁', ' ').ToString().Trim();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task WarmUpAsync()
    {
        // 0.5 s of silence at 16 kHz = 8000 samples
        var silence = new float[8000];
        try { await TranscribeAsync(silence); }
        catch { /* ignore warm-up errors */ }
    }

    private void DisposeModel()
    {
        _preprocessor?.Dispose();
        _encoder?.Dispose();
        _decoder?.Dispose();
        _joiner?.Dispose();
        _preprocessor = _encoder = _decoder = _joiner = null;
        _loadedModelDir = null;
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
    public BenchmarkRating Rating { get; set; }
}

public enum BenchmarkRating { Recommended, Usable, NotRecommended }

// ── Model download helper ─────────────────────────────────────────────────────

public static class BarkModelDownloader
{
    /// <summary>
    /// Where the converted model is hosted (files from tools/bark-onnx/export_bark_onnx.py).
    /// </summary>
    public const string RepoUrl = "https://huggingface.co/codingSalafi/akoway-bark-3.0-pro-onnx/resolve/main/";

    private static readonly (string Name, long Size, string Sha256)[] Files =
    {
        ("preprocessor.onnx",      1_192_352, "b7e95a3d22e474366f1d79b1c9a20178d57c896d8f9d5c58b2f6bbf5f29ddda0"),
        ("encoder.int8.onnx",    839_417_684, "377211191029a797971cc2eaf2a5aa653c1577319e76579dddd16241a890183f"),
        ("decoder.onnx",          48_874_113, "1d74e62cc3068b769ff8a2db4ea422c438fd02d19a1d0ebe69925332d14ae93d"),
        ("joiner.onnx",           21_020_669, "acbeb1c099f997611bf4b52a91595722ca1da76f83a3d43fa0ddba5167eb048e"),
        ("tokens.txt",                93_939, "d58544679ea4bc6ac563d1f545eb7d474bd6cfa467f0a6e2c1dc1c7d37e3c35d"),
        ("LICENSE-NOTICE.txt",           896, "15d87485db928258e079bbe5959e8ccbc3022c1e871ce596417d7b52d97964d0"),
    };

    public static long TotalBytes { get; } = Files.Sum(f => f.Size);

    public static bool IsDownloaded(string modelDir)
        => Files.All(f => new FileInfo(Path.Combine(modelDir, f.Name)) is { Exists: true } fi && fi.Length == f.Size);

    private const int MaxAttempts = 6;
    // No data for this long counts as a dropped connection
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Download every model file, resuming partial downloads and verifying
    /// each file's SHA-256. Dropped or stalled connections are retried
    /// automatically. Progress value = percent complete (0–100).
    /// </summary>
    public static async Task DownloadAsync(
        string modelDir,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(modelDir);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        long completedBytes = 0;
        foreach (var (name, size, sha256) in Files)
        {
            var destPath = Path.Combine(modelDir, name);
            if (new FileInfo(destPath) is { Exists: true } existingFile && existingFile.Length == size)
            {
                completedBytes += size;
                progress?.Report(100.0 * completedBytes / TotalBytes);
                continue;
            }

            var tmpPath = destPath + ".tmp";
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    await DownloadFileAsync(http, name, size, tmpPath, completedBytes, progress, ct);
                    break;
                }
                catch (Exception ex) when (attempt < MaxAttempts && !ct.IsCancellationRequested &&
                                           ex is HttpRequestException or IOException)
                {
                    System.Diagnostics.Debug.WriteLine($"[BarkModelDownloader] {name} attempt {attempt} failed: {ex.Message}. Resuming.");
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
                }
            }

            if (!await HasHashAsync(tmpPath, sha256, ct))
            {
                File.Delete(tmpPath);
                throw new InvalidDataException($"{name} failed verification. Please try the download again.");
            }

            File.Move(tmpPath, destPath, overwrite: true);
            completedBytes += size;
        }
    }

    /// <summary>
    /// Fetch one file into tmpPath, continuing from whatever is already there.
    /// Throws IOException if the connection drops, stalls or ends early.
    /// </summary>
    private static async Task DownloadFileAsync(
        HttpClient http, string name, long size, string tmpPath,
        long completedBytes, IProgress<double>? progress, CancellationToken ct)
    {
        long resumeFrom = File.Exists(tmpPath) ? new FileInfo(tmpPath).Length : 0;
        if (resumeFrom >= size) { File.Delete(tmpPath); resumeFrom = 0; }

        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, RepoUrl + name);
            if (resumeFrom > 0) request.Headers.Range = new RangeHeaderValue(resumeFrom, null);

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token);
            response.EnsureSuccessStatusCode();
            if (resumeFrom > 0 && response.StatusCode != HttpStatusCode.PartialContent)
                resumeFrom = 0; // server ignored the range — start over

            await using var modelStream = await response.Content.ReadAsStreamAsync(stall.Token);
            await using var fileStream = new FileStream(tmpPath, resumeFrom > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write);

            var buffer = new byte[81920]; // 80 KB chunks
            long fileBytes = resumeFrom;
            int read;

            while ((read = await modelStream.ReadAsync(buffer, stall.Token)) > 0)
            {
                stall.CancelAfter(StallTimeout); // data arrived — reset the stall timer
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                fileBytes += read;
                progress?.Report(100.0 * (completedBytes + fileBytes) / TotalBytes);
            }

            if (fileBytes < size)
                throw new IOException($"Connection closed early ({fileBytes:N0} of {size:N0} bytes).");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"No data received for {StallTimeout.TotalSeconds:0} s.");
        }
    }

    private static async Task<bool> HasHashAsync(string path, string expectedSha256, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexString(hash).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
    }
}
