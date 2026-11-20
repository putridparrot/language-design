using System.Collections.Concurrent;
using System.Reflection;

namespace Piper.Runtime;

/// <summary>
/// <c>[Transient]</c> on an error: trying again might work (a timeout, a service briefly unavailable), where other
/// errors (not found, not allowed) would only fail the same way again. On an enum variant it marks that variant; on a
/// whole enum, or a record or class, every error of that type. <c>[Retry]</c> tries a function again when it gives a
/// transient <c>Err</c> (as it does when it throws), <c>[CircuitBreaker]</c> counts only transient errors as failures,
/// and a workflow's activity is retried on one. See <see cref="PiperTransient"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = true)]
public sealed class TransientAttribute : Attribute;

/// <summary>Whether an error, or a <c>Result</c>'s <c>Err</c>, is <c>[Transient]</c>: worth trying again.</summary>
public static class PiperTransient
{
    private static readonly ConcurrentDictionary<Type, bool> Transient = new();
    private static readonly ConcurrentDictionary<Type, (PropertyInfo IsOk, PropertyInfo Error)?> Results = new();

    /// <summary>
    /// Whether <paramref name="error"/> is transient: its type (or, for a variant, its enum) is marked
    /// <c>[Transient]</c>, or it's a <see cref="TimeoutException"/>, which always is.
    /// </summary>
    public static bool IsTransient(object? error) =>
        error is not null && Transient.GetOrAdd(error.GetType(), type =>
            typeof(TimeoutException).IsAssignableFrom(type) || type.IsDefined(typeof(TransientAttribute), inherit: true));

    /// <summary>Whether <paramref name="result"/> is an <c>Err</c> holding a transient error.</summary>
    public static bool IsTransientFailure<T, E>(Result<T, E> result) => !result.IsOk && IsTransient(result.Error);

    /// <summary>
    /// Whether <paramref name="value"/> is a <c>Result</c> that's an <c>Err</c> holding a transient error; false for
    /// anything else. For code that sees results only as values of some <c>T</c>, such as a workflow's activities.
    /// </summary>
    public static bool IsTransientFailure(object? value)
    {
        if (value is null || ResultParts(value.GetType()) is not { } parts)
            return false;
        return !(bool)parts.IsOk.GetValue(value)! && IsTransient(parts.Error.GetValue(value));
    }

    private static (PropertyInfo IsOk, PropertyInfo Error)? ResultParts(Type type) =>
        Results.GetOrAdd(type, t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Result<,>)
            ? (t.GetProperty(nameof(Result<int, int>.IsOk))!, t.GetProperty(nameof(Result<int, int>.Error))!)
            : null);
}
