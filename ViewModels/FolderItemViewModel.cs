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

    public int    Id   => _folder.Id;
    public string Path => _folder.Path;

    /// <summary>Last path segment shown as the primary label.</summary>
    public string DisplayName =>
        System.IO.Path.GetFileName(_folder.Path.TrimEnd(
            System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));

    /// <summary>
    /// Abbreviated parent path shown beneath the folder name for disambiguation
    /// (e.g. "…\OneDrive\Pictures"). Empty when the path is already short.
    /// </summary>
    public string ParentPathHint
    {
        get
        {
            var trimmed = _folder.Path.TrimEnd(
                System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            var dir = System.IO.Path.GetDirectoryName(trimmed);
            if (string.IsNullOrEmpty(dir)) return string.Empty;

            var parts = dir.Split(new[]
                {
                    System.IO.Path.DirectorySeparatorChar,
                    System.IO.Path.AltDirectorySeparatorChar
                },
                StringSplitOptions.RemoveEmptyEntries);

            // Show last two path segments with ellipsis prefix when deeply nested.
            return parts.Length <= 2
                ? dir
                : $"…\\{parts[^2]}\\{parts[^1]}";
        }
    }

    [ObservableProperty] private int       _imageCount;
    [ObservableProperty] private bool      _isEnabled;
    [ObservableProperty] private DateTime? _lastScanned;

    // Set to true while Refresh() is applying an externally-sourced value so that
    // OnIsEnabledChanged does not fire a redundant DB write.
    private bool _refreshing;

    public FolderItemViewModel(LibraryFolder folder, int imageCount, Func<FolderItemViewModel, Task> onEnabledChanged)
    {
        _folder            = folder;
        _onEnabledChanged  = onEnabledChanged;
        _imageCount        = imageCount;
        _isEnabled         = folder.IsEnabled;
        _lastScanned       = folder.LastScanned;
    }

    /// <summary>Updates mutable display data without replacing the VM instance (preserves scroll position).</summary>
    internal void Refresh(int imageCount, bool isEnabled, DateTime? lastScanned)
    {
        ImageCount  = imageCount;
        LastScanned = lastScanned;
        if (IsEnabled != isEnabled)
        {
            _refreshing = true;
            IsEnabled   = isEnabled;
            _refreshing = false;
        }
    }

    partial void OnIsEnabledChanged(bool value)
    {
        _folder.IsEnabled = value;
        if (!_refreshing)
            _ = _onEnabledChanged(this);
    }
}
