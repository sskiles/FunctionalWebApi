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
/// Composition root: the single source of truth for all wiring.
///
/// Responsibilities:
/// <list type="bullet">
///   <item>Read configuration</item>
///   <item>Run schema bootstrap</item>
///   <item>Build infrastructure delegates (<see cref="NewConnection"/>, <see cref="Jwt"/>)</item>
///   <item>Build repository-shaped collaborators (<see cref="Authenticate"/>, <see cref="CreateInRepository"/>, etc.)</item>
///   <item>Build endpoint pipeline delegates (<see cref="LoginHandler"/>, <see cref="CreateUserHandler"/>, etc.)</item>
///   <item>Trigger downstream static constructors by returning <c>app.MapUserEndpoints()</c></item>
/// </list>
///
/// All downstream layers (<see cref="Repositories.UserRepository"/>, <see cref="Services.UserService"/>,
/// <see cref="Endpoints.UserEndpoints"/>) pull their readonly collaborators from this class via their
/// own static constructors. No <c>Bind()</c> methods, no parameter injection at the service layer.
/// </summary>
public static class Composition
{
    // ===== Infrastructure (set by RegisterAllEndpoints after config) =====
    /// <summary>Closed <see cref="SqliteConnection"/> factory — opened lazily by Dapper.</summary>
    public static Func<IDbConnection> NewConnection = null!;

    /// <summary>JWT signing configuration from <c>appsettings.json</c>.</summary>
    public static JwtConfig Jwt = null!;

    // ===== Repository-shaped collaborators (curried with connection factory) =====
    /// <summary>Authenticate by email + password chars.</summary>
    public static Func<string, char[], Task<Result<UserDto, Exception>>> Authenticate = null!;

    /// <summary>Create a new user from validated fields.</summary>
    public static Func<string, string, string, Task<Result<UserDto, Exception>>> CreateInRepository = null!;

    /// <summary>Get user by id.</summary>
    public static Func<int, Task<Result<UserDto, Exception>>> GetByIdInRepository = null!;

    /// <summary>List all users.</summary>
    public static Func<Task<ResultCollection<UserDto, Exception>>> ListInRepository = null!;

    /// <summary>Update password for a user.</summary>
    public static Func<int, string, Task<Result<int, Exception>>> UpdatePasswordInRepository = null!;

    // ===== Endpoint pipeline delegates (curried with services + JWT) =====
    /// <summary>POST /login pipeline.</summary>
    public static Func<LoginCmd, Task<Result<AuthToken, Exception>>> LoginHandler = null!;

    /// <summary>POST /users pipeline.</summary>
    public static Func<CreateUserCmd, Task<Result<UserDto, Exception>>> CreateUserHandler = null!;

    /// <summary>GET /users/{id} pipeline.</summary>
    public static Func<int, Task<Result<UserDto, Exception>>> GetByIdHandler = null!;

    /// <summary>GET /users pipeline.</summary>
    public static Func<Task<ResultCollection<UserDto, Exception>>> ListHandler = null!;

    /// <summary>PUT /users/{id}/password pipeline.</summary>
    public static Func<(int Id, ChangePasswordCmd Cmd), Task<Result<UserDto, Exception>>> ChangePasswordHandler = null!;

    /// <summary>
    /// Reads configuration, runs schema bootstrap, builds all delegates,
    /// and returns the application with user endpoints mapped.
    /// </summary>
    public static async Task<IApplicationBuilder> RegisterAllEndpoints(
        this WebApplication app, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Sqlite")!;

        // Schema bootstrap (sync for immediate failure visibility)
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

        // ---- Infrastructure ----
        NewConnection = () => new SqliteConnection(connectionString);
        Jwt = jwt;

        // ---- Repository-shaped collaborators (connection factory already applied) ----
        Authenticate = (email, chars) => UserRepository.TryAuthenticateAsync(email, chars);
        CreateInRepository = (name, email, password) => UserRepository.CreateAsync(name, email, password);
        GetByIdInRepository = id => UserRepository.GetByIdAsync(id);
        ListInRepository = () => UserRepository.ListAsync();
        UpdatePasswordInRepository = (userId, newPassword) => UserRepository.UpdatePasswordAsync(userId, newPassword);

        // ---- Endpoint pipeline delegates (services + JWT already applied) ----
        LoginHandler = async cmd => await UserService.LoginAsync(cmd);
        CreateUserHandler = async cmd => await UserService.CreateUserAsync(cmd);
        GetByIdHandler = async id => await UserRepository.GetByIdAsync(id);
        ListHandler = async () => await UserRepository.ListAsync();
        ChangePasswordHandler = async t => await UserService.ChangePasswordAsync(t.Id, t.Cmd);

        // Trigger UserEndpoints static ctor which pulls these into readonly fields
        return app.MapUserEndpoints();
    }
}