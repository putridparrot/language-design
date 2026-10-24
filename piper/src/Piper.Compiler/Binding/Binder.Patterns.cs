using System.Collections.Immutable;
using Piper.Syntax;

namespace Piper.Binding;

public sealed partial class Binder
{
    private BoundExpression BindMatch(MatchExpressionSyntax syntax, TypeSymbol? expected)
    {
        var scrutinee = RequireValue(BindExpression(syntax.Scrutinee), syntax.Scrutinee.Span);
        var scrutineeType = scrutinee.Type;

        var arms = new List<BoundMatchArm>();
        bool patternErrors = false;
        foreach (var arm in syntax.Arms)
        {
            var outer = _scope;
            _scope = new Scope(outer);

            int errorsBefore = _diagnostics.Count;
            var pattern = BindPattern(arm.Pattern, scrutineeType, allowBindings: true);
            patternErrors |= _diagnostics.Count != errorsBefore;

            BoundExpression? guard = null;
            if (arm.Guard is not null)
                guard = BindCondition(arm.Guard, allowPatternBindings: true);

            var body = BindExpression(arm.Body, expected);
            _scope = outer;
            arms.Add(new BoundMatchArm(pattern, guard, body));
        }

        var type = CommonType([.. arms.Select(a => a.Body.Type)], expected, out int failed);
        if (type is null)
        {
            var earlier = CommonType([.. arms.Take(failed).Select(a => a.Body.Type)], null, out _) ?? TypeSymbol.Error;
            _diagnostics.ReportIncompatibleArms(syntax.Arms[failed].Body.Span, earlier, arms[failed].Body.Type);
            type = TypeSymbol.Error;
        }

        if (NeedsBranchConversion(type))
            arms = arms.Select(a => new BoundMatchArm(a.Pattern, a.Guard, ConvertBranch(a.Body, type))).ToList();

        // A pattern that failed to bind acts as a wildcard; analysing it would only add noise.
        if (scrutineeType != TypeSymbol.Error && !patternErrors)
            CheckArms(syntax, scrutineeType, arms);

        return new BoundMatchExpression(scrutinee, [.. arms], type);
    }

    /// <summary>Warns about unreachable arms and reports a match that doesn't handle every value.</summary>
    private void CheckArms(MatchExpressionSyntax syntax, TypeSymbol scrutineeType, List<BoundMatchArm> arms)
    {
        // Guarded arms may not match, so they never make later arms unreachable or the match exhaustive.
        var covering = new List<BoundPattern>();
        for (int i = 0; i < arms.Count; i++)
        {
            if (!Exhaustiveness.IsUseful(covering, arms[i].Pattern))
                _diagnostics.ReportUnreachableArm(syntax.Arms[i].Pattern.Span);
            if (arms[i].Guard is null)
                covering.Add(arms[i].Pattern);
        }

        if (Exhaustiveness.FindMissingPattern(scrutineeType, covering) is { } missing)
            _diagnostics.ReportNonExhaustiveMatch(syntax.Keyword.Span, missing);
    }

    /// <param name="allowBindings">False inside '|' alternatives, where bindings aren't supported yet.</param>
    private BoundPattern BindPattern(PatternSyntax syntax, TypeSymbol expected, bool allowBindings)
    {
        switch (syntax)
        {
            case WildcardPatternSyntax or ErrorPatternSyntax:
                return new BoundWildcardPattern(expected);

            case NullPatternSyntax:
                if (expected.IsOption)
                    return new BoundConstructorPattern(_option.FindVariant("None")!, [], expected);
                if (expected != TypeSymbol.Error)
                    _diagnostics.ReportPatternTypeMismatch(syntax.Span, "A 'null' pattern", expected);
                return new BoundWildcardPattern(expected);

            case IdentifierPatternSyntax { Identifier: var name }:
            {
                if (name.IsMissing)
                    return new BoundWildcardPattern(expected);

                // A bare name is a fieldless variant of the matched enum if there is one, otherwise a binding.
                if (expected.OriginalDefinition is EnumSymbol @enum && @enum.FindVariant(name.Text) is { } variant)
                {
                    if (variant.Fields.IsEmpty)
                        return new BoundConstructorPattern(variant, [], expected);
                    _diagnostics.ReportPatternArity(syntax.Span, variant.QualifiedName, variant.Fields.Length, 0);
                    return new BoundWildcardPattern(expected);
                }

                if (!allowBindings)
                {
                    _diagnostics.ReportBindingInOrPattern(syntax.Span);
                    return new BoundWildcardPattern(expected);
                }

                // A later alternative of `A(x) | B(x)` binds the same x as the first.
                if (_orBindings is { } shared)
                {
                    if (!shared.TryGetValue(name.Text, out var existing))
                    {
                        _diagnostics.ReportOrPatternBinding(name.Span, name.Text, "isn't bound by the first alternative");
                        return new BoundWildcardPattern(expected);
                    }
                    if (existing.Type != expected && expected != TypeSymbol.Error && existing.Type != TypeSymbol.Error)
                        _diagnostics.ReportOrPatternBinding(name.Span, name.Text, $"is a '{expected}' here but a '{existing.Type}' in the first alternative");
                    _orBindingsUsed.Add(name.Text);
                    return new BoundBindingPattern(existing);
                }

                var variable = new VariableSymbol(name.Text, expected, isMutable: false, VariableKind.PatternVariable);
                if (!_scope.TryDeclare(variable))
                    _diagnostics.ReportDuplicateDeclaration(name.Span, name.Text);
                return new BoundBindingPattern(variable);
            }

            case ConstructorPatternSyntax constructor:
                return BindConstructorPattern(constructor, expected, allowBindings);

            case LiteralPatternSyntax literal:
                return ConvertPatternLiteral(literal, expected) is { } value
                    ? new BoundLiteralPattern(value, expected)
                    : new BoundWildcardPattern(expected);

            case RangePatternSyntax range:
                return BindRangePattern(range, expected);

            case TuplePatternSyntax tuple:
                return BindTuplePattern(tuple, expected, allowBindings);

            case OrPatternSyntax alternatives when allowBindings:
                return BindOrPatternWithBindings(alternatives, expected);

            case OrPatternSyntax alternatives:
                return new BoundOrPattern(
                    [.. alternatives.Alternatives.Select(a => BindPattern(a, expected, allowBindings: false))],
                    expected);

            default:
                throw new InvalidOperationException($"Unexpected pattern {syntax.GetType().Name}");
        }
    }

    /// <summary>While binding a later alternative of `A(x) | B(x)`: the first alternative's variables, and those used.</summary>
    private Dictionary<string, VariableSymbol>? _orBindings;
    private HashSet<string> _orBindingsUsed = [];

    /// <summary>
    /// `A(x) | B(x)` in a match arm or an `is` test: every alternative must bind the same names with the same types (the first
    /// one declares them; the others reuse them). C# can't declare variables inside an `or` pattern, so lowering splits
    /// such an arm into one per alternative, and an `is` test into one test per alternative.
    /// </summary>
    private BoundPattern BindOrPatternWithBindings(OrPatternSyntax syntax, TypeSymbol expected)
    {
        var first = BindPattern(syntax.Alternatives[0], expected, allowBindings: true);
        var names = Bindings(first).ToDictionary(v => v.Name);
        var alternatives = new List<BoundPattern> { first };

        var (outerShared, outerUsed) = (_orBindings, _orBindingsUsed);
        foreach (var alternative in syntax.Alternatives.Skip(1))
        {
            (_orBindings, _orBindingsUsed) = (names, []);
            alternatives.Add(BindPattern(alternative, expected, allowBindings: true));
            foreach (var missing in names.Keys.Except(_orBindingsUsed))
                _diagnostics.ReportOrPatternBinding(alternative.Span, missing, "must be bound by every alternative");
            outerUsed.UnionWith(_orBindingsUsed);
        }
        (_orBindings, _orBindingsUsed) = (outerShared, outerUsed);
        return new BoundOrPattern([.. alternatives], expected);
    }

    private static IEnumerable<VariableSymbol> Bindings(BoundPattern pattern) => pattern switch
    {
        BoundBindingPattern binding => [binding.Variable],
        BoundConstructorPattern constructor => constructor.Arguments.SelectMany(Bindings),
        BoundTuplePattern tuple => tuple.Elements.SelectMany(Bindings),
        BoundOrPattern or => or.Alternatives.Take(1).SelectMany(Bindings),
        _ => [],
    };

    private BoundPattern BindConstructorPattern(ConstructorPatternSyntax syntax, TypeSymbol expected, bool allowBindings)
    {
        var name = syntax.Name;
        if (name.IsMissing || syntax.Qualifier is { IsMissing: true })
            return new BoundWildcardPattern(expected);

        NamedTypeDefinition definition;
        Symbol constructor;
        ImmutableArray<FieldSymbol> fields;
        string displayName;

        if (syntax.Qualifier is { } qualifier)
        {
            if (_scope.Lookup(qualifier.Text) is not EnumSymbol @enum)
            {
                _diagnostics.ReportUnknownConstructorPattern(syntax.Span, $"{qualifier.Text}.{name.Text}", expected);
                return new BoundWildcardPattern(expected);
            }
            if (@enum.FindVariant(name.Text) is not { } variant)
            {
                _diagnostics.ReportUnknownVariant(name.Span, @enum, name.Text);
                return new BoundWildcardPattern(expected);
            }
            (definition, constructor, fields, displayName) = (@enum, variant, variant.Fields, variant.QualifiedName);
        }
        else if (expected.OriginalDefinition is EnumSymbol @enum && @enum.FindVariant(name.Text) is { } variant)
        {
            (definition, constructor, fields, displayName) = (@enum, variant, variant.Fields, variant.QualifiedName);
        }
        else if (_scope.Lookup(name.Text) is RecordSymbol record)
        {
            (definition, constructor, fields, displayName) = (record, record, record.Fields, record.Name);
        }
        else
        {
            if (expected != TypeSymbol.Error)
                _diagnostics.ReportUnknownConstructorPattern(name.Span, name.Text, expected);
            return new BoundWildcardPattern(expected);
        }

        // The matched value's type supplies a generic definition's type arguments.
        TypeSymbol constructedType;
        if (expected == TypeSymbol.Error)
        {
            constructedType = definition.Construct([.. definition.TypeParameters.Select(_ => TypeSymbol.Error)]);
        }
        else if (expected.OriginalDefinition != definition)
        {
            _diagnostics.ReportPatternTypeMismatch(syntax.Span, $"A '{displayName}' pattern", expected);
            return new BoundWildcardPattern(expected);
        }
        else
        {
            constructedType = expected;
        }

        if (syntax.Arguments.Length != fields.Length)
        {
            _diagnostics.ReportPatternArity(syntax.Span, displayName, fields.Length, syntax.Arguments.Length);
            return new BoundWildcardPattern(expected);
        }

        var arguments = syntax.Arguments.Zip(fields, (argument, field) =>
            BindPattern(argument, TypeSubstitution.FieldType(field, constructedType), allowBindings));
        return new BoundConstructorPattern(constructor, [.. arguments], constructedType);
    }

    private BoundPattern BindRangePattern(RangePatternSyntax syntax, TypeSymbol expected)
    {
        if (expected == TypeSymbol.Error)
            return new BoundWildcardPattern(expected);

        if (!expected.IsIntegral && expected != TypeSymbol.Char)
        {
            _diagnostics.ReportInvalidRangePattern(syntax.Span, expected);
            return new BoundWildcardPattern(expected);
        }

        if (ConvertPatternLiteral(syntax.Start, expected) is not { } lower ||
            ConvertPatternLiteral(syntax.End, expected) is not { } upper)
        {
            return new BoundWildcardPattern(expected);
        }

        int comparison = Comparer<object>.Default.Compare(lower, upper);
        if (comparison > 0 || (comparison == 0 && !syntax.IsInclusive))
        {
            _diagnostics.ReportEmptyRangePattern(syntax.Span);
            return new BoundWildcardPattern(expected);
        }

        return new BoundRangePattern(lower, upper, syntax.IsInclusive, expected);
    }

    /// <summary>
    /// Converts a literal pattern's value to the matched type, as C# converts a constant (widening, or an Int that fits
    /// a Byte), or reports a mismatch.
    /// </summary>
    private object? ConvertPatternLiteral(LiteralPatternSyntax syntax, TypeSymbol expected)
    {
        object? converted = (syntax.Value, expected.Name) switch
        {
            (null, _) => null,
            (_, "?") => null,
            (char c, "char") => c,
            (string s, "string") => s,
            (bool b, "bool") => b,
            _ => null,
        };
        if (syntax.Value is not (null or char or string or bool) && expected.IsNumeric)
        {
            var literal = BindLiteral(new LiteralExpressionSyntax(syntax.Span, syntax.Literal, syntax.Value));
            if ((Operators.IsImplicitlyConvertible(literal.Type, expected) || ConstantFolding.FitsImplicitly(literal, expected)) &&
                ConstantFolding.Cast(syntax.Value, expected, out var value) == ConstantFolding.Failure.None)
            {
                converted = value;
            }
        }

        // Only builtin types have literals, so compare by identity rather than trusting the name (records could shadow it).
        bool builtinTarget = TypeSymbol.Builtins.Contains(expected);
        if (converted is not null && builtinTarget)
            return converted;

        if (syntax.Value is not null && expected != TypeSymbol.Error)
        {
            var literalType = BindLiteral(new LiteralExpressionSyntax(syntax.Span, syntax.Literal, syntax.Value)).Type;
            _diagnostics.ReportPatternTypeMismatch(syntax.Span, $"A literal of type '{literalType}'", expected);
        }
        return null;
    }
}
