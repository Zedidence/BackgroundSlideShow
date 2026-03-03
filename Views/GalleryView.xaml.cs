using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BackgroundSlideShow.Models;
using BackgroundSlideShow.ViewModels;
using BackgroundSlideShow;

namespace BackgroundSlideShow.Views;

public partial class GalleryView : UserControl
{
    public GalleryView()
    {
        InitializeComponent();
    }

    // ── Image context menu ────────────────────────────────────────────────────

    private void ContextMenu_OpenFileLocation(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: ImageEntry img } && !string.IsNullOrEmpty(img.FilePath))
        {
            try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{img.FilePath}\""); }
            catch (Exception ex) { AppLogger.Error($"OpenFileLocation failed for: {img.FilePath}", ex); }
        }
    }

    private async void ContextMenu_ToggleExclude(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: ImageEntry img } && DataContext is ImageGalleryViewModel vm)
            await vm.ToggleExcludeImageAsync(img);
    }

    // ── Preview overlay ───────────────────────────────────────────────────────

    private void PreviewOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ImageGalleryViewModel vm)
            vm.SelectedImage = null;
    }

    private void PreviewContent_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void PreviewOverlay_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue)
            PreviewOverlayGrid.Focus();
    }

    private void PreviewOverlay_KeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not ImageGalleryViewModel vm) return;

        switch (e.Key)
        {
            case Key.Left:
                vm.NavigatePreviousCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Right:
                vm.NavigateNextCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                vm.ClosePreviewCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }
}
