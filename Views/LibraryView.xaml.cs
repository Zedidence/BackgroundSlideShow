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
        foreach (var path in paths.Where(Directory.Exists))
            await vm.AddFolderByPathAsync(path);
    }
}
