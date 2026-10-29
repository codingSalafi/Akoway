# Akoway

> **Offline, privacy-first voice dictation for Windows — powered by Underdog Bark 3.0 Pro.**

[![License: MIT](https://img.shields.io/badge/License-MIT-brightgreen.svg)](LICENSE)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows%2010%2F11-blue.svg)]()
[![.NET 8](https://img.shields.io/badge/.NET-8.0-purple.svg)]()

---

## What is Akoway?

Akoway is a lightweight Windows app that lets you dictate text into **any** application using your voice. Press a hotkey, speak, click to stop — your words appear wherever your cursor is. No cloud, no subscription, no internet required after setup. Everything runs locally on your machine using [Underdog Bark 3.0 Pro](https://huggingface.co/ConwayResearch/Underdog-Bark-3.0-Pro), an open speech recognition model.

---

## Features

- 🎙️ **Offline & Private** — audio never leaves your device
- ⚡ **Works anywhere** — dictate into VS Code, Notion, Word, browsers, anywhere
- 🪟 **Floating overlay** — minimal pill UI that stays out of your way
- ⌨️ **Global hotkey** — bring it up with `Ctrl+Alt+Space` from anywhere
- 🤖 **Bark 3.0 Pro** — state-of-the-art open speech recognition, with punctuation and capitals
- 🔧 **Customisable** — change hotkey, injection method, model
- 🚀 **Launches on startup** — always ready when you need it

---

## Download

**[⬇ Download Akoway for Windows](https://codingSalafi.github.io/akoway-website)**

> Windows 10 / 11 (64-bit) · No .NET installation required · ~50 MB installer

---

## Getting Started

1. Run `Akoway-Setup-v1.0.0.exe`
2. On first launch, download the speech model (~910 MB, internet required for this step only)
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
├── Core/               # DictationController, BarkEngine, HotkeyManager, VAD logic
├── Models/             # AppSettings, ModelInfo, enums
├── Native/             # Win32 P/Invoke (NativeMethods)
├── Services/           # SettingsService, ModelManager, StartupService
├── UI/                 # OverlayWindow, SettingsWindow, TrayManager, SetupWizard
└── Resources/
    └── Icons/          # App and tray icons
```

---

## Models

Akoway downloads its speech model on first run. Nothing is bundled in the installer.

| Model | Size | Speed (CPU) | Notes |
|-------|------|-------------|-------|
| Bark 3.0 Pro | ~910 MB | ~15× faster than real time | English, punctuation and capitals |

Bark 3.0 Pro is by [Underdog / Conway Research](https://huggingface.co/ConwayResearch/Underdog-Bark-3.0-Pro) (CC-BY 4.0), a fine-tune of NVIDIA Parakeet TDT 0.6B. Akoway runs an ONNX conversion of it with ONNX Runtime on the CPU; the encoder's matrix weights are int8, which keeps the same accuracy as the original. No GPU is needed.

To rebuild the ONNX files from the original weights, see [`tools/bark-onnx`](tools/bark-onnx):

```bash
pip install torch --index-url https://download.pytorch.org/whl/cpu
pip install -r tools/bark-onnx/requirements.txt
hf download ConwayResearch/Underdog-Bark-3.0-Pro --local-dir bark-pro
python tools/bark-onnx/export_bark_onnx.py --model bark-pro --out bark-pro-onnx
python tools/bark-onnx/compare_onnx.py --model bark-pro --onnx bark-pro-onnx some-clip.wav
```

---

## Settings Reference

| Setting | Default | Notes |
|---------|---------|-------|
| Hotkey | Ctrl+Alt+Space | Configurable in Settings |
| Mode | Toggle | Toggle or Push-to-Talk |
| Active model | Bark 3.0 Pro | |
| Injection | Auto | SendInput → Clipboard fallback |
| Launch on startup | Off | Configurable in Settings |

---

## Troubleshooting

| Problem | Fix |
|---------|-----|
| No text appears | Try **Injection: Clipboard** mode in Settings |
| Hotkey not working | Another app may own that key — change it in Settings |
| High latency | Close other heavy apps — the model runs on the CPU |
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

- [Underdog Bark 3.0 Pro](https://huggingface.co/ConwayResearch/Underdog-Bark-3.0-Pro) by Conway Research — the speech recognition model (CC-BY 4.0), derived from [NVIDIA Parakeet TDT 0.6B v3](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3) and moondream/parakeet-ultra
- [ONNX Runtime](https://onnxruntime.ai) — runs the model
- [Hardcodet.NotifyIcon.Wpf](https://github.com/hardcodet/wpf-notifyicon) — system tray support

---

## License

MIT © 2026 Akoway Contributors — see [LICENSE](LICENSE) for full text.
