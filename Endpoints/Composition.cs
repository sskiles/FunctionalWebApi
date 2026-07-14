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
/// A single <c>newConnection</c> delegate is constructed here and threaded
/// down to every per-route pipeline. The delegate returns a fresh,
/// <em>closed</em> <see cref="SqliteConnection"/>; the consumer opens it
/// (Dapper checks <c>WasClosed</c> on first query) and disposes it on
/// method exit. Mirrors the shape of <see cref="JwtConfig"/>: a small piece
/// of built-once data handed to lower layers that consume it.
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

        // One dataset for the lifetime of the app: a closed
        // SqliteConnection per request, opened lazily by Dapper. The driver
        // type leaks no further than this closure body.
        Func<IDbConnection> newConnection = () => new SqliteConnection(connectionString);

        // -- per-route pipeline delegates -----------------------------------

        Func<LoginCmd, Task<Result<AuthToken, Exception>>> loginHandler =
            async cmd => await UserService.LoginAsync(
                (email, chars) => UserRepository.TryAuthenticateAsync(newConnection, email, chars),
                jwt,
                cmd);

        Func<CreateUserCmd, Task<Result<UserDto, Exception>>> createUserHandler =
            async cmd => await UserService.CreateUserAsync(newConnection, cmd);

        Func<int, Task<Result<UserDto, Exception>>> getByIdHandler =
            async id => await UserRepository.GetByIdAsync(newConnection, id);

        Func<Task<ResultCollection<UserDto, Exception>>> listHandler =
            async () => await UserRepository.ListAsync(newConnection);

        Func<(int Id, ChangePasswordCmd Cmd), Task<Result<UserDto, Exception>>> changePasswordHandler =
            async t => await UserService.ChangePasswordAsync(newConnection, t.Id, t.Cmd);

        UserEndpoints.Bind(
            loginHandler: loginHandler,
            createUserHandler: createUserHandler,
            getByIdHandler: getByIdHandler,
            listHandler: listHandler,
            changePasswordHandler: changePasswordHandler);

        return app.MapUserEndpoints();
    }
}
