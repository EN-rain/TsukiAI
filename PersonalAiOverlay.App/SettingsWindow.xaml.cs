using System.Windows;
using PersonalAiOverlay.App.Models;

namespace PersonalAiOverlay.App;

public partial class SettingsWindow : Window
{
    public AppSettings Result { get; private set; }
    private readonly Action? _clearHistory;

    public SettingsWindow(
        AppSettings initial,
        string? fiveMinuteSummaries = null,
        string? hourlySummaries = null,
        Window? owner = null,
        Action? clearHistory = null
    )
    {
        if (owner != null)
            Owner = owner;
        InitializeComponent();
        Result = initial;
        _clearHistory = clearHistory;
        DataContext = new SettingsVm
        {
            IsActivityLoggingEnabled = initial.IsActivityLoggingEnabled,
            SampleIntervalMinutesText = initial.SampleIntervalMinutes.ToString(),
            SummarizeIntervalMinutesText = initial.SummarizeIntervalMinutes.ToString(),
            RetentionHoursForRawText = initial.RetentionHoursForRaw.ToString(),
            CaptureModeIndex = initial.CaptureMode == ScreenshotCaptureMode.ActiveWindow ? 1 : 0,
            ModelName = initial.ModelName,
            AutoStartOllama = initial.AutoStartOllama,
            StopOllamaOnExit = initial.StopOllamaOnExit,
            StartupGreetingEnabled = initial.StartupGreetingEnabled,
            ProactiveMessagesEnabled = initial.ProactiveMessagesEnabled,
            ProactiveMessageAfterMinutesText = initial.ProactiveMessageAfterMinutes.ToString(),
            UseGpu = initial.UseGpu,
            FiveMinuteSummariesText = fiveMinuteSummaries ?? "",
            HourlySummariesText = hourlySummaries ?? "",
        };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsVm vm)
            return;

        var sampleMinutes = ParseIntOr(vm.SampleIntervalMinutesText, AppSettings.Default.SampleIntervalMinutes, min: 1, max: 1440);
        var summarizeMinutes = ParseIntOr(vm.SummarizeIntervalMinutesText, AppSettings.Default.SummarizeIntervalMinutes, min: 1, max: 1440);
        var retentionHours = ParseIntOr(vm.RetentionHoursForRawText, AppSettings.Default.RetentionHoursForRaw, min: 1, max: 168);
        var proactiveAfterMinutes = ParseIntOr(vm.ProactiveMessageAfterMinutesText, AppSettings.Default.ProactiveMessageAfterMinutes, min: 1, max: 1440);

        var captureMode = vm.CaptureModeIndex == 1 ? ScreenshotCaptureMode.ActiveWindow : ScreenshotCaptureMode.FullScreen;

        Result = new AppSettings(
            IsActivityLoggingEnabled: vm.IsActivityLoggingEnabled,
            SampleIntervalMinutes: sampleMinutes,
            SummarizeIntervalMinutes: summarizeMinutes,
            CaptureMode: captureMode,
            RetentionHoursForRaw: retentionHours,
            ModelName: string.IsNullOrWhiteSpace(vm.ModelName) ? AppSettings.Default.ModelName : vm.ModelName.Trim(),
            AutoStartOllama: vm.AutoStartOllama,
            StopOllamaOnExit: vm.StopOllamaOnExit,
            StartupGreetingEnabled: vm.StartupGreetingEnabled,
            ProactiveMessagesEnabled: vm.ProactiveMessagesEnabled,
            ProactiveMessageAfterMinutes: proactiveAfterMinutes,
            UseGpu: vm.UseGpu
        );
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        try { _clearHistory?.Invoke(); } catch { }
    }

    private sealed class SettingsVm
    {
        public bool IsActivityLoggingEnabled { get; set; }
        public string SampleIntervalMinutesText { get; set; } = "5";
        public string SummarizeIntervalMinutesText { get; set; } = "60";
        public string RetentionHoursForRawText { get; set; } = "1";
        public int CaptureModeIndex { get; set; } = 0;
        public string ModelName { get; set; } = "llama3.2:3b";
        public bool AutoStartOllama { get; set; } = true;
        public bool StopOllamaOnExit { get; set; } = true;
        public bool StartupGreetingEnabled { get; set; } = true;
        public bool ProactiveMessagesEnabled { get; set; } = true;
        public string ProactiveMessageAfterMinutesText { get; set; } = "10";
        public bool UseGpu { get; set; } = true;
        public string FiveMinuteSummariesText { get; set; } = "";
        public string HourlySummariesText { get; set; } = "";
    }

    private static int ParseIntOr(string? text, int fallback, int min, int max)
    {
        if (!int.TryParse((text ?? "").Trim(), out var value))
            value = fallback;

        if (value < min) value = min;
        if (value > max) value = max;
        return value;
    }
}

