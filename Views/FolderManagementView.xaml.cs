using System.Windows;
using System.Windows.Controls;

namespace BackgroundSlideShow.Views;

public partial class FolderManagementView : UserControl
{
    public FolderManagementView() => InitializeComponent();

    private void View_DragOver(object sender, DragEventArgs e) => FolderDropHelper.DragOver(e);
    private async void View_Drop(object sender, DragEventArgs e) => await FolderDropHelper.Drop(this, e);
    private void RemoveAll_Click(object sender, RoutedEventArgs e) => FolderDropHelper.RemoveAll_Click(this);
}
