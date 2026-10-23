namespace Piper.Runtime;

/// <summary>
/// Piper's <c>T?</c>: either <c>Some(value)</c> or <c>None</c>. A small struct rather than a class hierarchy, so it
/// doesn't allocate, nests (<c>int??</c>) and defaults to None. Compiled patterns use its properties:
/// <c>{ IsSome: true, Value: var v }</c>.
/// </summary>
public readonly record struct Option<T>
{
    private Option(T value)
    {
        IsSome = true;
        Value = value;
    }

    public bool IsSome { get; }

    /// <summary>The value; only meaningful when <see cref="IsSome"/>.</summary>
    public T Value { get; }

    public static Option<T> Some(T value) => new(value);

    public static Option<T> None => default;

    public override string ToString() => IsSome ? $"Some({Value})" : "None";
}
