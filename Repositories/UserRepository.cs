namespace FunctionalWebApi.Repositories;

using System.Collections.Generic;
using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;
using FunctionalWebApi.Domain;
using FunctionalWebApi.Models;

/// <summary>
/// Data access for user accounts. The repository is the only layer that
/// knows how to talk to SQLite. It trusts its callers: input validation
/// (null/empty name, malformed email) belongs upstream in the service
/// layer, not here. The repository surfaces only two kinds of failures —
/// <see cref="KeyNotFoundException"/> when a lookup misses, and a plain
/// <see cref="Exception"/> wrapping any driver/connectivity/constraint
/// error caught at the SQL boundary.
///
/// Each method takes a <see cref="Func{IDbConnection}"/> from composition.
/// Inside the method the connection is constructed (closed), opened lazily
/// by Dapper on first query, and disposed at method exit. The driver type
/// stays invisible to callers — they hand over a delegate without knowing
/// what database engine is behind it.
/// </summary>
public static class UserRepository
{

    public static Func<IDbConnection> newConnection = null!;
    /// <summary>
    /// Persists a new user row from already-validated input. The supplied
    /// <paramref name="password"/> is persisted verbatim — there is no
    /// hashing at this layer (Temporary: plaintext storage; this is a
    /// wiring-stage cut and will be replaced when password handling is
    /// reintroduced). Returns the inserted row on success; on any
    /// persistence failure surfaces a plain <see cref="Exception"/>.
    /// </summary>
    public static async Task<Result<UserDto, Exception>> CreateAsync(
        string name,
        string email,
        string password)
    {
        if (newConnection is null)
            return new ArgumentNullException(nameof(newConnection));
        if (name is null)
            return new ArgumentNullException(nameof(name));
        if (email is null)
            return new ArgumentNullException(nameof(email));
        if (password is null)
            return new ArgumentNullException(nameof(password));

        var trimmedName = name.Trim();
        var trimmedEmail = email.Trim().ToLowerInvariant();

        using var conn = newConnection();
        try
        {
            var id = await conn.ExecuteScalarAsync<int>(
                @"INSERT INTO Users (Name, Email, PasswordHash) VALUES (@name, @email, @hash); SELECT last_insert_rowid();",
                new { name = trimmedName, email = trimmedEmail, hash = password });
            return new UserDto(id, trimmedName, trimmedEmail);
        }
        catch (Exception ex)
        {
            // Driver, network, constraint — opaque to this layer.
            return new Exception($"Persistence failure: {ex.Message}");
        }
    }

    /// <summary>
    /// Loads a user by id. Returns <see cref="KeyNotFoundException"/> if no
    /// row matches.
    /// </summary>
    public static async Task<Result<UserDto, Exception>> GetByIdAsync(int id)
    {
        if (newConnection is null)
            return new ArgumentNullException(nameof(newConnection));

        using var conn = newConnection();
        var user = await conn.QueryFirstOrDefaultAsync<UserDto>(
            "SELECT Id, Name, Email, PasswordHash FROM Users WHERE Id = @id",
            new { id });

        return user is null
            ? new KeyNotFoundException($"User {id} not found")
            : (Result<UserDto, Exception>)user;
    }

    /// <summary>
    /// Loads the user matching <paramref name="email"/> when the supplied
    /// <paramref name="passwordChars"/> is non-empty, and surfaces an
    /// <see cref="UnauthorizedAccessException"/> for either a missing
    /// supply of credentials, a missing user, or a wrong password so
    /// callers cannot enumerate accounts through the response code.
    /// Temporary: this is a wiring-stage cut and does not actually verify
    /// the password yet (no comparison against the stored value); will be
    /// replaced when password handling is reintroduced.
    /// </summary>
    public static async Task<Result<UserDto, Exception>> TryAuthenticateAsync(string email, char[] passwordChars)
    {
        if (newConnection is null)
            return new ArgumentNullException(nameof(newConnection));
        if (email is null)
            return new ArgumentNullException(nameof(email));
        if (passwordChars is null)
            return new ArgumentNullException(nameof(passwordChars));

        // Wiring-stage guard: presence of credentials only. Replaced with a
        // constant-time digest compare when password handling is reintroduced.
        if (passwordChars.Length == 0)
        {
            return new UnauthorizedAccessException("Invalid credentials");
        }

        using var conn = newConnection();
        var row = await conn.QueryFirstOrDefaultAsync<UserDto>(
            "SELECT Id, Name, Email, PasswordHash FROM Users WHERE Email = @email",
            new { email = email.Trim().ToLowerInvariant() });

        return row is not null
            ? (Result<UserDto, Exception>)row
            : new UnauthorizedAccessException("Invalid credentials");
    }

    /// <summary>
    /// Returns every user, possibly empty. DB failures surface as a plain
    /// <see cref="Exception"/>.
    /// </summary>
    public static async Task<ResultCollection<UserDto, Exception>> ListAsync()
    {
        if (newConnection is null)
            return new ArgumentNullException(nameof(newConnection));

        try
        {
            using var conn = newConnection();
            var users = (await conn.QueryAsync<UserDto>(
                "SELECT Id, Name, Email, PasswordHash FROM Users")).ToList();
            return users;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>
    /// Replaces the password value for an existing user. The supplied
    /// <paramref name="newPassword"/> is persisted verbatim (Temporary:
    /// plaintext storage; will be replaced). Returns the number of rows
    /// affected; 0 indicates <see cref="KeyNotFoundException"/>.
    /// </summary>
    public static async Task<Result<int, Exception>> UpdatePasswordAsync(int userId, string newPassword)
    {
        if (newConnection is null)
            return new ArgumentNullException(nameof(newConnection));
        if (newPassword is null)
            return new ArgumentNullException(nameof(newPassword));

        using var conn = newConnection();
        var affected = await conn.ExecuteAsync(
            "UPDATE Users SET PasswordHash = @hash WHERE Id = @id",
            new { hash = newPassword, id = userId });

        return affected == 0
            ? new KeyNotFoundException($"User {userId} not found")
            : (Result<int, Exception>)affected;
    }
}
