namespace FunctionalWebApi.Endpoints;

using System.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using FunctionalWebApi.Contracts;
using FunctionalWebApi.Domain;
using FunctionalWebApi.Models;
using FunctionalWebApi.Repositories;
using FunctionalWebApi.Services;

/// <summary>
/// Composition root: reads configuration, builds the per-operation pipeline
/// delegates (connection open + service/repository dispatch + JWT issuance
/// where relevant), and binds them into <see cref="UserEndpoints"/>. Every
/// delegate is parameter‑driven so the call chain — endpoint handler,
/// service, repository — receives its dependencies through closures rather
/// than static class references. The pipeline bodies live here; the
/// business logic bodies live in <see cref="UserService"/> and
/// <see cref="UserRepository"/>.
/// </summary>
public static class Composition
{
    /// <summary>
    /// Reads configuration, builds the connection lifecycle and per‑route
    /// pipeline delegates, binds them into <see cref="UserEndpoints"/>, and
    /// returns the application builder with the user routes registered.
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

        // -- connection lifecycle -------------------------------------------
        // A single connection opener captured by every per‑route pipeline.
        Func<Task<IDbConnection>> openConnection =
            () => SqliteConnectionFactory.OpenAsync(connectionString);

        // -- authenticate delegate ------------------------------------------
        // The login pipeline doesn't open a connection at the endpoint;
        // instead it routes through UserService.LoginAsync which delegates
        // the credential check through this closure. The closure owns
        // connection lifetime for the credential lookup.
        Func<string, char[], Task<Result<UserDto, Exception>>> authenticate =
            async (email, passwordChars) =>
            {
                using var conn = await openConnection();
                return await UserRepository.TryAuthenticateAsync(conn, email, passwordChars);
            };

        // -- per-route pipeline delegates -----------------------------------

        Func<LoginCmd, Task<Result<AuthToken, Exception>>> loginHandler =
            async cmd => await UserService.LoginAsync(authenticate, jwt, cmd);

        Func<CreateUserCmd, Task<Result<UserDto, Exception>>> createUserHandler =
            async cmd =>
            {
                using var conn = await openConnection();
                return await UserService.CreateUserAsync(conn, cmd);
            };

        Func<int, Task<Result<UserDto, Exception>>> getByIdHandler =
            async id =>
            {
                using var conn = await openConnection();
                return await UserRepository.GetByIdAsync(conn, id);
            };

        Func<Task<ResultCollection<UserDto, Exception>>> listHandler =
            async () =>
            {
                using var conn = await openConnection();
                return await UserRepository.ListAsync(conn);
            };

        Func<(int Id, ChangePasswordCmd Cmd), Task<Result<UserDto, Exception>>> changePasswordHandler =
            async t =>
            {
                using var conn = await openConnection();
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
