using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using FunctionalWebApi.Errors;

namespace FunctionalWebApi.Domain;

/// <summary>
/// Maps domain exceptions to HTTP response shapes:
/// <list type="bullet">
///   <item><see cref="NotFoundError"/> → 404</item>
///   <item><see cref="AuthError"/> → 401</item>
///   <item><see cref="ValidationError"/> → 400 with field‑level validation problem details</item>
///   <item><see cref="SqlError"/> with kind <c>ConstraintViolation</c> → 409</item>
///   <item>Anything else → <c>false</c> (ASP.NET Core's default ProblemDetails handles it)</item>
/// </list>
/// </summary>
internal sealed class DomainErrorHandler : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        return exception switch
        {
            NotFoundError => Write(httpContext, StatusCodes.Status404NotFound, problem: null),
            AuthError     => Write(httpContext, StatusCodes.Status401Unauthorized, problem: null),
            ValidationError ve => Write(httpContext, StatusCodes.Status400BadRequest, ve.Errors),
            SqlError sql when sql.Code == SqlError.Kind.ConstraintViolation
                              => Write(httpContext, StatusCodes.Status409Conflict, problem: null),
            _ => ValueTask.FromResult(false)
        };
    }

    private static async ValueTask<bool> Write(HttpContext httpContext, int statusCode, IDictionary<string, string[]>? problem)
    {
        httpContext.Response.StatusCode = statusCode;
        if (problem is { Count: > 0 })
        {
            // Use the derived JsonTypeInfo to ensure all fields (Errors, Status, Title)
            // are emitted under source-generation. Passing the base ProblemDetails info
            // would skip the derived dictionary in AOT-trim builds.
            await httpContext.Response.WriteAsJsonAsync(
                new ValidationProblemDetails(problem) { Status = statusCode },
                AppJsonSerializerContext.Default.ValidationProblemDetails!);
        }
        return true;
    }
}