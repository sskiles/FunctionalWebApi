# FunctionalWebApi — Functional, Native-AOT Web API

A minimal, production-ready ASP.NET Core 10 Web API built with a **purely functional** architecture, targeting **Native AOT** from day one.

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────────────┐
│                         FunctionalWebApi (Native AOT)                          │
├─────────────────────────────────────────────────────────────────────┤
│  Program.cs ──► Endpoints/Composition.cs ──► Endpoints/UserEndpoints│
│                      │                       │                      │
│                      ▼                       ▼                      │
│              Endpoints/UserEndpoints.cs      Composition.cs         │
│                      │                                           │
│                      ▼                                           │
│              Services/UserService.cs   ──►  Domain/*.cs              │
│                      │                                           │
│                      ▼                                           │
│              Data/UserRepository.cs ──► SQLite (Microsoft.Data.Sqlite) │
└─────────────────────────────────────────────────────────────────────┘
```

### Layer Separation

| Folder | Namespace | Responsibility |
|--------|-----------|----------------|
| `Contracts/` | `FunctionalWebApi.Contracts` | Transport types: `LoginCmd`, `CreateUserCmd`, `AuthToken`, `JwtConfig` — pure DTOs, no behaviour |
| `Domain/` | `FunctionalWebApi.Domain` (global) | Core algebraic types: `Result<T,E>`, `ResultCollection<T,E>`, `AppException` hierarchy (`NotFoundError`, `AuthError`, `ValidationError`, `SqlError`). Pure data, no I/O. |
| `Errors/` | `FunctionalWebApi.Errors` | Exception hierarchy rooted in `AppException : Exception`. Domain errors are `Exception` subclasses so they participate in the global exception handler. |
| `Models/` | `FunctionalWebApi.Models` | Domain DTOs: `UserDto` (serialisable, no logic). |
| `Security/` | `FunctionalWebApi.Security` | PBKDF2-HMAC-SHA256 password hashing (`ArgumentPasswordHasher`) with constant-time compare. |
| `Repositories/` | `FunctionalWebApi.Repositories` | Data access. `UserRepository` uses Dapper.AOT + `Microsoft.Data.Sqlite`. Static, stateless, connection-string scoped. |
| `Services/` | `FunctionalWebApi.Services` | Business logic. `UserService.LoginAsync` orchestrates constant-time password check + JWT issuance. |
| `Endpoints/` | `FunctionalWebApi.Endpoints` | HTTP surface. `UserEndpoints.cs` defines routes, `Composition.cs` wires config → delegates. |
| `Domain/` | `FunctionalWebApi.Domain` | Domain errors + global exception handler (`DomainErrorHandler : IExceptionHandler`). |

### Dependency Direction (Strict)

```
Program.cs (host)
  └── Endpoints/Composition.cs          → wires delegates
          │
          ▼
  Endpoints/UserEndpoints.cs  (static handlers)
          │
          ▼
      Services/UserService.cs   (domain logic, pure throws)
          │
          ▼
      Repositories/UserRepository.cs   (Dapper.AOT)
          │
          ▼
       Domain/Models + Errors   (pure data)
```

**No cycles.** Outer layers call inwards only. `Program.cs` knows everything; inner layers know nothing about ASP.NET Core, Dapper, or configuration.

---

### Key Design Decisions

| Concern | Decision | Rationale |
|---------|----------|-----------|
| **Native AOT from day one** | `<PublishAot>true</PublishAot>`, `Dapper.AOT`, `System.Text.Json` source-gen | Zero-reflection binary, 18 MB self-contained Linux x64 |
| **No `IServiceProvider` inside domain** | Pure functions + `Func<>` delegates passed by `Composition` | Testability, no DI container in domain |
| `Result<T, E>` + `ResultCollection` | Discriminated union via `readonly record struct` + implicit operators | Exhaustive pattern matching, no `null` surprises |
| Errors are `Exception` subtypes | Thrown at repository/service boundary, caught by `IExceptionHandler` (`DomainErrorHandler`) | Exceptions = control flow for exceptional cases; `Result<,>` for expected failures |
| PBKDF2-HMAC-SHA256 (600k iterations) | Constant-time `Verify` with `CryptographicOperations.FixedTimeEquals` | Timing-attack resistance on both login & confirm-password |
| `PasswordHasher` returns `salt\|hash` PHC string | Self-describing, versioned, iter-count baked in | Easy migration, no schema migration needed |
| Endpoints as `static` method groups | `UserEndpoints.MapUserEndpoints(this WebApplication app)` | Source-generated `RequestDelegateGenerator` → AOT-friendly |
| `Result<T,E>` + implicit operators | `return user` or `return new NotFoundError(...)` | No `Result.Ok()/Failure()` noise in business logic |
| `ResultCollection` for lists | Same wrapper, carries `IReadOnlyList<T>` | Consistent error surface for list endpoints |

---

### Running Locally

```bash
# Requirements: .NET 10 SDK (preview), SQLite
cd FunctionalWebApi
dotnet run
# → http://localhost:5000
```

### Endpoints

| Method | Path | Body | Success | Errors |
|--------|------|------|---------|--------|
| `POST /users` | `CreateUserCmd { Name, Email, Password, ConfirmPassword }` | 201 `UserDto` | 400 (mismatch), 409 (email exists) |
| `GET /users` | — | 200 `UserDto[]` | — |
| `GET /users/{id}` | — | 200 `UserDto` | 404 `NotFoundError` |
| `POST /login` | `LoginCmd { Username, Password }` | 200 `AuthToken { Token }` | 401 `AuthError` |

#### Example

```bash
# Register
curl -X POST http://localhost:5000/users \
  -H 'Content-Type: application/json' \
  -d '{"Name":"Alice","Email":"alice@example.com","Password":"correct horse battery staple","ConfirmPassword":"correct horse battery staple"}'

# Login
curl -X POST http://localhost:5000/login \
  -H 'Content-Type: application/json' \
  -d '{"Username":"alice@example.com","Password":"correct horse battery staple"}'
# → 200 { "token": "eyJ..." }
```

---

### Building & Running

```bash
# Debug (JIT)
dotnet run --project FunctionalWebApi.csproj

# Native AOT publish (linux-x64)
dotnet publish -c Release -p:PublishAot=true -p:RuntimeIdentifier=linux-x64
./bin/Release/net10.0/linux-x64/publish/FunctionalWebApi
```

### Native AOT

```bash
dotnet publish -c Release -p:PublishAot=true -p:RuntimeIdentifier=linux-x64
./bin/Release/net10.0/linux-x64/publish/FunctionalWebApi
```

- **Stripped native binary**: ~18 MB (`FunctionalWebApi`), no `dotnet` runtime required.
- **Trimming**: Enabled (`<PublishTrimmed>true</PublishTrimmed>`). `NoWarn` in `.csproj` silences known false-positives (`CS9270` Dapper interceptors, `NU1903` SQLite P/Invoke).
- Works on Linux x64; ARM64 via `-p:RuntimeIdentifier=linux-arm64`.

---

### Testing

```bash
dotnet test  # (add a test project when ready)
```

Recommended test targets:
- `Security.ArgumentPasswordHasher` (round-trip + constant-time)
- `UserRepository` against in-memory SQLite
- `UserService.LoginAsync` with good / bad passwords
- `DomainErrorHandler` mapping each exception to status code

---

### Folder Structure

```
FunctionalWebApi/
├── Contracts/           # API payload types
│   ├── Commands.cs
│   └── JwtConfig.cs
├── Domain/
│   ├── Result.cs               # Result<T,E> + ResultCollection
│   ├── ResultCollection.cs
│   └── DomainErrorHandler.cs  # IExceptionHandler → ProblemDetails
├── Errors/
│   └── ErrorTypes.cs           # AppException, NotFoundError, AuthError, ...
├── Models/
│   └── UserDto.cs
├── Errors/
│   └── ErrorTypes.cs           # AppException hierarchy
├── Security/
│   └── ArgumentPasswordHasher.cs   # PBKDF2-HMAC-SHA256, constant-time
├── Repositories/
│   └── UserRepository.cs          # Dapper.AOT + Sqlite
├── Services/
│   └── UserService.cs             # LoginAsync / CreateUserAsync
├── Endpoints/
│   ├── UserEndpoints.cs        # MapPost/MapGet + static handlers
│   ├── Composition.cs          # Config → delegates → bind
│   └── AppJsonSerializerContext.cs
├── Repositories/
│   └── UserRepository.cs          # Dapper.AOT + Microsoft.Data.Sqlite
├── Services/
│   └── UserService.cs           # LoginAsync / CreateUserAsync
├── Domain/
│   ├── Result.cs
│   ├── ResultCollection.cs
│   ├── DomainErrorHandler.cs
│   └── ErrorTypes.cs
├── Errors/
│   └── ErrorTypes.cs
├── Models/
│   └── UserDto.cs
├── Contracts/
│   ├── Commands.cs
│   └── JwtConfig.cs
├── Security/
│   └── ArgumentPasswordHasher.cs
├── Repositories/
│   └── UserRepository.cs
├── Services/
│   └── UserService.cs
├── Endpoints/
│   ├── UserEndpoints.cs
│   ├── Composition.cs
│   └── AppJsonSerializerContext.cs
├── Domain/
│   ├── Result.cs
│   ├── ResultCollection.cs
│   ├── DomainErrorHandler.cs
│   └── ErrorTypes.cs
├── Program.cs
├── DapperAot.cs
├── FunctionalWebApi.csproj
└── appsettings.json
```

---

### Extending

| Need | Where |
|------|-------|
| New resource (e.g. Orders) | `Endpoints/OrderEndpoints.cs` + `Repositories/OrderRepository.cs` |
| New auth (refresh tokens) | Add `RefreshToken` table + `TokenService` in `Services/` |
| OpenAPI / Swagger | Re-add `Swashbuckle.AspNetCore` + `AddSwaggerGen()` (not AOT-friendly, keep for dev) |
| Rate limiting / brute-force | `UseRateLimiter` + `Microsoft.AspNetCore.RateLimiting` in `Program.cs` |
| Email verification / password reset | New `TokenService` + email sender abstraction |

---

### License

MIT — see `LICENSE`.