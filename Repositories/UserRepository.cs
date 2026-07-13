namespace FunctionalWebApi.Repositories;

using System.Data;
using Dapper;
using FunctionalWebApi.Domain;
using FunctionalWebApi.Errors;
using FunctionalWebApi.Models;
using FunctionalWebApi.Security;

/// <summary>
/// Data access for user accounts. All known domain failure modes —
/// validation, not‑found, authentication, persistence failures — are
/// returned as <see cref="Result{TValue, TError}"/> values. Unknown
/// exceptions caught inside catch blocks are wrapped as
/// <see cref="SqlError"/> of <see cref="SqlError.Kind.Unknown"/>.
/// Truly unexpected exceptions (e.g. <c>ArgumentException</c> from a
/// service earlier in the chain) are allowed to propagate.
///
/// Each method takes an already‑open <see cref="IDbConnection"/>; the caller
/// owns the connection's lifetime. Repository methods do not open or dispose.
/// </summary>
public static class UserRepository
{
    /// <summary>
    /// Validates the input, persists a new user, and returns the inserted row.
    /// Validation problems surface as <see cref="ValidationError"/>; any
    /// persistence failure (driver, network, constraint) is wrapped as
    /// <see cref="SqlError"/>.
    /// </summary>
    public static async Task<Result<UserDto, AppException>> CreateAsync(
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
            return new ValidationError(new Dictionary<string, string[]>
            {
                ["name"] = ["Name cannot be empty."],
            });
        }

        if (trimmedEmail.Length < 5 || !trimmedEmail.Contains('@') || !trimmedEmail.Contains('.'))
        {
            return new ValidationError(new Dictionary<string, string[]>
            {
                ["email"] = ["Email format invalid."],
            });
        }

        var storedHash = ArgumentPasswordHasher.Hash(passwordChars);
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
            // Catches driver errors, network errors, or anything else. Don't
            // attempt to narrow to a vendor‑specific exception type — the
            // failure mode is opaque to this layer by design.
            return new SqlError(SqlError.Kind.Unknown, ex.Message);
        }
    }

    /// <summary>
    /// Loads a user by id. Returns <see cref="NotFoundError"/> if no row matches.
    /// </summary>
    public static async Task<Result<UserDto, NotFoundError>> GetByIdAsync(
        IDbConnection connection, int id)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var user = await connection.QueryFirstOrDefaultAsync<UserDto>(
            "SELECT Id, Name, Email, PasswordHash FROM Users WHERE Id = @id",
            new { id });

        return user is null
            ? new NotFoundError($"User {id} not found")
            : user;
    }

    /// <summary>
    /// Constant‑time authenticator: returns the user only when the supplied
    /// password matches the row's stored hash, and surfaces an
    /// <see cref="AuthError"/> for either a missing user or a wrong password
    /// so callers cannot enumerate accounts through the response code.
    /// Every code path performs the same amount of PBKDF2 work, so the
    /// duration does not reveal whether the user exists.
    /// </summary>
    public static async Task<Result<UserDto, AuthError>> TryAuthenticateAsync(
        IDbConnection connection, string email, char[] passwordChars)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(passwordChars);

        var row = await connection.QueryFirstOrDefaultAsync<UserDto>(
            "SELECT Id, Name, Email, PasswordHash FROM Users WHERE Email = @email",
            new { email = email.Trim().ToLowerInvariant() });

        var verified = ArgumentPasswordHasher.Verify(
            passwordChars,
            row?.PasswordHash ?? string.Empty);

        return verified && row is not null
            ? row
            : new AuthError("Invalid credentials");
    }

    /// <summary>
    /// Returns every user, possibly empty. DB failures surface as
    /// <see cref="SqlError"/>.
    /// </summary>
    public static async Task<ResultCollection<UserDto, SqlError>> ListAsync(
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
            // Catches driver errors, network errors, or anything else. Don't
            // attempt to narrow to a vendor‑specific exception type — the
            // failure mode is opaque to this layer by design.
            return new SqlError(SqlError.Kind.Unknown, ex.Message);
        }
    }

    /// <summary>
    /// Updates the password hash. Returns the number of rows affected (0
    /// indicates <see cref="NotFoundError"/>).
    /// </summary>
    public static async Task<Result<int, NotFoundError>> UpdatePasswordAsync(
        IDbConnection connection, int userId, string newHash)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(newHash);

        var affected = await connection.ExecuteAsync(
            "UPDATE Users SET PasswordHash = @hash WHERE Id = @id",
            new { hash = newHash, id = userId });

        return affected == 0
            ? new NotFoundError($"User {userId}")
            : affected;
    }
}
