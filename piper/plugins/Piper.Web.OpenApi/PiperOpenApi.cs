using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Piper.Runtime;

namespace Piper.Web.OpenApi;

/// <summary>OpenAPI documents describing Piper's types as they're written in JSON.</summary>
public static class PiperOpenApiExtensions
{
    /// <summary>
    /// An OpenAPI document (<c>app.MapOpenApi()</c> serves it at <c>/openapi/v1.json</c>) whose schemas describe Piper's
    /// types as Piper writes them: see <see cref="PiperSchemaTransformer"/>.
    /// </summary>
    public static IServiceCollection AddPiperOpenApi(this IServiceCollection services, string documentName = "v1") =>
        services.AddOpenApi(documentName, options => options.AddPiperSchemas());

    /// <summary>Piper's schemas, for a document set up some other way: <c>AddOpenApi(o => o.AddPiperSchemas())</c>.</summary>
    public static OpenApiOptions AddPiperSchemas(this OpenApiOptions options) =>
        options.AddSchemaTransformer<PiperSchemaTransformer>().AddOperationTransformer<PiperParameterTransformer>();
}

/// <summary>
/// A parameter read from the route, query or a header (<c>done: bool?</c>, <c>after: BookId</c>) is bound by its type's
/// <c>TryParse</c>, which ASP.NET Core describes as any string. This describes it as the value it takes: an Option's
/// (and isn't required, as None is the request leaving it out), or a newtype's.
/// </summary>
public sealed class PiperParameterTransformer : IOpenApiOperationTransformer
{
    public async Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        if (operation.Parameters is not { Count: > 0 } parameters)
            return;
        foreach (var description in context.Description.ParameterDescriptions)
        {
            if (description.Type is not { } type)
                continue;
            var (value, optional) = ValueOf(type);
            if (value == type)
                continue;
            if (parameters.OfType<OpenApiParameter>().FirstOrDefault(p => string.Equals(p.Name, description.Name, StringComparison.OrdinalIgnoreCase)) is not { } parameter)
                continue;
            // A route constraint (`{id:int}`) has already described a route value; only the requirement is left.
            if (parameter.In != ParameterLocation.Path)
                parameter.Schema = await context.GetOrCreateSchemaAsync(value, null, cancellationToken);
            if (optional)
                parameter.Required = false;
        }
    }

    /// <summary>
    /// The type a parameter's text is read as, through Options and newtypes, and whether it may be left out. An optional
    /// handler parameter is a <c>Nullable&lt;Option&lt;T&gt;&gt;</c>, so ASP.NET Core lets a request leave it out.
    /// </summary>
    private static (Type Value, bool Optional) ValueOf(Type type)
    {
        bool optional = false;
        while (true)
        {
            if (Nullable.GetUnderlyingType(type) is { } underlying)
            {
                optional = true;
                type = underlying;
            }
            else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Option<>))
            {
                optional = true;
                type = type.GetGenericArguments()[0];
            }
            else if (PiperNewtype.ValueType(type) is { } value)
                type = value;
            else
                return (type, optional);
        }
    }
}

/// <summary>
/// Piper's types each have a JSON converter, which the OpenAPI generator can't see through, so on their own they're
/// described as anything. This describes them as Piper writes them:
/// <list type="bullet">
/// <item>an Option is its value or <c>null</c>;</item>
/// <item>a newtype is its value;</item>
/// <item>an enum is one of its variants: a fieldless one is its name, a string, and one with fields an object with
/// <c>kind</c> (its name) and its fields;</item>
/// <item>a record with invariants is an object of its fields, those that aren't optional required.</item>
/// </list>
/// </summary>
public sealed class PiperSchemaTransformer : IOpenApiSchemaTransformer
{
    private const string SchemaId = "x-schema-id";

    public async Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        var type = context.JsonTypeInfo.Type;
        var options = context.JsonTypeInfo.Options;
        Task<OpenApiSchema> SchemaOf(Type t) => context.GetOrCreateSchemaAsync(t, null, cancellationToken);

        // An ordinary record: ASP.NET Core requires each constructor parameter, but an optional field may be left out.
        if (context.JsonTypeInfo.Kind == JsonTypeInfoKind.Object && schema.Required is { Count: > 0 } required)
        {
            foreach (var property in context.JsonTypeInfo.Properties.Where(p => IsOption(p.PropertyType)))
                required.Remove(property.Name);
        }

        if (IsOption(type))
        {
            var value = await SchemaOf(type.GetGenericArguments()[0]);
            Clear(schema);
            if (IsComponent(value))
                schema.OneOf = [new OpenApiSchema { Type = JsonSchemaType.Null }, value];
            else
            {
                CopyInto(schema, value);
                schema.Type = (value.Type ?? 0) | JsonSchemaType.Null;
            }
            // Inline wherever it's used, rather than an "OptionOfString" component of its own.
            schema.Metadata?.Remove(SchemaId);
            return;
        }

        if (PiperNewtype.ValueType(type) is { } valueType)
        {
            var value = await SchemaOf(valueType);
            Clear(schema);
            if (IsComponent(value))
                schema.AllOf = [value];
            else
                CopyInto(schema, value);
            return;
        }

        if (new PiperEnumJsonConverterFactory().CanConvert(type))
        {
            // Only fieldless variants: one of their names, as a C# enum written as a string is.
            if (Variants(type).All(v => !Fields(v, options).Any()))
            {
                Clear(schema);
                schema.Type = JsonSchemaType.String;
                schema.Enum = [.. Variants(type).Select(v => (JsonNode)JsonValue.Create(v.Name))];
                return;
            }
            var variants = new List<IOpenApiSchema>();
            foreach (var variant in Variants(type))
            {
                var fields = Fields(variant, options).ToList();
                if (fields.Count == 0)
                {
                    variants.Add(new OpenApiSchema { Type = JsonSchemaType.String, Enum = [JsonValue.Create(variant.Name)] });
                    continue;
                }
                var shape = new OpenApiSchema
                {
                    Type = JsonSchemaType.Object,
                    Properties = new Dictionary<string, IOpenApiSchema>
                    {
                        [PiperEnumJsonConverterFactory.Kind] = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [JsonValue.Create(variant.Name)] },
                    },
                    Required = new HashSet<string> { PiperEnumJsonConverterFactory.Kind },
                };
                await AddFields(shape, fields, SchemaOf);
                variants.Add(shape);
            }
            Clear(schema);
            schema.OneOf = variants;
            return;
        }

        if (HasPiperRecordConverter(type))
        {
            Clear(schema);
            schema.Type = JsonSchemaType.Object;
            schema.Properties = new Dictionary<string, IOpenApiSchema>();
            schema.Required = new HashSet<string>();
            await AddFields(schema, Fields(type, options), SchemaOf);
        }
    }

    private static async Task AddFields(OpenApiSchema shape, IEnumerable<(string Name, Type Type)> fields, Func<Type, Task<OpenApiSchema>> schemaOf)
    {
        foreach (var (name, type) in fields)
        {
            shape.Properties![name] = await schemaOf(type);
            if (!IsOption(type))
                shape.Required!.Add(name);
        }
    }

    private static bool IsOption(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Option<>);

    /// <summary>An enum's variants: its public nested records deriving from it.</summary>
    private static IEnumerable<Type> Variants(Type type) =>
        type.GetNestedTypes(BindingFlags.Public)
            .Select(nested => nested.IsGenericTypeDefinition && type.IsGenericType ? nested.MakeGenericType(type.GetGenericArguments()) : nested)
            .Where(nested => nested.BaseType == type);

    /// <summary>
    /// A record's (or variant's) fields as JSON names them: its primary constructor's parameters, which are its
    /// properties, named by <c>[JsonPropertyName]</c> or else the options' naming policy.
    /// </summary>
    private static IEnumerable<(string Name, Type Type)> Fields(Type record, JsonSerializerOptions options)
    {
        var constructor = record.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
        foreach (var parameter in constructor?.GetParameters() ?? [])
        {
            var property = record.GetProperty(parameter.Name!, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (property is null || property.IsDefined(typeof(JsonIgnoreAttribute)))
                continue;
            var name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                       ?? options.PropertyNamingPolicy?.ConvertName(property.Name) ?? property.Name;
            yield return (name, property.PropertyType);
        }
    }

    /// <summary>A record the compiler gave a converter of its own (for its invariants): a nested PiperJsonConverter, or the generic factory.</summary>
    private static bool HasPiperRecordConverter(Type type) =>
        type.GetCustomAttribute<JsonConverterAttribute>()?.ConverterType is { } converter &&
        ((converter.Name == "PiperJsonConverter" && converter.DeclaringType == type) || converter == typeof(PiperRecordJsonConverterFactory));

    /// <summary>A schema the document keeps as a component (a record, an enum), which is referred to rather than copied.</summary>
    private static bool IsComponent(OpenApiSchema schema) =>
        schema.Metadata?.ContainsKey(SchemaId) == true && (schema.Properties?.Count > 0 || schema.OneOf?.Count > 0 || schema.AllOf?.Count > 0);

    private static void Clear(OpenApiSchema schema)
    {
        schema.Type = null;
        schema.Format = null;
        schema.Properties = null;
        schema.Required = null;
        schema.Items = null;
        schema.OneOf = null;
        schema.AnyOf = null;
        schema.AllOf = null;
        schema.Enum = null;
        schema.AdditionalProperties = null;
        schema.Pattern = null;
    }

    private static void CopyInto(OpenApiSchema target, OpenApiSchema source)
    {
        target.Type = source.Type;
        target.Format = source.Format;
        target.Properties = source.Properties;
        target.Required = source.Required;
        target.Items = source.Items;
        target.OneOf = source.OneOf;
        target.AnyOf = source.AnyOf;
        target.AllOf = source.AllOf;
        target.Enum = source.Enum;
        target.AdditionalProperties = source.AdditionalProperties;
        target.Pattern = source.Pattern;
    }
}
