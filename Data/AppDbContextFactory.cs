namespace BackgroundSlideShow.Data;

/// <summary>
/// Creates short-lived <see cref="AppDbContext"/> instances. EF Core's <see cref="AppDbContext"/>
/// is not thread-safe, so each operation that touches the database must scope its own context
/// rather than sharing a long-lived instance across UI and background threads.
/// </summary>
public class AppDbContextFactory
{
    public AppDbContext Create() => new AppDbContext();
}
