namespace BackgroundSlideShow.Models;

/// <summary>
/// Join table: which library folders are explicitly assigned to a monitor when
/// <see cref="MonitorConfig.FolderAssignmentMode"/> is <see cref="FolderAssignmentMode.Selected"/>.
/// </summary>
public class MonitorFolderAssignment
{
    public int MonitorConfigId { get; set; }
    public int FolderId { get; set; }

    // Navigation
    public MonitorConfig? MonitorConfig { get; set; }
    public LibraryFolder? LibraryFolder { get; set; }
}
