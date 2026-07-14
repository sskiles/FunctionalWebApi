namespace FunctionalWebApi.Domain;

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Maps domain‑level exceptions thrown out of <see cref="Result{TValue, TError}.Error"/>
/// to HTTP response shapes:
/// <list type="bullet">
///   <item><see cref="UnauthorizedAccessException"/> → 401</item>
///   <item><see cref="System.Collections.Generic.KeyNotFoundException"/> → 404</item>
///   <item><see cref="ArgumentException"/> → 400</item>
///   <item>Anything else → <c>false</c> (ASP.NET Core's default ProblemDetails handles it as 500)</item>
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
            UnauthorizedAccessException => Set(httpContext, StatusCodes.Status401Unauthorized),
            KeyNotFoundException        => Set(httpContext, StatusCodes.Status404NotFound),
            ArgumentException           => Set(httpContext, StatusCodes.Status400BadRequest),
            _                           => ValueTask.FromResult(false),
        };
    }

    private static async ValueTask<bool> Set(HttpContext httpContext, int statusCode)
    {
        httpContext.Response.StatusCode = statusCode;
        return true;
    }
}
