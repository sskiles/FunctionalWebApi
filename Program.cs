#pragma warning disable SA1200 // Using directives should be placed correctly
using FunctionalWebApi.Infrastructure;
using Microsoft.AspNetCore.Http.Json;
#pragma warning restore SA1200 // Using directives should be placed correctly

// Globally enables Dapper.AOT code generation for this assembly. The source
// generator intercepts compatible Dapper call sites (e.g.
// `connection.QueryFirst<T>(sql, params)`) and produces IL that doesn't fall
// back on runtime Reflection.Emit — which is required for NativeAOT.
[assembly: Dapper.DapperAot(true)]

var builder = WebApplication.CreateBuilder(args);

// Source-generated JSON metadata keeps native AOT working without reflection
// at runtime. The minimal API surfaces IResult, which System.Text.Json must
// be able to materialise.
builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.TypeInfoResolver = AppJsonSerializerContext.Default;
});

// Domain exceptions are translated to HTTP responses by DomainErrorHandler.
builder.Services.AddExceptionHandler<FunctionalWebApi.Domain.DomainErrorHandler>();
builder.Services.AddProblemDetails();

var app = builder.Build();

// Exception handling middleware sits above everything else and relies on IExceptionHandler.
app.UseExceptionHandler();

// Composition runs the schema bootstrap and wires the route pipelines.
await app.RegisterAllEndpoints(app.Configuration);

await app.RunAsync();
