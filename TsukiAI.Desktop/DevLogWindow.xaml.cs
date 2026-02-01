using System.Windows;
using TsukiAI.Desktop.Services;

namespace TsukiAI.Desktop;

public partial class DevLogWindow : Window
{
    public DevLogWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            RefreshLog();
            DevLog.LogUpdated += OnLogUpdated;
        };
        Closed += (_, _) => DevLog.LogUpdated -= OnLogUpdated;
    }

    private void OnLogUpdated()
    {
        if (Dispatcher.CheckAccess())
            RefreshLog();
        else
            Dispatcher.BeginInvoke(RefreshLog);
    }

    private void RefreshLog()
    {
        LogBox.Text = DevLog.GetText();
        LogBox.ScrollToEnd();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshLog();

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        DevLog.Clear();
        RefreshLog();
    }
}
