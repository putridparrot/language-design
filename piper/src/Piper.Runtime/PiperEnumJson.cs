using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Piper.Runtime;

/// <summary>
/// A Piper enum in JSON (the compiler puts this on each enum's C# base record). A variant without fields is its name,
/// <c>"Open"</c>; one with fields is an object naming it in <c>"kind"</c>, then its fields, <c>{"kind": "Closed",
/// "reason": "done"}</c>. Reading accepts either form for a fieldless variant.
/// </summary>
public sealed class PiperEnumJsonConverterFactory : JsonConverterFactory
{
    /// <summary>The property naming a variant in its object.</summary>
    public const string Kind = "kind";

    // Only the enum's own base record, not its variants, which are serialized as ordinary records.
    public override bool CanConvert(Type type) => type.IsAbstract && Variants.Of(type).Count > 0;

    public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(EnumConverter<>).MakeGenericType(type))!;

    private sealed class EnumConverter<TEnum> : JsonConverter<TEnum> where TEnum : class
    {
        public override TEnum? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            var variants = Variants.Of(type);
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                {
                    var name = reader.GetString()!;
                    if (!variants.TryGetValue(name, out var variant) || variant.HasFields)
                        throw new JsonException($"'{name}' isn't a variant of {type.Name} without fields.");
                    return (TEnum)Activator.CreateInstance(variant.Type)!;
                }
                case JsonTokenType.StartObject:
                {
                    using var document = JsonDocument.ParseValue(ref reader);
                    if (!document.RootElement.TryGetProperty(Kind, out var kind) || kind.ValueKind != JsonValueKind.String)
                        throw new JsonException($"A {type.Name} needs a \"{Kind}\" naming its variant.");
                    if (!variants.TryGetValue(kind.GetString()!, out var variant))
                        throw new JsonException($"'{kind.GetString()}' isn't a variant of {type.Name}.");
                    return (TEnum)document.RootElement.Deserialize(variant.Type, options)!;
                }
                default:
                    throw new JsonException($"A {type.Name} is a variant's name or an object with a \"{Kind}\".");
            }
        }

        public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
        {
            var variant = Variants.Of(typeof(TEnum)).Values.First(v => v.Type == value.GetType());
            if (!variant.HasFields)
            {
                writer.WriteStringValue(variant.Name);
                return;
            }
            writer.WriteStartObject();
            writer.WriteString(Kind, variant.Name);
            foreach (var field in JsonSerializer.SerializeToElement(value, variant.Type, options).EnumerateObject())
                field.WriteTo(writer);
            writer.WriteEndObject();
        }
    }

    /// <summary>An enum's variants, by name: its nested records deriving from it (constructed, for a generic enum).</summary>
    private static class Variants
    {
        private static readonly ConcurrentDictionary<Type, Dictionary<string, Variant>> Cache = new();

        public static Dictionary<string, Variant> Of(Type type) => Cache.GetOrAdd(type, t =>
            t.GetNestedTypes(BindingFlags.Public)
                .Select(nested => nested.IsGenericTypeDefinition && t.IsGenericType ? nested.MakeGenericType(t.GetGenericArguments()) : nested)
                .Where(nested => nested.BaseType == t)
                .ToDictionary(nested => nested.Name, nested => new Variant(nested.Name, nested,
                    nested.GetConstructors().Any(c => c.GetParameters().Length > 0))));
    }

    private sealed record Variant(string Name, Type Type, bool HasFields);
}
