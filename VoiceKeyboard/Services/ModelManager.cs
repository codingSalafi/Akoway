using VoiceKeyboard.Core;
using VoiceKeyboard.Models;

namespace VoiceKeyboard.Services;

/// <summary>
/// Manages local Whisper model files: listing, downloading, deleting,
/// and providing paths to the active model.
/// </summary>
public sealed class ModelManager
{
    private readonly SettingsService _settings;

    public ModelManager(SettingsService settings)
    {
        _settings = settings;
        Directory.CreateDirectory(settings.Settings.ModelsDirectory);
    }

    public string ModelsDirectory => _settings.Settings.ModelsDirectory;

    public string GetModelPath(WhisperModelSize size)
        => WhisperModelDownloader.GetModelPath(size, ModelsDirectory);

    public string GetActiveModelPath()
        => GetModelPath(_settings.Settings.ActiveModel);

    public bool IsDownloaded(WhisperModelSize size)
        => WhisperModelDownloader.IsDownloaded(size, ModelsDirectory);

    public IReadOnlyList<ModelInfo> GetAllModels()
    {
        return new[]
        {
            WhisperModelSize.BaseEn,
            WhisperModelSize.SmallEn,
            WhisperModelSize.MediumEn
        }
        .Select(size =>
        {
            var path = GetModelPath(size);
            bool exists = File.Exists(path);
            return new ModelInfo
            {
                Size = size,
                FileName = WhisperModelDownloader.GetFileName(size),
                IsDownloaded = exists,
                FileSizeBytes = exists ? new FileInfo(path).Length : 0
            };
        })
        .ToList();
    }

    /// <summary>
    /// Download a model with progress reporting.
    /// Progress value = bytes downloaded so far.
    /// </summary>
    public async Task DownloadModelAsync(
        WhisperModelSize size,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        await WhisperModelDownloader.DownloadAsync(size, ModelsDirectory, progress, ct);
    }

    public void DeleteModel(WhisperModelSize size)
    {
        var path = GetModelPath(size);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>
    /// Suggest the best model based on available GPU VRAM.
    /// Falls back to base.en if nothing else is usable.
    /// </summary>
    public WhisperModelSize SuggestBestModel()
    {
        // Heuristic: read VRAM via NVML or just use a safe default.
        // small.en fits easily in 4 GB; medium.en is borderline.
        if (IsDownloaded(WhisperModelSize.SmallEn))
            return WhisperModelSize.SmallEn;
        if (IsDownloaded(WhisperModelSize.BaseEn))
            return WhisperModelSize.BaseEn;
        return WhisperModelSize.SmallEn; // will prompt download
    }
}
