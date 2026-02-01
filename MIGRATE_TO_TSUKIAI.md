# TsukiAI Migration Plan

## Overview
Rebuild the entire project as "TsukiAI" while preserving all functionality.

## New Project Structure

```
personalAi/
├── TsukiAI.sln                          # New solution file
├── TsukiAI.Core/                        # Renamed from TsukiAI.Core
│   ├── TsukiAI.Core.csproj
│   ├── Platform/
│   │   ├── IClipboard.cs
│   │   ├── IClock.cs
│   │   └── IUrlOpener.cs
│   ├── Utilities/
│   │   └── SimpleCalculator.cs
│   ├── Intents/
│   │   ├── IIntentHandler.cs
│   │   ├── IntentContext.cs
│   │   ├── IntentResult.cs
│   │   └── BuiltIn/
│   │       ├── TimeIntentHandler.cs
│   │       ├── OpenUrlIntentHandler.cs
│   │       ├── CalcIntentHandler.cs
│   │       ├── ClipboardIntentHandler.cs
│   │       └── HelpIntentHandler.cs
│   └── Assistant/
│       ├── AssistantEngine.cs
│       ├── AssistantEngineDependencies.cs
│       └── AssistantResponse.cs
│
└── TsukiAI.Desktop/                     # Renamed from TsukiAI.App
    ├── TsukiAI.Desktop.csproj
    ├── App.xaml
    ├── App.xaml.cs
    ├── MainWindow.xaml
    ├── MainWindow.xaml.cs
    ├── Constants.cs
    ├── Models/
    │   ├── AppSettings.cs
    │   ├── ActivitySample.cs
    │   ├── MemoryEntry.cs
    │   └── ScreenshotCaptureMode.cs
    ├── Services/
    │   ├── Ollama/
    │   │   ├── OllamaClient.cs
    │   │   └── OllamaProcessManager.cs
    │   ├── Collectors/
    │   │   ├── ForegroundWindowCollector.cs
    │   │   ├── IdleTimeProvider.cs
    │   │   └── ScreenshotCapturer.cs
    │   ├── Logging/
    │   │   ├── ActivityLoggingService.cs
    │   │   └── ActivitySampleStore.cs
    │   ├── Memory/
    │   │   ├── MemoryStore.cs
    │   │   └── MemoryLearner.cs
    │   ├── ResponseCache.cs
    │   ├── PromptBuilder.cs
    │   └── SettingsService.cs
    ├── ViewModels/
    │   ├── OverlayViewModel.cs
    │   ├── RelayCommand.cs
    │   └── AsyncRelayCommand.cs
    └── Interop/
        └── NativeMethods.cs
```

## Namespace Mapping

| Old Namespace | New Namespace |
|---------------|---------------|
| `TsukiAI.Core` | `TsukiAI.Core` |
| `TsukiAI.Core.Assistant` | `TsukiAI.Core.Assistant` |
| `TsukiAI.Core.Intents` | `TsukiAI.Core.Intents` |
| `TsukiAI.Core.Platform` | `TsukiAI.Core.Platform` |
| `TsukiAI.Core.Utilities` | `TsukiAI.Core.Utilities` |
| `TsukiAI.App` | `TsukiAI.Desktop` |
| `TsukiAI.App.Models` | `TsukiAI.Desktop.Models` |
| `TsukiAI.App.Services` | `TsukiAI.Desktop.Services` |
| `TsukiAI.App.ViewModels` | `TsukiAI.Desktop.ViewModels` |

## Key Changes

1. **Project Names**: 
   - `TsukiAI.Core` → `TsukiAI.Core`
   - `TsukiAI.App` → `TsukiAI.Desktop`

2. **AppConstants Updates**:
   ```csharp
   // Old
   public static class AppConstants
   {
       public const string OwnerName = "Rain";
   }
   
   // New
   public static class AppConstants
   {
       public const string OwnerName = "Rain";
       public const string AppName = "TsukiAI";
       public const string DefaultAssistantName = "Tsuki";
   }
   ```

3. **Window Title**: "TsukiAI Chat" → "TsukiAI"

4. **Modelfile**: Keep the same but update comments to reference TsukiAI

## Migration Steps

1. Create new solution and projects
2. Copy all .cs files with updated namespaces
3. Copy all .xaml files with updated namespaces
4. Update using statements
5. Update project references
6. Build and test

## Simplification Opportunity

Since this is a rebuild, consider:
1. Removing unused features
2. Consolidating similar services
3. Improving the architecture
4. Adding proper dependency injection
5. Better separation of concerns
