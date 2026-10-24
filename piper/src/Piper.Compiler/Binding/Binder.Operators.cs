using Piper.Syntax;

namespace Piper.Binding;

public sealed partial class Binder
{
    /// <summary>
    /// Checks an <c>operator</c> declared in an extend block and adds it to its type. Operators belong to records and
    /// enums declared in Piper, since C# declares them in the type. The instance form takes <c>this</c> as its left (or
    /// only) operand; the static form takes every operand as a parameter, one of them of the type itself.
    /// </summary>
    private bool RegisterOperator(TypeSymbol target, MethodSymbol method, FunctionDeclarationSyntax syntax)
    {
        var name = syntax.Name;
        if (target is not (RecordSymbol or EnumSymbol { BuiltinKind: BuiltinEnumKind.None }) || target is not NamedTypeDefinition definition)
        {
            _diagnostics.ReportInvalidOperator(name.Span, $"Operators can only be declared for records and enums declared in Piper, not '{target}'.");
            return false;
        }
        if (!method.TypeParameters.IsEmpty || syntax.Async is not null)
        {
            _diagnostics.ReportInvalidOperator(name.Span, "An operator can't be generic or async.");
            return false;
        }

        var operands = OperandTypes(method);
        bool unaryAllowed = name.Kind == TokenKind.Minus;
        if (operands.Count is not 2 && !(operands.Count == 1 && unaryAllowed))
        {
            var form = method.IsStatic ? $"{(unaryAllowed ? "one or two parameters" : "two parameters")} when static" : $"{(unaryAllowed ? "no parameter or one" : "one parameter")} besides 'this'";
            _diagnostics.ReportInvalidOperator(name.Span, $"'{name.Text}' takes {form}.");
            return false;
        }
        var self = definition is GenericTypeDefinition generic ? generic.SelfType : definition;
        if (method.IsStatic && !operands.Contains(self))
        {
            _diagnostics.ReportInvalidOperator(name.Span, $"A static operator needs a parameter of type '{self}'.");
            return false;
        }
        if (method.ReturnType == TypeSymbol.Unit)
        {
            _diagnostics.ReportInvalidOperator(name.Span, "An operator must return a value.");
            return false;
        }
        if (definition.Methods.Any(m => m.IsOperator && m.Name == method.Name && OperandTypes(m).SequenceEqual(operands)))
        {
            _diagnostics.ReportDuplicateDeclaration(name.Span, $"operator {name.Text}");
            return false;
        }
        definition.AddMethod(method);
        return true;
    }

    /// <summary>An operator's operand types: <c>this</c> and the parameters, or for a static operator just the parameters.</summary>
    private static List<TypeSymbol> OperandTypes(MethodSymbol method) =>
        [.. method.IsStatic ? [] : new[] { method.This!.Type }, .. method.Parameters.Select(p => p.Type)];

    /// <summary>
    /// A user-defined operator for <paramref name="operands"/>: one declared by an operand's type, in Piper or (as
    /// C#'s `op_GreaterThan` and so on) in .NET, whose operand types they convert to, preferring one they match
    /// exactly. Null if there's none.
    /// </summary>
    private (IReadOnlyList<TypeSymbol> OperandTypes, TypeSymbol ResultType)? FindUserOperator(string symbol, IReadOnlyList<BoundExpression> operands)
    {
        var candidates = new List<(IReadOnlyList<TypeSymbol> OperandTypes, TypeSymbol ResultType)>();
        foreach (var type in operands.Select(o => o.Type).Distinct())
        {
            if (type.OriginalDefinition is NamedTypeDefinition definition)
                candidates.AddRange(PiperOperators(definition, type, symbol));
            else if (IsClrBacked(type))
                candidates.AddRange(ClrOperators(type, symbol, operands.Count));
        }
        var applicable = candidates
            .Where(c => c.OperandTypes.Count == operands.Count && operands.Zip(c.OperandTypes).All(pair => IsConvertible(pair.First.Type, pair.Second)))
            .ToList();
        if (applicable.Count == 0)
            return null;
        return applicable.FirstOrDefault(c => operands.Zip(c.OperandTypes).All(pair => pair.First.Type == pair.Second)) is { OperandTypes: not null } exact
            ? exact
            : applicable[0];
    }

    /// <summary>The operators a Piper record or enum declares, at <paramref name="type"/>'s type arguments (`Vector<double>`'s, declared on `Vector<T>`).</summary>
    private static IEnumerable<(IReadOnlyList<TypeSymbol>, TypeSymbol)> PiperOperators(NamedTypeDefinition definition, TypeSymbol type, string symbol)
    {
        var map = new Dictionary<TypeParameterSymbol, TypeSymbol>();
        if (type is ConstructedTypeSymbol constructed)
        {
            for (int i = 0; i < definition.TypeParameters.Length; i++)
                map[definition.TypeParameters[i]] = constructed.TypeArguments[i];
        }
        foreach (var method in definition.Methods.Where(m => m.IsOperator && m.Name == symbol))
            yield return ([.. OperandTypes(method).Select(t => TypeSubstitution.Substitute(t, map))], TypeSubstitution.Substitute(method.ReturnType, map));
    }

    /// <summary>The names C# gives the operators a .NET type can declare.</summary>
    private static string? ClrOperatorName(string symbol, int operands) => (symbol, operands) switch
    {
        ("-", 1) => "op_UnaryNegation",
        ("+", 2) => "op_Addition",
        ("-", 2) => "op_Subtraction",
        ("*", 2) => "op_Multiply",
        ("/", 2) => "op_Division",
        ("%", 2) => "op_Modulus",
        ("<", 2) => "op_LessThan",
        ("<=", 2) => "op_LessThanOrEqual",
        (">", 2) => "op_GreaterThan",
        (">=", 2) => "op_GreaterThanOrEqual",
        ("==", 2) => "op_Equality",
        ("!=", 2) => "op_Inequality",
        _ => null,
    };

    /// <summary>A .NET type's operators: `date > other` on DateOnly, `end - start` on DateTime (a TimeSpan).</summary>
    private IEnumerable<(IReadOnlyList<TypeSymbol>, TypeSymbol)> ClrOperators(TypeSymbol type, string symbol, int operands)
    {
        if (ClrOperatorName(symbol, operands) is not { } name)
            yield break;
        var (clr, map) = ClrContext(type);
        foreach (var method in clr.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static).Where(m => m.Name == name))
        {
            var parameters = method.GetParameters().Select(p => ClrTypes.FromClr(p.ParameterType, map, _option)).ToList();
            if (parameters.Count == operands && parameters.All(p => p is not null) && ClrTypes.FromClr(method.ReturnType, map, _option) is { } result)
                yield return (parameters!, result);
        }
    }
}
