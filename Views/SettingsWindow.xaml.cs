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
}
