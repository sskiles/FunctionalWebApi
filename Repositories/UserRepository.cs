using Dapper;
using Microsoft.Data.Sqlite;
using MyApi.Errors;
using MyApi.Models;

namespace MyApi.Repositories;

public static class UserRepository
{
    /// <summary>
    /// Inserts a new user, hashing <paramref name="passwordChars"/> via the
    /// global password policy. Throws <see cref="ValidationError"/> if validation
    /// fails or <see cref="SqlError"/> on a unique‑constraint violation.
    /// </summary>
    public static async Task<UserDto> CreateAsync(
        string connectionString,
        string name,
        string email,
        char[] passwordChars)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(passwordChars);

        var trimmedName  = name.Trim();
        var trimmedEmail = email.Trim().ToLowerInvariant();

        if (trimmedName.Length == 0)
            throw new ValidationError(new Dictionary<string, string[]> { ["name"] = ["Name cannot be empty."] });
        if (trimmedEmail.Length < 5 || !trimmedEmail.Contains('@') || !trimmedEmail.Contains('.'))
            throw new ValidationError(new Dictionary<string, string[]> { ["email"] = ["Email format invalid."] });

        // Hash the password and immediately clear the plaintext buffer.
        var storedHash = MyApi.Security.ArgumentPasswordHasher.Hash(passwordChars);
        // `Hash` clears the buffer as part of its contract; just being defensive.
        Array.Clear(passwordChars, 0, passwordChars.Length);

        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync();

        try
        {
            var id = await conn.ExecuteScalarAsync<int>(
                @"INSERT INTO Users (Name, Email, PasswordHash) VALUES (@name, @email, @hash); SELECT last_insert_rowid();",
                new { name = trimmedName, email = trimmedEmail, hash = storedHash });
            return new UserDto(id, trimmedName, trimmedEmail);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new SqlError(SqlError.Kind.ConstraintViolation, ex.Message);
        }
    }

    /// <summary>
    /// Loads a user by id. Throws <see cref="NotFoundError"/> on miss.
    /// </summary>
    public static async Task<UserDto> GetByIdAsync(string connectionString, int id)
    {
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync();

        var user = await conn.QueryFirstOrDefaultAsync<UserDto>(
            "SELECT Id, Name, Email, PasswordHash FROM Users WHERE Id = @id",
            new { id });

        return user ?? throw new NotFoundError($"User {id} not found");
    }

    /// <summary>
    /// Constant‑time authenticator: returns the user only when the supplied
    /// password matches the row's stored hash. Every code path performs the
    /// same amount of PBKDF2 work, so the duration does not reveal whether the
    /// user exists.
    /// </summary>
    public static async Task<UserDto?> TryAuthenticateAsync(
        string connectionString, string email, char[] passwordChars)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(passwordChars);

        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync();

        var row = await conn.QueryFirstOrDefaultAsync<UserDto>(
            "SELECT Id, Name, Email, PasswordHash FROM Users WHERE Email = @email",
            new { email = email.Trim().ToLowerInvariant() });

        // Hand off to the same hasher that stored the value. The
        // plaintext‑to‑array buffer is consumed deterministically.
        var verified = MyApi.Security.ArgumentPasswordHasher.Verify(passwordChars, row?.PasswordHash ?? "");

        return verified ? row : null;
    }

    /// <summary>
    /// Returns every user, possibly empty.
    /// </summary>
    public static async Task<IReadOnlyList<UserDto>> ListAsync(string connectionString)
    {
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync();

        var users = await conn.QueryAsync<UserDto>(
            "SELECT Id, Name, Email, PasswordHash FROM Users");

        return users.ToList();
    }
}
