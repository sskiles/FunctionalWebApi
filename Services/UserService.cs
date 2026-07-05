using FunctionalWebApi.Contracts;
using FunctionalWebApi.Errors;
using FunctionalWebApi.Models;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using JwtReg = Microsoft.IdentityModel.JsonWebTokens.JwtRegisteredClaimNames;

namespace FunctionalWebApi.Services;

/// <summary>
/// Stateless service layer for user-facing operations. Failures throw
/// <see cref="AppException"/>-derived types; the request pipeline converts
/// them to HTTP responses via <see cref="FunctionalWebApi.Domain.DomainErrorHandler"/>.
/// </summary>
public static class UserService
{
    /// <summary>
    /// Verifies the credentials via the repository's constant‑time authenticator
    /// and issues a fresh JWT on success. Any failure — missing user, wrong
    /// password, malformed hash — surfaces as the same <see cref="AuthError"/>,
    /// so callers cannot enumerate accounts through the response code.
    /// </summary>
    public static async Task<AuthToken> LoginAsync(
        Func<string, char[], Task<UserDto?>> authenticate,
        JwtConfig jwt,
        LoginCmd cmd)
    {
        var passwordChars = cmd.Password.ToCharArray();
        UserDto? user = null;
        try
        {
            user = await authenticate(cmd.Username, passwordChars);
        }
        catch
        {
            Array.Clear(passwordChars, 0, passwordChars.Length);
            throw new AuthError("Invalid credentials");
        }

        // Always clear the plaintext from memory, success or failure.
        Array.Clear(passwordChars, 0, passwordChars.Length);

        if (user is null)
            throw new AuthError("Invalid credentials");

        var jwtKey            = jwt.Key;
        var jwtIssuer         = jwt.Issuer;
        var jwtAudience       = jwt.Audience;
        var jwtExpiresMinutes = jwt.ExpiresMinutes;

        var tokenHandler = new JsonWebTokenHandler();
        var key = Encoding.UTF8.GetBytes(jwtKey);
        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(JwtReg.Sub,  user.Id.ToString()),
                new Claim(JwtReg.Jti,  Guid.NewGuid().ToString()),
                new Claim("uid",      user.Id.ToString())
            }),
            Expires = DateTime.UtcNow.AddMinutes(jwtExpiresMinutes),
            Issuer = jwtIssuer,
            Audience = jwtAudience,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256)
        };
        var jwtValue = tokenHandler.CreateToken(tokenDescriptor);
        return new AuthToken(jwtValue);
    }

    /// <summary>
    /// Validates the supplied <see cref="CreateUserCmd"/>, confirms that the
    /// two password fields match (server‑side, regardless of client validation),
    /// and persists the user through the repository.
    ///
    /// Throws <see cref="ValidationError"/> for any input problem and
    /// <see cref="AppException"/>‑derived DB errors from <see cref="FunctionalWebApi.Repositories.UserRepository"/>.
    /// </summary>
    public static async Task<UserDto> CreateUserAsync(
        CreateUserCmd cmd,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        // Copy plaintext into mutable buffers so we can wipe them after use.
        var password         = cmd.Password.ToCharArray();
        var confirmPassword  = cmd.ConfirmPassword.ToCharArray();
        try
        {
            // Constant‑time equality check: identical inputs produce identical
            // PBKDF2 digests (deterministic salt); mismatches yield constant‑time
            // rejection. `AreEqual` wipes both buffers for us.
            if (!FunctionalWebApi.Security.ArgumentPasswordHasher.AreEqual(password, confirmPassword))
                throw new ValidationError(new Dictionary<string, string[]>
                {
                    ["confirmPassword"] = new[] { "Password confirmation does not match." }
                });

            // Hand a fresh plaintext to the repository; it will hash + clear.
            var user = await FunctionalWebApi.Repositories.UserRepository.CreateAsync(
                connectionString: FunctionalWebApi.Composition.ConnectionString!,
                name:             cmd.Name,
                email:            cmd.Email,
                passwordChars:    cmd.Password.ToCharArray()); // repository hashes & clears

            return user;
        }
        finally
        {
            // The helper may have wiped one or both. Make sure they are gone.
            Array.Clear(password, 0, password.Length);
            Array.Clear(confirmPassword, 0, confirmPassword.Length);
        }
    }
}