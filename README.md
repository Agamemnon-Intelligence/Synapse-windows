# Synapse for Windows

Run AI models on your PC, or use free cloud models, in one app. The Windows edition of [Synapse for macOS](https://github.com/Agamemnon-Intelligence/Synapse-macOS).

- **Local models**: browse HuggingFace, get recommendations checked against your GPU (VRAM) and RAM, and run them with llama.cpp. Synapse installs the right llama.cpp build for you (CUDA for NVIDIA, Vulkan for AMD/Intel, CPU, ROCm, ARM64) and fits as many layers on the GPU as free VRAM allows. Downloads resume after interruptions.
- **Free cloud models**: Google AI Studio, Groq, OpenRouter's free models, or any OpenAI-compatible server (Ollama, LM Studio, OpenAI, Mistral, …). Keys are stored in **Windows Credential Manager**.
- **Capabilities** you can turn on per chat: web search, image generation, computer access (PowerShell, files, apps, clipboard, screenshots), a browser the model can drive, and websites hosted on localhost. Risky actions ask first.
- **Thinking levels**: Fast, Normal, Think hard, and Think harder (adds a self-review pass).
- **Projects** with shared files and instructions; the model can read, search, write and edit project files (PDF, Word, PowerPoint and Excel included).
- **Usage view** with live Groq limits and request counts for every provider.
- **Windows touches**: tray icon (the local API keeps running), start with Windows, `Ctrl+Alt+Space` global shortcut, notifications when replies finish, dark/light/system theme, drag and drop, paste images, command palette (`Ctrl+K`).
- **Terminal**: `synapse chat`, `synapse run <model> "prompt"`, `synapse models`. Synapse also serves every model on `http://127.0.0.1:11435/v1` (OpenAI-compatible).

## Install

Download `Synapse-<version>-win-x64-setup.exe` (or `arm64`) from [Releases](https://github.com/Agamemnon-Intelligence/Synapse-windows/releases) and run it. No admin rights needed. Requires Windows 10 1809+ and the WebView2 Runtime (built into Windows 11; Synapse offers the download if it's missing).

## Build

Requires the .NET 10 SDK. On Windows:

```powershell
./build.ps1              # tests, app, CLI, portable zip, and an installer if Inno Setup 6 is installed
./build.ps1 -Arch arm64
```

Or just run it: `dotnet run --project src/Synapse.App`.

### Project layout

| Path | What |
|---|---|
| `src/Synapse.Core` | Everything that isn't Windows-specific: agent loop, tools, providers, HuggingFace, llama.cpp engine, local API, websites, usage, and the UI bridge. |
| `src/Synapse.App` | WPF shell: WebView2 interface (`ui/`), the browser panel, Credential Manager, PowerShell, screenshots, tray, hotkey. |
| `src/Synapse.App/ui` | The interface (HTML/CSS/JS, no build step). |
| `src/Synapse.Cli` | The `synapse` command (published to `cli\` next to the app). |
| `src/Synapse.DevHost` | Runs the real app core behind any browser, for working on the UI from any OS: `dotnet run --project src/Synapse.DevHost` then open http://127.0.0.1:3939. |
| `tests/` | xUnit tests (including the agent loop against a fake OpenAI server) and UI tests. |

## Türkçe

Synapse, yapay zekâ modellerini bilgisayarınızda yerel olarak (llama.cpp) veya ücretsiz bulut servisleriyle (Google AI Studio, Groq, OpenRouter) tek bir uygulamada çalıştırır. Web araması, görsel üretme, bilgisayar erişimi (PowerShell, dosyalar, ekran görüntüsü), modelin kullanabildiği tarayıcı ve localhost'ta web sitesi oluşturma özellikleri sohbet bazında açılıp kapatılabilir. API anahtarları Windows Kimlik Bilgisi Yöneticisi'nde saklanır.

## License

MIT © 2026 mertyesileducation
