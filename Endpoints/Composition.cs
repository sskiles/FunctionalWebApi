namespace FunctionalWebApi.Endpoints;

using System.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using FunctionalWebApi.Contracts;
using FunctionalWebApi.Domain;
using FunctionalWebApi.Models;
using FunctionalWebApi.Repositories;
using FunctionalWebApi.Services;
using Microsoft.Data.Sqlite;

/// <summary>
/// Composition root: reads configuration, builds the per-operation pipeline
/// delegates (connection construction + service/repository dispatch + JWT
/// issuance where relevant), and binds them into <see cref="UserEndpoints"/>.
///
/// Connections handed to <see cref="UserRepository"/> and
/// <see cref="UserService"/> are constructed <em>closed</em>. Dapper opens
/// lazily on first query and ASP.NET Core's connection pooling handles the
/// rest; no explicit <c>OpenAsync</c> call lives in this layer or in the
/// repositories.
/// </summary>
public static class Composition
{
    /// <summary>
    /// Reads configuration, builds the per‑route pipeline delegates, binds
    /// them into <see cref="UserEndpoints"/>, and returns the application
    /// builder with the user routes registered.
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

        // -- per-route pipeline delegates -----------------------------------
        // Each closure owns its connection's lifetime via `await using`. The
        // SqliteConnection is handed to the repository closed; Dapper
        // auto-opens on first use. Disposal returns the connection to the
        // Microsoft.Data.Sqlite pool.

        Func<LoginCmd, Task<Result<AuthToken, Exception>>> loginHandler =
            async cmd => await UserService.LoginAsync(
                async (email, chars) =>
                {
                    await using var conn = new SqliteConnection(connectionString);
                    return await UserRepository.TryAuthenticateAsync(conn, email, chars);
                },
                jwt,
                cmd);

        Func<CreateUserCmd, Task<Result<UserDto, Exception>>> createUserHandler =
            async cmd =>
            {
                await using var conn = new SqliteConnection(connectionString);
                return await UserService.CreateUserAsync(conn, cmd);
            };

        Func<int, Task<Result<UserDto, Exception>>> getByIdHandler =
            async id =>
            {
                await using var conn = new SqliteConnection(connectionString);
                return await UserRepository.GetByIdAsync(conn, id);
            };

        Func<Task<ResultCollection<UserDto, Exception>>> listHandler =
            async () =>
            {
                await using var conn = new SqliteConnection(connectionString);
                return await UserRepository.ListAsync(conn);
            };

        Func<(int Id, ChangePasswordCmd Cmd), Task<Result<UserDto, Exception>>> changePasswordHandler =
            async t =>
            {
                await using var conn = new SqliteConnection(connectionString);
                return await UserService.ChangePasswordAsync(conn, t.Id, t.Cmd);
            };

        UserEndpoints.Bind(
            loginHandler: loginHandler,
            createUserHandler: createUserHandler,
            getByIdHandler: getByIdHandler,
            listHandler: listHandler,
            changePasswordHandler: changePasswordHandler);

        return app.MapUserEndpoints();
    }
}
