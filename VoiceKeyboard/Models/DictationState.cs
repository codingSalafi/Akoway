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
    public SpeechModel Model { get; set; }
    public long FileSizeBytes { get; set; }
    public bool IsDownloaded { get; set; }
    public string DisplayName => Model switch
    {
        SpeechModel.BarkPro   => "Bark 3.0 Pro (~910 MB) — Most accurate (Default)",
        SpeechModel.BarkClean => "Bark 3.0 Clean (~240 MB) — Removes ums and repeats",
        _ => Name
    };
}
