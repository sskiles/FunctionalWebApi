namespace FunctionalWebApi.Domain;

/// <summary>
/// Discriminated result of an operation.
/// On success <see cref="Value"/> is the desired result.
/// On failure <see cref="Error"/> carries an <see cref="Exception"/>‑derived
/// value describing what went wrong. The concrete exception type (e.g.
/// <see cref="KeyNotFoundException"/>, <see cref="UnauthorizedAccessException"/>,
/// <see cref="ArgumentException"/>) lets the handler pipeline map the error
/// to the appropriate HTTP status without inventing a parallel hierarchy.
/// </summary>
public readonly record struct Result<TValue, TError>
    where TError : Exception
{
    public TValue? Value { get; }
    public TError? Error { get; }
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;

    internal Result(TValue value)
    {
        Value = value;
        Error = default;
        IsSuccess = true;
    }

    internal Result(TError error)
    {
        Value = default;
        Error = error;
        IsSuccess = false;
    }

    // Implicit operators cover the happy/sad paths: callers simply return the
    // underlying value or error, and the wrapping happens automatically.
    public static implicit operator Result<TValue, TError>(TValue value) => new(value);
    public static implicit operator Result<TValue, TError>(TError error) => new(error);

    public TResult Match<TResult>(Func<TValue, TResult> onSuccess, Func<TError, TResult> onFailure)
    {
        return IsSuccess ? onSuccess(Value!) : onFailure(Error!);
    }
}
