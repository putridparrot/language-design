using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Piper.Binding;
using Piper.Syntax;
using Piper.Text;

namespace Piper.LanguageServer;

/// <summary>
/// Signature help: inside a call's parentheses, the signatures of what's being called (each overload of a .NET method),
/// with the parameter for the argument at the cursor highlighted.
/// </summary>
public static class SignatureHelp
{
    public static JsonNode? Help(AnalysisResult analysis, SourceText source, int position)
    {
        if (analysis.Units.FirstOrDefault(u => u.Source == source) is not { } unit)
            return null;
        var call = SyntaxWalker.DescendantsAndSelf(unit)
            .OfType<CallExpressionSyntax>()
            .Select(c => (Call: c, Open: OpenParenthesis(source, c)))
            .Where(c => c.Open >= 0 && c.Open < position && position <= ArgumentsEnd(source, c.Call))
            .OrderBy(c => c.Call.Span.Length)
            .FirstOrDefault();
        if (call.Call is null)
            return null;

        var signatures = Signatures(analysis, source, call.Call.Callee).OrderBy(s => s.Parameters).ToList();
        if (signatures.Count == 0)
            return null;
        int argument = ArgumentIndex(source, call.Open + 1, position);
        int active = Math.Max(0, signatures.FindIndex(s => s.Parameters > argument));

        var array = new JsonArray();
        foreach (var signature in signatures)
            array.Add(signature.Json);
        return new JsonObject { ["signatures"] = array, ["activeSignature"] = active, ["activeParameter"] = argument };
    }

    /// <summary>The offset of the call's <c>(</c>, or -1.</summary>
    private static int OpenParenthesis(SourceText source, CallExpressionSyntax call)
    {
        int i = call.Callee.Span.End;
        while (i < source.Length && char.IsWhiteSpace(source[i]))
            i++;
        return i < source.Length && source[i] == '(' ? i : -1;
    }

    /// <summary>
    /// Where the cursor stops being in the arguments: before the <c>)</c>, or, while it's still unwritten, after any
    /// spaces and commas following the last argument.
    /// </summary>
    private static int ArgumentsEnd(SourceText source, CallExpressionSyntax call)
    {
        if (source[call.Span.End - 1] == ')')
            return call.Span.End - 1;
        int end = call.Span.End;
        while (end < source.Length && (char.IsWhiteSpace(source[end]) || source[end] == ','))
            end++;
        return end;
    }

    /// <summary>The number of commas between <paramref name="start"/> and <paramref name="position"/> outside brackets and literals.</summary>
    private static int ArgumentIndex(SourceText source, int start, int position)
    {
        int index = 0, depth = 0;
        for (int i = start; i < position && i < source.Length; i++)
        {
            switch (source[i])
            {
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    depth--;
                    break;
                case ',' when depth == 0:
                    index++;
                    break;
                case '"' or '\'':
                    char quote = source[i];
                    for (i++; i < position && i < source.Length && source[i] != quote; i++)
                    {
                        if (source[i] == '\\')
                            i++;
                    }
                    break;
            }
        }
        return index;
    }

    private sealed record Signature(JsonObject Json, int Parameters);

    private static IEnumerable<Signature> Signatures(AnalysisResult analysis, SourceText source, ExpressionSyntax callee)
    {
        var name = callee switch
        {
            NameExpressionSyntax n => n.Identifier,
            MemberAccessExpressionSyntax m => m.Member,
            _ => (Token?)null,
        };
        if (name is not { } token)
            return [];

        var symbol = analysis.Semantic.OccurrenceAt(source, token.Span.Start) is { } occurrence && occurrence.Span == token.Span ? occurrence.Symbol : null;
        switch (symbol)
        {
            case FunctionSymbol function:
                return [Function(function)];
            case RecordSymbol record:
                return [Build($"{record.Name}{SymbolDisplay.TypeParameters(record.TypeParameters)}", record.Fields.Select(f => $"{f.Name}: {f.Type}{(f.Default is { } d ? $" = {d.Text}" : "")}"), "")];
            case VariantSymbol variant:
                return [Build($"{variant.Enum.Name}.{variant.Name}", variant.Fields.Select(f => $"{f.Name}: {f.Type}"), "")];
            case TypeSymbol type when type.OriginalDefinition is not NamedTypeDefinition && ClrTypes.ToClr(type) is { } clr:
                return clr.GetConstructors().Select(c => Clr(clr.Name.Split('`')[0], c.GetParameters(), null));
        }

        // A .NET method: the receiver is a type (for a static method) or a value.
        if (callee is not MemberAccessExpressionSyntax member)
            return [];
        var target = member.Target.Span;
        if (analysis.Semantic.OccurrenceAt(source, target.End) is { Symbol: TypeSymbol staticType } typeName && typeName.Span.End == target.End)
            return ClrMethods(staticType, token.Text, instance: false);
        if (analysis.Semantic.ExpressionEndingAt(source, target.End) is { Type: var valueType } && valueType != TypeSymbol.Error)
            return ClrMethods(valueType, token.Text, instance: true);
        return [];
    }

    private static Signature Function(FunctionSymbol function)
    {
        var owner = function is MethodSymbol method ? $"{method.ContainingType}." : "";
        var result = function.ReturnType == TypeSymbol.Unit ? "" : $" => {function.ReturnType}";
        var signature = Build(
            $"fn {owner}{function.Name}{SymbolDisplay.TypeParameters(function.TypeParameters)}",
            function.Parameters.Select(p => $"{p.Name}: {p.Type}{(p.Default is { } d ? $" = {d.Text}" : "")}"),
            result);
        // The doc comment and preconditions show under the signature, where the arguments are being written.
        var notes = function.Preconditions.Select(c => $"requires {c.Text}").Prepend(function.Documentation).OfType<string>().ToList();
        if (notes.Count > 0)
            signature.Json["documentation"] = string.Join("\n\n", notes);
        var parameters = signature.Json["parameters"]!.AsArray();
        for (int i = 0; i < parameters.Count && i < function.Parameters.Length; i++)
        {
            if (function.Parameters[i].Documentation is { } parameterDocumentation)
                parameters[i]!["documentation"] = parameterDocumentation;
        }
        return signature;
    }

    private static IEnumerable<Signature> ClrMethods(TypeSymbol type, string name, bool instance)
    {
        if (type.OriginalDefinition is NamedTypeDefinition || ClrTypes.ToClr(type) is not { ContainsGenericParameters: false } clr)
            return [];
        var flags = BindingFlags.Public | BindingFlags.FlattenHierarchy | (instance ? BindingFlags.Instance : BindingFlags.Static);
        var methods = clr.IsInterface ? clr.GetMethods(flags).Concat(clr.GetInterfaces().SelectMany(i => i.GetMethods(flags))) : clr.GetMethods(flags);
        return methods
            .Where(m => m.Name == name && !m.IsSpecialName)
            .Select(m => Clr($"{clr.Name.Split('`')[0]}.{m.Name}", m.GetParameters(), m.ReturnType));
    }

    private static Signature Clr(string name, ParameterInfo[] parameters, Type? returnType)
    {
        var result = returnType is null || returnType == typeof(void) ? "" : $" => {TypeName(returnType)}";
        return Build($"fn {name}", parameters.Select(p => $"{(p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "")}{p.Name}: {TypeName(p.ParameterType)}"), result);
    }

    /// <summary>A .NET type as Piper writes it: <c>int</c>, <c>List&lt;string&gt;</c>, <c>char[]</c>.</summary>
    private static string TypeName(Type type)
    {
        if (type.IsByRef)
            return TypeName(type.GetElementType()!);
        if (type.IsArray)
            return $"{TypeName(type.GetElementType()!)}[{new string(',', type.GetArrayRank() - 1)}]";
        if (ClrTypes.BuiltinTypes.FirstOrDefault(b => b.Value == type).Key is { } builtin)
            return builtin.Name;
        if (type.IsGenericType)
            return $"{type.Name.Split('`')[0]}<{string.Join(", ", type.GetGenericArguments().Select(TypeName))}>";
        return type.Name;
    }

    private static Signature Build(string prefix, IEnumerable<string> parameters, string suffix)
    {
        var label = new StringBuilder(prefix).Append('(');
        var ranges = new JsonArray();
        foreach (var parameter in parameters)
        {
            if (ranges.Count > 0)
                label.Append(", ");
            ranges.Add(new JsonObject { ["label"] = new JsonArray(label.Length, label.Length + parameter.Length) });
            label.Append(parameter);
        }
        label.Append(')').Append(suffix);
        return new Signature(new JsonObject { ["label"] = label.ToString(), ["parameters"] = ranges }, ranges.Count);
    }
}
