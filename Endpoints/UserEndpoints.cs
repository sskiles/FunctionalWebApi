using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using MyApi.Contracts;
using MyApi.Models;
using MyApi.Services;
using MyApi.Repositories;

namespace MyApi.Endpoints;

/// <summary>
/// HTTP endpoints for user accounts: registration, authentication, and lookup.
/// Each route is declared against a single resource (users + login), keeping
/// the wiring for one resource type in one file.
/// </summary>
public static class UserEndpoints
{
    // Cached delegates — Composition calls <see cref="Bind"/> once at startup
    // so request handlers don't have to re‑build connection strings on every call.
    private static Func<string, char[], Task<UserDto?>> Authenticate = null!;
    private static string ConnectionString = "";
    private static JwtConfig Jwt               = null!;

    /// <summary>
    /// Called by <see cref="Composition.RegisterAllEndpoints"/> at startup.
    /// Stores the connection string and the JWT config so each request handler
    /// can issue calls into the data and service layers without re‑reading
    /// configuration.
    /// </summary>
    public static void Bind(
        Func<string, char[], Task<UserDto?>> authenticate,
        string connectionString,
        JwtConfig jwt)
    {
        Authenticate    = authenticate;
        ConnectionString = connectionString;
        Jwt             = jwt;
    }

    /// <summary>
    /// Maps the user endpoints onto the supplied <see cref="WebApplication"/>.
    /// </summary>
    public static IApplicationBuilder MapUserEndpoints(this WebApplication app)
    {
        // Method-group references are passed to MapPost/MapGet so the minimal-API
        // request delegate generator (RDG) can statically see the handler symbol.
        // Routing a lambda declared inline, or assigning the function to an
        // intermediate local, would force the runtime RequestDelegateFactory to
        // fall back to reflection-based handling under native AOT.

        app.MapPost("/login",        Login)
           .Produces<AuthToken>()
           .Produces(StatusCodes.Status401Unauthorized)
           .WithName("Login");

        app.MapPost("/users",         Create)
           .Produces<UserDto>()
           .Produces(StatusCodes.Status409Conflict)
           .Produces(StatusCodes.Status400BadRequest)
           .WithName("CreateUser");

        app.MapGet("/users/{id:int}", GetById)
           .Produces<UserDto>()
           .Produces(StatusCodes.Status404NotFound)
           .WithName("GetUser");

        app.MapGet("/users",          List)
           .Produces<IReadOnlyList<UserDto>>()
           .WithName("GetUsers");

        return app;
    }

    // --- handler bodies ---------------------------------------------------
    // Each handler is plain code that calls the domain services / repository.
    // Failures are propagated as exceptions and translated by
    // <see cref="MyApi.Domain.DomainErrorHandler"/>.

    private static async Task<IResult> Login(LoginCmd cmd)
    {
        var token = await UserService.LoginAsync(Authenticate, Jwt, cmd);
        return Results.Ok(token);
    }

    private static async Task<IResult> Create(CreateUserCmd cmd)
    {
        var user = await UserService.CreateUserAsync(cmd);
        return Results.Ok(user);
    }

    private static async Task<IResult> GetById(int id)
    {
        var user = await UserRepository.GetByIdAsync(ConnectionString, id);
        return Results.Ok(user);
    }

    private static async Task<IResult> List()
    {
        var users = await UserRepository.ListAsync(ConnectionString);
        return Results.Ok(users);
    }
}
