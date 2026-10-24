using Piper.Binding;

namespace Piper.LanguageServer;

/// <summary>Describes symbols in Piper syntax, for hovers and completion details: <c>fn area() =&gt; double</c>.</summary>
public static class SymbolDisplay
{
    public static string Describe(Symbol symbol) => symbol switch
    {
        VariableSymbol { Kind: VariableKind.Parameter } v => $"{v.Name}: {v.Type}",
        VariableSymbol { Kind: VariableKind.This } v => $"this: {v.Type}",
        VariableSymbol v => $"{(v.IsMutable ? "mut" : "let")} {v.Name}: {v.Type}",
        MethodSymbol m => Function(m, $"{m.ContainingType}."),
        FunctionSymbol f => Function(f, ""),
        RecordSymbol r => $"record {r.Name}{TypeParameters(r.TypeParameters)}({Fields(r.Fields)}){Requires(r.Invariants)}",
        EnumSymbol e => $"enum {e.Name}{TypeParameters(e.TypeParameters)} {{ {string.Join(", ", e.Variants.Select(Variant))} }}",
        TraitSymbol t => Trait(t),
        VariantSymbol v => $"{v.Enum.Name}.{Variant(v)}",
        FieldSymbol f => $"{f.Name}: {f.Type}",
        TypeParameterSymbol p => p.Constraints.IsEmpty ? $"type parameter {p.Name}" : $"type parameter {p.Name}: {string.Join(" + ", p.Constraints)}",
        ClrTypeSymbol clr => $".NET {Kind(clr.Type)} {clr.Type.FullName ?? clr.Name}",
        TypeSymbol type when ClrTypes.ToClr(type) is { } clr => $"{type.Name} (.NET {clr.FullName})",
        TypeSymbol type => type.Name,
        NamespaceSymbol n => $"namespace {n.FullName}",
        _ => symbol.Name,
    };

    private static string Function(FunctionSymbol function, string owner)
    {
        var modifiers = (function is MethodSymbol { IsStatic: true } ? "static " : "") + (function.IsAsync ? "async " : "");
        var parameters = string.Join(", ", function.Parameters.Select(p => $"{p.Name}: {p.Type}{(p.Default is { } d ? $" = {d.Text}" : "")}"));
        var result = function.ReturnType == TypeSymbol.Unit ? "" : $" => {function.ReturnType}";
        if (function is MethodSymbol { IsOperator: true })
            return $"{modifiers}operator {function.Name}({parameters}){result}{Requires(function.Preconditions)}";
        return $"{modifiers}fn {owner}{function.Name}{TypeParameters(function.TypeParameters)}({parameters}){result}{Requires(function.Preconditions)}";
    }

    /// <summary>A function's or record's `requires` clauses, one per line.</summary>
    private static string Requires(IEnumerable<BoundContract> contracts) => string.Concat(contracts.Select(c => $"\n    requires {c.Text}"));

    private static string Trait(TraitSymbol trait)
    {
        var parents = trait.Parents.IsEmpty ? "" : $": {string.Join(", ", trait.Parents)}";
        var methods = trait.Methods.Select(m => "    " + Function(m, "") + (m.IsAbstract ? ";" : " = …;"));
        return $"trait {trait.Name}{TypeParameters(trait.TypeParameters)}{parents} {{\n{string.Join("\n", methods)}\n}}";
    }

    private static string Variant(VariantSymbol variant) => variant.Fields.IsEmpty ? variant.Name : $"{variant.Name}({Fields(variant.Fields)})";

    private static string Fields(IEnumerable<FieldSymbol> fields) =>
        string.Join(", ", fields.Select(f => $"{f.Name}: {f.Type}{(f.Default is { } d ? $" = {d.Text}" : "")}"));

    public static string TypeParameters(IReadOnlyCollection<TypeParameterSymbol> parameters) =>
        parameters.Count == 0
            ? ""
            : $"<{string.Join(", ", parameters.Select(p => p.Constraints.IsEmpty ? p.Name : $"{p.Name}: {string.Join(" + ", p.Constraints)}"))}>";

    private static string Kind(Type type) =>
        type.IsInterface ? "interface" : type.IsEnum ? "enum" : type.IsValueType ? "struct" : "class";
}
