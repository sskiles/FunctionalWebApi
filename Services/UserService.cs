namespace FunctionalWebApi.Services;

using System.Data;
using System.Security.Claims;
using System.Text;
using FunctionalWebApi.Contracts;
using FunctionalWebApi.Domain;
using FunctionalWebApi.Errors;
using FunctionalWebApi.Endpoints;
using FunctionalWebApi.Models;
using FunctionalWebApi.Repositories;
using FunctionalWebApi.Security;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using JwtReg = Microsoft.IdentityModel.JsonWebTokens.JwtRegisteredClaimNames;

/// <summary>
/// Stateless service layer for user-facing operations. Known failure modes
/// surface as <see cref="Result{TValue, TError}"/> carrying the
/// <see cref="AppException"/>-derived value. Unknown infrastructure failures
/// remain as uncaught exceptions and surface as HTTP 500 downstream.
///
/// Each method takes an already‑open <see cref="IDbConnection"/> managed by
/// the caller. The service never owns the connection's lifetime.
/// </summary>
public static class UserService
{
    /// <summary>
    /// Verifies the credentials via the repository's constant‑time
    /// authenticator and issues a fresh JWT on success. Any failure surfaces as
    /// a single <see cref="AuthError"/> so callers cannot enumerate accounts.
    /// </summary>
    /// <remarks>
    /// The <paramref name="authenticate"/> delegate is built by <see cref="Composition"/>
    /// and is responsible for opening and disposing its own
    /// <see cref="IDbConnection"/>. The login flow does not need a shared
    /// connection since it performs only one DB read.
    /// </remarks>
    public static async Task<Result<AuthToken, AuthError>> LoginAsync(
        Func<string, char[], Task<Result<UserDto, AuthError>>> authenticate,
        JwtConfig jwt,
        LoginCmd cmd)
    {
        ArgumentNullException.ThrowIfNull(authenticate);
        ArgumentNullException.ThrowIfNull(cmd);

        var passwordChars = cmd.Password.ToCharArray();
        Result<UserDto, AuthError> authResult;
        try
        {
            authResult = await authenticate(cmd.Username, passwordChars);
        }
        catch
        {
            Array.Clear(passwordChars, 0, passwordChars.Length);
            return new AuthError("Invalid credentials");
        }

        Array.Clear(passwordChars, 0, passwordChars.Length);

        if (authResult.IsFailure)
        {
            return authResult.Error!;
        }

        var user = authResult.Value!;
        var jwtKey = jwt.Key;
        var jwtIssuer = jwt.Issuer;
        var jwtAudience = jwt.Audience;
        var jwtExpiresMinutes = jwt.ExpiresMinutes;

        var tokenHandler = new JsonWebTokenHandler();
        var key = Encoding.UTF8.GetBytes(jwtKey);
        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(JwtReg.Sub, user.Id.ToString()),
                new Claim(JwtReg.Jti, Guid.NewGuid().ToString()),
                new Claim("uid",      user.Id.ToString()),
            }),
            Expires = DateTime.UtcNow.AddMinutes(jwtExpiresMinutes),
            Issuer = jwtIssuer,
            Audience = jwtAudience,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(key),
                SecurityAlgorithms.HmacSha256),
        };
        var jwtValue = tokenHandler.CreateToken(tokenDescriptor);
        return new AuthToken(jwtValue);
    }

    /// <summary>
    /// Validates the supplied <see cref="CreateUserCmd"/>, confirms that the
    /// two password fields match (constant‑time, regardless of client
    /// validation), and persists the user through the repository. Returns
    /// either the new <see cref="UserDto"/>, a <see cref="ValidationError"/>
    /// for input problems, or a <see cref="SqlError"/> if the email is taken.
    /// </summary>
    public static async Task<Result<UserDto, AppException>> CreateUserAsync(
        IDbConnection connection,
        CreateUserCmd cmd,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(cmd);

        var password = cmd.Password.ToCharArray();
        var confirmPassword = cmd.ConfirmPassword.ToCharArray();
        try
        {
            if (!ArgumentPasswordHasher.AreEqual(password, confirmPassword))
            {
                return new ValidationError(new Dictionary<string, string[]>
                {
                    ["confirmPassword"] = ["Password confirmation does not match."],
                });
            }

            var repoResult = await UserRepository.CreateAsync(
                connection: connection,
                name: cmd.Name,
                email: cmd.Email,
                passwordChars: cmd.Password.ToCharArray());

            return repoResult;
        }
        finally
        {
            Array.Clear(password, 0, password.Length);
            Array.Clear(confirmPassword, 0, confirmPassword.Length);
        }
    }

    /// <summary>
    /// Changes a user's password after verifying the current one. Returns the
    /// updated <see cref="UserDto"/>, or one of:
    /// <see cref="NotFoundError"/>, <see cref="AuthError"/>,
    /// <see cref="ValidationError"/>.
    /// </summary>
    public static async Task<Result<UserDto, AppException>> ChangePasswordAsync(
        IDbConnection connection,
        int userId,
        ChangePasswordCmd cmd)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(cmd);

        var loaded = await UserRepository.GetByIdAsync(connection, userId);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }
        var user = loaded.Value!;

        var currentChars = cmd.CurrentPassword.ToCharArray();
        try
        {
            if (!ArgumentPasswordHasher.Verify(currentChars, user.PasswordHash ?? string.Empty))
            {
                return new AuthError("Current password is incorrect.");
            }
        }
        finally
        {
            Array.Clear(currentChars, 0, currentChars.Length);
        }

        var newPasswordChars = cmd.NewPassword.ToCharArray();
        var confirmPasswordChars = cmd.ConfirmNewPassword.ToCharArray();
        try
        {
            if (!ArgumentPasswordHasher.AreEqual(newPasswordChars, confirmPasswordChars))
            {
                return new ValidationError(new Dictionary<string, string[]>
                {
                    ["confirmPassword"] = ["New password confirmation does not match."],
                });
            }

            var newHash = ArgumentPasswordHasher.Hash(cmd.NewPassword.ToCharArray());

            var update = await UserRepository.UpdatePasswordAsync(connection, userId, newHash);
            if (update.IsFailure)
            {
                return update.Error!;
            }

            var refreshed = await UserRepository.GetByIdAsync(connection, userId);
            return refreshed.IsFailure
                ? refreshed.Error!
                : refreshed.Value!;
        }
        finally
        {
            Array.Clear(newPasswordChars, 0, newPasswordChars.Length);
            Array.Clear(confirmPasswordChars, 0, confirmPasswordChars.Length);
        }
    }
}
