namespace BackgroundSlideShow.Models;

public class LibraryFolder
{
    public int Id { get; set; }
    public string Path { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public DateTime? LastScanned { get; set; }

    public ICollection<ImageEntry> Images { get; set; } = new List<ImageEntry>();
}
