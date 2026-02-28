using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using BackgroundSlideShow.Models;

namespace BackgroundSlideShow.ViewModels;

/// <summary>
/// Represents a single library folder in the per-monitor folder assignment list.
/// Toggling <see cref="IsAssigned"/> notifies the parent <see cref="MonitorViewModel"/>
/// via <see cref="PropertyChanged"/>, which persists the change and updates the engine.
/// </summary>
public partial class FolderAssignmentItemViewModel : ObservableObject
{
    public LibraryFolder Folder { get; }

    /// <summary>Short display name — last segment of the folder path.</summary>
    public string DisplayName =>
        Path.GetFileName(Folder.Path.TrimEnd(Path.DirectorySeparatorChar,
                                             Path.AltDirectorySeparatorChar))
        ?? Folder.Path;

    [ObservableProperty]
    private bool _isAssigned;

    public FolderAssignmentItemViewModel(LibraryFolder folder, bool isAssigned)
    {
        Folder = folder;
        _isAssigned = isAssigned;
    }
}
