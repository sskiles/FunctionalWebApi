namespace FunctionalWebApi.Services;

using System.Data;
using System.Security.Claims;
using System.Text;
using FunctionalWebApi.Contracts;
using FunctionalWebApi.Domain;
using FunctionalWebApi.Infrastructure;
using FunctionalWebApi.Models;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using JwtReg = Microsoft.IdentityModel.JsonWebTokens.JwtRegisteredClaimNames;

/// <summary>
/// Stateless service layer for user-facing operations. Known failure modes
/// surface as <see cref="Result{TValue, TError}"/> carrying a BCL
/// <see cref="Exception"/> value. Unknown infrastructure failures remain
/// as uncaught exceptions and surface as HTTP 500 downstream.
///
/// Collaborators are pulled from <see cref="Composition"/> via the static
/// constructor. The service never references <see cref="Repositories.UserRepository"/>
/// by name; <see cref="Composition"/> wires the delegates after currying
/// the connection factory. This keeps the layered separation clean: the
/// service validates, shapes, and applies use-case rules, and the
/// repository only persists.
///
/// TEMPORARY: passwords are passed and persisted verbatim. No hashing,
/// no verification, no constant-time compare. When password handling is
/// properly reintroduced this layer will own the hashing and provide a
/// proper <see cref="UnauthorizedAccessException"/> path.
/// </summary>
public static class UserService
{
    private static readonly JwtConfig _jwt;
    private static readonly Func<string, char[], Task<Result<UserDto, Exception>>> _authenticate;
    private static readonly Func<string, string, string, Task<Result<UserDto, Exception>>> _createInRepository;
    private static readonly Func<int, Task<Result<UserDto, Exception>>> _getByIdInRepository;
    private static readonly Func<int, string, Task<Result<int, Exception>>> _updatePasswordInRepository;

    static UserService()
    {
        _jwt = Composition.Jwt;
        _authenticate = Composition.Authenticate;
        _createInRepository = Composition.CreateInRepository;
        _getByIdInRepository = Composition.GetByIdInRepository;
        _updatePasswordInRepository = Composition.UpdatePasswordInRepository;
    }

    /// <summary>
    /// Verifies the credentials via the repository's authenticator and
    /// issues a fresh JWT on success. Any failure surfaces as a single
    /// <see cref="UnauthorizedAccessException"/> so callers cannot enumerate
    /// accounts.
    /// </summary>
    /// <remarks>
    /// The <see cref="_authenticate"/> delegate is built by
    /// <see cref="Composition.RegisterAllEndpoints"/> with the connection
    /// factory already applied. The login flow performs only one DB read.
    /// </remarks>
    public static async Task<Result<AuthToken, Exception>> LoginAsync(LoginCmd cmd)
    {
        if (cmd is null)
            return new ArgumentNullException(nameof(cmd));

        var passwordChars = cmd.Password.ToCharArray();
        try
        {
            var authResult = await _authenticate(cmd.Username, passwordChars);
            if (authResult.IsFailure)
            {
                return authResult.Error!;
            }

            var user = authResult.Value!;
            var jwtValue = IssueToken(user);
            return new AuthToken(jwtValue);
        }
        finally
        {
            Array.Clear(passwordChars, 0, passwordChars.Length);
        }
    }

    /// <summary>
    /// Validates the supplied <see cref="CreateUserCmd"/>, confirms that the
    /// two password fields match (plain server-side comparison, regardless
    /// of client validation), and persists the user through the repository
    /// via the injected <see cref="_createInRepository"/> delegate
    /// (already curried with a connection factory). Returns either the new
    /// <see cref="UserDto"/>, an <see cref="ArgumentException"/> describing
    /// the input problem, or a plain <see cref="Exception"/> for storage
    /// failures.
    /// </summary>
    public static async Task<Result<UserDto, Exception>> CreateUserAsync(CreateUserCmd cmd)
    {
        if (cmd is null)
            return new ArgumentNullException(nameof(cmd));

        if (string.IsNullOrWhiteSpace(cmd.Name))
        {
            return new ArgumentException("Name cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(cmd.Email))
        {
            return new ArgumentException("Email is required.");
        }

        if (cmd.Password.Length == 0)
        {
            return new ArgumentException("Password cannot be empty.");
        }

        if (!string.Equals(cmd.Password, cmd.ConfirmPassword, StringComparison.Ordinal))
        {
            return new ArgumentException("Password confirmation does not match.");
        }

        var trimmedName = cmd.Name.Trim();
        var trimmedEmail = cmd.Email.Trim().ToLowerInvariant();

        if (trimmedEmail.Length < 5 || !trimmedEmail.Contains('@') || !trimmedEmail.Contains('.'))
        {
            return new ArgumentException("Email format invalid.");
        }

        return await _createInRepository(trimmedName, trimmedEmail, cmd.Password);
    }

    /// <summary>
    /// Changes a user's password by updating it via the repository's update
    /// delegate. The two repository collaborators
    /// (<see cref="_getByIdInRepository"/> and
    /// <see cref="_updatePasswordInRepository"/>) are injected as
    /// method-group delegates already bound to a connection factory. Returns
    /// the updated <see cref="UserDto"/>, or one of:
    /// <see cref="KeyNotFoundException"/>, <see cref="UnauthorizedAccessException"/>,
    /// or <see cref="ArgumentException"/>.
    /// </summary>
    /// <remarks>TEMPORARY: current-password is not verified. The endpoint
    /// confirms the value is non-empty only. Will be replaced when
    /// password handling is reintroduced.</remarks>
    public static async Task<Result<UserDto, Exception>> ChangePasswordAsync(int userId, ChangePasswordCmd cmd)
    {
        if (cmd is null)
            return new ArgumentNullException(nameof(cmd));

        var loaded = await _getByIdInRepository(userId);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        if (cmd.CurrentPassword.Length == 0)
        {
            return new ArgumentException("Current password cannot be empty.");
        }

        if (cmd.NewPassword.Length == 0)
        {
            return new ArgumentException("New password cannot be empty.");
        }

        if (!string.Equals(cmd.NewPassword, cmd.ConfirmNewPassword, StringComparison.Ordinal))
        {
            return new ArgumentException("New password confirmation does not match.");
        }

        var update = await _updatePasswordInRepository(userId, cmd.NewPassword);
        if (update.IsFailure)
        {
            return update.Error!;
        }

        var refreshed = await _getByIdInRepository(userId);
        return refreshed.IsFailure
            ? refreshed.Error!
            : refreshed.Value!;
    }

    // --- token issuance --------------------------------------------------
    // Kept private to this service because it touches ClaimsIdentity +
    // SigningCredentials, both pure orchestration concerns that don't
    // belong in endpoints or repositories.

    private static string IssueToken(UserDto user)
    {
        var key = Encoding.UTF8.GetBytes(_jwt.Key);
        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(JwtReg.Sub, user.Id.ToString()),
                new Claim(JwtReg.Jti, Guid.NewGuid().ToString()),
                new Claim("uid", user.Id.ToString()),
            }),
            Expires = DateTime.UtcNow.AddMinutes(_jwt.ExpiresMinutes),
            Issuer = _jwt.Issuer,
            Audience = _jwt.Audience,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(key),
                SecurityAlgorithms.HmacSha256),
        };
        return new JsonWebTokenHandler().CreateToken(tokenDescriptor);
    }
}