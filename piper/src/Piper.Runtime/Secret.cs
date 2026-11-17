using System.Text.Json;
using System.Text.Json.Serialization;

namespace Piper.Runtime;

/// <summary>
/// A value that mustn't leak: a password, a connection string, a webhook's URL with its token. It prints, formats and
/// is written to JSON as <c>***</c>, so a secret logged, printed, shown in a dashboard or returned by an API by mistake
/// gives nothing away; and the compiler refuses one in a string's hole (<c>$"…{secret}…"</c>, a log's message), the
/// usual way one leaks. <see cref="Reveal"/> is the one way to its value, for where it's meant to go. Read from JSON,
/// it's the value written, so configuration and requests can carry one in.
/// </summary>
[JsonConverter(typeof(SecretJsonConverterFactory))]
public sealed class Secret<T>(T value) : IFormattable
{
    /// <summary>What a secret shows wherever it's printed.</summary>
    public const string Hidden = "***";

    /// <summary>The value, for where it's meant to go: <c>UseSqlite(settings.connection.Reveal())</c>.</summary>
    public T Reveal() => value;

    public override string ToString() => Hidden;

    public string ToString(string? format, IFormatProvider? formatProvider) => Hidden;
}

/// <summary>Writes a <see cref="Secret{T}"/> as <c>"***"</c>, and reads one from its value.</summary>
public sealed class SecretJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Secret<>);

    public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(SecretConverter<>).MakeGenericType(type.GetGenericArguments()[0]))!;

    private sealed class SecretConverter<T> : JsonConverter<Secret<T>>
    {
        public override Secret<T>? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            new(JsonSerializer.Deserialize<T>(ref reader, options)!);

        public override void Write(Utf8JsonWriter writer, Secret<T> value, JsonSerializerOptions options) =>
            writer.WriteStringValue(Secret<T>.Hidden);
    }
}
