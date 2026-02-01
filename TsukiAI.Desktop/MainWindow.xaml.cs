using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using TsukiAI.Desktop.Models;
using TsukiAI.Desktop.Services;
using TsukiAI.Desktop.ViewModels;

namespace TsukiAI.Desktop;

public partial class MainWindow : Window
{
    public MainWindow(OverlayViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;

        vm.PropertyChanged += VmOnPropertyChanged;
        Closing += MainWindow_Closing;
        
        // Show loading screen initially if auto-start is enabled
        UpdateLoadingScreen(vm);
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (System.Windows.Application.Current is not App app || app.QuitRequested)
            return;
        e.Cancel = true;
        Hide();
    }

    private void VmOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OverlayViewModel.ConversationText) ||
            e.PropertyName == nameof(OverlayViewModel.DisplayedConversationText))
        {
            // ConversationScroll.ScrollToEnd(); // x:Name not set in XAML
        }

        if (e.PropertyName == nameof(OverlayViewModel.IsThinking)) 
        {
            if (DataContext is OverlayViewModel vm)
            {
                var storyboard = ThinkingOverlay.Resources["ThinkingDotsStoryboard"] as Storyboard;
                if (vm.IsThinking)
                    storyboard?.Begin(ThinkingOverlay, true);
                else
                    storyboard?.Stop(ThinkingOverlay);
            }
        }
        
        if (e.PropertyName == nameof(OverlayViewModel.IsLoadingModel) ||
            e.PropertyName == nameof(OverlayViewModel.LoadingStatusText))
        {
            if (DataContext is OverlayViewModel vm)
            {
                UpdateLoadingScreen(vm);
            }
        }
    }
    
    private void UpdateLoadingScreen(OverlayViewModel vm)
    {
        if (vm.IsLoadingModel)
        {
            // LoadingOverlay.Visibility = Visibility.Visible; // x:Name not set
            // InputTextBox.IsEnabled = false; // x:Name not set
            // SendButton.IsEnabled = false; // x:Name not set
        }
        else
        {
            // LoadingOverlay.Visibility = Visibility.Collapsed; // x:Name not set
            // InputTextBox.IsEnabled = true; // x:Name not set
            // SendButton.IsEnabled = true; // x:Name not set
            // InputTextBox.Focus(); // x:Name not set
        }
    }

    private void Input_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (DataContext is OverlayViewModel vm && !string.IsNullOrWhiteSpace(vm.InputText))
                vm.InputText = "";
            else
                Close();
            return;
        }

        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            // PreviewKeyDown ensures Enter doesn't insert a newline first.
            e.Handled = true;

            if (DataContext is OverlayViewModel vm)
                vm.SubmitCommand.Execute(null);
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not OverlayViewModel vm)
            return;

        var initial = SettingsService.Load();
        var dialog = new SettingsWindow(
            initial,
            fiveMinuteSummaries: vm.FiveMinuteSummariesText,
            hourlySummaries: vm.HourlySummariesText,
            owner: this,
            clearHistory: () => vm.ClearCommand.Execute(null)
        );

        if (dialog.ShowDialog() == true)
        {
            var saved = dialog.Result;
            if (System.Windows.Application.Current is App app)
                app.ApplySettings(saved, vm);
        }
    }

    private void Profile_Click(object sender, RoutedEventArgs e)
    {
        var w = new ProfileWindow { Owner = this };
        w.ShowDialog();
    }

    private void DevLog_Click(object sender, RoutedEventArgs e)
    {
        var w = new DevLogWindow { Owner = this };
        w.ShowDialog();
    }

    private void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is OverlayViewModel vm)
        {
            var w = new DiagnosticsWindow(vm.ModelName) { Owner = this };
            w.Show();
        }
    }
}