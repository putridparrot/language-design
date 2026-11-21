using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Piper.Binding;

namespace Piper.Plugins;

/// <summary>What a compilation does with the shapes of what crosses the wire (see <see cref="PluginAnalysis.CheckWireShape"/>).</summary>
public enum WireShapeMode
{
    /// <summary>Compare with the recorded shape, if there is one, reporting breaking changes; never write (the editor, tests of the compiler).</summary>
    Check,

    /// <summary>Compare, and record the shape when there's none yet or the change is compatible (<c>piper build</c>, <c>run</c> and <c>test</c>).</summary>
    Record,

    /// <summary>Record the shape even over breaking changes (<c>--accept-breaking</c>): the change is deliberate.</summary>
    AcceptBreaking,

    /// <summary>
    /// Compare with a shape recorded earlier (on disk, or at a git tag: <see cref="CompilationOptions.ShapeBaseline"/>),
    /// collecting what changed in <see cref="CompilationResult.Shapes"/> rather than reporting it; never write
    /// (<c>piper check</c>).
    /// </summary>
    Compare,
}

/// <summary>
/// One thing a program answers over the wire, for a shape: an api's call (<c>GET /api/issues/{}</c>), or a message
/// another process sends. <paramref name="Key"/> is what identifies it on the wire (renaming it breaks callers);
/// <paramref name="Name"/> is for people. <paramref name="Inputs"/> are what callers send (an Option, or a value with a
/// default, may be left out); <paramref name="Output"/> describes the answer, made of <paramref name="OutputTypes"/>.
/// </summary>
public sealed record WireOperation(
    string Key, string Name, ImmutableArray<(string Name, TypeSymbol Type)> Inputs, string? Output, ImmutableArray<TypeSymbol> OutputTypes);

/// <summary>
/// The shape of a program's wire contract (an api's calls, and every record, enum and newtype they send or answer, with
/// which way each goes), as recorded in a checked-in <c>Name.shape.json</c>, and what makes a change to it breaking for
/// callers built against the old one: a rolling update, or a client already installed.
/// </summary>
public static class WireShapes
{
    private const string Request = "request", Response = "response";

    /// <summary>The shape of <paramref name="operations"/>, as the JSON that's recorded.</summary>
    public static JsonObject Describe(string name, IEnumerable<WireOperation> operations)
    {
        var types = new SortedDictionary<string, (TypeSymbol Type, bool Request, bool Response)>(StringComparer.Ordinal);
        void Walk(TypeSymbol type, bool request, bool response)
        {
            foreach (var argument in type.TypeArguments)
                Walk(argument, request, response);
            var definition = type.OriginalDefinition;
            if (definition is not (RecordSymbol or EnumSymbol { BuiltinKind: BuiltinEnumKind.None }))
                return;
            var key = definition.Name;
            if (types.TryGetValue(key, out var seen) && (seen.Request || !request) && (seen.Response || !response))
                return;
            types[key] = (definition, request || seen.Request, response || seen.Response);
            foreach (var field in FieldsOf(definition))
                Walk(field.Type, request, response);
        }

        var calls = new JsonObject();
        foreach (var operation in operations.OrderBy(o => o.Key, StringComparer.Ordinal))
        {
            var inputs = new JsonObject();
            foreach (var (inputName, type) in operation.Inputs.OrderBy(i => i.Name, StringComparer.Ordinal))
            {
                inputs[inputName] = type.ToString();
                Walk(type, request: true, response: false);
            }
            foreach (var type in operation.OutputTypes)
                Walk(type, request: false, response: true);
            calls[operation.Key] = new JsonObject { ["name"] = operation.Name, ["inputs"] = inputs, ["output"] = operation.Output };
        }

        var described = new JsonObject();
        foreach (var (key, (type, request, response)) in types)
        {
            var shape = new JsonObject { ["sent"] = request && response ? "both" : request ? Request : Response };
            switch (type)
            {
                case RecordSymbol { IsNewtype: true } newtype:
                    shape["kind"] = "newtype";
                    shape["value"] = newtype.Fields.IsEmpty ? "?" : newtype.Fields[0].Type.ToString();
                    break;
                case RecordSymbol record:
                    shape["kind"] = "record";
                    shape["fields"] = Fields(record.Fields);
                    break;
                case EnumSymbol @enum:
                    shape["kind"] = "enum";
                    var variants = new JsonObject();
                    foreach (var variant in @enum.Variants)
                        variants[variant.Name] = Fields(variant.Fields);
                    shape["variants"] = variants;
                    break;
            }
            described[key] = shape;
        }
        return new JsonObject { ["name"] = name, ["calls"] = calls, ["types"] = described };
    }

    private static IEnumerable<FieldSymbol> FieldsOf(TypeSymbol definition) => definition switch
    {
        RecordSymbol record => record.Fields,
        EnumSymbol @enum => @enum.Variants.SelectMany(v => v.Fields),
        _ => [],
    };

    /// <summary>Each field's type, and <c>" = default"</c> after one that has a default, so callers may leave it out.</summary>
    private static JsonObject Fields(IEnumerable<FieldSymbol> fields)
    {
        var shape = new JsonObject();
        foreach (var field in fields)
            shape[field.Name] = field.Default is null ? field.Type.ToString() : $"{field.Type} = default";
        return shape;
    }

    /// <summary>The recorded text: indented, its keys in a stable order, ending with a newline.</summary>
    public static string Write(JsonObject shape) =>
        shape.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
            .ReplaceLineEndings("\n") + "\n";

    /// <summary>
    /// What in <paramref name="now"/> breaks callers built against <paramref name="before"/>: a call removed, an input
    /// they don't send, an answer of another type; a field or variant a side can't read; a type changed.
    /// </summary>
    public static ImmutableArray<string> Breaking(JsonObject before, JsonObject now) => Breaking(before, now, replayed: false);

    /// <summary>
    /// The same, for a contract whose "callers" are instances already running, replaying what they recorded (a
    /// workflow's steps, keyed by their place: <c>2: activity shop.Charge</c>): said so.
    /// </summary>
    public static ImmutableArray<string> Breaking(JsonObject before, JsonObject now, bool replayed)
    {
        var callers = replayed ? "instances already running" : "callers built before";
        var breaks = new List<string>();
        var oldCalls = before["calls"] as JsonObject ?? new JsonObject();
        var newCalls = now["calls"] as JsonObject ?? new JsonObject();
        foreach (var (key, oldCall) in oldCalls)
        {
            var name = Text(oldCall?["name"]) ?? key;
            if (newCalls[key] is not JsonObject newCall)
            {
                breaks.Add(replayed
                    ? $"step {key} is gone or has moved, but {callers} recorded it there, and would replay differently; add new steps after the old ones, or make this a new version (`version 2`), keeping the old code for them (`workflow FulfilV1 as Fulfil version 1`)"
                    : $"nothing answers {(name == key ? key : $"{key} ({name})")} any more, but {callers} still send it");
                continue;
            }
            var oldInputs = oldCall?["inputs"] as JsonObject ?? new JsonObject();
            foreach (var (input, type) in newCall["inputs"] as JsonObject ?? new JsonObject())
            {
                var typeText = Text(type) ?? "";
                if (oldInputs[input] is not { } oldType)
                {
                    if (!IsOptional(typeText))
                        breaks.Add($"{name} now needs '{input}' ({typeText}), which {callers} don't send; make it optional");
                }
                else if (Text(oldType) != typeText)
                    breaks.Add($"{name} takes '{input}' as {typeText}, but {callers} send {Text(oldType)}");
            }
            if (Text(oldCall?["output"]) != Text(newCall["output"]))
                breaks.Add($"{name} answers {Text(newCall["output"])}, but {callers} read {Text(oldCall?["output"])}");
        }
        breaks.AddRange(BreakingTypes(before, now, callers));
        return [.. breaks];
    }

    /// <summary>
    /// What in <paramref name="now"/>'s records, enums and newtypes breaks <paramref name="callers"/> of
    /// <paramref name="before"/> (<c>"events saved already"</c>, for a contract of a plugin's own): a field or variant
    /// a side can't read, a type changed.
    /// </summary>
    public static ImmutableArray<string> BreakingTypes(JsonObject before, JsonObject now, string callers)
    {
        var breaks = new List<string>();
        var newTypes = now["types"] as JsonObject ?? new JsonObject();
        foreach (var (typeName, oldNode) in before["types"] as JsonObject ?? new JsonObject())
        {
            // A type no longer sent: whatever used it changed, and that's reported there.
            if (oldNode is not JsonObject oldType || newTypes[typeName] is not JsonObject newType)
                continue;
            var sent = new[] { Text(oldType["sent"]), Text(newType["sent"]) };
            bool request = sent.Any(s => s is Request or "both"), response = sent.Any(s => s is Response or "both");
            var kind = Text(newType["kind"]);
            if (Text(oldType["kind"]) != kind)
            {
                breaks.Add($"{typeName} is now a{(kind is "enum" ? "n" : "")} {kind}, not a{(Text(oldType["kind"]) is "enum" ? "n" : "")} {Text(oldType["kind"])}");
                continue;
            }
            switch (kind)
            {
                case "newtype" when Text(oldType["value"]) != Text(newType["value"]):
                    breaks.Add($"{typeName} is now a {Text(newType["value"])}, not a {Text(oldType["value"])}");
                    break;
                case "record":
                    CompareFields(typeName, oldType["fields"] as JsonObject ?? new JsonObject(), newType["fields"] as JsonObject ?? new JsonObject(), request, response, breaks, callers);
                    break;
                case "enum":
                    var oldVariants = oldType["variants"] as JsonObject ?? new JsonObject();
                    var newVariants = newType["variants"] as JsonObject ?? new JsonObject();
                    foreach (var (variant, fields) in oldVariants)
                    {
                        if (newVariants[variant] is JsonObject newFields)
                            CompareFields($"{typeName}.{variant}", fields as JsonObject ?? new JsonObject(), newFields, request, response, breaks, callers);
                        else if (request)
                            breaks.Add($"{typeName}.{variant} is gone, but {callers} may still send it");
                    }
                    foreach (var (variant, _) in newVariants)
                    {
                        if (response && oldVariants[variant] is null)
                            breaks.Add($"{typeName} has a new variant, {variant}, which {callers} can't read");
                    }
                    break;
            }
        }
        return [.. breaks];
    }

    /// <summary>
    /// A field callers send (<paramref name="request"/>) can't become needed unless it may be left out; one they read
    /// (<paramref name="response"/>) can't go unless it could already be missing; and no field can change its type.
    /// </summary>
    private static void CompareFields(string owner, JsonObject before, JsonObject now, bool request, bool response, List<string> breaks, string callers)
    {
        foreach (var (field, oldNode) in before)
        {
            var oldText = Text(oldNode) ?? "";
            if (now[field] is not { } newNode)
            {
                if (response && !IsOptional(oldText))
                    breaks.Add($"{owner}'s field '{field}' is gone, but {callers} need it");
                continue;
            }
            var newText = Text(newNode) ?? "";
            if (TypeOf(oldText) != TypeOf(newText))
                breaks.Add($"{owner}'s field '{field}' is now {TypeOf(newText)}, not {TypeOf(oldText)}");
            else if (request && IsOptional(oldText) && !IsOptional(newText))
                breaks.Add($"{owner}'s field '{field}' no longer has a default, but {callers} may leave it out");
        }
        foreach (var (field, newNode) in now)
        {
            if (request && before[field] is null && !IsOptional(Text(newNode) ?? ""))
                breaks.Add($"{owner} has a new field, '{field}', which {callers} don't send; give it a default, or make it optional");
        }
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string TypeOf(string field) => field.EndsWith(" = default", StringComparison.Ordinal) ? field[..^" = default".Length] : field;

    /// <summary>An Option, or a field with a default: something a caller may leave out.</summary>
    private static bool IsOptional(string field) => field.EndsWith('?') || field.EndsWith(" = default", StringComparison.Ordinal);
}
