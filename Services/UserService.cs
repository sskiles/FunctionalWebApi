namespace FunctionalWebApi.Services;

using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FunctionalWebApi.Contracts;
using FunctionalWebApi.Domain;
using FunctionalWebApi.Endpoints;
using FunctionalWebApi.Models;
using FunctionalWebApi.Repositories;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using JwtReg = Microsoft.IdentityModel.JsonWebTokens.JwtRegisteredClaimNames;

/// <summary>
/// Stateless service layer for user-facing operations. Known failure modes
/// surface as <see cref="Result{TValue, TError}"/> carrying a BCL
/// <see cref="Exception"/> value. Unknown infrastructure failures remain
/// as uncaught exceptions and surface as HTTP 500 downstream.
///
/// Each method takes an already‑open <see cref="IDbConnection"/> managed by
/// the caller. The service never owns the connection's lifetime. Password
/// verification logic is inlined; there is no dedicated hasher class.
/// </summary>
public static class UserService
{
    /// <summary>
    /// Verifies the credentials via the repository's constant‑time
    /// authenticator and issues a fresh JWT on success. Any failure surfaces
    /// as a single <see cref="UnauthorizedAccessException"/> so callers
    /// cannot enumerate accounts.
    /// </summary>
    /// <remarks>
    /// The <paramref name="authenticate"/> delegate is built by
    /// <see cref="Composition.RegisterAllEndpoints"/> and is responsible for
    /// opening and disposing its own <see cref="IDbConnection"/>. The login
    /// flow performs only one DB read.
    /// </remarks>
    public static async Task<Result<AuthToken, Exception>> LoginAsync(
        Func<string, char[], Task<Result<UserDto, Exception>>> authenticate,
        JwtConfig jwt,
        LoginCmd cmd)
    {
        ArgumentNullException.ThrowIfNull(authenticate);
        ArgumentNullException.ThrowIfNull(cmd);

        var passwordChars = cmd.Password.ToCharArray();
        Result<UserDto, Exception> authResult;
        try
        {
            authResult = await authenticate(cmd.Username, passwordChars);
        }
        catch
        {
            Array.Clear(passwordChars, 0, passwordChars.Length);
            return new UnauthorizedAccessException("Invalid credentials");
        }

        Array.Clear(passwordChars, 0, passwordChars.Length);

        if (authResult.IsFailure)
        {
            // Surface whatever exception the delegate returned (typically
            // UnauthorizedAccessException for any auth failure).
            return authResult.Error!;
        }

        var user = authResult.Value!;

        var tokenHandler = new JsonWebTokenHandler();
        var key = Encoding.UTF8.GetBytes(jwt.Key);
        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(JwtReg.Sub, user.Id.ToString()),
                new Claim(JwtReg.Jti, Guid.NewGuid().ToString()),
                new Claim("uid",      user.Id.ToString()),
            }),
            Expires = DateTime.UtcNow.AddMinutes(jwt.ExpiresMinutes),
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(key),
                SecurityAlgorithms.HmacSha256),
        };
        var jwtValue = tokenHandler.CreateToken(tokenDescriptor);
        return new AuthToken(jwtValue);
    }

    /// <summary>
    /// Validates the supplied <see cref="CreateUserCmd"/>, confirms that the
    /// two password fields match (plain server‑side comparison, regardless
    /// of client validation), and persists the user through the repository.
    /// Returns either the new <see cref="UserDto"/>, an
    /// <see cref="ArgumentException"/> describing the input problem, or a
    /// plain <see cref="Exception"/> for storage failures.
    /// </summary>
    public static async Task<Result<UserDto, Exception>> CreateUserAsync(
        Func<IDbConnection> newConnection,
        CreateUserCmd cmd,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(newConnection);
        ArgumentNullException.ThrowIfNull(cmd);

        if (!string.Equals(cmd.Password, cmd.ConfirmPassword, StringComparison.Ordinal))
        {
            return new ArgumentException("Password confirmation does not match.");
        }

        var passwordChars = cmd.Password.ToCharArray();
        try
        {
            return await UserRepository.CreateAsync(newConnection, cmd.Name, cmd.Email, passwordChars);
        }
        finally
        {
            // Repository also clears; defensive second wipe in case it short-circuited.
            Array.Clear(passwordChars, 0, passwordChars.Length);
        }
    }

    /// <summary>
    /// Changes a user's password after verifying the current one. Returns
    /// the updated <see cref="UserDto"/>, or one of:
    /// <see cref="KeyNotFoundException"/>,
    /// <see cref="UnauthorizedAccessException"/>, or
    /// <see cref="ArgumentException"/>.
    /// </summary>
    public static async Task<Result<UserDto, Exception>> ChangePasswordAsync(
        Func<IDbConnection> newConnection,
        int userId,
        ChangePasswordCmd cmd)
    {
        ArgumentNullException.ThrowIfNull(newConnection);
        ArgumentNullException.ThrowIfNull(cmd);

        var loaded = await UserRepository.GetByIdAsync(newConnection, userId);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }
        var user = loaded.Value!;

        // Verify the current password against the stored hash. Constant-time
        // comparison via the same PBKDF2 path used at registration.
        var currentPasswordChars = cmd.CurrentPassword.ToCharArray();
        try
        {
            if (!VerifyPassword(currentPasswordChars, user.PasswordHash ?? string.Empty))
            {
                return new UnauthorizedAccessException("Current password is incorrect.");
            }
        }
        finally
        {
            Array.Clear(currentPasswordChars, 0, currentPasswordChars.Length);
        }

        if (!string.Equals(cmd.NewPassword, cmd.ConfirmNewPassword, StringComparison.Ordinal))
        {
            return new ArgumentException("New password confirmation does not match.");
        }

        var newPasswordChars = cmd.NewPassword.ToCharArray();
        try
        {
            var newHash = HashPassword(newPasswordChars);
            var update = await UserRepository.UpdatePasswordAsync(newConnection, userId, newHash);
            if (update.IsFailure)
            {
                return update.Error!;
            }

            var refreshed = await UserRepository.GetByIdAsync(newConnection, userId);
            return refreshed.IsFailure
                ? refreshed.Error!
                : refreshed.Value!;
        }
        finally
        {
            Array.Clear(newPasswordChars, 0, newPasswordChars.Length);
        }
    }

    // --- inline password helpers -----------------------------------------
    // Same PBKDF2-HMAC-SHA256 recipe as the repository. Kept private to
    // this service so the verification step lives next to the orchestration
    // that calls it.

    private const int Iterations = 600_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

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
