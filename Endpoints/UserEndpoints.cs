namespace FunctionalWebApi.Endpoints;

using System.Data;
using FunctionalWebApi.Contracts;
using FunctionalWebApi.Domain;
using FunctionalWebApi.Errors;
using FunctionalWebApi.Models;
using FunctionalWebApi.Repositories;
using FunctionalWebApi.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

/// <summary>
/// HTTP endpoints for user accounts: registration, authentication, lookup,
/// and password change. Each route is declared against a single resource
/// (users + login), keeping the wiring for one resource type in one file.
///
/// Handlers convert <see cref="Result{TValue, TError}"/> into HTTP responses:
/// on success the value is wrapped in <see cref="Results.Ok(object?)"/>; on
/// failure the carried <see cref="AppException"/> is thrown so the global
/// <see cref="FunctionalWebApi.Domain.DomainErrorHandler"/> can translate it.
///
/// Each handler opens an <see cref="IDbConnection"/> for the request via
/// <see cref="OpenConnection"/> (configured at startup by
/// <see cref="Composition.RegisterAllEndpoints"/>) and disposes it before
/// returning. <see cref="ArgumentNullException.ThrowIfNull"/> style guards
/// ensure a misconfigured binding surfaces immediately.
/// </summary>
public static class UserEndpoints
{
    // Cached delegates — Composition calls <see cref="Bind"/> once at startup
    // so request handlers don't have to re‑build connection strings on every call.
    private static Func<Task<IDbConnection>> OpenConnection = null!;
    private static Func<string, char[], Task<Result<UserDto, AuthError>>> Authenticate = null!;
    private static JwtConfig Jwt = null!;

    /// <summary>
    /// Called by <see cref="Composition.RegisterAllEndpoints"/> at startup.
    /// Stores the connection opener, the login delegate, and the JWT config
    /// so each request handler can issue calls into the data and service
    /// layers without re‑reading configuration.
    /// </summary>
    public static void Bind(
        Func<Task<IDbConnection>> openConnection,
        Func<string, char[], Task<Result<UserDto, AuthError>>> authenticate,
        JwtConfig jwt)
    {
        OpenConnection = openConnection;
        Authenticate = authenticate;
        Jwt = jwt;
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
        _ = app.MapPost("/login", Login)
           .Produces<AuthToken>()
           .Produces(StatusCodes.Status401Unauthorized)
           .WithName("Login");

        _ = app.MapPost("/users", Create)
           .Produces<UserDto>()
           .Produces(StatusCodes.Status409Conflict)
           .Produces(StatusCodes.Status400BadRequest)
           .WithName("CreateUser");

        _ = app.MapPut("/users/{id:int}/password", ChangePassword)
           .Produces<UserDto>()
           .Produces(StatusCodes.Status400BadRequest)
           .Produces(StatusCodes.Status401Unauthorized)
           .Produces(StatusCodes.Status404NotFound)
           .WithName("ChangePassword");

        _ = app.MapGet("/users/{id:int}", GetById)
           .Produces<UserDto>()
           .Produces(StatusCodes.Status404NotFound)
           .WithName("GetUser");

        _ = app.MapGet("/users", List)
           .Produces<IReadOnlyList<UserDto>>()
           .WithName("GetUsers");

        return app;
    }

    // --- handlers ---------------------------------------------------------
    // Each handler opens a connection for the lifetime of the request,
    // disposes it on exit, and unwraps the Result. On failure the inner
    // AppException is thrown so DomainErrorHandler can map it to a status.

    private static async Task<IResult> Login(LoginCmd cmd)
    {
        var result = await UserService.LoginAsync(Authenticate, Jwt, cmd);
        return Unwrap(result);
    }

    private static async Task<IResult> Create(CreateUserCmd cmd)
    {
        using var conn = await OpenConnection();
        var result = await UserService.CreateUserAsync(conn, cmd);
        return Unwrap(result);
    }

    private static async Task<IResult> GetById(int id)
    {
        using var conn = await OpenConnection();
        var result = await UserRepository.GetByIdAsync(conn, id);
        return Unwrap(result);
    }

    private static async Task<IResult> List()
    {
        using var conn = await OpenConnection();
        var result = await UserRepository.ListAsync(conn);
        if (result.IsFailure)
        {
            throw result.Error!;
        }
        return Results.Ok(result.Items);
    }

    private static async Task<IResult> ChangePassword(int id, ChangePasswordCmd cmd)
    {
        using var conn = await OpenConnection();
        var result = await UserService.ChangePasswordAsync(conn, id, cmd);
        return Unwrap(result);
    }

    // --- Unwrap helpers ---------------------------------------------------
    private static IResult Unwrap<TValue, TError>(Result<TValue, TError> result)
        where TError : AppException
    {
        if (result.IsFailure)
        {
            throw result.Error!;
        }
        return Results.Ok(result.Value);
    }
}
