namespace Piper.Runtime;

/// <summary>Piper's <c>Result&lt;T, E&gt;</c>: either <c>Ok(value)</c> or <c>Err(error)</c>.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(ResultJsonConverterFactory))]
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

/// <summary>
/// A Result in JSON is an object with one property: <c>{"ok": value}</c> or <c>{"err": error}</c>. So a Result can be
/// sent and read back, as a remote actor's reply is.
/// </summary>
public sealed class ResultJsonConverterFactory : System.Text.Json.Serialization.JsonConverterFactory
{
    public override bool CanConvert(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Result<,>);

    public override System.Text.Json.Serialization.JsonConverter CreateConverter(Type type, System.Text.Json.JsonSerializerOptions options) =>
        (System.Text.Json.Serialization.JsonConverter)Activator.CreateInstance(typeof(ResultJsonConverter<,>).MakeGenericType(type.GetGenericArguments()))!;

    private sealed class ResultJsonConverter<T, E> : System.Text.Json.Serialization.JsonConverter<Result<T, E>>
    {
        private const string Expected = "Expected a Result: {\"ok\": …} or {\"err\": …}.";

        public override Result<T, E> Read(ref System.Text.Json.Utf8JsonReader reader, Type type, System.Text.Json.JsonSerializerOptions options)
        {
            if (reader.TokenType != System.Text.Json.JsonTokenType.StartObject || !reader.Read() || reader.TokenType != System.Text.Json.JsonTokenType.PropertyName)
                throw new System.Text.Json.JsonException(Expected);
            var name = reader.GetString();
            reader.Read();
            Result<T, E> result;
            if (string.Equals(name, "ok", StringComparison.OrdinalIgnoreCase))
                result = Result<T, E>.Ok(System.Text.Json.JsonSerializer.Deserialize<T>(ref reader, options)!);
            else if (string.Equals(name, "err", StringComparison.OrdinalIgnoreCase))
                result = Result<T, E>.Err(System.Text.Json.JsonSerializer.Deserialize<E>(ref reader, options)!);
            else
                throw new System.Text.Json.JsonException(Expected);
            if (!reader.Read() || reader.TokenType != System.Text.Json.JsonTokenType.EndObject)
                throw new System.Text.Json.JsonException(Expected);
            return result;
        }

        public override void Write(System.Text.Json.Utf8JsonWriter writer, Result<T, E> value, System.Text.Json.JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WritePropertyName(value.IsOk ? "ok" : "err");
            if (value.IsOk)
                System.Text.Json.JsonSerializer.Serialize(writer, value.Value, options);
            else
                System.Text.Json.JsonSerializer.Serialize(writer, value.Error, options);
            writer.WriteEndObject();
        }
    }
}
