using System.Reflection;
using System.Text.Json.Nodes;
using Piper.Binding;
using Piper.Text;

namespace Piper.LanguageServer;

/// <summary>
/// Completion: after a dot, the members of the value or type before it; elsewhere, the names in scope, the program's
/// top-level declarations and the keywords.
/// </summary>
public static class Completion
{
    private static readonly string[] Keywords =
    [
        "fn", "let", "mut", "if", "else", "while", "for", "in", "return", "break", "continue", "record", "enum", "match",
        "trait", "extend", "static", "this", "null", "is", "namespace", "using", "private", "try", "catch", "finally",
        "defer", "throw", "out", "true", "false", "async", "await", "yield", "ref",
    ];

    // LSP CompletionItemKind values.
    private const int MethodKind = 2, FunctionKind = 3, FieldKind = 5, VariableKind = 6, ClassKind = 7, InterfaceKind = 8,
        ModuleKind = 9, PropertyKind = 10, EnumKind = 13, KeywordKind = 14, EnumMemberKind = 20, StructKind = 22, EventKind = 23,
        TypeParameterKind = 25;

    public static JsonNode Items(AnalysisResult analysis, SourceText source, int position)
    {
        // Back over the identifier being typed, to a dot if there is one.
        int start = position;
        while (start > 0 && (char.IsLetterOrDigit(source[start - 1]) || source[start - 1] == '_'))
            start--;
        var items = start > 0 && source[start - 1] == '.'
            ? MemberItems(analysis, source, start - 1)
            : ScopeItems(analysis, source, position);

        var array = new JsonArray();
        foreach (var (label, kind, detail, documentation) in items.DistinctBy(i => i.Label).OrderBy(i => i.Label, StringComparer.Ordinal))
        {
            var item = new JsonObject { ["label"] = label, ["kind"] = kind };
            if (detail is not null)
                item["detail"] = detail;
            if (documentation is not null)
                item["documentation"] = new JsonObject { ["kind"] = "markdown", ["value"] = documentation };
            array.Add(item);
        }
        if (start > 0 && source[start - 1] == '.')
            AddOptionalItems(analysis, source, start - 1, position, array);
        return array;
    }

    // LSP CompletionItemKind and InsertTextFormat values.
    private const int SnippetKind = 15, SnippetFormat = 2;

    /// <summary>
    /// After <c>i.</c> on an optional, which has no members of its own: the wrapped type's members, reached with
    /// <c>?.</c> (choosing one turns the <c>.</c> into <c>?.</c>), and snippets that replace <c>i.</c> with the ways to
    /// unwrap it: <c>match</c>, <c>if … is Some</c>, <c>let … else</c> and <c>??</c>.
    /// </summary>
    private static void AddOptionalItems(AnalysisResult analysis, SourceText source, int dot, int position, JsonArray array)
    {
        if (analysis.Semantic.ExpressionEndingAt(source, dot) is not { Type: { IsOption: true } optional } receiver)
            return;

        var dotRange = PiperLanguageServer.Range(source, new TextSpan(dot, 1));
        foreach (var (label, kind, detail, documentation) in Members(analysis, optional.TypeArguments[0], instance: true).DistinctBy(i => i.Label))
        {
            var item = new JsonObject
            {
                ["label"] = label,
                ["kind"] = kind,
                ["detail"] = $"{detail} (through ?.)",
                ["additionalTextEdits"] = new JsonArray(new JsonObject { ["range"] = dotRange.DeepClone(), ["newText"] = "?." }),
            };
            if (documentation is not null)
                item["documentation"] = new JsonObject { ["kind"] = "markdown", ["value"] = documentation };
            array.Add(item);
        }

        // The snippets replace the receiver and the dot, so they filter on that text too.
        var text = source.ToString(receiver.Span);
        var snippetText = text.Replace("\\", "\\\\").Replace("$", "\\$").Replace("}", "\\}");
        var whole = PiperLanguageServer.Range(source, TextSpan.FromBounds(receiver.Span.Start, position));
        (string Label, string Snippet, string Detail)[] snippets =
        [
            ("match", $"match {snippetText} {{\n\tSome(${{1:value}}) => $2,\n\tNone => $0,\n}}", "match on Some and None"),
            ("ifSome", $"if {snippetText} is Some(${{1:value}}) {{\n\t$0\n}}", "run code when there's a value"),
            ("letElse", $"let Some(${{1:value}}) = {snippetText} else {{ ${{2:return;}} }};", "unwrap the value, or leave"),
            ("orDefault", $"{snippetText} ?? ${{1:fallback}}", "the value, or a fallback when there's none"),
        ];
        foreach (var (label, snippet, detail) in snippets)
        {
            array.Add(new JsonObject
            {
                ["label"] = label,
                ["kind"] = SnippetKind,
                ["detail"] = detail,
                ["filterText"] = $"{text}.{label}",
                ["insertTextFormat"] = SnippetFormat,
                ["textEdit"] = new JsonObject { ["range"] = whole.DeepClone(), ["newText"] = snippet },
            });
        }
    }

    // Names

    private static IEnumerable<(string Label, int Kind, string? Detail, string? Documentation)> ScopeItems(AnalysisResult analysis, SourceText source, int position)
    {
        for (var scope = analysis.Semantic.ScopeAt(source, position); scope is not null; scope = scope.Parent)
        {
            foreach (var symbol in scope.Symbols)
                yield return Item(symbol);
        }

        // Top-level declarations are reached through namespace lookups, which scopes can't enumerate.
        foreach (var type in analysis.Program.Types.Where(t => !t.IsPrivate || t.DeclaringFile == source))
            yield return Item(type);
        foreach (var function in analysis.Program.Functions.Select(f => f.Symbol).Where(f => !f.IsPrivate || f.DeclaringFile == source))
            yield return Item(function);
        foreach (var type in TypeSymbol.Builtins.Where(t => t != TypeSymbol.Unit))
            yield return (type.Name, KeywordKind, null, null);
        foreach (var keyword in Keywords)
            yield return (keyword, KeywordKind, null, null);
    }

    private static (string Label, int Kind, string? Detail, string? Documentation) Item(Symbol symbol) => (symbol.Name, KindOf(symbol), Detail(symbol), symbol.Documentation);

    private static string? Detail(Symbol symbol) => symbol is TraitSymbol trait ? $"trait {trait.Name}" : SymbolDisplay.Describe(symbol);

    private static int KindOf(Symbol symbol) => symbol switch
    {
        VariableSymbol => VariableKind,
        MethodSymbol => MethodKind,
        FunctionSymbol => FunctionKind,
        RecordSymbol => StructKind,
        EnumSymbol => EnumKind,
        TraitSymbol => InterfaceKind,
        VariantSymbol => EnumMemberKind,
        FieldSymbol => FieldKind,
        TypeParameterSymbol => TypeParameterKind,
        NamespaceSymbol => ModuleKind,
        _ => ClassKind,
    };

    // Members

    private static IEnumerable<(string Label, int Kind, string? Detail, string? Documentation)> MemberItems(AnalysisResult analysis, SourceText source, int dot)
    {
        // `Shape.` names a type: its variants and static methods. `shape.` is a value: its fields and instance methods.
        if (analysis.Semantic.OccurrenceAt(source, dot) is { Span.End: var end, Symbol: TypeSymbol type } && end == dot)
            return Members(analysis, type, instance: false);
        if (analysis.Semantic.ExpressionEndingAt(source, dot) is { Type: var valueType } && valueType != TypeSymbol.Error)
            return Members(analysis, valueType, instance: true);
        return [];
    }

    private static IEnumerable<(string Label, int Kind, string? Detail, string? Documentation)> Members(AnalysisResult analysis, TypeSymbol type, bool instance)
    {
        var definition = type.OriginalDefinition;
        if (instance && definition is RecordSymbol record)
        {
            foreach (var field in record.Fields)
                yield return (field.Name, FieldKind, $"{field.Name}: {TypeSubstitution.FieldType(field, type)}", null);
        }
        if (instance && type is TupleTypeSymbol tuple)
        {
            for (int i = 0; i < tuple.ElementTypes.Length; i++)
                yield return (tuple.ElementNames[i] ?? $"Item{i + 1}", FieldKind, tuple.ElementTypes[i].ToString(), null);
        }
        if (!instance && definition is EnumSymbol @enum)
        {
            foreach (var variant in @enum.Variants)
                yield return Item(variant);
        }

        foreach (var method in PiperMethods(analysis, type).Where(m => m.IsStatic != instance))
            yield return Item(method);

        // Option and Result are structs in Piper.Runtime, but their .NET members aren't part of Piper.
        if (definition is not NamedTypeDefinition && ClrTypes.ToClr(type) is { ContainsGenericParameters: false } clr)
        {
            foreach (var member in ClrMembers(clr, instance))
                yield return member;
        }
    }

    /// <summary>A type's extend methods and those of the traits it implements (or, for a type parameter, is bounded by).</summary>
    private static IEnumerable<MethodSymbol> PiperMethods(AnalysisResult analysis, TypeSymbol type)
    {
        var definition = type.OriginalDefinition;
        if (definition is NamedTypeDefinition named)
        {
            foreach (var method in named.Methods)
                yield return method;
        }
        foreach (var method in analysis.Program.Methods.Select(m => (MethodSymbol)m.Symbol).Where(m => m.ContainingType == definition && definition is not NamedTypeDefinition))
            yield return method;

        IEnumerable<TypeSymbol> traits = type switch
        {
            TypeParameterSymbol parameter => parameter.Constraints,
            _ when type.IsTrait => [type],
            _ when definition is NamedTypeDefinition named2 => named2.Traits,
            _ => [],
        };
        traits = traits.Concat(analysis.Program.ExternalConformances.Where(c => c.Target == definition).Select(c => c.Trait));
        var seen = new HashSet<TraitSymbol>();
        var pending = new Queue<TypeSymbol>(traits);
        while (pending.TryDequeue(out var trait))
        {
            if (trait.TraitDefinition is not { } traitDefinition || !seen.Add(traitDefinition))
                continue;
            foreach (var method in traitDefinition.Methods)
                yield return method;
            foreach (var parent in traitDefinition.Parents)
                pending.Enqueue(parent);
        }
    }

    private static IEnumerable<(string Label, int Kind, string? Detail, string? Documentation)> ClrMembers(Type? type, bool instance)
    {
        if (type is null)
            yield break;
        var flags = BindingFlags.Public | BindingFlags.FlattenHierarchy | (instance ? BindingFlags.Instance : BindingFlags.Static);
        var members = type.IsInterface
            ? type.GetMembers(flags).Concat(type.GetInterfaces().SelectMany(i => i.GetMembers(flags)))
            : type.GetMembers(flags);
        foreach (var member in members)
        {
            switch (member)
            {
                case MethodInfo { IsSpecialName: false } method when method.DeclaringType != typeof(object) || method.Name is "ToString" or "Equals" or "GetHashCode":
                    yield return (method.Name, MethodKind, $"{method.ReturnType.Name} {method.Name}({string.Join(", ", method.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})", null);
                    break;
                case PropertyInfo property when property.GetIndexParameters().Length == 0:
                    yield return (property.Name, PropertyKind, $"{property.PropertyType.Name} {property.Name}", null);
                    break;
                case FieldInfo field:
                    yield return (field.Name, FieldKind, $"{field.FieldType.Name} {field.Name}", null);
                    break;
                case EventInfo @event:
                    yield return (@event.Name, EventKind, $"event {@event.EventHandlerType?.Name} {@event.Name}", null);
                    break;
            }
        }

        // LINQ, as Piper sees it after `using System.Linq;`.
        if (instance && type.GetInterfaces().Append(type).Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)))
        {
            foreach (var method in typeof(Enumerable).GetMethods(BindingFlags.Public | BindingFlags.Static))
                yield return (method.Name, MethodKind, "(System.Linq extension)", null);
        }
    }
}
