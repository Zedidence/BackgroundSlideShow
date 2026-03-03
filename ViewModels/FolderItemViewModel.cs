using BackgroundSlideShow.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BackgroundSlideShow.ViewModels;

/// <summary>
/// View-model wrapper for a <see cref="LibraryFolder"/> that exposes image count,
/// relative last-scanned time, and a two-way IsEnabled toggle with DB persistence.
/// </summary>
public partial class FolderItemViewModel : ObservableObject
{
    private readonly LibraryFolder _folder;
    private readonly Func<FolderItemViewModel, Task> _onEnabledChanged;

    public int Id => _folder.Id;
    public string Path => _folder.Path;

    /// <summary>Last path segment shown in the list; full path is always in the tooltip.</summary>
    public string DisplayName =>
        System.IO.Path.GetFileName(_folder.Path.TrimEnd(
            System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));

    public DateTime? LastScanned => _folder.LastScanned;

    [ObservableProperty]
    private int _imageCount;

    [ObservableProperty]
    private bool _isEnabled;

    public FolderItemViewModel(LibraryFolder folder, int imageCount, Func<FolderItemViewModel, Task> onEnabledChanged)
    {
        _folder = folder;
        _onEnabledChanged = onEnabledChanged;
        _imageCount = imageCount;
        _isEnabled = folder.IsEnabled;
    }

    partial void OnIsEnabledChanged(bool value)
    {
        _folder.IsEnabled = value;
        _ = _onEnabledChanged(this);
    }
}
