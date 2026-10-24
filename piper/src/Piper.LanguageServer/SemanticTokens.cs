using System.Text.Json.Nodes;
using Piper.Binding;
using Piper.Text;

namespace Piper.LanguageServer;

/// <summary>
/// Semantic highlighting: colours each name by what the binder found it to be (a variant, a trait, a parameter, …),
/// which the grammar can't tell from the text.
/// </summary>
public static class SemanticTokens
{
    public static readonly string[] TokenTypes =
        ["namespace", "class", "struct", "enum", "interface", "typeParameter", "parameter", "variable", "property", "enumMember", "function", "method"];

    public static readonly string[] TokenModifiers = ["declaration", "readonly", "static", "defaultLibrary"];

    private const int Declaration = 1, Readonly = 2, Static = 4, DefaultLibrary = 8;

    public static JsonObject Legend() => new()
    {
        ["tokenTypes"] = new JsonArray([.. TokenTypes.Select(t => JsonValue.Create(t))]),
        ["tokenModifiers"] = new JsonArray([.. TokenModifiers.Select(m => JsonValue.Create(m))]),
    };

    /// <summary>The tokens of <paramref name="source"/>, encoded as LSP's relative five-integer groups.</summary>
    public static JsonObject Tokens(AnalysisResult analysis, SourceText source)
    {
        var data = new JsonArray();
        int previousLine = 0, previousColumn = 0, previousEnd = 0;
        var occurrences = analysis.Semantic.Occurrences
            .Where(o => o.File == source)
            .OrderBy(o => o.Span.Start)
            .ThenBy(o => o.Span.Length);
        foreach (var occurrence in occurrences)
        {
            // Tokens can't overlap, and a name is only recorded once per symbol, but may be recorded twice.
            if (occurrence.Span.Start < previousEnd || Classify(occurrence) is not var (type, modifiers))
                continue;
            var (line, column) = source.GetLineColumn(occurrence.Span.Start);
            line--;
            column--;
            data.Add(line - previousLine);
            data.Add(line == previousLine ? column - previousColumn : column);
            data.Add(occurrence.Span.Length);
            data.Add(Array.IndexOf(TokenTypes, type));
            data.Add(modifiers);
            (previousLine, previousColumn, previousEnd) = (line, column, occurrence.Span.End);
        }
        return new JsonObject { ["data"] = data };
    }

    private static (string Type, int Modifiers)? Classify(SymbolOccurrence occurrence)
    {
        int declaration = occurrence.IsDeclaration ? Declaration : 0;
        (string, int)? result = occurrence.Symbol switch
        {
            NamespaceSymbol => ("namespace", 0),
            VariableSymbol { Kind: VariableKind.This } => null,
            VariableSymbol { Kind: VariableKind.Parameter } => ("parameter", 0),
            VariableSymbol variable => ("variable", variable.IsMutable ? 0 : Readonly),
            FieldSymbol => ("property", Readonly),
            VariantSymbol => ("enumMember", 0),
            MethodSymbol method => ("method", method.IsStatic ? Static : 0),
            FunctionSymbol => ("function", 0),
            RecordSymbol => ("struct", 0),
            EnumSymbol => ("enum", 0),
            TraitSymbol => ("interface", 0),
            TypeParameterSymbol => ("typeParameter", 0),
            // Builtin types are keywords, which the grammar already colours.
            TypeSymbol type when TypeSymbol.Builtins.Contains(type) => null,
            TypeSymbol type when ClrTypes.ToClr(type) is { } clr => (clr.IsInterface ? "interface" : clr.IsEnum ? "enum" : clr.IsValueType ? "struct" : "class", DefaultLibrary),
            _ => null,
        };
        return result is var (tokenType, modifiers) ? (tokenType, modifiers | declaration) : null;
    }
}
