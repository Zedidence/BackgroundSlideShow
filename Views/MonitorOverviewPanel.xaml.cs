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
        var vm = (DataContext ?? Window.GetWindow(this)?.DataContext) as MainViewModel;
        if (vm is null) return;

        foreach (var m in vm.Monitors) m.IsSelected = false;
        mvm.IsSelected = true;
        vm.SelectedMonitor = mvm;
    }
}
