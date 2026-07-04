# MyApi — Agent Reference

Native-AOT-ready ASP.NET Core 10 Web API. Functional architecture, discriminated-union error handling, zero-reflection.

## Build & Run

```bash
# Debug (JIT)
dotnet run

# Native AOT (linux-x64, ~18 MB self-contained)
dotnet publish -c Release -p:PublishAot=true -p:RuntimeIdentifier=linux-x64

# Run AOT binary
./bin/Release/net10.0/linux-x64/publish/MyApi

# Test (no test project yet — recommended targets below)
```

## Layers (Strict Dependency Direction, Outside → In)

```
Program.cs                                   # host entry
  └── Endpoints/Composition.cs               # wires config → delegates
        └── Endpoints/UserEndpoints.cs      # static method-group handlers (RDG-friendly)
              └── Services/UserService.cs   # domain logic, throws AppException
                    └── Repositories/UserRepository.cs  # Dapper.AOT + Sqlite
                          └── Domain/Models + Errors    # pure data
```

**Rule**: inner layers never reference outer. Never `using` `Microsoft.AspNetCore.*`, `Dapper`, or `Microsoft.Data.Sqlite` from `Domain/`, `Models/`, or `Errors/`.

## Folder Responsibilities

| Folder | Namespace | Contents |
|--------|-----------|----------|
| `Contracts/` | `MyApi.Contracts` | `LoginCmd`, `CreateUserCmd`, `AuthToken`, `JwtConfig` — DTOs only |
| `Domain/` | `MyApi.Domain` (global) | `Result<T,E>`, `ResultCollection<T,E>`, `AppException` hierarchy, `DomainErrorHandler` |
| `Errors/` | `MyApi.Errors` | `AppException` base + concrete exceptions (`NotFoundError`, `AuthError`, `ValidationError`, `SqlError`) |
| `Models/` | `MyApi.Models` | `UserDto` (and future domain entities) |
| `Security/` | `MyApi.Security` | `ArgumentPasswordHasher` — PBKDF2-HMAC-SHA256, 600k iterations, constant-time verify |
| `Repositories/` | `MyApi.Repositories` | `UserRepository` — Dapper.AOT + `Microsoft.Data.Sqlite`, static methods |
| `Services/` | `MyApi.Services` | `UserService` — orchestrating, returns `Result<,>` or throws |
| `Endpoints/` | `MyApi.Endpoints` | `UserEndpoints.cs` (handlers + MapMethod), `Composition.cs` (host wiring), `AppJsonSerializerContext.cs` |

## Adding a New Resource (e.g. Orders)

1. **Error class**: `Errors/ErrorTypes.cs` — add `DuplicateOrderError : AppException { }` if needed
2. **Model**: `Models/OrderDto.cs` — record with `[JsonSerializable]` types if Dapper-bound
3. **Repository**: `Repositories/OrderRepository.cs` — static methods, Dapper.AOT, throw `NotFoundError` etc, never `using Microsoft.AspNetCore.*`
4. **Service**: `Services/OrderService.cs` — pure orchestration
5. **Endpoints**: `Endpoints/OrderEndpoints.cs` — `MapOrderEndpoints(this WebApplication app)` extension method, **static method-group** handlers
6. **Wire in `Composition.cs`**: `OrderEndpoints.Bind(...)` then `app.MapOrderEndpoints()`
7. **Swagger excluded** by `NoWarn`; add types to `AppJsonSerializerContext`

## Result<T,E> Pattern

```csharp
public readonly record struct Result<TValue, TError> where TError : AppException
{
    public TValue? Value { get; init; }
    public TError? Error { get; init; }
    public bool IsSuccess => Error is null;
    public bool IsFailure => Error is not null;

    internal Result(TValue value) : this() { Value = value; Error = null; }
    internal Result(TError error) : this() { Value = default; Error = error; }

    public static implicit operator Result<TValue, TError>(TValue value) => new(value);
    public static implicit operator Result<TValue, TError>(TError error) => new(error);
}
```

**Usage**:
```csharp
public async Task<Result<UserDto, NotFoundError>> GetByIdAsync(int id, CancellationToken ct)
{
    var user = await _repo.FindByIdAsync(id, ct);
    return user is not null
        ? _mapper.Map(user)                  // implicit → Result<T,E>(TValue)
        : new NotFoundError($"User {id}");  // implicit → Result<T,E>(TError)
}
```

## Error Hierarchy — `AppException : Exception`

```csharp
public abstract class AppException(string message) : Exception(message);

public sealed class NotFoundError(string what)             : AppException($"{what} not found");
public sealed class AuthError(string reason = "Invalid")   : AppException($"Auth: {reason}");
public sealed class ValidationError(...)                   : AppException("Validation failed");
public sealed class SqlError(string code, string message)  : AppException(message);
```

`DomainErrorHandler` maps these to HTTP status codes via `IExceptionHandler`:
- `NotFoundError` → 404
- `AuthError` → 401
- `ValidationError` → 400 with `ValidationProblemDetails`
- `SqlError(ConstraintViolation)` → 409
- anything else → falls through to default ProblemDetails

## Password Hashing Pattern (`Security/ArgumentPasswordHasher`)

- **Algorithm**: PBKDF2-HMAC-SHA256, 600 000 iterations, 16-byte salt, 32-byte hash
- **Encoding**: `pbkdf2-sha256$600000$<base64(salt)><base64(hash)>` (PHC-style self-describing)
- **Constant-time compare** for both login (`Verify`) and registration (`AreEqual`) — compare uses deterministic zero salt to neutralise timing diff
- All branches call `CryptographicOperations.FixedTimeEquals` regardless of length mismatch

## Endpoint Pattern — AOT/RDG-Safe

```csharp
public static IApplicationBuilder MapUserEndpoints(this WebApplication app)
{
    // Pass method group directly — NEVER use lambda capture or local Func var.
    app.MapPost("/users", Create).Produces<UserDto>().WithName("CreateUser");
    app.MapGet("/users/{id:int}", GetById).Produces<UserDto>().WithName("GetUser");
    return app;
}

private static async Task<UserDto> Create(CreateUserCmd cmd, CancellationToken ct) { ... }
```

**Rules**:
- Handlers are `private static` method-group references
- No lambda capture into `MapPost` (breaks `RequestDelegateGenerator`)
- No local `Func<>` variable between handler and `MapPost` (also breaks source-gen)
- Use `[FromBody]`-bound records; JSON via source-gen `AppJsonSerializerContext`

## Dapper.AOT Setup

1. Project property: `<DapperAotInterceptorsNamespaces>MyApi.Repositories</DapperAotInterceptorsNamespaces>`
2. `[DapperAot]` on every entity that has Dapper extensions generated
3. `UserDto` is bound by `[DapperAot]` + `[DapperAot("...")]` for SQL strings
4. **Trim/AOT**: `<PublishTrimmed>true</PublishTrimmed>`, suppress `CS9270` (Dapper interceptors), `NU1903` (SQLite native P/Invoke)

## Native AOT Constraints

- **No runtime reflection** (RDG handles minimal APIs at compile-time via source-gen)
- **No `Activator.CreateInstance`**, no `Type.GetType()`
- **No `JsonSerializer` without source-gen** — always `JsonTypeInfo<T>`
- **No `await using dynamic` / IL-emit** libraries
- All types reachable from JSON, JWT, Dapper, MVC are explicitly registered in source generators

## Packages (in MyApi.csproj)

- `Microsoft.AspNetCore.OpenApi` (removed for Swaggerless mode — comments only)
- `Dapper` + `Dapper.AOT` (source-gen)
- `Microsoft.Data.Sqlite`
- `Microsoft.IdentityModel.Tokens` + `Microsoft.IdentityModel.JsonWebTokens` (use `JsonWebTokenHandler`, not `JwtSecurityTokenHandler`)
- `System.Text.Json` (source-gen via `AppJsonSerializerContext`)
- `Microsoft.AspNetCore.App` framework reference (transitive AOT support)

Configure `<RootNamespace>MyApi</RootNamespace>`, `<ImplicitUsings>enable</ImplicitUsings>`.

## Current Endpoints

| Method | Path | Body | Returns | Errors |
|--------|------|------|---------|--------|
| `POST` | `/users` | `CreateUserCmd { Name, Email, Password, ConfirmPassword }` | 200 `UserDto` | 400 mismatch, 409 email exists |
| `GET` | `/users` | — | 200 `UserDto[]` | — |
| `GET` | `/users/{id}` | — | 200 `UserDto` | 404 |
| `POST` | `/login` | `LoginCmd { Username, Password }` | 200 `AuthToken { Token }` | 401 |

## SQLite Schema

On startup, `EnsureSchema()` runs:
```sql
CREATE TABLE IF NOT EXISTS users (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    name          TEXT    NOT NULL,
    email         TEXT    NOT NULL UNIQUE,
    password_hash TEXT    NOT NULL
);
```

## Testing Strategy (When Added)

Recommended targets:
1. `ArgumentPasswordHasher` — hash/verify round-trip + constant-time
2. `UserRepository` — round-trip against in-memory SQLite (`Data Source=:memory:`)
3. `UserService` — login (good/bad), create (mismatch/exists)
4. `DomainErrorHandler` — each exception type maps to right HTTP code

Test project tooling: `xunit`, `Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio`.

## Conventions Summary

- **Strings & records** not classes for DTOs
- **`internal sealed`** for non-DI types
- **`private static`** for endpoint handlers
- **`throw`** AppException, never `new FailureResult(...)`
- **File-scoped namespaces** (clear `Domain/`, `Errors/`, etc. avoid full namespaces)
- **No DI** for domain services — pass `Func<>` delegates built by `Composition`
- **`TEXT` PRIMARY KEY** auto-generated by SQLite for IDENTITY

## Why These Choices?

| Decision | Reason |
|---------|--------|
| AOT-first | ~5 MB binary, second-fastest startup, single-file publish, no runtime patching |
| Exception-based errors | Cleaner code than `Result<T,E>` everywhere, AOT validates catch blocks at compile-time, ProblemDetails already standard |
| `Func<>` injection not DI | Testability without container, faster cold start, no reflection |
| PBKDF2 not BCrypt | Pure .NET 10, no native dependency, PHC-style format future-proofs migrations |
| Static handlers not lambdas | Source-gen → AOT trim-safe; runtime lambdas force rolling reflection fallback |

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

2. **DO NOT** use Swashbuckle/SwaggerGen — not AOT-trim-safe. Use `Microsoft.OpenApi` if OpenAPI doc needed (or skip entirely).

3. **DO NOT** add `services.AddScoped<IUserRepository, UserRepository>()` — domain is DI-free. Repositories and services are static.

4. **DO NOT** use `Type.GetType(...)`, `Activator.CreateInstance(...)`, `JsonSerializer.Serialize(anonymous object)`. All AOT-incompatible.

5. **Entry point must be `partial` or use source-gen** for `IExceptionHandler` discovery. Prefer `internal sealed class DomainErrorHandler : IExceptionHandler` with explicit registration in `Program.cs`.

## Quick File Tree

```
MyApi/
├── Contracts/Commands.cs
├── Contracts/JwtConfig.cs
├── Domain/Result.cs
├── Domain/ResultCollection.cs
├── Domain/DomainErrorHandler.cs        # IExceptionHandler
├── Domain/ErrorTypes.cs               # AppException moved here from Errors/
├── Models/UserDto.cs
├── Security/ArgumentPasswordHasher.cs
├── Repositories/UserRepository.cs     # Dapper.AOT
├── Services/UserService.cs
├── Endpoints/UserEndpoints.cs         # MapUserEndpoints + handlers
├── Endpoints/Composition.cs            # config → Bind → Map
├── Endpoints/AppJsonSerializerContext.cs
├── Program.cs                          # WebApplication.CreateBuilder, bind
├── DapperAot.cs                        # [assembly: DapperAot]
├── MyApi.csproj
└── appsettings.json
```
