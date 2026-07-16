namespace FunctionalWebApi.Infrastructure;

using System.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using FunctionalWebApi.Contracts;
using FunctionalWebApi.Domain;
using FunctionalWebApi.Endpoints;
using FunctionalWebApi.Models;
using FunctionalWebApi.Repositories;
using FunctionalWebApi.Services;
using Microsoft.Data.Sqlite;

/// <summary>
/// Composition root: reads configuration, runs the schema bootstrap, and
/// wires the per-route pipeline delegates into <see cref="UserEndpoints"/>.
///
/// The composition here is the only place in the system that knows how
/// the layers connect. References flow:
/// <list type="bullet">
///   <item>transport endpoints (HTTP) — <see cref="Endpoints.UserEndpoints"/></item>
///   <item>use case / validation layer — <see cref="Services.UserService"/></item>
///   <item>persistence layer — <see cref="Repositories.UserRepository"/></item>
/// </list>
/// Endpoints that need validation/shaping (e.g. registration, login,
/// password change) pass through the service; pure lookups (read by id,
/// list) call the repository directly because the service would add no
/// value for those operations.
///
/// A single <c>newConnection</c> delegate is constructed once and used to
/// curry the repository method-groups into business-shaped delegates. The
/// driver type stays invisible to the service layer — it only sees
/// functions taking domain inputs and returning domain results.
///
/// Lives in <see cref="FunctionalWebApi.Infrastructure"/> alongside the
/// other framework/wiring artefacts so the feature folders under
/// <c>Endpoints/</c>, <c>Services/</c>, and <c>Repositories/</c> stay free
/// of system-wiring concerns.
/// </summary>
public static class Composition
{
    /// <summary>
    /// Reads configuration, runs the schema bootstrap, builds the
    /// per-route pipeline delegates, binds them into
    /// <see cref="UserEndpoints"/>, and returns the application builder
    /// with the user routes registered.
    /// </summary>
    public static async Task<IApplicationBuilder> RegisterAllEndpoints(
        this WebApplication app, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Sqlite")!;

        // Make sure the underlying SQLite schema exists before any route
        // can serve a request. Idempotent via `IF NOT EXISTS`.
        var schemaResult = await SchemaBootstrap.EnsureCreatedAsync(connectionString);
        if (schemaResult.IsFailure)
        {
            throw schemaResult.Error!;
        }

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
        // The connection factory is applied once here, producing business-
        // shaped delegates that the service layer consumes without any
        // infrastructure knowledge.

        Func<string, char[], Task<Result<UserDto, Exception>>> authenticate =
            (email, chars) => UserRepository.TryAuthenticateAsync(newConnection, email, chars);

        Func<LoginCmd, Task<Result<AuthToken, Exception>>> loginHandler =
            async cmd => await UserService.LoginAsync(authenticate, jwt, cmd);

        Func<string, string, string, Task<Result<UserDto, Exception>>> createUserInRepository =
            (name, email, password) => UserRepository.CreateAsync(newConnection, name, email, password);

        Func<CreateUserCmd, Task<Result<UserDto, Exception>>> createUserHandler =
            async cmd => await UserService.CreateUserAsync(createUserInRepository, cmd);

        Func<int, Task<Result<UserDto, Exception>>> getByIdHandler =
            async id => await UserRepository.GetByIdAsync(newConnection, id);

        Func<Task<ResultCollection<UserDto, Exception>>> listHandler =
            async () => await UserRepository.ListAsync(newConnection);

        Func<int, Task<Result<UserDto, Exception>>> getByIdInRepository =
            id => UserRepository.GetByIdAsync(newConnection, id);

        Func<int, string, Task<Result<int, Exception>>> updatePasswordInRepository =
            (userId, newPassword) => UserRepository.UpdatePasswordAsync(newConnection, userId, newPassword);

        Func<(int Id, ChangePasswordCmd Cmd), Task<Result<UserDto, Exception>>> changePasswordHandler =
            async t => await UserService.ChangePasswordAsync(
                getByIdInRepository,
                updatePasswordInRepository,
                t.Id,
                t.Cmd);

        UserEndpoints.Bind(
            loginHandler: loginHandler,
            createUserHandler: createUserHandler,
            getByIdHandler: getByIdHandler,
            listHandler: listHandler,
            changePasswordHandler: changePasswordHandler);

        return app.MapUserEndpoints();
    }
}