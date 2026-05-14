using System.IO;
using System.Windows;
using System.Windows.Controls;
using BackgroundSlideShow.ViewModels;

namespace BackgroundSlideShow.Views;

internal static class FolderDropHelper
{
    internal static void DragOver(DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    internal static async Task Drop(UserControl view, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (view.DataContext is not FolderListViewModel vm) return;
        var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
        await vm.AddFoldersByPathAsync(paths.Where(Directory.Exists));
    }

    internal static void RemoveAll_Click(UserControl view)
    {
        var result = MessageBox.Show(
            "Remove all folders from the library?\n\nAll images will be removed from the database.",
            "Remove All Folders",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;
        if (view.DataContext is FolderListViewModel vm)
            vm.RemoveAllFoldersCommand.Execute(null);
    }
}
