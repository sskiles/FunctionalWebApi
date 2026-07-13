namespace FunctionalWebApi.Domain;

using FunctionalWebApi.Errors;

/// <summary>
/// Discriminated result for operations that yield multiple values.
/// On success <see cref="Items"/> carries a materialized <see cref="IReadOnlyList{T}"/>.
/// On failure <see cref="Error"/> carries an <see cref="AppException"/>-derived
/// value describing what went wrong.
/// </summary>
public readonly record struct ResultCollection<TValue, TError>
    where TError : AppException
{
    public IReadOnlyList<TValue>? Items { get; }
    public TError? Error { get; }
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;

    internal ResultCollection(List<TValue> values)
    {
        Items = values;
        Error = default;
        IsSuccess = true;
    }

    internal ResultCollection(TError error)
    {
        Items = null;
        Error = error;
        IsSuccess = false;
    }

    // Implicit operators cover both paths: callers can simply return a
    // materialized list or an error and the struct wraps it automatically.
    public static implicit operator ResultCollection<TValue, TError>(TError error) => new(error);
    public static implicit operator ResultCollection<TValue, TError>(List<TValue> values) => new(values);

    public TResult Match<TResult>(
        Func<IReadOnlyList<TValue>, TResult> onSuccess,
        Func<TError, TResult> onFailure)
    {
        return IsSuccess ? onSuccess(Items!) : onFailure(Error!);
    }
}