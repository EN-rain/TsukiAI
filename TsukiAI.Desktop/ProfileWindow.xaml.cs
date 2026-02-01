using System.Windows;

namespace TsukiAI.Desktop;

public partial class ProfileWindow : Window
{
    public ProfileWindow()
    {
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

