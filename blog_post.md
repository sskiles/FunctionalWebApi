---
layout: post
author: Shane Skiles
title: "Functional C# Web APIs with Native AOT"
tags: [dotnet, functional programming, native aot, dependency rejection, c#]
---

I have been playing around with functional programming concepts in C# for a while now.
Nothing too serious, just seeing what F# functionality and concepts
I can utilize in C# and what the pros and cons are.
I enjoy the concepts of pure functions, immutability,
and treating data and functions interchangabily.
It's fun to see how far you can push C# toward a purely functional language with immutability
and explicit dependencies before the compiler complains.
Recently, I started wondering what a web API built with these ideas might look like,
especially targeting native AOT from the start.

The result is a small project called
[https://github.com/sskiles/FunctionalWebApi](`FunctionalWebApi`).
It is not meant to revolutionize anything.
It is just a reference implementation for building an ASP.NET Core Web API with
explicit dependencies, pure functions, and no runtime reflection.
Here is a brief summary of some of the design approaches I took.

Most ASP.NET Core applications use Dependency Injection (DI) as the
primary mechanism for composing services.
I have used DI for years and it is definitely better than hard-coding dependencies.
But the more I worked with it, the more I noticed a few things that started to bother me.
But sometimes it can be frustrating tracing where dependencies are
coming from or going... or how to most efficiently coax one into a new location.

The actual needs of a service are invisible without reading the container configuration.
Service A depends on Service B, which depends on Service C,
and now A implicitly depends on everything C needs.
Plus, DI containers can rely on runtime reflection,
which can be tricky when trimming which is needed for native AOT.

Well, we injection a dependencies into the client to access the dependency members in client methods.
Why not just inject the needed method/property directly into the client method?
If you start passing functions and values directly to the client methods,
the client container doesn't need to resolve anything.
Creating more of a dependency rejection than a dependency injection.

Here is what the difference looks like in practice.
With DI, you register everything in a container and eventually everything works out:

```csharp
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IJwtService, JwtService>();
builder.Services.AddScoped<UserService>();
```

With dependency rejection, the function signature itself is the contract.
Every need is visible in the parameter list:

```csharp
var loginHandler = (LoginCmd cmd, CancellationToken ct) =>
    UserService.LoginAsync(
        findByEmail: UserRepository.FindByEmailAsync,
        verifyPassword: ArgumentPasswordHasher.Verify,
        createToken: JwtService.CreateToken,
        connectionString: configuration.GetConnectionString("Sqlite")!,
        cmd, ct);
```

No hidden state, no service locator calls inside business logic, and no reflection.
Just a flat list of explicit dependencies.
The compiler tells you if something is missing.
It's composition - combining simple functions to create a more complex funtion.

Every service, repository, and endpoint handler in this project is a static method.
I went this route for a few reasons.

First, there is no mutable state to reason about.
No `this`, no fields, no injected dependencies mutating under you.
Second, thread safety is free.
There is no instance data being shared, there is no instance.
Third, static method-group references are AOT-friendly.
The Request Delegate Generator can see them at compile time and
avoids the reflection fallback that lambda closures would trigger.

It also means testing is trivial.
You do not need a DI container or a mocking framework to test a static method.
You just pass what it needs.

Another take away from functional programming is the `Result` return type.
This allows you to return success and failure result in the same data type.
There are some approaches to this making their way into C#, but nothing built in yet.
Here is a simple roll-your-own example:

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

No ceremony.
You just return the value or the error.
The caller has to handle both branches because the compiler says so.
For truly exceptional conditions, catch the exception and return that value instead of `throw`ing.
And for business-logic/validation failures, just create your own exception and return that.

I targeted native AOT from the beginning because I wanted to understand
exactly what it forbids and whether those restrictions would actually be a problem.

The answer, for this project at least, is no.
The biggest issues were needing to declare every DTO in a `JsonSerializerContext`
and needing pass method groups directly to `MapPost` and `MapGet` instead of lambdas.

Those issues are really more like guardrails, removing potential runtime bugs.
Trimming is another benefit.
The linker walks the call graph from `Main` and throws away everything it cannot reach.
A minimal API with SQLite, JSON, and JWT handling compiles to about 18 MB.
That is a self-contained binary with no runtime installation required.

Because every dependency is an explicit `Func<>` parameter,
testing doesn't require a special framework.
Everything is a function and you know what the function can return.
Here is what a test for the login logic looks like:

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

I expected a bit more trouble when wiring up the functions in composition root,
but explicitly passing functions and values turned out to be simpler than I thought.
Once you get started, the `Func<>` signatures end up being much more transparent
than `services.AddScoped<>()`.

Getting everything wired up and working was a fun exercise.
I definitely have a greater appreciation and respect for the functional programming approach.
There are some tricks in there that I have used before, but not as a
fundamental aspect of a program in C#.

I also think trimmin is something I'll start doing on all future programs even when not directly targetting AOT.
Aside from reduced binary size, you get a faster start up,
and it forces you to take that extra step sometimes to be "correct" and get a performance boost.
It may not always possible/feasible, but it is rewarding. Being a step closer to AOT ready is just a benefit - even though you should probably only publish for AOT when specifically targetting systems without the dotnet framework.
In general, RyuJIT (the modern .NET Core JIT) will outperform static AOT binaries by dynamically optimizing "hot paths" in the runtime.

The repository is on GitHub if you want to take a look.
It is a reference implementation, not a framework.
Treat it as a starting point for thinking about how functional ideas can fit into ASP.NET Core,
or as a working example of what native AOT can look like in practice.

Anyway, that is what I have been working on.
It may not be revolutionary, but it was a fun exercise in seeing how far
I could push C# toward explicit, side-effect-free code without leaving the ecosystem entirely.

Shane
