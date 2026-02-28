using System.Windows;
using System.Windows.Input;
using BackgroundSlideShow.Services;
using BackgroundSlideShow.ViewModels;
using H.NotifyIcon;
using H.NotifyIcon.Core;

namespace BackgroundSlideShow.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly AppSettings _appSettings;

    public MainWindow(MainViewModel vm, AppSettings appSettings)
    {
        InitializeComponent();
        _vm = vm;
        _appSettings = appSettings;
        DataContext = vm;
    }

    protected override async void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        await _vm.InitializeAsync();
    }

    private bool _libraryVisible = true;

    private void ToggleLibrary_Click(object sender, RoutedEventArgs e)
    {
        _libraryVisible = !_libraryVisible;
        LibraryPanel.Visibility    = _libraryVisible ? Visibility.Visible : Visibility.Collapsed;
        LibrarySplitter.Visibility = _libraryVisible ? Visibility.Visible : Visibility.Collapsed;
        LibraryColumn.Width        = _libraryVisible ? new GridLength(220) : new GridLength(0);
        SplitterColumn.Width       = _libraryVisible ? new GridLength(4)   : new GridLength(0);
        ToggleLibraryBtn.Content   = _libraryVisible ? "\u2261 Hide Library" : "\u2261 Show Library";
    }

    private void Overview_Click(object sender, RoutedEventArgs e)
    {
        GalleryPanel.Visibility = Visibility.Collapsed;
        MonitorContentArea.Visibility = Visibility.Visible;
        _vm.SelectedMonitor = null;
    }

    private void Gallery_Click(object sender, RoutedEventArgs e)
    {
        _vm.SelectedMonitor = null;
        MonitorContentArea.Visibility = Visibility.Collapsed;
        GalleryPanel.Visibility = Visibility.Visible;
    }

    private void ShowSettings_Click(object sender, RoutedEventArgs e)
    {
        new SettingsWindow(_appSettings) { Owner = this }.ShowDialog();
    }

    private void MonitorCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ViewModels.MonitorViewModel mvm)
        {
            GalleryPanel.Visibility = Visibility.Collapsed;
            MonitorContentArea.Visibility = Visibility.Visible;
            _vm.SelectedMonitor = mvm;
        }
    }

    private void MinimizeToTray_Click(object sender, RoutedEventArgs e)
    {
        Hide();

        if (!_appSettings.HasShownTrayHint)
        {
            _appSettings.HasShownTrayHint = true;
            _appSettings.Save();

            if (Application.Current.Resources["TrayIcon"] is TaskbarIcon tray)
            {
                tray.ShowNotification(
                    "Still running",
                    "Background Slideshow is still running in the system tray. Double-click the tray icon to reopen.",
                    NotificationIcon.Info);
            }
        }
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // The X button always exits the application completely.
        // To hide to tray instead, use the "Minimize to Tray" button in the nav bar.
        Application.Current.Shutdown();
    }
}
