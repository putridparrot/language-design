using System.Collections.Immutable;
using Piper.Syntax;
using Piper.Text;

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
        {
            _diagnostics.ReportNonExhaustiveMatch(syntax.Keyword.Span, missing);
            _semantic?.AddIncompleteMatch(new IncompleteMatch(_file.Source, syntax, Exhaustiveness.MissingArms(scrutineeType, covering)));
        }
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
                    Record(name, variant);
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

                var variable = Declared(new VariableSymbol(name.Text, expected, isMutable: false, VariableKind.PatternVariable), name);
                if (!_scope.TryDeclare(variable))
                    _diagnostics.ReportDuplicateDeclaration(name.Span, name.Text);
                return new BoundBindingPattern(variable);
            }

            // `Point(x, y)` on a `Point?` or an `object?` (such as a nullable .NET property): there's a value and it
            // matches, so `Some(Point(x, y))`. None doesn't match, as null doesn't in C#'s `o is Point(var x, var y)`.
            case ConstructorPatternSyntax constructor when expected.IsOption && !NamesOptionVariant(constructor):
                return new BoundConstructorPattern(
                    _option.FindVariant("Some")!, [BindConstructorPattern(constructor, expected.TypeArguments[0], allowBindings)], expected);

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

            case ListPatternSyntax list:
                return BindListPattern(list, expected, allowBindings);

            case SlicePatternSyntax slice:
                _diagnostics.ReportSlicePattern(slice.Span, "'...' is only allowed in a list pattern, such as '[first, ...rest]'.");
                return new BoundWildcardPattern(expected);

            case PropertyPatternSyntax properties:
                return BindPropertyPattern(properties, expected, allowBindings);

            case RelationalPatternSyntax relational:
                return BindRelationalPattern(relational, expected);

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

    /// <summary>Whether the pattern is Option's own <c>Some(…)</c> or <c>None</c> (bare or qualified).</summary>
    private static bool NamesOptionVariant(ConstructorPatternSyntax pattern) =>
        pattern.Qualifier is { Text: "Option" } || (pattern.Qualifier is null && pattern.Name.Text is "Some" or "None");

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
        BoundListPattern list => list.Elements.SelectMany(Bindings).Concat(list.Slice is { } slice ? Bindings(slice) : []),
        BoundPropertyPattern properties => properties.Properties.SelectMany(p => Bindings(p.Pattern)),
        BoundOrPattern or => or.Alternatives.Take(1).SelectMany(Bindings),
        _ => [],
    };

    private BoundPattern BindConstructorPattern(ConstructorPatternSyntax syntax, TypeSymbol expected, bool allowBindings)
    {
        if (ResolvePatternConstructor(syntax.Qualifier, syntax.Name, expected, syntax.Span) is not var (constructor, fields, constructedType, displayName))
            return new BoundWildcardPattern(expected);

        if (syntax.Arguments.Length != fields.Length)
        {
            _diagnostics.ReportPatternArity(syntax.Span, displayName, fields.Length, syntax.Arguments.Length);
            return new BoundWildcardPattern(expected);
        }

        var arguments = syntax.Arguments.Zip(fields, (argument, field) =>
            BindPattern(argument, TypeSubstitution.FieldType(field, constructedType), allowBindings));
        return new BoundConstructorPattern(constructor, [.. arguments], constructedType);
    }

    /// <summary>
    /// The variant or record that <c>Circle</c>, <c>Shape.Circle</c> or <c>Point</c> names in a pattern matching
    /// <paramref name="expected"/>, with its fields and the type it's constructed at; null once an error is reported.
    /// </summary>
    private (Symbol Constructor, ImmutableArray<FieldSymbol> Fields, TypeSymbol Type, string DisplayName)? ResolvePatternConstructor(
        Token? qualifier, Token name, TypeSymbol expected, TextSpan span)
    {
        if (name.IsMissing || qualifier is { IsMissing: true })
            return null;

        NamedTypeDefinition definition;
        Symbol constructor;
        ImmutableArray<FieldSymbol> fields;
        string displayName;

        if (qualifier is { } enumName)
        {
            if (_scope.Lookup(enumName.Text) is not EnumSymbol @enum)
            {
                _diagnostics.ReportUnknownConstructorPattern(span, $"{enumName.Text}.{name.Text}", expected);
                return null;
            }
            if (@enum.FindVariant(name.Text) is not { } variant)
            {
                _diagnostics.ReportUnknownVariant(name.Span, @enum, name.Text);
                return null;
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
            return null;
        }
        Record(name, constructor);

        // The matched value's type supplies a generic definition's type arguments.
        if (expected == TypeSymbol.Error)
            return (constructor, fields, definition.Construct([.. definition.TypeParameters.Select(_ => TypeSymbol.Error)]), displayName);
        if (expected.OriginalDefinition != definition)
        {
            // `match message { Point(x, y) => … }` on an object, or on a trait or .NET interface the type implements:
            // a type test, as C#'s `message is Point(var x, var y)`.
            if (!definition.IsGeneric && CanHold(expected, definition))
                return (constructor, fields, definition, displayName);
            _diagnostics.ReportPatternTypeMismatch(span, $"A '{displayName}' pattern", expected);
            return null;
        }
        return (constructor, fields, expected, displayName);
    }

    /// <summary>Whether a value of type <paramref name="expected"/> can be a <paramref name="definition"/>: it's `object`, or a trait or interface the type implements.</summary>
    private bool CanHold(TypeSymbol expected, NamedTypeDefinition definition) =>
        (expected is ClrTypeSymbol { Type: var type } && type == typeof(object)) ||
        ((expected.IsTrait || expected is ClrTypeSymbol { Type.IsInterface: true }) && Implements(definition, expected));

    /// <summary>
    /// <c>Point { x: 0 }</c> or <c>{ x: 0 }</c> on a record (or a variant) is a constructor pattern with <c>_</c> for the
    /// fields it doesn't name; <c>{ Length: &gt; 5 }</c> on a .NET type tests its properties and fields.
    /// </summary>
    private BoundPattern BindPropertyPattern(PropertyPatternSyntax syntax, TypeSymbol expected, bool allowBindings)
    {
        BoundPattern Unchecked()
        {
            foreach (var property in syntax.Properties)
                BindPattern(property.Pattern, TypeSymbol.Error, allowBindings);
            return new BoundWildcardPattern(expected);
        }

        var seen = new HashSet<string>();
        bool IsDuplicate(Token name)
        {
            if (seen.Add(name.Text))
                return false;
            _diagnostics.ReportDuplicateProperty(name.Span, name.Text);
            return true;
        }

        var resolved = syntax.Name is { } typeName
            ? ResolvePatternConstructor(syntax.Qualifier, typeName, expected, syntax.Span)
            : expected.OriginalDefinition is RecordSymbol record
                ? (record, record.Fields, expected, record.Name)
                : null;
        if (resolved is var (constructor, fields, constructedType, _))
        {
            var arguments = fields.Select(f => (BoundPattern)new BoundWildcardPattern(TypeSubstitution.FieldType(f, constructedType))).ToArray();
            foreach (var property in syntax.Properties)
            {
                int index = fields.Select(f => f.Name).ToList().IndexOf(property.Name.Text);
                if (index < 0)
                {
                    if (!property.Name.IsMissing)
                        _diagnostics.ReportUnknownMember(property.Name.Span, constructedType, property.Name.Text);
                    BindPattern(property.Pattern, TypeSymbol.Error, allowBindings);
                    continue;
                }
                Record(property.Name, fields[index]);
                var pattern = BindPattern(property.Pattern, arguments[index].Type, allowBindings);
                if (!IsDuplicate(property.Name))
                    arguments[index] = pattern;
            }
            return new BoundConstructorPattern(constructor, [.. arguments], constructedType);
        }
        if (syntax.Name is not null || expected == TypeSymbol.Error)
            return Unchecked();

        if (!IsClrBacked(expected))
        {
            _diagnostics.ReportPropertyPatternType(syntax.Span, expected);
            return Unchecked();
        }

        // Each property is read as `value.Name` would be, from a stand-in for the matched value.
        var receiver = new BoundVariableExpression(new VariableSymbol("value", expected, isMutable: false, VariableKind.Local));
        var properties = new List<BoundPropertySubpattern>();
        foreach (var property in syntax.Properties)
        {
            if (property.Name.IsMissing || IsDuplicate(property.Name))
            {
                BindPattern(property.Pattern, TypeSymbol.Error, allowBindings);
                continue;
            }
            switch (BindClrInstanceMember(receiver, property.Name))
            {
                case BoundClrMemberExpression read:
                    properties.Add(new BoundPropertySubpattern(read.Member, BindPattern(property.Pattern, read.Type, allowBindings)));
                    break;
                case BoundErrorExpression:
                    BindPattern(property.Pattern, TypeSymbol.Error, allowBindings);
                    break;
                default:
                    _diagnostics.ReportNullablePropertyPattern(property.Name.Span, property.Name.Text);
                    BindPattern(property.Pattern, TypeSymbol.Error, allowBindings);
                    break;
            }
        }
        // `{}` matches anything, since Piper values are never null.
        return properties.Count == 0 ? new BoundWildcardPattern(expected) : new BoundPropertyPattern([.. properties], expected);
    }

    /// <summary>
    /// <c>[first, ...rest]</c> on an array, string, List or other .NET collection with a Count (or Length) and an int
    /// indexer, as C# allows. Only arrays, strings and Lists can bind the <c>...</c> part, which has the matched type.
    /// </summary>
    private BoundPattern BindListPattern(ListPatternSyntax syntax, TypeSymbol expected, bool allowBindings)
    {
        var shape = expected == TypeSymbol.Error ? null : ListShape(expected);
        if (shape is null && expected != TypeSymbol.Error)
            _diagnostics.ReportListPatternType(syntax.Span, expected);
        var elementType = shape?.Element ?? TypeSymbol.Error;

        var elements = new List<BoundPattern>();
        int sliceIndex = -1;
        BoundPattern? slice = null;
        foreach (var element in syntax.Elements)
        {
            if (element is not SlicePatternSyntax { Name: var name })
            {
                elements.Add(BindPattern(element, elementType, allowBindings));
                continue;
            }
            if (sliceIndex >= 0)
            {
                _diagnostics.ReportSlicePattern(element.Span, "A list pattern can only have one '...'.");
                continue;
            }
            sliceIndex = elements.Count;
            slice = new BoundWildcardPattern(expected);
            if (name is not { IsMissing: false, Text: not "_" } binding)
                continue;
            if (shape is { CanSlice: false })
                _diagnostics.ReportSlicePattern(element.Span, $"'...{binding.Text}' can't bind the rest of a '{expected}'; only arrays, strings and Lists can be sliced. Write '...' alone.");
            else
                slice = BindPattern(new IdentifierPatternSyntax(binding.Span, binding), expected, allowBindings);
        }
        return shape is null ? new BoundWildcardPattern(expected) : new BoundListPattern([.. elements], sliceIndex, slice, elementType, expected);
    }

    /// <summary>The element type of a type a list pattern can match, and whether a <c>...</c> can bind its slices.</summary>
    private (TypeSymbol Element, bool CanSlice)? ListShape(TypeSymbol type)
    {
        if (type is ClrTypeSymbol { IsArray: true, Rank: 1 } array)
            return (array.TypeArguments[0], true);
        if (type == TypeSymbol.String)
            return (TypeSymbol.Char, true);
        if (type is ClrTypeSymbol { TypeArguments: [var listElement] } list && list.Definition.Type == typeof(List<>))
            return (listElement, true);
        if (!IsClrBacked(type) || ClrTypes.ToClr(type) is not { } clr || ElementTypeOf(type) is not { } element)
            return null;

        var properties = clr.GetProperties().Concat(clr.GetInterfaces().SelectMany(i => i.GetProperties())).ToList();
        bool countable = properties.Any(p => p.Name is "Count" or "Length" && p.PropertyType == typeof(int) && p.GetIndexParameters().Length == 0);
        bool indexable = properties.Any(p => p.GetIndexParameters() is [{ ParameterType: var index }] && index == typeof(int));
        return countable && indexable ? (element, false) : null;
    }

    /// <summary><c>&lt; 0</c>, <c>&gt;= 'a'</c>: on numbers and chars, as in C#.</summary>
    private BoundPattern BindRelationalPattern(RelationalPatternSyntax syntax, TypeSymbol expected)
    {
        if (expected == TypeSymbol.Error)
            return new BoundWildcardPattern(expected);
        if (!expected.IsNumeric && expected != TypeSymbol.Char)
        {
            _diagnostics.ReportRelationalPatternType(syntax.Span, syntax.Operator.Text, expected);
            return new BoundWildcardPattern(expected);
        }
        if (ConvertPatternLiteral(syntax.Value, expected) is not { } value)
            return new BoundWildcardPattern(expected);

        var pattern = new BoundRelationalPattern(syntax.Operator.Text, value, expected);
        if (Exhaustiveness.IsEmpty(pattern))
        {
            _diagnostics.ReportEmptyRelationalPattern(syntax.Span, expected);
            return new BoundWildcardPattern(expected);
        }
        return pattern;
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
