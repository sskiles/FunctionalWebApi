namespace FunctionalWebApi.Endpoints;

using System.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using FunctionalWebApi.Contracts;
using FunctionalWebApi.Domain;
using FunctionalWebApi.Models;
using FunctionalWebApi.Repositories;
using FunctionalWebApi.Errors;

/// <summary>
/// Composition root: reads configuration, builds the connection-opening
/// factory and the login delegate, and binds them into
/// <see cref="UserEndpoints"/> so handlers can call the data and service
/// layers without re‑reading configuration at request time.
/// </summary>
public static class Composition
{
    /// <summary>
    /// Reads configuration, builds the connection opener and the login
    /// delegate, binds them into <see cref="UserEndpoints"/>, and registers
    /// the user routes on the supplied <see cref="WebApplication"/>.
    /// </summary>
    public static IApplicationBuilder RegisterAllEndpoints(
        this WebApplication app, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Sqlite")!;

        var jwtSection = configuration.GetSection("Jwt");
        var jwt = new JwtConfig(
            Key: jwtSection["Key"]!,
            Issuer: jwtSection["Issuer"]!,
            Audience: jwtSection["Audience"]!,
            ExpiresMinutes: int.Parse(jwtSection["ExpiresMinutes"]!));

        // Connection opener: each request gets its own IDbConnection; the
        // ADO.NET pool recycles the underlying handle so this is cheap.
        Func<Task<IDbConnection>> openConnection =
            () => SqliteConnectionFactory.OpenAsync(connectionString);

        // Login delegate: opens its own connection and runs the constant‑time
        // authenticator. The service layer calls this through an opaque
        // Func<,> signature and never sees the connection.
        Func<string, char[], Task<Result<UserDto, AuthError>>> authenticate =
            (email, passwordChars) => OpenAuthenticate(openConnection, email, passwordChars);

        UserEndpoints.Bind(openConnection, authenticate, jwt);

        return app.MapUserEndpoints();
    }

    private static async Task<Result<UserDto, AuthError>> OpenAuthenticate(
        Func<Task<IDbConnection>> openConnection, string email, char[] passwordChars)
    {
        using var conn = await openConnection();
        return await UserRepository.TryAuthenticateAsync(conn, email, passwordChars);
    }
}
