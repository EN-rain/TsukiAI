# Exporting TsukiAI Overlay

Yes, the application **will run** if you export it to an EXE and move it to another machine.

### ✅ Verification Results
I have successfully built the standalone executable for you.
- **Location**: `TsukiAI.App\bin\Release\net8.0-windows\win-x64\publish\TsukiAI.App.exe`
- **Size**: ~153 MB (Self-contained, includes .NET runtime)

### 📋 Requirements for Target Machine
1.  **Ollama**: must be installed.
2.  **Internet**: Required on *first run* to automatically pull the AI model (`llama3.2:3b`).
    - *Note: The app will automatically handle pulling the model if it's missing.*
3.  **OS**: Windows x64.

### ❓ FAQ
**Q: Do I need to copy the `Modelfile`?**
A: **No.** The app manages the personality ("Tsuki") internally by injecting it into the system prompt. You don't need to manually create the model on the new machine.

**Q: Where are settings saved?**
A: `%APPDATA%\TsukiAI` (created automatically).

### 🚀 How to Run
Simply copy the `TsukiAI.App.exe` to any folder on the new machine and run it.
