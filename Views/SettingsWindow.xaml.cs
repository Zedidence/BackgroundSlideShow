using System.Windows;
using BackgroundSlideShow.Services;
using Wpf.Ui.Controls;

namespace BackgroundSlideShow.Views;

public partial class SettingsWindow : FluentWindow
{
    private readonly AppSettings _appSettings;

    public SettingsWindow(AppSettings appSettings)
    {
        InitializeComponent();
        _appSettings = appSettings;

        // Load current values into controls
        ChkLaunchOnStartup.IsChecked    = _appSettings.LaunchOnStartup;
        ChkTransitionsEnabled.IsChecked = _appSettings.TransitionsEnabled;
        SliderTransitionDuration.Value  = _appSettings.TransitionDurationMs;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _appSettings.LaunchOnStartup      = ChkLaunchOnStartup.IsChecked == true;
        _appSettings.TransitionsEnabled   = ChkTransitionsEnabled.IsChecked == true;
        _appSettings.TransitionDurationMs = (int)SliderTransitionDuration.Value;

        _appSettings.Save();
        Close();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            "This will permanently delete:\n\n" +
            "  \u2022 All indexed image folders and library data\n" +
            "  \u2022 The thumbnail cache\n" +
            "  \u2022 App settings\n\n" +
            "Your actual image files on disk are not affected.\n\n" +
            "The application will close after the reset. Are you sure?",
            "Reset All Data",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (result != System.Windows.MessageBoxResult.Yes) return;

        Close();
        ((App)Application.Current).ResetAndShutdown();
    }
}
