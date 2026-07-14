#pragma warning disable SA1200 // Using directives should be placed correctly
using Dapper;
using FunctionalWebApi;
using FunctionalWebApi.Endpoints;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Data.Sqlite;
#pragma warning restore SA1200 // Using directives should be placed correctly

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

// Ensure DB schema exists, using the connection string the composition root
// is about to validate.
var configuration = app.Configuration;
var connectionString = configuration.GetConnectionString("Sqlite")!;
// Dapper auto-opens the connection on first use; we don't pre-open here.
await using (var conn = new SqliteConnection(connectionString))
{
    _ = await conn.ExecuteAsync(@"
        CREATE TABLE IF NOT EXISTS Users (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL,
            Email TEXT NOT NULL UNIQUE,
            PasswordHash TEXT NOT NULL DEFAULT ''
        );");
}

// Everything wired in one call.
app.RegisterAllEndpoints(configuration);

await app.RunAsync();
