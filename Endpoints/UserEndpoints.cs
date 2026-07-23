namespace FunctionalWebApi.Endpoints;

using FunctionalWebApi.Contracts;
using FunctionalWebApi.Domain;
using FunctionalWebApi.Infrastructure;
using FunctionalWebApi.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

/// <summary>
/// HTTP endpoints for user accounts: registration, authentication, lookup,
/// and password change. Each route is declared against a single resource
/// (users + login), keeping the wiring for one resource type in one file.
///
/// Handlers convert <see cref="Result{TValue, TError}"/> into HTTP responses:
/// on success the value is wrapped in <see cref="Results.Ok(object?)"/>; on
/// failure the carried <see cref="Exception"/> is thrown so the global
/// <see cref="FunctionalWebApi.Domain.DomainErrorHandler"/> can translate it
/// to the appropriate HTTP status (401/404/400 etc.).
///
/// Handler bodies are deliberately thin: each one invokes a single
/// per-operation pipeline delegate pulled from <see cref="Composition"/> via
/// the static constructor, then unwraps the result.
/// </summary>
public static class UserEndpoints
{
    // Pipeline delegates pulled from Composition at static init.
    public static readonly Func<LoginCmd, Task<Result<AuthToken, Exception>>> LoginHandler;
    public static readonly Func<CreateUserCmd, Task<Result<UserDto, Exception>>> CreateUserHandler;
    public static readonly Func<int, Task<Result<UserDto, Exception>>> GetByIdHandler;
    public static readonly Func<Task<ResultCollection<UserDto, Exception>>> ListHandler;
    public static readonly Func<(int Id, ChangePasswordCmd Cmd), Task<Result<UserDto, Exception>>> ChangePasswordHandler;

    static UserEndpoints()
    {
        LoginHandler = Composition.LoginHandler;
        CreateUserHandler = Composition.CreateUserHandler;
        GetByIdHandler = Composition.GetByIdHandler;
        ListHandler = Composition.ListHandler;
        ChangePasswordHandler = Composition.ChangePasswordHandler;
    }

    /// <summary>
    /// Maps the user endpoints onto the supplied <see cref="WebApplication"/>.
    /// Method-group references are passed to <c>MapPost</c>/<c>MapGet</c> so
    /// the minimal-API request delegate generator can statically see each
    /// handler symbol; lambdas at this layer would force the runtime factory
    /// to fall back to reflection-based dispatch under native AOT.
    /// </summary>
    public static IApplicationBuilder MapUserEndpoints(this WebApplication app)
    {
        _ = app.MapPost("/login", Login)
           .Produces<AuthToken>()
           .Produces(StatusCodes.Status401Unauthorized)
           .WithName("Login");

        _ = app.MapPost("/users", Create)
           .Produces<UserDto>()
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

    // --- handler bodies ---------------------------------------------------
    // Each handler is one invocation of an injected delegate, then Unwrap.

    private static async Task<IResult> Login(LoginCmd cmd)
        => Unwrap(await LoginHandler(cmd));

    private static async Task<IResult> Create(CreateUserCmd cmd)
        => Unwrap(await CreateUserHandler(cmd));

    private static async Task<IResult> GetById(int id)
        => Unwrap(await GetByIdHandler(id));

    private static async Task<IResult> List()
        => Unwrap(await ListHandler());

    private static async Task<IResult> ChangePassword(int id, ChangePasswordCmd cmd)
        => Unwrap(await ChangePasswordHandler((id, cmd)));

    // --- Unwrap helpers ---------------------------------------------------
    // On failure the carried Exception is thrown so DomainErrorHandler can
    // map it to the right HTTP status (UnauthorizedAccessException → 401,
    // KeyNotFoundException → 404, ArgumentException → 400, etc.).

    private static IResult Unwrap<TValue, TError>(Result<TValue, TError> result)
        where TError : Exception
    {
        if (result.IsFailure)
        {
            throw result.Error!;
        }
        return Results.Ok(result.Value);
    }

    private static IResult Unwrap<TValue, TError>(ResultCollection<TValue, TError> result)
        where TError : Exception
    {
        if (result.IsFailure)
        {
            throw result.Error!;
        }
        return Results.Ok(result.Items);
    }
}