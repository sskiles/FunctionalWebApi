# FunctionalWebApi — Agent Reference

Native-AOT-ready ASP.NET Core 10 Web API. Functional architecture, discriminated-union error handling, zero-reflection.

## Build & Run

```bash
# Debug (JIT)
dotnet run

# Native AOT (linux-x64, ~18 MB self-contained)
dotnet publish -c Release -p:PublishAot=true -p:RuntimeIdentifier=linux-x64

# Run AOT binary
./bin/Release/net10.0/linux-x64/publish/FunctionalWebApi

# Test (no test project yet — recommended targets below)
```

## Layers (Strict Dependency Direction, Outside → In)

```
Program.cs                                   # host entry
  └── Infrastructure/Composition.cs          # wires config → delegates
        ├── Endpoints/UserEndpoints.cs      # static method-group handlers (RDG-friendly)
        └── Services/UserService.cs         # validate/shape/apply rules
              └── Repositories/UserRepository.cs  # Dapper.AOT + Sqlite
                    └── Domain/ + Models/   # pure data / BCL exceptions
```

**Rule**: inner layers never reference outer. Never `using` `Microsoft.AspNetCore.*`, `Dapper`, or `Microsoft.Data.Sqlite` from `Domain/`, `Models/`, `Contracts/`, `Services/`, or `Repositories/`.

## Folder Responsibilities

| Folder | Namespace | Contents |
|--------|-----------|----------|
| `Contracts/` | `FunctionalWebApi.Contracts` | `LoginCmd`, `CreateUserCmd`, `AuthToken`, `ChangePasswordCmd`, `JwtConfig` — DTOs only |
| `Domain/` | `FunctionalWebApi.Domain` (global) | `Result<T,E>`, `ResultCollection<T,E>`, `DomainErrorHandler` |
| `Models/` | `FunctionalWebApi.Models` | `UserDto` (and future domain entities) |
| `Repositories/` | `FunctionalWebApi.Repositories` | `UserRepository` — Dapper.AOT + `Microsoft.Data.Sqlite`, static methods |
| `Services/` | `FunctionalWebApi.Services` | `UserService` — orchestrating, returns `Result<,>` |
| `Endpoints/` | `FunctionalWebApi.Endpoints` | `UserEndpoints.cs` (handlers + MapMethod) |
| `Infrastructure/` | `FunctionalWebApi.Infrastructure` | `Composition.cs` (host wiring), `AppJsonSerializerContext.cs`, `SchemaBootstrap.cs` |

## Adding a New Resource (e.g. Orders)

1. **Model**: `Models/OrderDto.cs` — record with `[DapperAot]` if Dapper-bound
2. **Repository**: `Repositories/OrderRepository.cs` — static methods, Dapper.AOT, return `Result<,>` with BCL exceptions, never `using Microsoft.AspNetCore.*`
3. **Service**: `Services/OrderService.cs` — pure orchestration, takes repo method-groups as parameters, validates/shapes/rules
4. **Endpoints**: `Endpoints/OrderEndpoints.cs` — `MapOrderEndpoints(this WebApplication app)` extension method, **static method-group** handlers
5. **Wire in `Composition.cs`**: build per-route delegates passing repo method-groups, then `UserEndpoints.Bind(...)` + `app.MapOrderEndpoints()`
6. Add types to `AppJsonSerializerContext`

## Result<T,E> Pattern

```csharp
public readonly record struct Result<TValue, TError>
    where TError : Exception
{
    public TValue? Value { get; }
    public TError? Error { get; }
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;

    internal Result(TValue value) { Value = value; Error = default; IsSuccess = true; }
    internal Result(TError error) { Value = default; Error = error; IsSuccess = false; }

    public static implicit operator Result<TValue, TError>(TValue value) => new(value);
    public static implicit operator Result<TValue, TError>(TError error) => new(error);

    public TResult Match<TResult>(Func<TValue, TResult> onSuccess, Func<TError, TResult> onFailure)
        => IsSuccess ? onSuccess(Value!) : onFailure(Error!);
}
```

**Usage** (repo returns `Result<UserDto, Exception>`, service returns same):
```csharp
public async Task<Result<UserDto, Exception>> GetByIdAsync(int id, CancellationToken ct)
{
    var user = await _repo.FindByIdAsync(id, ct);
    return user is not null ? user : new KeyNotFoundException($"User {id}");
}
```

## Error Handling — BCL Exception Types Only

No custom exception hierarchy. `DomainErrorHandler : IExceptionHandler` maps:

| Exception | HTTP Status |
|-----------|-------------|
| `UnauthorizedAccessException` | 401 |
| `System.Collections.Generic.KeyNotFoundException` | 404 |
| `ArgumentException` | 400 |
| anything else | 500 (default ProblemDetails) |

Endpoints throw the carried `Exception` on `Result.IsFailure`; middleware translates.

## Password Handling — Temporary Plaintext

**Current state (wiring pass)**: passwords stored verbatim in `PasswordHash` column. No hashing, no verification, no constant-time compare.

**Next pass** (when you say the word): `UserService` owns `HashPassword(char[])` + `VerifyPassword(char[], string)` (PBKDF2-HMAC-SHA256, 600k iterations, PHC-style string); `UserRepository` receives already-hashed input and persists it.

## Endpoint Pattern — AOT/RDG-Safe

```csharp
public static IApplicationBuilder MapUserEndpoints(this WebApplication app)
{
    // Pass method group directly — NEVER lambda or local Func.
    app.MapPost("/users", Create).Produces<UserDto>().WithName("CreateUser");
    app.MapGet("/users/{id:int}", GetById).Produces<UserDto>().WithName("GetUser");
    return app;
}

private static async Task<IResult> Create(CreateUserCmd cmd)
    => Unwrap(await CreateUserHandler(cmd));  // injected delegate
```

**Rules**:
- Handlers are `private static` method-group references
- No lambda capture into `MapPost` (breaks `RequestDelegateGenerator`)
- No local `Func<>` variable between handler and `MapPost` (also breaks source-gen)
- `[FromBody]`-bound records; JSON via source-gen `AppJsonSerializerContext`

## Dapper.AOT Setup

1. Project property: `<InterceptorsNamespaces>Dapper.AOT</InterceptorsNamespaces>`
2. `[DapperAot]` on every entity that has Dapper extensions generated (`UserDto`)
3. **Trim/AOT**: `<PublishTrimmed>true</PublishTrimmed>`, `<PublishAot>true</PublishAot>`, `<SelfContained>true</SelfContained>`, suppress `CS9270` (Dapper interceptors), `NU1903` (SQLite native advisory)

## Native AOT Constraints

- **No runtime reflection** (RDG handles minimal APIs at compile-time via source-gen)
- **No `Activator.CreateInstance`**, no `Type.GetType()`
- **No `JsonSerializer` without source-gen** — always `JsonTypeInfo<T>`
- **No `await using dynamic` / IL-emit** libraries
- All types reachable from JSON, JWT, Dapper, MVC are explicitly registered in source generators

## Packages (in FunctionalWebApi.csproj)

- `Dapper` + `Dapper.AOT` (source-gen)
- `Microsoft.Data.Sqlite`
- `System.IdentityModel.Tokens.Jwt` (use `JsonWebTokenHandler`, not `JwtSecurityTokenHandler`)
- `System.Text.Json` (source-gen via `AppJsonSerializerContext`)
- `Microsoft.AspNetCore.App` framework reference (transitive AOT support)

Configure `<RootNamespace>FunctionalWebApi</RootNamespace>`, `<ImplicitUsings>enable</ImplicitUsings>`.

## Current Endpoints

| Method | Path | Body | Returns | Errors |
|--------|------|------|---------|--------|
| `POST` | `/users` | `CreateUserCmd { Name, Email, Password, ConfirmPassword }` | 200 `UserDto` | 400 mismatch/empty, 409 email exists |
| `GET` | `/users` | — | 200 `UserDto[]` | — |
| `GET` | `/users/{id}` | — | 200 `UserDto` | 404 |
| `POST` | `/login` | `LoginCmd { Username, Password }` | 200 `AuthToken { Token }` | 401 |
| `PUT` | `/users/{id}/password` | `ChangePasswordCmd { CurrentPassword, NewPassword, ConfirmNewPassword }` | 200 `UserDto` | 400/401/404 |

## SQLite Schema

On startup, `SchemaBootstrap.EnsureCreatedAsync` runs:
```sql
CREATE TABLE IF NOT EXISTS Users (
    Id           INTEGER PRIMARY KEY AUTOINCREMENT,
    Name         TEXT    NOT NULL,
    Email        TEXT    NOT NULL UNIQUE,
    PasswordHash TEXT    NOT NULL DEFAULT ''
);
```

## Testing Strategy (When Added)

Recommended targets:
1. `UserRepository` — round-trip against in-memory SQLite (`Data Source=:memory:`)
2. `UserService` — login (good/bad), create (mismatch/empty), change-password (wrong current/mismatch)
3. `DomainErrorHandler` — each exception type maps to right HTTP code

Test project tooling: `xunit`, `Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio`.

## Conventions Summary

- **Strings & records** not classes for DTOs
- **`internal sealed`** for non-DI types
- **`private static`** for endpoint handlers
- **`throw`** carried exception on failure, never `new FailureResult(...)`
- **File-scoped namespaces**
- **No DI** for domain services — pass `Func<>` delegates built by `Composition`
- **`TEXT` PRIMARY KEY** auto-generated by SQLite for IDENTITY

## Why These Choices?

| Decision | Reason |
|---------|--------|
| AOT-first | ~18 MB binary, fast startup, single-file publish, no runtime patching |
| BCL exceptions over custom hierarchy | Cleaner code, AOT validates catch blocks at compile-time, ProblemDetails standard |
| `Func<>` injection not DI | Testability without container, faster cold start, no reflection |
| PBKDF2 (future) | Pure .NET 10, no native dependency, PHC-style format future-proofs migrations |
| Static handlers not lambdas | Source-gen → AOT trim-safe; runtime lambdas force reflection fallback |

## Gotchas to Avoid

1. **DO NOT** write endpoint as:
   ```csharp
   var handler = async (cmd) => await ...;   // BAD: breaks RDG
   app.MapPost("/x", handler);               // BAD
   ```
   Always:
   ```csharp
   app.MapPost("/x", MyHandler);             // GOOD: static method group
   private static async Task<...> MyHandler(...) { ... }
   ```

2. **DO NOT** use Swashbuckle/SwaggerGen — not AOT-trim-safe.

3. **DO NOT** add `services.AddScoped<IUserRepository, UserRepository>()` — domain is DI-free. Repositories and services are static.

4. **DO NOT** use `Type.GetType(...)`, `Activator.CreateInstance(...)`, `JsonSerializer.Serialize(anonymous object)`. All AOT-incompatible.

5. **Entry point must be `partial` or use source-gen** for `IExceptionHandler` discovery. Prefer `internal sealed class DomainErrorHandler : IExceptionHandler` with explicit registration in `Program.cs`.

## Quick File Tree

```
FunctionalWebApi/
├── Contracts/{Commands.cs, JwtConfig.cs}
├── Domain/{Result.cs, ResultCollection.cs, DomainErrorHandler.cs}
├── Endpoints/UserEndpoints.cs
├── Infrastructure/{AppJsonSerializerContext.cs, Composition.cs, SchemaBootstrap.cs}
├── Models/UserDto.cs
├── Repositories/UserRepository.cs     # Dapper.AOT
├── Services/UserService.cs
├── Program.cs                          # 34 lines: builder + DI + UseExceptionHandler + RegisterAllEndpoints + RunAsync
├── FunctionalWebApi.csproj
└── appsettings.json
```