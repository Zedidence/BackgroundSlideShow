using System.Windows;
using System.Windows.Controls;

namespace BackgroundSlideShow.Views;

public partial class LibraryView : UserControl
{
    public LibraryView() => InitializeComponent();

    private void LibraryView_DragOver(object sender, DragEventArgs e) => FolderDropHelper.DragOver(e);
    private async void LibraryView_Drop(object sender, DragEventArgs e) => await FolderDropHelper.Drop(this, e);
    private void RemoveAll_Click(object sender, RoutedEventArgs e) => FolderDropHelper.RemoveAll_Click(this);
}
