namespace FunctionalWebApi.Repositories;

using System.Collections.Generic;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using FunctionalWebApi.Domain;
using FunctionalWebApi.Models;

#pragma warning disable CA1825 // argument.Name.Length == 0 - trimmed by Trim
/// <summary>
/// Data access for user accounts. All known domain failure modes —
/// validation, not‑found, authentication, persistence failures — are
/// returned as <see cref="Result{TValue, TError}"/> carrying a BCL
/// <see cref="Exception"/> value. Unknown exceptions caught inside catch
/// blocks are wrapped as a plain <see cref="Exception"/>.
///
/// Each method takes an already‑open <see cref="IDbConnection"/>; the caller
/// owns the connection's lifetime. Repository methods do not open or dispose.
/// Password hashing is inlined as a single PBKDF2 step. There is no
/// dedicated hasher class.
/// </summary>
public static class UserRepository
{
    /// <summary>PBKDF2 iteration count baked into the stored hash prefix.</summary>
    private const int Iterations = 600_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    /// <summary>
    /// Validates the input, hashes the password, persists a new user, and
    /// returns the inserted row. Validation problems surface as
    /// <see cref="ArgumentException"/>; persistence failures surface as a
    /// plain <see cref="Exception"/>.
    /// </summary>
    public static async Task<Result<UserDto, Exception>> CreateAsync(
        IDbConnection connection,
        string name,
        string email,
        char[] passwordChars)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(passwordChars);

        var trimmedName = name.Trim();
        var trimmedEmail = email.Trim().ToLowerInvariant();

        if (trimmedName.Length == 0)
        {
            return new ArgumentException("Name cannot be empty.");
        }

        if (trimmedEmail.Length < 5 || !trimmedEmail.Contains('@') || !trimmedEmail.Contains('.'))
        {
            return new ArgumentException("Email format invalid.");
        }

        var storedHash = HashPassword(passwordChars);
        Array.Clear(passwordChars, 0, passwordChars.Length);

        try
        {
            var id = await connection.ExecuteScalarAsync<int>(
                @"INSERT INTO Users (Name, Email, PasswordHash) VALUES (@name, @email, @hash); SELECT last_insert_rowid();",
                new { name = trimmedName, email = trimmedEmail, hash = storedHash });
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
    public static async Task<Result<UserDto, Exception>> GetByIdAsync(
        IDbConnection connection, int id)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var user = await connection.QueryFirstOrDefaultAsync<UserDto>(
            "SELECT Id, Name, Email, PasswordHash FROM Users WHERE Id = @id",
            new { id });

        return user is null
            ? new KeyNotFoundException($"User {id} not found")
            : (Result<UserDto, Exception>)user;
    }

    /// <summary>
    /// Constant‑time authenticator: returns the user only when the supplied
    /// password matches the row's stored hash, and surfaces an
    /// <see cref="UnauthorizedAccessException"/> for either a missing user
    /// or a wrong password so callers cannot enumerate accounts through the
    /// response code. Every code path performs the same amount of PBKDF2
    /// work, so the duration does not reveal whether the user exists.
    /// </summary>
    public static async Task<Result<UserDto, Exception>> TryAuthenticateAsync(
        IDbConnection connection, string email, char[] passwordChars)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(passwordChars);

        var row = await connection.QueryFirstOrDefaultAsync<UserDto>(
            "SELECT Id, Name, Email, PasswordHash FROM Users WHERE Email = @email",
            new { email = email.Trim().ToLowerInvariant() });

        var verified = VerifyPassword(passwordChars, row?.PasswordHash ?? string.Empty);

        return verified && row is not null
            ? (Result<UserDto, Exception>)row
            : new UnauthorizedAccessException("Invalid credentials");
    }

    /// <summary>
    /// Returns every user, possibly empty. DB failures surface as a plain
    /// <see cref="Exception"/>.
    /// </summary>
    public static async Task<ResultCollection<UserDto, Exception>> ListAsync(
        IDbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        try
        {
            var users = (await connection.QueryAsync<UserDto>(
                "SELECT Id, Name, Email, PasswordHash FROM Users")).ToList();
            return users;
        }
        catch (Exception ex)
        {
            return new Exception($"Persistence failure: {ex.Message}");
        }
    }

    /// <summary>
    /// Updates the password hash. Returns the number of rows affected (0
    /// indicates <see cref="KeyNotFoundException"/>).
    /// </summary>
    public static async Task<Result<int, Exception>> UpdatePasswordAsync(
        IDbConnection connection, int userId, string newHash)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(newHash);

        var affected = await connection.ExecuteAsync(
            "UPDATE Users SET PasswordHash = @hash WHERE Id = @id",
            new { hash = newHash, id = userId });

        return affected == 0
            ? new KeyNotFoundException($"User {userId} not found")
            : (Result<int, Exception>)affected;
    }

    // --- inline password helpers -----------------------------------------
    // Hashing borrows the constant‑time verify path from the standard PBKDF2
    // recipe. There is no dedicated hasher class; the operations live next
    // to the data access that uses them.

    private static string HashPassword(char[] passwordChars)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = new Rfc2898DeriveBytes(
            Encoding.UTF8.GetBytes(new string(passwordChars)),
            salt,
            Iterations,
            HashAlgorithmName.SHA256).GetBytes(HashSize);
        return $"{Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    private static bool VerifyPassword(char[] passwordChars, string stored)
    {
        try
        {
            var parts = stored.Split('$');
            if (parts.Length != 3) return Burn();
            if (!int.TryParse(parts[0], out var iterations)) return Burn();
            var salt   = Convert.FromBase64String(parts[1]);
            var expect = Convert.FromBase64String(parts[2]);
            if (salt.Length != SaltSize || expect.Length != HashSize) return Burn();

            var candidate = new Rfc2898DeriveBytes(
                Encoding.UTF8.GetBytes(new string(passwordChars)),
                salt,
                iterations,
                HashAlgorithmName.SHA256).GetBytes(HashSize);

            return CryptographicOperations.FixedTimeEquals(expect, candidate);
        }
        finally
        {
            Array.Clear(passwordChars, 0, passwordChars.Length);
        }
    }

    /// <summary>
    /// Burns an equivalent amount of PBKDF2 work so timing leaks no
    /// information about whether a stored hash was malformed.
    /// </summary>
    private static bool Burn()
    {
        _ = new Rfc2898DeriveBytes(
            Array.Empty<byte>(),
            new byte[SaltSize],
            Iterations,
            HashAlgorithmName.SHA256).GetBytes(HashSize);
        return false;
    }
}
#pragma warning restore CA1825
