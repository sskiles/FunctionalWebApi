# FunctionalWebApi

> A reference implementation for building ASP.NET Core Web APIs with functional programming principles, targeting Native AOT from day one.

## Philosophy

This project demonstrates that **functional programming and web APIs are not contradictory** — they are complementary. By rejecting mutable state, side-effect-laden dependencies, and framework-magic DI containers, we achieve:

- **Predictability**: The same input always produces the same output
- **Testability**: Pure functions with explicit dependencies require no mocking frameworks
- **Composability**: Small, focused functions compose into larger workflows
- **AOT Safety**: No reflection, no runtime code generation, no hidden costs

## What is Dependency Rejection?

Most ASP.NET Core applications use **Dependency Injection (DI)** as the primary mechanism for composing services. While DI improves testability over hard-coded dependencies, it introduces its own problems:

- **Hidden dependencies**: You cannot tell what a service needs by looking at its constructor alone — you must understand the container's configuration
- **Framework coupling**: Business logic becomes entangled with `IServiceProvider`, lifetime management, and scoped service resolution
- **Opaque transitive dependencies**: Service A → B → C means A implicitly depends on everything C needs
- **AOT/Trimming hostile**: DI containers rely on runtime reflection (`Activator.CreateInstance`, `Type.GetType`)

**Dependency Rejection** flips this. Instead of injecting opaque interfaces, we pass explicit functions and values at composition time:

```csharp
// BEFORE: DI container — who knows what UserService actually needs?
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IJwtService, JwtService>();
builder.Services.AddScoped<UserService>();

// AFTER: Explicit dependencies — every need is visible in the signature
var login = UserService.LoginAsync(
    findByEmail: UserRepository.FindByEmailAsync,
    verifyPassword: ArgumentPasswordHasher.Verify,
    createToken: AuthService.CreateToken,
    connectionString: configuration.GetConnectionString("Sqlite")!);
```

The `LoginAsync` method is pure: it receives exactly what it needs, no more, no less. The function signature is the contract. There is no hidden state, no surprise dependencies, and no framework magic required to understand the code.

### Why No DI Container?

| DI Container | Dependency Rejection |
|-------------|----------------------|
| `Func<IServiceProvider, T>` | `Func<TIn, TOut>` |
| Runtime reflection | Compile-time verification |
| Hidden graph of dependencies | Flat, explicit parameter list |
| Scoped lifetimes managed by framework | Lifetimes are just `using` blocks |
| Hard to test without `TestServer` or mocks | Plain C# functions, testable with `new` |
| Reflection breaks AOT trimming | Zero runtime reflection |

## The Functional Stack

### Result&lt;T, E&gt; — Explicit Error Handling

Exceptions for control flow are invisible in the type system. `Result<T, E>` makes success and failure first-class:

```csharp
public readonly record struct Result<TValue, TError>
{
    public TValue? Value { get; }
    public TError? Error { get; }
    public bool IsSuccess { get; }

    public static implicit operator Result<TValue, TError>(TValue value) => new(value);
    public static implicit operator Result<TValue, TError>(TError error) => new(error);
}
```

**Usage**: no ceremony, just return the value or error:

```csharp
public async Task<Result<UserDto, NotFoundError>> GetByIdAsync(int id)
{
    var user = await _findById(id);
    return user is not null ? user : new NotFoundError($"User {id}");
}
```

The caller *must* handle both branches. The compiler enforces this.

### AppException Hierarchy — When You Need to Throw

Not all errors are business-logic failures. Some are truly exceptional (database disconnected, disk full, network partition). For these, we use a typed exception hierarchy:

```csharp
public abstract class AppException(string message) : Exception(message);
public sealed class NotFoundError(string what) : AppException($"{what} not found");
public sealed class AuthError(string reason = "Invalid") : AppException($"Auth: {reason}");
```

These propagate to a single `IExceptionHandler` which maps them to HTTP status codes. The benefit: **one place** to change how all auth failures, not-found errors, and validation failures are represented.

### Static, Stateless Functions

Every service, repository, and handler is a `static` method. There is no `class` state, no `this`, no fields to mutate:

```csharp
internal static class UserService
{
    public static async Task<Result<AuthToken, AuthError>> LoginAsync(
        Func<string, CancellationToken, Task<UserDto?>> findByEmail,
        Func<string, string, bool> verifyPassword,
        Func<string, string> createToken,
        string connectionString,
        LoginCmd cmd,
        CancellationToken ct)
    {
        // Pure logic: no hidden state, no service locator calls
    }
}
```

**Why static?**
- No mutable state to reason about
- Thread-safe by default
- No allocation overhead from instantiating service classes
- Method-group references are AOT-friendly (no lambda closures)
- Easier to reason about: `Input → Output`, period

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                        HTTP Transport                           │
│  Endpoints/UserEndpoints.cs  │  Endpoints/Composition.cs        │
│  MapPost/MapGet             │  Wires config → pure functions    │
└─────────────────────────────────────────────────────────────────┘
                              │
├─────────────────────────────────────────────────────────────────┤
│                        Business Logic                           │
│  Services/UserService.cs    │  Pure functions, throws AppException│
└─────────────────────────────────────────────────────────────────┘
                              │
├─────────────────────────────────────────────────────────────────┤
│                        Data Access                              │
│  Repositories/UserRepository.cs  │  Dapper.AOT, static methods │
└─────────────────────────────────────────────────────────────────┘
                              │
├─────────────────────────────────────────────────────────────────┤
│                        Pure Domain                              │
│  Domain/Result.cs           │  Algebraic data types           │
│  Errors/ErrorTypes.cs       │  Exception hierarchy            │
│  Models/UserDto.cs          │  Immutable records              │
└─────────────────────────────────────────────────────────────────┘
```

## Why These Decisions?

| Decision | Alternative | Why We Chose This |
|----------|-------------|-------------------|
 | `static` methods | Instance classes with DI | No mutable state, no reflection, AOT-safe method groups |
| `Result<T,E>` | Exceptions everywhere | Explicit error branches, compiler-enforced handling |
| Typed `AppException` | Generic `Exception` | Single `IExceptionHandler` maps to HTTP, typed catch blocks |
| Dapper.AOT | EF Core | No runtime model building, source-generated SQL, trim-safe |
| `Func<>` injection | `IServiceProvider` | Every dependency visible in the signature, testable with plain `new` |
| Native AOT | JIT runtime | ~18 MB self-contained binary, 50ms cold start, no runtime patching |
| PBKDF2 (600k iterations) | BCrypt/Argon2 | Pure .NET, no native dependencies, PHC format future-proofs migrations |
| Records | Classes | Immutable by default, value equality, concise syntax |
| File-scoped namespaces | Block-scoped | Less nesting, clearer intent |

## Security

### Password Hashing

- **Algorithm**: PBKDF2-HMAC-SHA256, 600,000 iterations (OWASP 2023)
- **Salt**: 16 bytes cryptographically random
- **Hash**: 32 bytes
- **Format**: `pbkdf2-sha256$600000$<base64(salt)><base64(hash)>` (PHC-style)
- **Constant-time verification**: `CryptographicOperations.FixedTimeEquals` prevents timing attacks on both login and registration paths

### JWT Tokens

- Uses `JsonWebTokenHandler` (not `JwtSecurityTokenHandler`) — AOT-compatible, no reflection
- Short expiry (60 minutes)
- Claims include `sub` (user ID), `jti` (token ID for revocation future-proofing)

## Native AOT

```bash
dotnet publish -c Release -p:PublishAot=true -p:RuntimeIdentifier=linux-x64
```

- **Binary size**: ~18 MB (self-contained, no runtime required)
- **Cold start**: ~50ms
- **Trimming**: `<PublishTrimmed>true</PublishTrimmed>` — unused code elided
- **Reflection-free**: All JSON, JWT, and Dapper types registered in source generators

### AOT Constraints We Embrace

- No `Activator.CreateInstance`
- No `Type.GetType`
- No `JsonSerializer.Serialize(object)` — always use `JsonTypeInfo<T>`
- No runtime lambdas in `MapPost`/`MapGet` — pass method groups directly
- All DTOs registered in `AppJsonSerializerContext`

## Testing Strategy

Because dependencies are explicit `Func<>` parameters, testing requires **no mocking framework**:

```csharp
[Test]
public async Task LoginAsync_WithValidCredentials_ReturnsToken()
{
    // Arrange: pure functions, just pass what you need
    var result = await UserService.LoginAsync(
        findByEmail: (email, ct) => Task.FromResult(new UserDto(1, "Alice", email, "hash")),
        verifyPassword: (p, h) => true,
        createToken: id => "mock-token",
        connectionString: "::memory:",
        cmd: new LoginCmd("alice@example.com", "password"),
        CancellationToken.None);

    // Assert
    result.IsSuccess.Should().BeTrue();
    result.Value!.Token.Should().Be("mock-token");
}
```

No `Moq`, no `TestServer`, no `WebApplicationFactory`. Just C#.

## Endpoints

| Method | Path | Returns | Errors |
|--------|------|---------|--------|
| POST | `/users` | `UserDto` | 400 (validation), 409 (email exists) |
| GET | `/users` | `UserDto[]` | — |
| GET | `/users/{id}` | `UserDto` | 404 |
| POST | `/login` | `AuthToken` | 401 |

## Running

```bash
# Debug
dotnet run

# Native AOT
dotnet publish -c Release -p:PublishAot=true -p:RuntimeIdentifier=linux-x64
./bin/Release/net10.0/linux-x64/publish/FunctionalWebApi
```

## Extending

**Add a new resource (e.g., Orders):**

1. **Error**: `Errors/ErrorTypes.cs` — `DuplicateOrderError : AppException`
2. **Model**: `Models/OrderDto.cs` — `record` with `[JsonSerializable]`
3. **Repository**: `Repositories/OrderRepository.cs` — static methods, Dapper.AOT
4. **Service**: `Services/OrderService.cs` — pure orchestration
5. **Endpoints**: `Endpoints/OrderEndpoints.cs` — `MapOrderEndpoints(this WebApplication app)`
6. **Wire**: `Endpoints/Composition.cs` — `OrderEndpoints.Bind(...)` then `app.MapOrderEndpoints()`
7. **Add types** to `AppJsonSerializerContext`

## License

MIT