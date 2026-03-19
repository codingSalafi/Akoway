using System.Text.Json;
using VoiceKeyboard.Models;

namespace VoiceKeyboard.Services;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> to/from a JSON file in
/// %AppData%\Akoway\settings.json.
/// </summary>
public sealed class SettingsService
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Akoway");

    private static readonly string SettingsPath =
        Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public AppSettings Settings { get; private set; } = new();

    public async Task LoadAsync()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                await using var fs = File.OpenRead(SettingsPath);
                var loaded = await JsonSerializer.DeserializeAsync<AppSettings>(fs, JsonOptions);
                if (loaded != null) Settings = loaded;
            }
        }
        catch
        {
            // Corrupt file → use defaults
            Settings = new AppSettings();
        }
    }

    public async Task SaveAsync()
    {
        Directory.CreateDirectory(SettingsDir);
        await using var fs = File.Create(SettingsPath);
        await JsonSerializer.SerializeAsync(fs, Settings, JsonOptions);
    }

    public void Save() => SaveAsync().GetAwaiter().GetResult();
}
