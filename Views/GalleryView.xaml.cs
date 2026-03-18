using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BackgroundSlideShow.Models;
using BackgroundSlideShow.ViewModels;

namespace BackgroundSlideShow.Views;

public partial class GalleryView : UserControl
{
    private double _savedScrollOffset;

    public GalleryView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    // ── Scroll position preservation ────────────────────────────────────────

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ImageGalleryViewModel oldVm)
        {
            oldVm.BeforeImagesRefresh -= SaveScrollPosition;
            oldVm.AfterImagesRefresh -= RestoreScrollPosition;
            oldVm.PropertyChanged -= OnVmPropertyChanged;
        }

        if (e.NewValue is ImageGalleryViewModel newVm)
        {
            newVm.BeforeImagesRefresh += SaveScrollPosition;
            newVm.AfterImagesRefresh += RestoreScrollPosition;
            newVm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void SaveScrollPosition()
    {
        var sv = FindScrollViewer(ImageListBox);
        if (sv != null)
            _savedScrollOffset = sv.VerticalOffset;
    }

    private void RestoreScrollPosition()
    {
        // Defer to after the layout pass so the new items have been measured.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            var sv = FindScrollViewer(ImageListBox);
            if (sv != null)
                sv.ScrollToVerticalOffset(_savedScrollOffset);
        });
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer sv) return sv;
            var result = FindScrollViewer(child);
            if (result != null) return result;
        }
        return null;
    }

    // ── Focus preview overlay when it opens ─────────────────────────────────

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ImageGalleryViewModel.IsPreviewVisible)
            && sender is ImageGalleryViewModel vm && vm.IsPreviewVisible)
        {
            // Defer focus until after the opacity animation starts and the grid is hit-testable.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
                () => PreviewOverlayGrid.Focus());
        }
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
