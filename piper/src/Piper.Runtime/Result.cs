namespace Piper.Runtime;

/// <summary>Piper's <c>Result&lt;T, E&gt;</c>: either <c>Ok(value)</c> or <c>Err(error)</c>.</summary>
public readonly record struct Result<T, E>
{
    private Result(bool isOk, T value, E error)
    {
        IsOk = isOk;
        Value = value;
        Error = error;
    }

    public bool IsOk { get; }

    /// <summary>The value; only meaningful when <see cref="IsOk"/>.</summary>
    public T Value { get; }

    /// <summary>The error; only meaningful when not <see cref="IsOk"/>.</summary>
    public E Error { get; }

    public static Result<T, E> Ok(T value) => new(true, value, default!);

    public static Result<T, E> Err(E error) => new(false, default!, error);

    public override string ToString() => IsOk ? $"Ok({Value})" : $"Err({Error})";
}
