using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OnePieceApi.Models;
using OnePieceApi.Retrieval;
using Xunit;

namespace one_piece_api.Tests;

/// <summary>
/// Covers the EF Core persistence layer against a throwaway database file. No external services needed.
/// </summary>
public class SqliteDatabaseServiceTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"one_piece_test_{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _provider;
    private readonly SqliteDatabaseService _service;

    public SqliteDatabaseServiceTests()
    {
        var services = new ServiceCollection();
        services.AddPooledDbContextFactory<OnePieceDbContext>(options =>
            options.UseSqlite(BuildConnectionString(SqliteOpenMode.ReadWriteCreate)));

        _provider = services.BuildServiceProvider();
        _service = new SqliteDatabaseService(_provider.GetRequiredService<IDbContextFactory<OnePieceDbContext>>());
    }

    public Task InitializeAsync() => _service.InitializeDatabaseAsync();

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
    }

    private string BuildConnectionString(SqliteOpenMode mode) =>
        new SqliteConnectionStringBuilder { DataSource = _databasePath, Mode = mode }.ToString();

    private static EpisodeRecord Episode(ulong id, float rating = 8.0f, int season = 1) => new()
    {
        Id = id,
        Title = $"Episode {id}",
        Overview = $"Overview for episode {id}",
        Season = season,
        EpisodeNumber = (int)id,
        ReleaseYear = 1999,
        Rating = rating
    };

    [Fact]
    public async Task InsertEpisodesAsync_RoundTripsRecordsThroughTheCodeFirstSchema()
    {
        await _service.InsertEpisodesAsync([Episode(1, rating: 9.1f), Episode(2, rating: 7.4f)]);

        var result = await _service.ExecuteSqlQueryAsync("SELECT Id, Title, Rating FROM Episodes ORDER BY Id");

        Assert.Contains("Episode 1", result);
        Assert.Contains("Episode 2", result);

        // A float Rating widens to a double on the way out of SQLite, so it must be formatted
        // rather than emitted as 9.100000381469727. It must also not pick up a locale's
        // decimal comma, which would reach the prompt as "9,1".
        Assert.Contains("9.1", result);
        Assert.DoesNotContain("9.10000", result);
        Assert.DoesNotContain("9,1", result);
    }

    [Fact]
    public async Task ExecuteSqlQueryAsync_TrimsDoublePrecisionNoiseFromAggregates()
    {
        await _service.InsertEpisodesAsync([Episode(1, rating: 8.0f), Episode(2, rating: 9.0f)]);

        var result = await _service.ExecuteSqlQueryAsync("SELECT AVG(Rating) AS Average FROM Episodes");

        Assert.Contains("8.5", result);
        Assert.DoesNotContain("8.50000", result);
    }

    [Fact]
    public async Task InsertEpisodesAsync_PreservesUnsignedKeysBeyondIntRange()
    {
        // The key is a ulong mapped onto SQLite's signed INTEGER, so a large value must survive the conversion.
        const ulong largeId = (ulong)int.MaxValue + 500;
        await _service.InsertEpisodesAsync([Episode(largeId)]);

        var result = await _service.ExecuteSqlQueryAsync("SELECT Id FROM Episodes");

        Assert.Contains(largeId.ToString(), result);
    }

    [Fact]
    public async Task InsertEpisodesAsync_IsANoOp_ForAnEmptyBatch()
    {
        await _service.InsertEpisodesAsync([]);

        var result = await _service.ExecuteSqlQueryAsync("SELECT Id FROM Episodes");

        Assert.Contains("0 rows returned", result);
    }

    [Fact]
    public async Task InitializeDatabaseAsync_CreatesTheIndexesTheRouterQueriesRelyOn()
    {
        var result = await _service.ExecuteSqlQueryAsync(
            "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'Episodes'");

        Assert.Contains("Season", result);
        Assert.Contains("ReleaseYear", result);
        Assert.Contains("Rating", result);
    }

    [Theory]
    [InlineData("DROP TABLE Episodes;")]
    [InlineData("DELETE FROM Episodes")]
    [InlineData("UPDATE Episodes SET Rating = 0")]
    [InlineData("PRAGMA writable_schema = 1")]
    public async Task ExecuteSqlQueryAsync_RejectsAnythingButSelect(string sql)
    {
        var result = await _service.ExecuteSqlQueryAsync(sql);

        Assert.StartsWith("Error: Only read-only 'SELECT' SQL statements are allowed", result);
    }

    [Fact]
    public async Task ExecuteSqlQueryAsync_RejectsAStatementSmuggledInAfterASelect()
    {
        // A DbCommand runs every statement in its text, so the SELECT prefix alone is not a guard.
        await _service.InsertEpisodesAsync([Episode(1)]);

        var result = await _service.ExecuteSqlQueryAsync("SELECT 1; DROP TABLE Episodes;");

        Assert.StartsWith("Error: Only a single 'SELECT' statement is allowed", result);

        // The table is still there.
        var rows = await _service.ExecuteSqlQueryAsync("SELECT Id FROM Episodes");
        Assert.Contains("1", rows);
    }

    [Fact]
    public async Task ExecuteSqlQueryAsync_AllowsATrailingSemicolon()
    {
        await _service.InsertEpisodesAsync([Episode(1)]);

        var result = await _service.ExecuteSqlQueryAsync("SELECT Id FROM Episodes;");

        Assert.DoesNotContain("Error:", result);
        Assert.Contains("1", result);
    }

    [Fact]
    public async Task ReadOnlyConnection_BlocksWritesAtTheEngine()
    {
        // The prefix checks are defence in depth. This is the guarantee the service actually
        // relies on: the connection the generated SQL runs on physically cannot write.
        await _service.InsertEpisodesAsync([Episode(1)]);

        await using var connection = new SqliteConnection(BuildConnectionString(SqliteOpenMode.ReadOnly));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Episodes";

        var ex = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        Assert.Contains("readonly", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteSqlQueryAsync_TruncatesLargeResultsSoTheyCannotOverrunThePrompt()
    {
        var episodes = Enumerable.Range(1, 150).Select(i => Episode((ulong)i)).ToList();
        await _service.InsertEpisodesAsync(episodes);

        var result = await _service.ExecuteSqlQueryAsync("SELECT * FROM Episodes");

        Assert.Contains("truncated to the first 100 rows", result);

        // Header, separator, 100 rows, truncation note.
        var lines = result.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(103, lines.Length);
    }

    [Fact]
    public async Task ExecuteSqlQueryAsync_ReportsSqlErrorsInsteadOfThrowing()
    {
        var result = await _service.ExecuteSqlQueryAsync("SELECT NoSuchColumn FROM Episodes");

        Assert.StartsWith("SQL Execution Error:", result);
    }
}
