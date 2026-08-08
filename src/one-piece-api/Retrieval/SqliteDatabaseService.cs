using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OnePieceApi.Models;

namespace OnePieceApi.Retrieval;

/// <summary>
/// Service to manage the local SQLite database of One Piece episodes for structured SQL-RAG routing.
/// </summary>
public class SqliteDatabaseService
{
    /// <summary>
    /// Maximum number of rows rendered into an LLM prompt. Unbounded results blow the model's context window.
    /// </summary>
    private const int MaxResultRows = 100;

    private readonly IDbContextFactory<OnePieceDbContext> _contextFactory;

    /// <summary>
    /// Options for the read-only handle used by LLM-generated SQL. Built once, because building
    /// DbContextOptions rebuilds the model and that is far too expensive to repeat per query.
    /// </summary>
    private readonly DbContextOptions<OnePieceDbContext> _readOnlyOptions;

    public SqliteDatabaseService(IDbContextFactory<OnePieceDbContext> contextFactory)
    {
        _contextFactory = contextFactory;

        // Derive the read-only handle from the configured write connection so both always
        // point at the same file.
        using var context = contextFactory.CreateDbContext();
        var readOnlyConnectionString = new SqliteConnectionStringBuilder(context.Database.GetConnectionString())
        {
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();

        _readOnlyOptions = new DbContextOptionsBuilder<OnePieceDbContext>()
            .UseSqlite(readOnlyConnectionString)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options;
    }

    /// <summary>
    /// Drops and recreates the Episodes schema from the code-first model.
    /// </summary>
    public async Task InitializeDatabaseAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        await context.Database.EnsureDeletedAsync(cancellationToken);
        await context.Database.EnsureCreatedAsync(cancellationToken);
    }

    /// <summary>
    /// Inserts a batch of episode records in a single transaction.
    /// </summary>
    public async Task InsertEpisodesAsync(IReadOnlyCollection<EpisodeRecord> episodes, CancellationToken cancellationToken = default)
    {
        if (episodes.Count == 0)
        {
            return;
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        // Every entity here is new, so the change detector has nothing to find and its
        // per-entity scan on AddRange is pure overhead.
        context.ChangeTracker.AutoDetectChangesEnabled = false;

        context.Episodes.AddRange(episodes);
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Executes an LLM-generated SQL query and returns the results formatted as a text table.
    /// Runs against a read-only connection, so only SELECT statements can succeed.
    /// </summary>
    public async Task<string> ExecuteSqlQueryAsync(string sql, CancellationToken cancellationToken = default)
    {
        var trimmedSql = sql.Trim();

        if (!trimmedSql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            return "Error: Only read-only 'SELECT' SQL statements are allowed for security reasons.";
        }

        // A DbCommand runs every statement in its text, so an appended statement would execute
        // even though the text starts with SELECT. Allow only a trailing terminator.
        if (trimmedSql.TrimEnd(';').Contains(';'))
        {
            return "Error: Only a single 'SELECT' statement is allowed for security reasons.";
        }

        try
        {
            // Generated SQL has an unpredictable column shape (aggregates, aliases, GROUP BY),
            // so it is streamed through a reader rather than materialized into an entity.
            await using var context = new OnePieceDbContext(_readOnlyOptions);
            var connection = context.Database.GetDbConnection();
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = trimmedSql;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!reader.HasRows)
            {
                return "Query executed successfully. 0 rows returned.";
            }

            var sb = new StringBuilder();

            // Format column headers
            var columnCount = reader.FieldCount;
            for (int i = 0; i < columnCount; i++)
            {
                sb.Append(reader.GetName(i));
                if (i < columnCount - 1) sb.Append(" | ");
            }

            var headerWidth = sb.Length;
            sb.AppendLine();
            sb.AppendLine(new string('-', Math.Max(20, headerWidth)));

            // Format rows, capped so a `SELECT *` cannot overrun the model's context window
            var rowCount = 0;
            var truncated = false;
            while (await reader.ReadAsync(cancellationToken))
            {
                if (rowCount == MaxResultRows)
                {
                    truncated = true;
                    break;
                }

                for (int i = 0; i < columnCount; i++)
                {
                    sb.Append(FormatValue(reader.GetValue(i)));
                    if (i < columnCount - 1) sb.Append(" | ");
                }
                sb.AppendLine();
                rowCount++;
            }

            if (truncated)
            {
                sb.AppendLine($"... results truncated to the first {MaxResultRows} rows.");
            }

            return sb.ToString();
        }
        catch (SqliteException ex)
        {
            return $"SQL Execution Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Renders a column value for an LLM prompt.
    /// </summary>
    private static string FormatValue(object value) => value switch
    {
        DBNull => "NULL",

        // SQLite REAL is a double, so a float Rating of 9.1 reads back as 9.100000381469727 and an
        // AVG() carries full double precision. Neither helps the model, and both cost tokens.
        double d => d.ToString("0.####", CultureInfo.InvariantCulture),

        _ => value.ToString() ?? string.Empty
    };
}
