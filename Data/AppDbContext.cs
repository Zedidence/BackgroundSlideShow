using System.IO;
using BackgroundSlideShow.Models;
using Microsoft.EntityFrameworkCore;

namespace BackgroundSlideShow.Data;

public class AppDbContext : DbContext
{
    public DbSet<ImageEntry> Images { get; set; }
    public DbSet<LibraryFolder> LibraryFolders { get; set; }
    public DbSet<MonitorConfig> MonitorConfigs { get; set; }
    public DbSet<MonitorFolderAssignment> MonitorFolderAssignments { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dbPath = Path.Combine(appData, "BackgroundSlideShow", "library.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        options.UseSqlite($"Data Source={dbPath}");
    }

    /// <summary>
    /// Creates the SQLite schema, with recovery from a stale DB that was previously
    /// touched by a failed <c>MigrateAsync</c> call. That call creates
    /// <c>__EFMigrationsHistory</c> but none of the app tables, causing
    /// <c>EnsureCreatedAsync</c> to see "tables exist" and skip schema creation.
    /// </summary>
    public async Task EnsureSchemaAsync()
    {
        await Database.EnsureCreatedAsync();

        // Verify an app table actually exists. If not, a stale __EFMigrationsHistory
        // fooled EnsureCreated into doing nothing — delete and start fresh.
        var conn = Database.GetDbConnection();
        var wasOpen = conn.State == System.Data.ConnectionState.Open;
        if (!wasOpen) await conn.OpenAsync();

        long appTableCount;
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='MonitorConfigs'";
            appTableCount = (long)(await cmd.ExecuteScalarAsync())!;
        }
        finally
        {
            if (!wasOpen) conn.Close();
        }

        if (appTableCount == 0)
        {
            await Database.EnsureDeletedAsync();
            await Database.EnsureCreatedAsync();
            return;
        }

        // Apply additive column migrations for existing databases.
        // EnsureCreatedAsync only creates the schema from scratch; it won't add
        // columns that were introduced after the DB was first created.
        var conn2 = Database.GetDbConnection();
        var wasOpen2 = conn2.State == System.Data.ConnectionState.Open;
        if (!wasOpen2) await conn2.OpenAsync();
        try
        {
            await AddColumnIfMissingAsync(conn2, "Images", "IsExcluded", "INTEGER NOT NULL DEFAULT 0");
            // ImagePoolMode replaces the old UseSmartFit bool. Default 3 = Smart, matching the
            // previous default of UseSmartFit=true, so existing users keep the same behaviour.
            await AddColumnIfMissingAsync(conn2, "MonitorConfigs", "ImagePoolMode", "INTEGER NOT NULL DEFAULT 3");
            // FolderAssignmentMode: 0 = All (default), 1 = Selected.
            await AddColumnIfMissingAsync(conn2, "MonitorConfigs", "FolderAssignmentMode", "INTEGER NOT NULL DEFAULT 0");
            // CollageEnabled: 0 = disabled (default), 1 = enabled.
            await AddColumnIfMissingAsync(conn2, "MonitorConfigs", "CollageEnabled", "INTEGER NOT NULL DEFAULT 0");
            // CollageChance: 0–100 probability of collage per slide. Default 30 (≈ 1-in-3).
            await AddColumnIfMissingAsync(conn2, "MonitorConfigs", "CollageChance", "INTEGER NOT NULL DEFAULT 30");

            // Create the MonitorFolderAssignments join table for existing databases.
            await using var createTable = conn2.CreateCommand();
            createTable.CommandText = """
                CREATE TABLE IF NOT EXISTS MonitorFolderAssignments (
                    MonitorConfigId INTEGER NOT NULL,
                    FolderId        INTEGER NOT NULL,
                    PRIMARY KEY (MonitorConfigId, FolderId),
                    FOREIGN KEY (MonitorConfigId) REFERENCES MonitorConfigs(Id) ON DELETE CASCADE,
                    FOREIGN KEY (FolderId)        REFERENCES LibraryFolders(Id) ON DELETE CASCADE
                )
                """;
            await createTable.ExecuteNonQueryAsync();
        }
        finally
        {
            if (!wasOpen2) conn2.Close();
        }
    }

    private static async Task AddColumnIfMissingAsync(
        System.Data.Common.DbConnection conn, string table, string column, string definition)
    {
        await using var pragma = conn.CreateCommand();
        pragma.CommandText = $"PRAGMA table_info({table})";
        var existing = new List<string>();
        await using (var reader = await pragma.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                existing.Add(reader.GetString(1)); // column index 1 = name
        }

        if (!existing.Contains(column, StringComparer.OrdinalIgnoreCase))
        {
            await using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
            await alter.ExecuteNonQueryAsync();
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ImageEntry>(e =>
        {
            e.HasIndex(i => i.FilePath).IsUnique();
            e.HasOne(i => i.LibraryFolder)
             .WithMany(f => f.Images)
             .HasForeignKey(i => i.LibraryFolderId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LibraryFolder>(e =>
        {
            e.HasIndex(f => f.Path).IsUnique();
        });

        modelBuilder.Entity<MonitorConfig>(e =>
        {
            e.HasIndex(m => m.MonitorId).IsUnique();
        });

        modelBuilder.Entity<MonitorFolderAssignment>(e =>
        {
            e.HasKey(a => new { a.MonitorConfigId, a.FolderId });
            e.HasOne(a => a.MonitorConfig)
             .WithMany()
             .HasForeignKey(a => a.MonitorConfigId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(a => a.LibraryFolder)
             .WithMany()
             .HasForeignKey(a => a.FolderId)
             .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
