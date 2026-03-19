namespace VoiceKeyboard.Models;

public enum DictationStatus
{
    Idle,           // Waiting for hotkey
    Listening,      // Microphone open, capturing audio
    Processing,     // Transcribing buffered audio
    Injecting,      // Inserting text into target app
    Error           // Something went wrong
}

public class DictationStateChangedEventArgs : EventArgs
{
    public DictationStatus NewState { get; }
    public string? Message { get; }

    public DictationStateChangedEventArgs(DictationStatus state, string? message = null)
    {
        NewState = state;
        Message = message;
    }
}

public class ModelInfo
{
    public string Name { get; set; } = "";
    public string FileName { get; set; } = "";
    public WhisperModelSize Size { get; set; }
    public long FileSizeBytes { get; set; }
    public bool IsDownloaded { get; set; }
    public string DisplayName => Size switch
    {
        WhisperModelSize.BaseEn   => "base.en  (~75 MB)  — Fast",
        WhisperModelSize.SmallEn  => "small.en (~242 MB) — Balanced (Default)",
        WhisperModelSize.MediumEn => "medium.en (~1.5 GB) — High Accuracy",
        _ => Name
    };
}
