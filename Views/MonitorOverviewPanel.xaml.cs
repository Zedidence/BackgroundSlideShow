using System.Windows;
using System.Windows.Controls;
using BackgroundSlideShow.ViewModels;

namespace BackgroundSlideShow.Views;

public partial class MonitorOverviewPanel : UserControl
{
    public MonitorOverviewPanel()
    {
        InitializeComponent();
    }

    private void Configure_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe)
        {
            AppLogger.Warn("Configure_Click: sender is not FrameworkElement");
            return;
        }
        if (fe.DataContext is not MonitorViewModel mvm)
        {
            AppLogger.Warn($"Configure_Click: DataContext is {fe.DataContext?.GetType().Name ?? "null"}, not MonitorViewModel");
            return;
        }
        var win = Window.GetWindow(this) as MainWindow;
        if (win is null)
        {
            AppLogger.Warn($"Configure_Click: Window.GetWindow returned {Window.GetWindow(this)?.GetType().Name ?? "null"}");
            return;
        }
        AppLogger.Info($"Configure_Click: navigating to {mvm.DisplayName}");
        win.NavigateToMonitor(mvm);
    }
}
