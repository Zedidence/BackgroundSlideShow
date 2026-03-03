using System.Windows;
using BackgroundSlideShow.Services;

namespace BackgroundSlideShow.Views;

public partial class SettingsWindow : Window
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
        // Persist settings on close
        _appSettings.LaunchOnStartup      = ChkLaunchOnStartup.IsChecked == true;
        _appSettings.TransitionsEnabled   = ChkTransitionsEnabled.IsChecked == true;
        _appSettings.TransitionDurationMs = (int)SliderTransitionDuration.Value;

        _appSettings.Save();
        Close();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "This will permanently delete:\n\n" +
            "  • All indexed image folders and library data\n" +
            "  • The thumbnail cache\n" +
            "  • App settings\n\n" +
            "Your actual image files on disk are not affected.\n\n" +
            "The application will close after the reset. Are you sure?",
            "Reset All Data",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        Close();
        ((App)Application.Current).ResetAndShutdown();
    }
}
