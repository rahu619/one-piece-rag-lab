using System;
using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;
using OnePieceApi.Models;

namespace OnePieceApi.Retrieval;

/// <summary>
/// Service to manage a local SQLite database of One Piece episodes for structured SQL-RAG routing.
/// </summary>
public class SqliteDatabaseService
{
    private readonly string _connectionString;

    public SqliteDatabaseService()
    {
        // Store the database file in the executing directory
        var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "one_piece.db");
        _connectionString = $"Data Source={dbPath}";
    }

    /// <summary>
    /// Recreates the Episodes database schema.
    /// </summary>
    public void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        // Drop and recreate table to match the ingestion lifecycle of the vector store
        command.CommandText = @"
            DROP TABLE IF EXISTS Episodes;
            CREATE TABLE Episodes (
                Id INTEGER PRIMARY KEY,
                Title TEXT NOT NULL,
                Overview TEXT NOT NULL,
                Season INTEGER NOT NULL,
                EpisodeNumber INTEGER NOT NULL,
                ReleaseYear INTEGER NOT NULL,
                Rating REAL NOT NULL
            );";
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Inserts an episode record into the SQLite database.
    /// </summary>
    public void InsertEpisode(EpisodeRecord episode)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO Episodes (Id, Title, Overview, Season, EpisodeNumber, ReleaseYear, Rating)
            VALUES ($id, $title, $overview, $season, $episodeNumber, $releaseYear, $rating);";
        
        command.Parameters.AddWithValue("$id", (long)episode.Id);
        command.Parameters.AddWithValue("$title", episode.Title ?? string.Empty);
        command.Parameters.AddWithValue("$overview", episode.Overview ?? string.Empty);
        command.Parameters.AddWithValue("$season", episode.Season);
        command.Parameters.AddWithValue("$episodeNumber", episode.EpisodeNumber);
        command.Parameters.AddWithValue("$releaseYear", episode.ReleaseYear);
        command.Parameters.AddWithValue("$rating", (double)episode.Rating);

        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Executes a SQL query against the SQLite database and returns the results formatted as a text table.
    /// Only allows read-only (SELECT) statements for safety.
    /// </summary>
    public string ExecuteSqlQuery(string sql)
    {
        var normalizedSql = sql.Trim().ToUpperInvariant();
        if (!normalizedSql.StartsWith("SELECT"))
        {
            return "Error: Only read-only 'SELECT' SQL statements are allowed for security reasons.";
        }

        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = sql;

            using var reader = command.ExecuteReader();

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
            sb.AppendLine();
            sb.AppendLine(new string('-', Math.Max(20, sb.Length - 1)));

            // Format rows
            while (reader.Read())
            {
                for (int i = 0; i < columnCount; i++)
                {
                    var val = reader.GetValue(i);
                    sb.Append(val == DBNull.Value ? "NULL" : val.ToString());
                    if (i < columnCount - 1) sb.Append(" | ");
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"SQL Execution Error: {ex.Message}";
        }
    }
}
