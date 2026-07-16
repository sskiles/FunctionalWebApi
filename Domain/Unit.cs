namespace FunctionalWebApi.Domain;

/// <summary>
/// A type with exactly one value, used as the success payload when an
/// operation has no meaningful return value but still participates in the
/// <see cref="Result{TValue, TError}"/> pattern.
/// </summary>
public readonly record struct Unit
{
    /// <summary>The sole instance of <see cref="Unit"/> (default(Unit)).</summary>
    public static readonly Unit Default = default;
}