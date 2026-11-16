namespace Piper.Runtime;

/// <summary>Where .NET's null meets Piper's Option: compiled code calls these at the boundary.</summary>
public static class PiperInterop
{
    /// <summary>
    /// A .NET value that may be null, as an Option. Unconstrained, so it serves a type parameter too (<c>Deserialize&lt;T&gt;</c>
    /// in a generic Piper function): for a value-type T, .NET's <c>T?</c> is T itself, never null, so it's always Some.
    /// </summary>
    public static Option<T> FromNullableReference<T>(T? value) =>
        value is null ? Option<T>.None : Option<T>.Some(value);

    public static Option<T> FromNullableValue<T>(T? value) where T : struct =>
        value.HasValue ? Option<T>.Some(value.GetValueOrDefault()) : Option<T>.None;

    /// <summary>
    /// A .NET task whose result may be null (<c>FirstOrDefaultAsync</c>, <c>ReadLineAsync</c>), as one whose result is an
    /// Option: <c>await</c> gives None for null.
    /// </summary>
    public static async Task<Option<T>> FromNullableTask<T>(Task<T?> task) =>
        FromNullableReference(await task.ConfigureAwait(false));

    public static async ValueTask<Option<T>> FromNullableTask<T>(ValueTask<T?> task) =>
        FromNullableReference(await task.ConfigureAwait(false));

    /// <summary>An Option passed where .NET accepts null: None as null (<c>default</c> for a value-type T, as C#'s <c>T?</c> is).</summary>
    public static T? ToNullableReference<T>(Option<T> value) => value.IsSome ? value.Value : default;

    public static T? ToNullableValue<T>(Option<T> value) where T : struct => value.IsSome ? value.Value : null;

    /// <summary>
    /// Options passed where .NET takes a sequence of nullable values (<c>Enumerable.Sum(IEnumerable&lt;decimal?&gt;)</c>),
    /// each None a null, as they're read: so Sum, Min, Max and Average skip None as C#'s skip null.
    /// </summary>
    public static IEnumerable<T?> ToNullableValues<T>(IEnumerable<Option<T>> values) where T : struct =>
        values.Select(v => v.IsSome ? (T?)v.Value : null);

    /// <summary>An Option of a value type (or a type parameter) passed as <c>object?</c>: its value boxed, or null.</summary>
    public static object? ToNullableObject<T>(Option<T> value) => value.IsSome ? value.Value : null;

    /// <summary>
    /// For <c>out let v</c> where .NET may write null: the call's own result passes through, and <paramref name="converted"/>
    /// gets the written value as an Option (<c>AfterOutReference(d.TryGetValue(k, out var t), t, out v)</c>).
    /// </summary>
    public static TResult AfterOutReference<TResult, T>(TResult result, T? value, out Option<T> converted)
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
