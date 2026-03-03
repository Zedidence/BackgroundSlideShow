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
        ToggleLibraryBtn.Content   = _libraryVisible ? "\u25C0 Library" : "\u25B6 Library";
    }

    private void SetActiveNav(System.Windows.Controls.Button active)
    {
        OverviewBtn.Tag = null;
        GalleryBtn.Tag = null;
        active.Tag = "Active";
    }

    private void ClearMonitorSelection()
    {
        foreach (var m in _vm.Monitors) m.IsSelected = false;
    }

    private void Overview_Click(object sender, RoutedEventArgs e)
    {
        GalleryPanel.Visibility = Visibility.Collapsed;
        MonitorContentArea.Visibility = Visibility.Visible;
        _vm.SelectedMonitor = null;
        ClearMonitorSelection();
        SetActiveNav(OverviewBtn);
    }

    private void Gallery_Click(object sender, RoutedEventArgs e)
    {
        _vm.SelectedMonitor = null;
        ClearMonitorSelection();
        MonitorContentArea.Visibility = Visibility.Collapsed;
        GalleryPanel.Visibility = Visibility.Visible;
        SetActiveNav(GalleryBtn);
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
            ClearMonitorSelection();
            mvm.IsSelected = true;
            _vm.SelectedMonitor = mvm;
            // Clear nav active state — the detail panel is neither Overview nor Gallery
            OverviewBtn.Tag = null;
            GalleryBtn.Tag = null;
        }
    }

    private void HideToTray()
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

    private void MinimizeToTray_Click(object sender, RoutedEventArgs e) => HideToTray();

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
            HideToTray();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // The X button always exits the application completely.
        Application.Current.Shutdown();
    }
}
