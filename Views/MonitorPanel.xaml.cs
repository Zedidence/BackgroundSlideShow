using System.Windows;
using System.Windows.Controls;
using BackgroundSlideShow.ViewModels;

namespace BackgroundSlideShow.Views;

public partial class MonitorPanel : UserControl
{
    public MonitorPanel()
    {
        InitializeComponent();
    }

    private void IntervalPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn &&
            int.TryParse(btn.Tag?.ToString(), out int seconds) &&
            DataContext is MonitorViewModel vm)
        {
            vm.Config.IntervalSeconds = seconds;
        }
    }
}
