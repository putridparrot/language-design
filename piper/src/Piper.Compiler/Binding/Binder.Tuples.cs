using System.Collections.Immutable;
using System.Reflection;
using Piper.Syntax;
using Piper.Text;

namespace Piper.Binding;

/// <summary>
/// Tuples and deconstruction, as in C#: <c>(1, "a")</c> is a <c>ValueTuple</c>, elements are read as <c>t.Item1</c> or
/// by name, and tuples, records and .NET types with a <c>Deconstruct</c> method can be taken apart with
/// <c>let (a, b) = value;</c>, <c>(a, b) = (b, a);</c>, <c>for (k, v) in dictionary</c> and <c>match t { (0, y) =&gt; … }</c>.
/// </summary>
public sealed partial class Binder
{
    private TypeSymbol BindTupleType(TupleTypeSyntax syntax)
    {
        var types = syntax.Elements.Select(e => BindValueType(e.Type, "tuple element")).ToImmutableArray();
        var names = TupleNames(syntax.Elements.Select(e => (e.Name, (string?)null)).ToList());
        return types.Contains(TypeSymbol.Error) || types.Length < 2 ? TypeSymbol.Error : TupleTypeSymbol.Create(types, names);
    }

    /// <summary>
    /// Element names from explicit names (checked: no duplicates, no <c>ItemN</c> but at position N) and, as in C#,
    /// names inferred from the elements (<c>(a, p.y)</c> names its elements a and y), dropped where they'd clash.
    /// </summary>
    private ImmutableArray<string?> TupleNames(List<(Token? Explicit, string? Inferred)> elements)
    {
        var names = new string?[elements.Count];
        for (int i = 0; i < elements.Count; i++)
        {
            if (elements[i].Explicit is not { IsMissing: false } name)
                continue;
            if (IsItemName(name.Text, out int position) && position != i + 1)
                _diagnostics.ReportReservedTupleName(name.Span, name.Text);
            else if (names.Contains(name.Text))
                _diagnostics.ReportDuplicateTupleName(name.Span, name.Text);
            else
                names[i] = name.Text;
        }

        var inferred = elements.Select(e => e.Inferred).ToList();
        for (int i = 0; i < elements.Count; i++)
        {
            if (names[i] is null && inferred[i] is { } candidate && !IsItemName(candidate, out _) &&
                !names.Contains(candidate) && inferred.Count(n => n == candidate) == 1)
            {
                names[i] = candidate;
            }
        }
        return [.. names];
    }

    private static bool IsItemName(string name, out int position)
    {
        position = 0;
        return name.StartsWith("Item") && int.TryParse(name.AsSpan(4), out position) && name == $"Item{position}";
    }

    /// <summary><c>(1, "a")</c>; elements take their types from the expected tuple type when there is one.</summary>
    private BoundExpression BindTuple(TupleExpressionSyntax syntax, TypeSymbol? expected)
    {
        var target = expected is TupleTypeSymbol tuple && tuple.ElementTypes.Length == syntax.Elements.Length ? tuple : null;
        var elements = ImmutableArray.CreateBuilder<BoundExpression>();
        for (int i = 0; i < syntax.Elements.Length; i++)
        {
            var element = syntax.Elements[i];
            var wanted = target?.ElementTypes[i];
            var bound = RequireValue(BindExpression(element.Expression, wanted), element.Expression.Span);
            // Convert here (not later as a whole) so an element can become Some(…) or widen.
            if (wanted is not null && IsConvertible(bound.Type, wanted))
                bound = Widen(bound, wanted);
            elements.Add(bound);
        }

        var names = TupleNames([.. syntax.Elements.Select(e => (e.Name, InferredTupleName(e.Expression)))]);
        if (elements.Count < 2 || elements.Any(e => e.Type == TypeSymbol.Error))
            return new BoundErrorExpression();
        return new BoundTupleExpression(elements.ToImmutable(), TupleTypeSymbol.Create([.. elements.Select(e => e.Type)], names));
    }

    private static string? InferredTupleName(ExpressionSyntax expression) => expression switch
    {
        NameExpressionSyntax { TypeArguments.IsEmpty: true, Identifier: { Kind: TokenKind.Identifier, IsMissing: false } name } => name.Text,
        MemberAccessExpressionSyntax { TypeArguments.IsEmpty: true, Member.IsMissing: false } access => access.Member.Text,
        _ => null,
    };

    /// <summary>
    /// A tuple converts to another of the same length when each element does, as C# converts implicitly: by numeric
    /// widening, to a trait, or between tuples. (An element can't become Some(…) here, as C# has no such conversion;
    /// tuple literals convert element by element instead.)
    /// </summary>
    private bool IsTupleConvertible(TupleTypeSymbol from, TupleTypeSymbol to) =>
        from.ElementTypes.Length == to.ElementTypes.Length &&
        from.ElementTypes.Zip(to.ElementTypes).All(pair =>
            pair.First == pair.Second ||
            (pair.First is TupleTypeSymbol innerFrom && pair.Second is TupleTypeSymbol innerTo && IsTupleConvertible(innerFrom, innerTo)) ||
            (!pair.Second.IsOption && pair.First is not TupleTypeSymbol && IsConvertible(pair.First, pair.Second)));

    // Deconstruction

    /// <summary>The types a value of <paramref name="type"/> splits into for a deconstruction into <paramref name="count"/> parts, or null.</summary>
    private ImmutableArray<TypeSymbol>? DeconstructionTypes(TypeSymbol type, int count)
    {
        if (type is TupleTypeSymbol tuple)
            return tuple.ElementTypes.Length == count ? tuple.ElementTypes : null;

        // A record deconstructs into its fields, as a C# positional record does.
        if (type.OriginalDefinition is RecordSymbol record)
            return record.Fields.Length == count ? [.. record.Fields.Select(f => TypeSubstitution.FieldType(f, type))] : null;

        // .NET: a public Deconstruct(out …) method, e.g. KeyValuePair's.
        if (IsClrBacked(type))
        {
            var (clrType, map) = ClrContext(type);
            foreach (var method in clrType.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.Name == "Deconstruct"))
            {
                var parameters = method.GetParameters();
                if (parameters.Length != count || !parameters.All(p => p.IsOut))
                    continue;
                var types = parameters.Select(p => ClrTypes.FromClr(p.ParameterType.GetElementType()!, map, _option)).ToList();
                if (types.All(t => t is not null))
                    return [.. types.Select(t => t!)];
            }
        }
        return null;
    }

    /// <summary>Declares a variable for each name in <c>(a, (b, _))</c>, typed by the parts of <paramref name="type"/>.</summary>
    private BoundParenthesizedDesignation BindDesignation(
        ParenthesizedDesignationSyntax syntax,
        TypeSymbol type,
        bool isMutable,
        VariableKind kind,
        List<(VariableSymbol Variable, Token Name)> declared)
    {
        var types = type == TypeSymbol.Error ? null : DeconstructionTypes(type, syntax.Elements.Length);
        if (types is null && type != TypeSymbol.Error)
            _diagnostics.ReportCannotDeconstruct(syntax.Span, type, syntax.Elements.Length);

        var elements = ImmutableArray.CreateBuilder<BoundDesignation>();
        for (int i = 0; i < syntax.Elements.Length; i++)
        {
            var elementType = types?[i] ?? TypeSymbol.Error;
            switch (syntax.Elements[i])
            {
                case SingleDesignationSyntax { IsDiscard: true }:
                    elements.Add(new BoundDiscardDesignation(elementType));
                    break;
                case SingleDesignationSyntax single:
                {
                    var variable = new VariableSymbol(single.Name.Text, elementType, isMutable, kind);
                    declared.Add((variable, single.Name));
                    elements.Add(new BoundVariableDesignation(variable, elementType));
                    break;
                }
                case ParenthesizedDesignationSyntax nested:
                    elements.Add(BindDesignation(nested, elementType, isMutable, kind, declared));
                    break;
            }
        }
        return new BoundParenthesizedDesignation(elements.ToImmutable(), type);
    }

    /// <summary>Declares the variables of a designation in the current scope (after its value has been bound).</summary>
    private void DeclareAll(List<(VariableSymbol Variable, Token Name)> declared)
    {
        foreach (var (variable, name) in declared)
        {
            if (!name.IsMissing && !_scope.TryDeclare(variable))
                _diagnostics.ReportDuplicateDeclaration(name.Span, variable.Name);
        }
    }

    /// <summary><c>let (a, b) = value;</c></summary>
    private BoundStatement BindDeconstructionDeclaration(DeconstructionDeclarationSyntax syntax)
    {
        var value = RequireValue(BindExpression(syntax.Initializer), syntax.Initializer.Span);
        var declared = new List<(VariableSymbol, Token)>();
        var designation = BindDesignation(syntax.Designation, value.Type, syntax.IsMutable, VariableKind.Local, declared);
        DeclareAll(declared);
        return new BoundExpressionStatement(new BoundDeconstructionExpression(designation, value, declares: true));
    }

    /// <summary><c>(a, b) = (b, a)</c>: assigns existing mutable variables; the whole right side is evaluated first.</summary>
    private BoundExpression BindDeconstructionAssignment(AssignmentExpressionSyntax syntax, TupleExpressionSyntax target)
    {
        var value = RequireValue(BindExpression(syntax.Value), syntax.Value.Span);
        if (syntax.Operator.Kind != TokenKind.Equals)
        {
            _diagnostics.ReportInvalidAssignmentTarget(target.Span);
            return new BoundErrorExpression();
        }

        var designation = BindAssignedDesignation(target, value.Type);
        return designation is null || value.Type == TypeSymbol.Error
            ? new BoundErrorExpression()
            : new BoundDeconstructionExpression(designation, value, declares: false);
    }

    private BoundParenthesizedDesignation? BindAssignedDesignation(TupleExpressionSyntax target, TypeSymbol type)
    {
        var types = type == TypeSymbol.Error ? null : DeconstructionTypes(type, target.Elements.Length);
        if (types is null && type != TypeSymbol.Error)
            _diagnostics.ReportCannotDeconstruct(target.Span, type, target.Elements.Length);

        bool valid = true;
        var elements = ImmutableArray.CreateBuilder<BoundDesignation>();
        for (int i = 0; i < target.Elements.Length; i++)
        {
            var elementType = types?[i] ?? TypeSymbol.Error;
            var element = target.Elements[i].Expression;
            switch (element)
            {
                case NameExpressionSyntax { Identifier.Text: "_" } when _scope.Lookup("_") is null:
                    elements.Add(new BoundDiscardDesignation(elementType));
                    break;

                case NameExpressionSyntax { Identifier: { IsMissing: false } name } when _scope.Lookup(name.Text) is VariableSymbol variable:
                    if (!variable.IsMutable)
                        _diagnostics.ReportCannotAssignImmutable(name.Span, variable);
                    // The part converts as C# would: identically or by widening (not by wrapping in Some).
                    else if (elementType != TypeSymbol.Error && variable.Type != elementType &&
                             !(Operators.IsImplicitlyConvertible(elementType, variable.Type) || (variable.Type is TraitSymbol && IsConvertible(elementType, variable.Type))))
                        _diagnostics.ReportCannotConvert(element.Span, elementType, variable.Type);
                    elements.Add(new BoundVariableDesignation(variable, elementType));
                    break;

                case TupleExpressionSyntax nested when BindAssignedDesignation(nested, elementType) is { } inner:
                    elements.Add(inner);
                    break;

                case TupleExpressionSyntax:
                    valid = false;
                    break;

                default:
                    BindExpression(element);
                    _diagnostics.ReportInvalidAssignmentTarget(element.Span);
                    valid = false;
                    break;
            }
        }
        return valid ? new BoundParenthesizedDesignation(elements.ToImmutable(), type) : null;
    }

    // Patterns

    /// <summary><c>(0, y)</c> against a tuple of the same length.</summary>
    private BoundPattern BindTuplePattern(TuplePatternSyntax syntax, TypeSymbol expected, bool allowBindings)
    {
        if (expected is not TupleTypeSymbol tuple)
        {
            if (expected != TypeSymbol.Error)
                _diagnostics.ReportPatternTypeMismatch(syntax.Span, "A tuple pattern", expected);
            foreach (var element in syntax.Elements)
                BindPattern(element, TypeSymbol.Error, allowBindings);
            return new BoundWildcardPattern(expected);
        }
        if (tuple.ElementTypes.Length != syntax.Elements.Length)
        {
            _diagnostics.ReportPatternArity(syntax.Span, tuple.Name, tuple.ElementTypes.Length, syntax.Elements.Length);
            return new BoundWildcardPattern(expected);
        }
        return new BoundTuplePattern([.. syntax.Elements.Zip(tuple.ElementTypes, (e, t) => BindPattern(e, t, allowBindings))], tuple);
    }
}
