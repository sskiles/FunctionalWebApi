namespace FunctionalWebApi.Infrastructure;

using Dapper;
using FunctionalWebApi.Domain;
using Microsoft.Data.Sqlite;

/// <summary>
/// One-shot startup hook that ensures the SQLite schema exists before the
/// first request is served. Lives in <see cref="FunctionalWebApi.Infrastructure"/>
/// alongside other cross-cutting framework plumbing (composition, JSON
/// serializer context). Schema layout is intentionally minimal — business
/// owned by the repository layer, this owns the table shell.
/// </summary>
public static class SchemaBootstrap
{
    /// <summary>
    /// Idempotently creates the <c>Users</c> table if it doesn't already
    /// exist. The driver-pool-managed <see cref="SqliteConnection"/> is
    /// closed on entry; Dapper opens it on first query and it is returned
    /// to the pool when the surrounding <c>await using</c> exits.
    /// </summary>
    /// <remarks>
    /// Safe to call repeatedly: <c>CREATE TABLE IF NOT EXISTS</c> is a no-op
    /// when the table is already present. Schema evolution (new tables,
    /// index additions, column changes) would live here as additional DDL
    /// statements or be replaced by a real migration tool.
    /// </remarks>
    public static async Task<Result<Unit, Exception>> EnsureCreatedAsync(string connectionString)
    {
        if (connectionString is null)
            return new ArgumentNullException(nameof(connectionString));

        try
        {
            await using var conn = new SqliteConnection(connectionString);
            await conn.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS Users (
    Id           INTEGER PRIMARY KEY AUTOINCREMENT,
    Name         TEXT    NOT NULL,
    Email        TEXT    NOT NULL UNIQUE,
    PasswordHash TEXT    NOT NULL DEFAULT ''
);");
            return Unit.Default;
        }
        catch (Exception ex)
        {
            return new Exception($"Schema bootstrap failed: {ex.Message}");
        }
    }
}