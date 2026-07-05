# FunctionalWebApi

> A reference implementation for building ASP.NET Core Web APIs with functional programming principles, targeting Native AOT from day one.

## What is This?

A minimal, production-ready Web API that demonstrates how functional programming practices (explicit dependencies, pure functions, immutable data) integrate cleanly with ASP.NET Core. No frameworks beyond the standard stack. No runtime reflection. No hidden state.

## Dependency Rejection

Most ASP.NET Core applications use **Dependency Injection** as the primary composition mechanism. DI improves on hard-coded dependencies, but introduces its own problems:

- **Hidden dependencies**: A service's actual needs are invisible without reading the container configuration
- **Framework coupling**: Business logic becomes entangled with `IServiceProvider`, lifetime management, and scoped resolution
- **Opaque transitivity**: Service A -> B -> C means A implicitly depends on everything C needs
- **AOT/Trimming hostile**: DI containers rely on runtime reflection (`Activator.CreateInstance`)

**Dependency Rejection** passes explicit functions and values at composition time:

```csharp
// DI is opaque. What does UserService actually need?
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IJwtService, JwtService>();
builder.Services.AddScoped<UserService>();

// Dependency Rejection: every need is visible in the signature
var loginHandler = (LoginCmd cmd, CancellationToken ct) =>
    UserService.LoginAsync(
        findByEmail: UserRepository.FindByEmailAsync,
        verifyPassword: ArgumentPasswordHasher.Verify,
        createToken: JwtService.CreateToken,
        connectionString: configuration.GetConnectionString("Sqlite")!,
        cmd, ct);
```

The function signature *is* the contract. There are no hidden states, no surprise dependencies, and no framework magic required to understand the code.

### Why No DI Container?

| DI Container | Dependency Rejection |
|---|---|
| `Func<IServiceProvider, T>` | `Func<TIn, TOut>` |
| Runtime reflection | Compile-time verification |
| Hidden graph of dependencies | Flat, explicit parameter list |
| Scoped lifetimes managed by framework | Lifetimes are just `using` blocks |
| Hard to test without `TestServer` or mocks | Plain functions, testable with `new` |
| Reflection breaks AOT trimming | Zero runtime reflection |

## The Functional Stack

### Result<T, E>

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

No ceremony. Just return the value or the error:

```csharp
public async Task<Result<UserDto, NotFoundError>> GetByIdAsync(int id)
{
    var user = await _findById(id);
    return user is not null ? user : new NotFoundError($"User {id}");
}
```

The caller *must* handle both branches. The compiler enforces this.

### AppException

Not all errors are business-logic failures. Some are truly exceptional. We use a typed hierarchy:

```csharp
public abstract class AppException(string message) : Exception(message);
public sealed class NotFoundError(string what) : AppException($"{what} not found");
public sealed class AuthError(string reason = "Invalid") : AppException($"Auth: {reason}");
```

These propagate to a single `IExceptionHandler` which maps them to HTTP status codes. The benefit: **one place** to change how all auth failures, not-found errors, and validation failures are represented.

### Static, Stateless Functions

Every service, repository, and endpoint handler is a `static` method. No `class` state, no `this`, no mutable fields:

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
        // Pure logic. No hidden state. No service locator.
    }
}
```

**Why static?**
- No mutable state to reason about
- Thread-safe by default
- No allocation overhead from instantiating service classes
- Method-group references are AOT-friendly (no lambda closures)
- Input -> Output. Nothing else.

## Design Decisions

| What | Why |
|---|---|
| `static` methods | Immutable state, AOT-safe method groups, no allocation |
| `Result<T,E>` | Explicit error branches, compiler-enforced handling |
| Typed `AppException` | Single exception handler maps to HTTP; typed catch blocks |
| Dapper.AOT | No runtime model building, source-generated SQL, trim-safe |
| `Func<>` injection | Every dependency visible in the signature |
| Native AOT | ~18 MB binary, 50ms cold start, no runtime |
| Records | Immutable by default, value equality, concise syntax |
| File-scoped namespaces | Less nesting, clearer intent |

## Testing

Because dependencies are explicit `Func<>` parameters, testing requires **no mocking framework**:

```csharp
[Test]
public async Task LoginAsync_WithValidCredentials_ReturnsToken()
{
    var result = await UserService.LoginAsync(
        findByEmail: (email, ct) => Task.FromResult(new UserDto(1, "Alice", email, "hash")),
        verifyPassword: (p, h) => true,
        createToken: id => "mock-token",
        connectionString: "::memory:",
        cmd: new LoginCmd("alice@example.com", "password"),
        CancellationToken.None);

    result.IsSuccess.Should().BeTrue();
    result.Value!.Token.Should().Be("mock-token");
}
```

No `Moq`, no `TestServer`, no `WebApplicationFactory`. Just C#.

## Publishing

### What is Native AOT?

Native Ahead-of-Time compilation translates .NET Intermediate Language into machine code at build time, not at runtime. The output is a platform-specific executable with no dependency on a .NET runtime installation. Startup is near-instant because there is no JIT warm-up. Memory footprint is smaller because the runtime itself is not loaded: only the code that is statically reachable from the entry point is included.

### What AOT Forbids

These are not limitations. They are guardrails that remove entire classes of runtime bugs:

- **No `Activator.CreateInstance`**: type instantiation must be known at compile time
- **No `Type.GetType`**: runtime type discovery is impossible without reflection metadata
- **No `JsonSerializer.Serialize(object)`**: always use `JsonTypeInfo<T>` with source-generated context
- **No runtime lambdas in `MapPost`/`MapGet`**: pass method groups directly; the Request Delegate Generator walks the syntax tree at compile time
- **No `dynamic`**: requires runtime code generation that the AOT compiler cannot pre-allocate

### Trimming

`<PublishTrimmed>` is inherited from `<PublishAot>`. The linker performs static analysis starting from `Main` and traverses every method call, type reference, and field access. Anything not reached by this graph is removed from the final binary. This is why a minimal API with DTOs, JWT handling, and SQLite access compiles to ~18 MB instead of the ~80 MB a self-contained JIT application would require.

For JSON, JWT, and Dapper types to survive trimming, they must be explicitly rooted in `AppJsonSerializerContext` or annotated with `[JsonSerializable]`. This seems like ceremony, but it is the compiler forcing you to declare your serialization contract: a contract that was always implicit and fragile in reflection-based code.

### Target Architectures

| RID | Platform | Notes |
|---|---|---|
| `linux-x64` | Linux x86_64 | Primary target; CI/CD default |
| `linux-arm64` | Linux ARM64 | Servers, Raspberry Pi, AWS Graviton |
| `win-x64` | Windows x86_64 | Requires Visual Studio Build Tools or Windows SDK |
| `win-arm64` | Windows ARM64 | Surface Pro X, dev kits |

### Publish Commands

**Linux x64 (primary)**

```bash
dotnet publish -c Release \
    -p:PublishAot=true \
    -p:RuntimeIdentifier=linux-x64 \
    -p:SelfContained=true

# Output
# bin/Release/net10.0/linux-x64/publish/FunctionalWebApi
# chmod +x and run directly
```

**Windows x64**

```bash
dotnet publish -c Release \
    -p:PublishAot=true \
    -p:RuntimeIdentifier=win-x64 \
    -p:SelfContained=true

# Output
# bin/Release/net10.0/win-x64/publish/FunctionalWebApi.exe
```

## License

MIT