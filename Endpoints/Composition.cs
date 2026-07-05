namespace FunctionalWebApi.Endpoints;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using FunctionalWebApi.Contracts;
using FunctionalWebApi.Repositories;

/// <summary>
/// Composition root: reads configuration, wires the user endpoints' data and
/// service delegates, and registers their routes on the supplied
/// <see cref="WebApplication"/>.
/// </summary>
public static class Composition
{
    /// <summary>
    /// Gets cached SQLite connection string, exposed so the endpoint module can
    /// pass it to the data layer. <see langword="internal"/> because callers
    /// outside the assembly don't need it.
    /// </summary>
    internal static string ConnectionString { get; private set; } = string.Empty;

    /// <summary>
    /// Reads configuration, builds the login delegate and binds it into
    /// <see cref="UserEndpoints"/> so its handlers can issue calls without
    /// re‑reading the configuration on every request.
    /// </summary>
    public static IApplicationBuilder RegisterAllEndpoints(
        this WebApplication app, IConfiguration configuration)
    {
        ConnectionString = configuration.GetConnectionString("Sqlite")!;

        var jwtSection = configuration.GetSection("Jwt");
        var jwt = new JwtConfig(
            Key: jwtSection["Key"]!,
            Issuer: jwtSection["Issuer"]!,
            Audience: jwtSection["Audience"]!,
            ExpiresMinutes: int.Parse(jwtSection["ExpiresMinutes"]!));

        // Bind the login delegate plus connection string + JWT into the user
        // endpoint module.  The endpoints get bound before routes are mapped,
        // so handlers see the configuration when they execute.
        UserEndpoints.Bind(
            authenticate: (email, passwordChars) => UserRepository.TryAuthenticateAsync(
                ConnectionString,
                email,
                passwordChars),
            connectionString: ConnectionString,
            jwt: jwt);

        return app.MapUserEndpoints();
    }
}
