namespace FunctionalWebApi.Repositories;

using System.Data;
using Microsoft.Data.Sqlite;

/// <summary>
/// Constructs and opens <see cref="IDbConnection"/> instances backed by
/// <see cref="SqliteConnection"/>. The driver type is contained inside this
/// namespace so callers can hold a connection through the BCL
/// <see cref="IDbConnection"/> interface alone.
/// </summary>
public static class SqliteConnectionFactory
{
    /// <summary>
    /// Opens a fresh <see cref="SqliteConnection"/> for the given
    /// <paramref name="connectionString"/> and returns it typed as
    /// <see cref="IDbConnection"/>. The caller owns the lifetime and must
    /// dispose the result.
    /// </summary>
    public static async Task<IDbConnection> OpenAsync(string connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync();
        return conn;
    }
}
