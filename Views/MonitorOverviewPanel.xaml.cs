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
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not MonitorViewModel mvm) return;
        (Window.GetWindow(this) as MainWindow)?.NavigateToMonitor(mvm);
    }
}
