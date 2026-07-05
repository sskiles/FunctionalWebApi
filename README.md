# FunctionalWebApi

> A reference implementation for building ASP.NET Core Web APIs with functional programming principles, targeting Native AOT from day one.

## What is This?

A minimal, production-ready Web API that demonstrates how functional programming practices — explicit dependencies, pure functions, immutable data — integrate cleanly with ASP.NET Core. No frameworks beyond the standard stack. No runtime reflection. No hidden state.

## Dependency Rejection

Most ASP.NET Core applications use **Dependency Injection** as the primary composition mechanism. DI improves on hard-coded dependencies, but introduces its own problems:

- **Hidden dependencies**: A service's actual needs are invisible without reading the container configuration
- **Framework coupling**: Business logic becomes entangled with `IServiceProvider`, lifetime management, and scoped resolution
- **Opaque transitivity**: Service A → B → C means A implicitly depends on everything C needs
- **AOT/Trimming hostile**: DI containers rely on runtime reflection (`Activator.CreateInstance`)

**Dependency Rejection** passes explicit functions and values at composition time:

```csharp
// DI: opaque — what does UserService actually need?
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

### Result&lt;T, E&gt;

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
- Input → Output. Nothing else.

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

## Running / Publishing

### Development

```bash
dotnet run
```

### Native AOT Publish

```bash
dotnet publish -c Release -p:PublishAot=true -p:RuntimeIdentifier=linux-x64
```

This produces a self-contained binary in `bin/Release/net10.0/linux-x64/publish/FunctionalWebApi` with no external runtime dependency.

### Trimming

`<PublishTrimmed>true</PublishTrimmed>` is inherited from `<PublishAot>true`. The linker analyzes all code paths reachable from the entry point and removes everything else. DTOs used in JSON, JWT, or Dapper must be explicitly rooted in `AppJsonSerializerContext`. Anything only reached via reflection is removed — which is desirable, unless you actually needed it.

### Target Architectures

- `linux-x64` — primary target; tested and verified
- `linux-arm64` — change `RuntimeIdentifier` to `linux-arm64`
- `win-x64` / `win-arm64` — supported by the runtime, not the primary development target

### What AOT Forbids

These are not limitations. They are guardrails that remove entire classes of runtime bugs:

- No `Activator.CreateInstance`
- No `Type.GetType`
- No `JsonSerializer.Serialize(object)` — always use `JsonTypeInfo<T>`
- No runtime lambdas in `MapPost`/`MapGet` — pass method groups directly

## License

MIT