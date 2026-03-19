# Akoway

> **Offline, privacy-first voice dictation for Windows — powered by OpenAI Whisper.**

[![License: MIT](https://img.shields.io/badge/License-MIT-brightgreen.svg)](LICENSE)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows%2010%2F11-blue.svg)]()
[![.NET 8](https://img.shields.io/badge/.NET-8.0-purple.svg)]()

---

## What is Akoway?

Akoway is a lightweight Windows app that lets you dictate text into **any** application using your voice. Press a hotkey, speak, click to stop — your words appear wherever your cursor is. No cloud, no subscription, no internet required after setup. Everything runs locally on your machine using [OpenAI's Whisper](https://github.com/openai/whisper) speech recognition.

---

## Features

- 🎙️ **Offline & Private** — audio never leaves your device
- ⚡ **Works anywhere** — dictate into VS Code, Notion, Word, browsers, anywhere
- 🪟 **Floating overlay** — minimal pill UI that stays out of your way
- ⌨️ **Global hotkey** — bring it up with `Ctrl+Alt+Space` from anywhere
- 🤖 **Multiple Whisper models** — choose speed vs accuracy (Base / Small / Medium)
- 🔧 **Customisable** — change hotkey, injection method, model, GPU acceleration
- 🚀 **Launches on startup** — always ready when you need it

---

## Download

**[⬇ Download Akoway for Windows](https://codingSalafi.github.io/akoway-website)**

> Windows 10 / 11 (64-bit) · No .NET installation required · ~50 MB installer

---

## Getting Started

1. Run `Akoway-Setup-v1.0.0.exe`
2. On first launch, choose and download a Whisper model (internet required for this step only)
3. Press `Ctrl+Alt+Space` anywhere to bring up the overlay
4. Click the mic to start dictating, click again to stop and insert text

---

## Building from Source

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8)
- Windows 10/11 64-bit
- Visual Studio 2022 (recommended) or VS Code with C# extension

### Steps

```bash
git clone https://github.com/codingSalafi/akoway.git
cd akoway
```

Open `VoiceKeyboard/VoiceKeyboard.sln` in Visual Studio and press **F5** to run.

Or from the command line:

```bash
dotnet run --project VoiceKeyboard/VoiceKeyboard/VoiceKeyboard.csproj
```

### Publishing (release build)

```powershell
dotnet publish VoiceKeyboard/VoiceKeyboard/VoiceKeyboard.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishReadyToRun=true `
  -o publish/
```

---

## Project Structure

```
VoiceKeyboard/
├── Core/               # DictationController, HotkeyManager, VAD logic
├── Models/             # AppSettings, ModelInfo, enums
├── Native/             # Win32 P/Invoke (NativeMethods)
├── Services/           # SettingsService, ModelManager, StartupService
├── UI/                 # OverlayWindow, SettingsWindow, TrayManager, SetupWizard
└── Resources/
    └── Icons/          # App and tray icons
```

---

## Models

Akoway downloads Whisper models on first run. Nothing is bundled in the installer.

| Model | Size | Speed | Accuracy |
|-------|------|-------|----------|
| base.en | 142 MB | Fastest | Good |
| small.en | 244 MB | Balanced | Better ⭐ Recommended |
| medium.en | 769 MB | Slower | Best |

---

## Settings Reference

| Setting | Default | Notes |
|---------|---------|-------|
| Hotkey | Ctrl+Alt+Space | Configurable in Settings |
| Mode | Toggle | Toggle or Push-to-Talk |
| Active model | small.en | base.en / small.en / medium.en |
| GPU | Enabled | Requires CUDA runtime |
| Injection | Auto | SendInput → Clipboard fallback |
| Launch on startup | Off | Configurable in Settings |

---

## Troubleshooting

| Problem | Fix |
|---------|-----|
| No text appears | Try **Injection: Clipboard** mode in Settings |
| Hotkey not working | Another app may own that key — change it in Settings |
| High latency | Enable GPU in Settings or switch to base.en |
| Microphone not found | Check Windows Sound settings — default mic is used |

---

## Contributing

Contributions are welcome — bug fixes, new features, or even a port to macOS/Linux!

1. Fork the repo
2. Create a branch: `git checkout -b feature/my-feature`
3. Commit your changes
4. Open a Pull Request

---

## Acknowledgements

- [whisper.net](https://github.com/sandrohanea/whisper.net) — .NET bindings for Whisper
- [Hardcodet.NotifyIcon.Wpf](https://github.com/hardcodet/wpf-notifyicon) — system tray support
- [OpenAI Whisper](https://github.com/openai/whisper) — the underlying speech recognition model

---

## License

MIT © 2026 Akoway Contributors — see [LICENSE](LICENSE) for full text.
