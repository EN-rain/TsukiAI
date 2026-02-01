# TsukiAI (TsukiAI Overlay)

A lightweight, **always-on-top AI companion** for Windows (WPF).  
Runs locally using [Ollama](https://ollama.com/), providing an anime-style interactive assistant that watches your activity and keeps you company.

![Status](https://img.shields.io/badge/Status-Active-success)
![Platform](https://img.shields.io/badge/Platform-Windows%20(x64)-blue)
![AI](https://img.shields.io/badge/AI-Ollama%20Local-orange)

## ✨ Features

- **Overlay UI**: Transparent, click-through, and always on top. Hides from the taskbar.
- **Global Hotkey**: Press `Ctrl + Alt + Space` to toggle the visibility instantly.
- **Local AI (Privacy)**: Uses mostly `llama3.2:3b` running locally on your machine.
- **Activity & Emotion System**:
  - Monitors active windows to summarize what you are working on.
  - **Tsuki** (the AI personality) reacts to your work habits (e.g., praises focus, teases for being idle).
  - Adapts her UI color based on her "mood" (Happy=Pink, Focused=Green, Angry=Red, etc.).
- **Command Engine**:
  - `calc <math>`: Quick calculations (e.g., `calc 100/4`).
  - `open <url>`: Open websites quickly.
  - `time`: Check the time.
  - `copy <text>`: Copy text to clipboard.
  - Falls back to **Chat Mode** if no command matches.

## 🚀 Getting Started

### Prerequisites
1.  **Windows 10/11 (x64)**
2.  **[Ollama](https://ollama.com/download)** installed and running.

### Installation & Run
1.  Download the latest release or build from source.
2.  Run `TsukiAI.App.exe`.
3.  The app will start quietly in the **System Tray**.
4.  Press `Ctrl + Alt + Space` to see it.

> **Note**: On first run, it will automatically pull the required AI model (`llama3.2:3b`) if you don't have it. This may take a few minutes.

## 🛠 Configuration

### Custom Personality (Tsuki)
The app injects the "Tsuki" personality automatically. However, if you want to customize it manually, a `Modelfile` is provided in the repo.

**To manually update the model:**
```bash
ollama create tsuki -f Modelfile
```
Then update `settings.json` in `%APPDATA%\TsukiAI` to use `tsuki` as the model name.

## ⌨️ Shortcuts & Commands

| Command | Description |
| :--- | :--- |
| `Ctrl + Alt + Space` | Toggle Overlay |
| `calc 1+1` | Calculate math expression |
| `open google.com` | Open a website in default browser |
| `quit` / `exit` | Close the application |

## 📦 Build from Source

Requirements: **.NET 8 SDK**

```powershell
# Clone the repo
git clone https://github.com/EN-rain/TsukiAI.git

# Run the app
dotnet run --project TsukiAI.App
```