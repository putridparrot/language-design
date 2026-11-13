using System.Linq.Expressions;
using System.Reflection;

namespace Piper.Runtime;

/// <summary>
/// Marks the record struct a Piper <c>newtype IssueId = int;</c> compiles to, whose one field, <c>value</c>, it stands
/// for. Libraries that store values (the data plugin's EF Core converters) find newtypes by it.
/// </summary>
[AttributeUsage(AttributeTargets.Struct)]
public sealed class PiperNewtypeAttribute : Attribute;

/// <summary>Recognising newtypes, and getting at their values.</summary>
public static class PiperNewtype
{
    /// <summary>The type of <paramref name="type"/>'s value if it's a newtype; otherwise null.</summary>
    public static Type? ValueType(Type type) =>
        type.IsValueType && type.GetCustomAttribute<PiperNewtypeAttribute>() is not null &&
        type.GetConstructors() is [var constructor] && constructor.GetParameters() is [var parameter]
            ? parameter.ParameterType
            : null;
}

/// <summary>A newtype's value and constructor as delegates, made once per type.</summary>
public static class PiperNewtype<TNewtype, TValue> where TNewtype : struct
{
    /// <summary>The newtype's value: <c>id.value</c>.</summary>
    public static readonly Func<TNewtype, TValue> Value = MakeValue();

    /// <summary>A newtype of a value, checking its invariants: <c>IssueId(5)</c>.</summary>
    public static readonly Func<TValue, TNewtype> Make = MakeConstructor();

    private static Func<TNewtype, TValue> MakeValue()
    {
        var newtype = Expression.Parameter(typeof(TNewtype));
        var property = typeof(TNewtype).GetProperty("value") is { } found && found.PropertyType == typeof(TValue)
            ? found
            : throw new InvalidOperationException($"'{typeof(TNewtype).Name}' isn't a newtype of '{typeof(TValue).Name}'.");
        return Expression.Lambda<Func<TNewtype, TValue>>(Expression.Property(newtype, property), newtype).Compile();
    }

    private static Func<TValue, TNewtype> MakeConstructor()
    {
        var value = Expression.Parameter(typeof(TValue));
        var constructor = typeof(TNewtype).GetConstructor([typeof(TValue)])
            ?? throw new InvalidOperationException($"'{typeof(TNewtype).Name}' isn't a newtype of '{typeof(TValue).Name}'.");
        return Expression.Lambda<Func<TValue, TNewtype>>(Expression.New(constructor, value), value).Compile();
    }
}
