using System.Text.Json;
using System.Text.Json.Serialization;

namespace Piper.Runtime;

/// <summary>
/// The JSON converter of a generic Piper record with invariants: the compiler nests one,
/// <c>PiperJsonConverter</c>, in each such record, and this closes it with the record's type arguments, which an
/// attribute can't name.
/// </summary>
public sealed class PiperRecordJsonConverterFactory : JsonConverterFactory
{
    public const string ConverterName = "PiperJsonConverter";

    public override bool CanConvert(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition().GetNestedType(ConverterName) is not null;

    public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(
            type.GetGenericTypeDefinition().GetNestedType(ConverterName)!.MakeGenericType(type.GetGenericArguments()))!;
}
