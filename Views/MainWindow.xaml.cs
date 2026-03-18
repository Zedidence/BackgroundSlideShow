using System.Windows;
using System.Windows.Input;
using BackgroundSlideShow.Services;
using BackgroundSlideShow.ViewModels;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Wpf.Ui.Controls;

namespace BackgroundSlideShow.Views;

public partial class MainWindow : FluentWindow
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

    // ── NavigationView ──────────────────────────────────────────────────────

    private void RootNavigation_SelectionChanged(NavigationView sender, RoutedEventArgs args)
    {
        if (sender.SelectedItem is not NavigationViewItem item) return;
        var tag = item.Tag as string;

        switch (tag)
        {
            case "overview":
                ShowOverview();
                break;
            case "gallery":
                ShowGallery();
                break;
            case "gif":
                ShowGifMode();
                break;
            case "lockscreen":
                ShowLockScreen();
                break;
            case "settings":
                ShowSettings();
                break;
        }
    }

    private void ShowOverview()
    {
        GalleryPanel.Visibility       = Visibility.Collapsed;
        GifPanel.Visibility           = Visibility.Collapsed;
        LockScreenPanel.Visibility    = Visibility.Collapsed;
        MonitorContentArea.Visibility = Visibility.Visible;
        _vm.SelectedMonitor = null;
        ClearMonitorSelection();
        ShowLibrarySidebar(true);
    }

    private void ShowGallery()
    {

        _vm.SelectedMonitor = null;
        ClearMonitorSelection();
        MonitorContentArea.Visibility = Visibility.Collapsed;
        GifPanel.Visibility           = Visibility.Collapsed;
        LockScreenPanel.Visibility    = Visibility.Collapsed;
        GalleryPanel.Visibility       = Visibility.Visible;
        ShowLibrarySidebar(true);
    }

    private void ShowGifMode()
    {
        _vm.SelectedMonitor = null;
        ClearMonitorSelection();
        MonitorContentArea.Visibility = Visibility.Collapsed;
        GalleryPanel.Visibility       = Visibility.Collapsed;
        LockScreenPanel.Visibility    = Visibility.Collapsed;
        GifPanel.Visibility           = Visibility.Visible;
        ShowLibrarySidebar(false);
    }

    private void ShowLockScreen()
    {
        _vm.SelectedMonitor = null;
        ClearMonitorSelection();
        MonitorContentArea.Visibility = Visibility.Collapsed;
        GalleryPanel.Visibility       = Visibility.Collapsed;
        GifPanel.Visibility           = Visibility.Collapsed;
        LockScreenPanel.Visibility    = Visibility.Visible;
        ShowLibrarySidebar(false);
    }

    private void ShowSettings()
    {
        new SettingsWindow(_appSettings) { Owner = this }.ShowDialog();
    }

    // ── Library sidebar ─────────────────────────────────────────────────────

    private void ShowLibrarySidebar(bool show)
    {
        LibraryPanel.Visibility    = show ? Visibility.Visible : Visibility.Collapsed;
        LibrarySplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        LibraryColumn.Width        = show ? new GridLength(220) : new GridLength(0);
        SplitterColumn.Width       = show ? new GridLength(4)   : new GridLength(0);
    }

    // ── Monitor selection ───────────────────────────────────────────────────

    private void ClearMonitorSelection()
    {
        foreach (var m in _vm.Monitors) m.IsSelected = false;
    }

    private void MonitorCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is MonitorViewModel mvm)
        {
            GalleryPanel.Visibility       = Visibility.Collapsed;
            GifPanel.Visibility           = Visibility.Collapsed;
            LockScreenPanel.Visibility    = Visibility.Collapsed;
            MonitorContentArea.Visibility = Visibility.Visible;
            ClearMonitorSelection();
            mvm.IsSelected = true;
            _vm.SelectedMonitor = mvm;
        }
    }

    // ── Tray ────────────────────────────────────────────────────────────────

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
        Application.Current.Shutdown();
    }
}
