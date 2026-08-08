using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OnePieceApi.Models;

namespace OnePieceApi.Retrieval;

/// <summary>
/// Code-first EF Core context for the local episode database backing the SQL routing path.
/// </summary>
public class OnePieceDbContext(DbContextOptions<OnePieceDbContext> options) : DbContext(options)
{
    public const string DatabaseFileName = "one_piece.db";

    public DbSet<EpisodeRecord> Episodes => Set<EpisodeRecord>();

    /// <summary>
    /// Builds a connection string for the database file that sits alongside the executing assembly.
    /// Read-only mode is what actually guarantees LLM-generated SQL cannot mutate the database;
    /// the SELECT prefix check is only defence in depth.
    /// </summary>
    public static string BuildConnectionString(SqliteOpenMode mode) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(AppContext.BaseDirectory, DatabaseFileName),
            Mode = mode
        }.ToString();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var episode = modelBuilder.Entity<EpisodeRecord>();

        episode.ToTable("Episodes");
        episode.HasKey(e => e.Id);

        // Ids are assigned by the ingestor so they stay aligned with the Qdrant keys.
        // SQLite has no unsigned integer type, so the key is stored as a signed INTEGER.
        episode.Property(e => e.Id)
            .HasConversion<long>()
            .ValueGeneratedNever();

        episode.Property(e => e.Title).IsRequired();
        episode.Property(e => e.Overview).IsRequired();

        // The embedding lives in Qdrant; the relational copy only backs analytical queries.
        episode.Ignore(e => e.OverviewEmbedding);

        // The router generates aggregates and filters over these columns.
        episode.HasIndex(e => e.Season);
        episode.HasIndex(e => e.ReleaseYear);
        episode.HasIndex(e => e.Rating);
    }
}
