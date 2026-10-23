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
            {
                guard = BindCondition(arm.Guard, allowPatternBindings: true);
                if (ContainsStatements(guard))
                    _diagnostics.ReportGuardContainsStatements(arm.Guard.Span);
            }

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

    /// <summary>
    /// Lowering turns a match into an if/else-if chain whose conditions are `x is Pattern &amp;&amp; guard`,
    /// which leaves no place to run statements hoisted out of a guard. Blocks with statements and
    /// nested matches are the only expressions that need such statements.
    /// </summary>
    private static bool ContainsStatements(BoundExpression expression) => expression switch
    {
        BoundBlockExpression block => block.Statements.Length > 0 || (block.Trailing is { } t && ContainsStatements(t)),
        BoundMatchExpression => true,
        BoundIfExpression @if => ContainsStatements(@if.Condition) || ContainsStatements(@if.Then) ||
                                 (@if.Else is { } e && ContainsStatements(e)),
        BoundUnaryExpression unary => ContainsStatements(unary.Operand),
        BoundBinaryExpression binary => ContainsStatements(binary.Left) || ContainsStatements(binary.Right),
        BoundConversionExpression conversion => ContainsStatements(conversion.Expression),
        BoundFieldAccessExpression access => ContainsStatements(access.Target),
        BoundAssignmentExpression assignment => ContainsStatements(assignment.Value),
        BoundCallExpression call => call.Arguments.Any(ContainsStatements),
        BoundMethodCallExpression call => (call.Receiver is { } r && ContainsStatements(r)) || call.Arguments.Any(ContainsStatements),
        BoundRecordCreationExpression creation => creation.Arguments.Any(ContainsStatements),
        BoundVariantCreationExpression creation => creation.Arguments.Any(ContainsStatements),
        BoundInterpolatedStringExpression interpolated => interpolated.Parts.Any(ContainsStatements),
        BoundIsPatternExpression test => ContainsStatements(test.Expression),
        // These evaluate their operand once, so anything but a variable is first stored in a temporary.
        BoundNullCoalescingExpression coalescing =>
            coalescing.Left is not BoundVariableExpression || ContainsStatements(coalescing.Right),
        BoundConditionalAccessExpression access =>
            access.Target is not BoundVariableExpression || ContainsStatements(access.WhenNotNull),
        BoundTryExpression => true,
        BoundClrInvocationExpression call => (call.Receiver is { } r && ContainsStatements(r)) || call.Arguments.Any(ContainsStatements),
        BoundClrMemberExpression member => member.Receiver is { } receiver && ContainsStatements(receiver),
        BoundNullableBoundaryExpression boundary => ContainsStatements(boundary.Operand),
        BoundTupleExpression tuple => tuple.Elements.Any(ContainsStatements),
        // `out let n` is declared by a statement before the call.
        BoundOutArgument output => output.Declares,
        BoundTupleElementExpression element => ContainsStatements(element.Tuple),
        _ => false,
    };

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

            case OrPatternSyntax alternatives:
                return new BoundOrPattern(
                    [.. alternatives.Alternatives.Select(a => BindPattern(a, expected, allowBindings: false))],
                    expected);

            default:
                throw new InvalidOperationException($"Unexpected pattern {syntax.GetType().Name}");
        }
    }

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

        if (expected != TypeSymbol.Int && expected != TypeSymbol.Long && expected != TypeSymbol.Char)
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

    /// <summary>Converts a literal pattern's value to the matched type (widening Int to Long/Double), or reports a mismatch.</summary>
    private object? ConvertPatternLiteral(LiteralPatternSyntax syntax, TypeSymbol expected)
    {
        object? converted = (syntax.Value, expected.Name) switch
        {
            (null, _) => null,
            (_, "?") => null,
            (int i, "Int") => i,
            (int i, "Long") => (long)i,
            (int i, "Double") => (double)i,
            (long l, "Long") => l,
            (long l, "Double") => (double)l,
            (double d, "Double") => d,
            (char c, "Char") => c,
            (string s, "String") => s,
            (bool b, "Bool") => b,
            _ => null,
        };

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
