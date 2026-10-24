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
    /// A user-defined operator for <paramref name="operands"/>: one declared by an operand's type, whose operand types
    /// they convert to (preferring one they match exactly). Null if there's none.
    /// </summary>
    private (MethodSymbol Operator, IReadOnlyList<TypeSymbol> OperandTypes, TypeSymbol ResultType)? FindUserOperator(string symbol, IReadOnlyList<BoundExpression> operands)
    {
        var candidates = new List<(MethodSymbol, IReadOnlyList<TypeSymbol>, TypeSymbol)>();
        foreach (var type in operands.Select(o => o.Type).Distinct())
        {
            if (type.OriginalDefinition is not NamedTypeDefinition definition)
                continue;
            // `Vector<double>`'s operators, declared on `Vector<T>`.
            var map = new Dictionary<TypeParameterSymbol, TypeSymbol>();
            if (type is ConstructedTypeSymbol constructed)
            {
                for (int i = 0; i < definition.TypeParameters.Length; i++)
                    map[definition.TypeParameters[i]] = constructed.TypeArguments[i];
            }
            foreach (var method in definition.Methods.Where(m => m.IsOperator && m.Name == symbol))
            {
                var types = OperandTypes(method).Select(t => TypeSubstitution.Substitute(t, map)).ToList();
                if (types.Count == operands.Count && operands.Zip(types).All(pair => IsConvertible(pair.First.Type, pair.Second)))
                    candidates.Add((method, types, TypeSubstitution.Substitute(method.ReturnType, map)));
            }
        }
        return candidates.Count == 0
            ? null
            : candidates.FirstOrDefault(c => operands.Zip(c.Item2).All(pair => pair.First.Type == pair.Second)) is { Item1: not null } exact
                ? exact
                : candidates[0];
    }
}
