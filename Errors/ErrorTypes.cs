namespace MyApi.Errors;

/// <summary>
/// Base type for all domain errors. Subclasses of <see cref="Exception"/> flow
/// through <see cref="ResultType.Result{TValue, TError}"/> as the failure
/// channel, letting the handler pipeline pattern-match on the concrete
/// exception type instead of inventing parallel hierarchies.
/// </summary>
public abstract class AppException : Exception
{
    protected AppException()                                       { }
    protected AppException(string message)                         : base(message) { }
    protected AppException(string message, Exception inner)        : base(message, inner) { }
}

/// <summary>
/// Resource lookup failed.
/// </summary>
public sealed class NotFoundError : AppException
{
    public NotFoundError()                           : base()            { }
    public NotFoundError(string message)             : base(message)     { }
}

/// <summary>
/// Validation problem: a payload failed model validation.
/// </summary>
public sealed class ValidationError : AppException
{
    public IDictionary<string, string[]> Errors { get; } =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

    public ValidationError() : base("One or more validation errors occurred.") { }
    public ValidationError(IDictionary<string, string[]> errors)
        : base("One or more validation errors occurred.")
    {
        foreach (var kvp in errors) Errors[kvp.Key] = kvp.Value;
    }
}

/// <summary>
/// Authentication or authorization failed.
/// </summary>
public sealed class AuthError : AppException
{
    public AuthError()                          : base("Unauthorized.") { }
    public AuthError(string message)            : base(message)        { }
}

/// <summary>
/// Marker for SQL data-layer failures. Repositories project raw
/// <c>SqliteException</c> into this when they want deterministic shape.
/// </summary>
public sealed class SqlError : AppException
{
    public enum Kind { NotFound, ConstraintViolation, Unknown }

    public Kind Code { get; }

    public SqlError(Kind code, string? message = null) : base(message ?? code.ToString())
    {
        Code = code;
    }

    public SqlError(Kind code, Exception inner) : base(code.ToString(), inner)
    {
        Code = code;
    }
}