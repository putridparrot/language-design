using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Piper.Runtime;

/// <summary>
/// A Piper record with invariants (<c>requires</c>) in JSON (the compiler puts this on such records). It reads and writes
/// as an ordinary record, its fields by name, but a value its invariants reject is a <see cref="JsonException"/> with
/// the invariant's message rather than an <see cref="ArgumentException"/>: bad input, not a failure, so ASP.NET Core
/// answers 400 rather than 500. A missing field is its type's default (None for an Option).
/// </summary>
public sealed class PiperRecordJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type type) => Shape.Of(type) is not null;

    public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(RecordConverter<>).MakeGenericType(type))!;

    private sealed class RecordConverter<TRecord> : JsonConverter<TRecord> where TRecord : class
    {
        public override TRecord? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException($"A {type.Name} is a JSON object.");
            var shape = Shape.Of(type)!;
            using var document = JsonDocument.ParseValue(ref reader);
            var arguments = shape.Fields.Select(field =>
                Find(document.RootElement, Name(field.Name, options), options) is { } value
                    ? value.Deserialize(field.ParameterType, options)
                    : field.ParameterType.IsValueType ? Activator.CreateInstance(field.ParameterType) : null).ToArray();
            try
            {
                return (TRecord)shape.Constructor.Invoke(arguments);
            }
            catch (TargetInvocationException e) when (e.InnerException is ArgumentException invalid)
            {
                throw new JsonException(invalid.Message, invalid);
            }
        }

        public override void Write(Utf8JsonWriter writer, TRecord value, JsonSerializerOptions options)
        {
            var shape = Shape.Of(typeof(TRecord))!;
            writer.WriteStartObject();
            foreach (var field in shape.Fields)
            {
                writer.WritePropertyName(Name(field.Name, options));
                JsonSerializer.Serialize(writer, shape.Properties[field.Name!].GetValue(value), field.ParameterType, options);
            }
            writer.WriteEndObject();
        }

        private static string Name(string? field, JsonSerializerOptions options) =>
            options.PropertyNamingPolicy?.ConvertName(field!) ?? field!;

        private static JsonElement? Find(JsonElement json, string name, JsonSerializerOptions options)
        {
            if (json.TryGetProperty(name, out var exact))
                return exact;
            if (!options.PropertyNameCaseInsensitive)
                return null;
            foreach (var property in json.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    return property.Value;
            }
            return null;
        }
    }

    /// <summary>A record's primary constructor, whose parameters are its fields, and the properties that read them.</summary>
    private sealed record Shape(ConstructorInfo Constructor, ParameterInfo[] Fields, Dictionary<string, PropertyInfo> Properties)
    {
        private static readonly ConcurrentDictionary<Type, Shape?> Cache = new();

        // The constructor whose parameters all have properties of the same name: the primary one, not the copy constructor.
        public static Shape? Of(Type type) => Cache.GetOrAdd(type, t =>
        {
            var properties = t.GetProperties(BindingFlags.Public | BindingFlags.Instance).ToDictionary(p => p.Name);
            return t.GetConstructors()
                .Where(c => c.GetParameters() is var parameters && parameters.All(p => p.Name is { } name && properties.ContainsKey(name)) &&
                            !parameters.Any(p => p.ParameterType == t))
                .Select(c => new Shape(c, c.GetParameters(), properties))
                .OrderByDescending(s => s.Fields.Length)
                .FirstOrDefault();
        });
    }
}
