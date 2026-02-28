namespace BackgroundSlideShow.Models;

public class ImageEntry
{
    public int Id { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public double AspectRatio => Width > 0 && Height > 0 ? (double)Width / Height : 1.0;
    public bool IsLandscape => Width >= Height;
    public bool IsPortrait => Height > Width;
    public long FileSize { get; set; }
    public DateTime LastModified { get; set; }
    public bool IsExcluded { get; set; }

    // Navigation
    public int LibraryFolderId { get; set; }
    public LibraryFolder? LibraryFolder { get; set; }
}
