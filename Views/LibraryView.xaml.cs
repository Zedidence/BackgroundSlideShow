using System.IO;
using System.Windows;
using System.Windows.Controls;
using BackgroundSlideShow.ViewModels;

namespace BackgroundSlideShow.Views;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
    }

    // ── Drag-and-drop ─────────────────────────────────────────────────────────

    private void LibraryView_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void LibraryView_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (DataContext is not FolderListViewModel vm) return;

        var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
        await vm.AddFoldersByPathAsync(paths.Where(Directory.Exists));
    }

    private void RemoveAll_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Remove all folders from the library?\n\nAll images will be removed from the database.",
            "Remove All Folders",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;
        if (DataContext is FolderListViewModel vm)
            vm.RemoveAllFoldersCommand.Execute(null);
    }
}
