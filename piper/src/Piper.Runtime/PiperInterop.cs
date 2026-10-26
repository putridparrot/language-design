namespace Piper.Runtime;

/// <summary>Where .NET's null meets Piper's Option: compiled code calls these at the boundary.</summary>
public static class PiperInterop
{
    public static Option<T> FromNullableReference<T>(T? value) where T : class =>
        value is null ? Option<T>.None : Option<T>.Some(value);

    public static Option<T> FromNullableValue<T>(T? value) where T : struct =>
        value.HasValue ? Option<T>.Some(value.GetValueOrDefault()) : Option<T>.None;

    public static T? ToNullableReference<T>(Option<T> value) where T : class => value.IsSome ? value.Value : null;

    public static T? ToNullableValue<T>(Option<T> value) where T : struct => value.IsSome ? value.Value : null;

    /// <summary>An Option of a value type (or a type parameter) passed as <c>object?</c>: its value boxed, or null.</summary>
    public static object? ToNullableObject<T>(Option<T> value) => value.IsSome ? value.Value : null;

    /// <summary>
    /// For <c>out let v</c> where .NET may write null: the call's own result passes through, and <paramref name="converted"/>
    /// gets the written value as an Option (<c>AfterOutReference(d.TryGetValue(k, out var t), t, out v)</c>).
    /// </summary>
    public static TResult AfterOutReference<TResult, T>(TResult result, T? value, out Option<T> converted) where T : class
    {
        converted = FromNullableReference(value);
        return result;
    }

    /// <inheritdoc cref="AfterOutReference{TResult, T}"/>
    public static TResult AfterOutValue<TResult, T>(TResult result, T? value, out Option<T> converted) where T : struct
    {
        converted = FromNullableValue(value);
        return result;
    }
}
