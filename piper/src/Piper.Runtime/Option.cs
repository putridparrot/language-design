namespace Piper.Runtime;

/// <summary>
/// Piper's <c>T?</c>: either <c>Some(value)</c> or <c>None</c>. A small struct rather than a class hierarchy, so it
/// doesn't allocate, nests (<c>int??</c>) and defaults to None. Compiled patterns use its properties:
/// <c>{ IsSome: true, Value: var v }</c>.
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(OptionJsonConverterFactory))]
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

    /// <summary>
    /// Reads text as an optional value, as ASP.NET Core does to bind a route or query value: no text is None, and
    /// otherwise the text must parse as a T (a string, or a type with a <c>TryParse</c>, such as int or Guid).
    /// </summary>
    public static bool TryParse(string? text, IFormatProvider? provider, out Option<T> result)
    {
        result = default;
        if (text is null)
            return true;
        if (!OptionParsing.TryParse(typeof(T), text, provider, out var value))
            return false;
        result = new((T)value!);
        return true;
    }
}

/// <summary>Parses text as a type, for <see cref="Option{T}.TryParse"/>.</summary>
internal static class OptionParsing
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, System.Reflection.MethodInfo?> Parsers = new();

    public static bool TryParse(Type type, string text, IFormatProvider? provider, out object? value)
    {
        value = null;
        if (type == typeof(string))
        {
            value = text;
            return true;
        }
        if (type.IsEnum)
            return Enum.TryParse(type, text, ignoreCase: true, out value);

        // T.TryParse(string, IFormatProvider, out T), which numbers, dates and Guid have publicly, or else
        // T.TryParse(string, out T) (bool's other one is an explicit IParsable<bool> implementation).
        var parse = Parsers.GetOrAdd(type, t =>
            t.GetMethod("TryParse", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, [typeof(string), typeof(IFormatProvider), t.MakeByRefType()]) ??
            t.GetMethod("TryParse", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, [typeof(string), t.MakeByRefType()]));
        if (parse is null)
            return false;
        object?[] arguments = parse.GetParameters().Length == 3
            ? [text, provider ?? System.Globalization.CultureInfo.InvariantCulture, null]
            : [text, null];
        if (parse.Invoke(null, arguments) is not true)
            return false;
        value = arguments[^1];
        return true;
    }
}

/// <summary>
/// An Option in JSON is its value, or <c>null</c> for None; a property that's missing reads as None too, as a
/// constructor parameter's default.
/// </summary>
public sealed class OptionJsonConverterFactory : System.Text.Json.Serialization.JsonConverterFactory
{
    public override bool CanConvert(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Option<>);

    public override System.Text.Json.Serialization.JsonConverter CreateConverter(Type type, System.Text.Json.JsonSerializerOptions options) =>
        (System.Text.Json.Serialization.JsonConverter)Activator.CreateInstance(typeof(OptionJsonConverter<>).MakeGenericType(type.GetGenericArguments()[0]))!;

    private sealed class OptionJsonConverter<T> : System.Text.Json.Serialization.JsonConverter<Option<T>>
    {
        // Null comes to the converter, as None.
        public override bool HandleNull => true;

        public override Option<T> Read(ref System.Text.Json.Utf8JsonReader reader, Type type, System.Text.Json.JsonSerializerOptions options) =>
            reader.TokenType == System.Text.Json.JsonTokenType.Null
                ? Option<T>.None
                : Option<T>.Some(System.Text.Json.JsonSerializer.Deserialize<T>(ref reader, options)!);

        public override void Write(System.Text.Json.Utf8JsonWriter writer, Option<T> value, System.Text.Json.JsonSerializerOptions options)
        {
            if (value.IsSome)
                System.Text.Json.JsonSerializer.Serialize(writer, value.Value, options);
            else
                writer.WriteNullValue();
        }
    }
}
