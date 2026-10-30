using VoiceKeyboard.Core;
using VoiceKeyboard.Models;

namespace VoiceKeyboard.Services;

/// <summary>
/// Manages local speech model folders: listing, downloading, deleting,
/// and providing paths to the active model.
/// </summary>
public sealed class ModelManager
{
    private readonly SettingsService _settings;

    public ModelManager(SettingsService settings)
    {
        _settings = settings;
        Directory.CreateDirectory(settings.Settings.ModelsDirectory);
        DeleteWhisperModels();
    }

    public string ModelsDirectory => _settings.Settings.ModelsDirectory;

    public string GetModelPath(SpeechModel model)
        => Path.Combine(ModelsDirectory, BarkModels.For(model).FolderName);

    public string GetActiveModelPath()
        => GetModelPath(_settings.Settings.ActiveModel);

    public bool IsDownloaded(SpeechModel model)
        => BarkModelDownloader.IsDownloaded(BarkModels.For(model), GetModelPath(model));

    public IReadOnlyList<ModelInfo> GetAllModels()
    {
        return Enum.GetValues<SpeechModel>()
        .Select(model =>
        {
            var path = GetModelPath(model);
            bool exists = IsDownloaded(model);
            return new ModelInfo
            {
                Model = model,
                FileName = BarkModels.For(model).FolderName,
                IsDownloaded = exists,
                FileSizeBytes = exists
                    ? new DirectoryInfo(path).EnumerateFiles().Sum(f => f.Length)
                    : 0
            };
        })
        .ToList();
    }

    /// <summary>
    /// Download a model with progress reporting.
    /// Progress value = percent complete (0–100).
    /// </summary>
    public async Task DownloadModelAsync(
        SpeechModel model,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        await BarkModelDownloader.DownloadAsync(BarkModels.For(model), GetModelPath(model), progress, ct);
    }

    public void DeleteModel(SpeechModel model)
    {
        var path = GetModelPath(model);
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    /// <summary>
    /// Earlier versions of Akoway used Whisper (ggml-*.bin). Those files can't be
    /// used any more, so free the space (up to ~1.5 GB each).
    /// </summary>
    private void DeleteWhisperModels()
    {
        foreach (var file in Directory.EnumerateFiles(ModelsDirectory, "ggml-*.bin*"))
        {
            try { File.Delete(file); }
            catch { /* in use or read-only — leave it */ }
        }
    }
}
