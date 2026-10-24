using System.Collections.Immutable;
using Piper.Syntax;
using Piper.Text;

namespace Piper.Binding;

public sealed partial class Binder
{
    /// <param name="expected">
    /// The type the context wants, if known (a variable's annotation, a parameter, the return type).
    /// It is only a hint, used to infer type arguments such as the T in <c>let x: Option&lt;Int&gt; = Option.None;</c>;
    /// callers still convert the result.
    /// </param>
    private BoundExpression BindExpression(ExpressionSyntax syntax, TypeSymbol? expected = null)
    {
        var bound = BindExpressionCore(syntax, expected);
        bound.SourceSpan ??= syntax.Span;
        return bound;
    }

    private BoundExpression BindExpressionCore(ExpressionSyntax syntax, TypeSymbol? expected) => syntax switch
    {
        LiteralExpressionSyntax s => BindLiteral(s),
        InterpolatedStringExpressionSyntax s => BindInterpolatedString(s),
        NullLiteralExpressionSyntax s => BindNull(s, expected),
        NameExpressionSyntax s => BindName(s, expected),
        IsExpressionSyntax s => BindIs(s, allowBindings: false),
        ConditionalAccessExpressionSyntax s => BindConditionalAccess(s),
        ImplicitReceiverSyntax => new BoundVariableExpression(_implicitReceivers.Peek()),
        TryExpressionSyntax s => BindTry(s),
        LambdaExpressionSyntax s => BindLambda(s, expected),
        IndexExpressionSyntax s => BindIndex(s),
        CollectionExpressionSyntax s => BindCollection(s, expected),
        TupleExpressionSyntax s => BindTuple(s, expected),
        RecordSpreadExpressionSyntax s => BindRecordSpread(s, expected),
        ThrowExpressionSyntax s => BindThrow(s),
        AwaitExpressionSyntax s => BindAwait(s),
        OutArgumentSyntax s => ReportMisplacedOut(s),
        UnaryExpressionSyntax s => BindUnary(s),
        CastExpressionSyntax s => BindCast(s),
        BinaryExpressionSyntax s => BindBinary(s),
        RangeExpressionSyntax s => BindRange(s),
        AssignmentExpressionSyntax s => BindAssignment(s),
        CallExpressionSyntax s => BindCall(s, expected),
        PipeExpressionSyntax s => BindPipe(s, expected),
        MemberAccessExpressionSyntax s => BindMemberAccess(s, expected),
        ParenthesizedExpressionSyntax s => BindExpression(s.Expression, expected),
        IfExpressionSyntax s => BindIf(s, expected),
        MatchExpressionSyntax s => BindMatch(s, expected),
        BlockExpressionSyntax s => BindBlock(s, expected),
        ErrorExpressionSyntax => new BoundErrorExpression(),
        _ => throw new InvalidOperationException($"Unexpected expression {syntax.GetType().Name}"),
    };

    private static BoundExpression BindLiteral(LiteralExpressionSyntax syntax) => syntax.Value switch
    {
        int value => new BoundLiteralExpression(value, TypeSymbol.Int),
        long value => new BoundLiteralExpression(value, TypeSymbol.Long),
        uint value => new BoundLiteralExpression(value, TypeSymbol.UInt),
        ulong value => new BoundLiteralExpression(value, TypeSymbol.ULong),
        float value => new BoundLiteralExpression(value, TypeSymbol.Float),
        decimal value => new BoundLiteralExpression(value, TypeSymbol.Decimal),
        double value => new BoundLiteralExpression(value, TypeSymbol.Double),
        char value => new BoundLiteralExpression(value, TypeSymbol.Char),
        string value => new BoundLiteralExpression(value, TypeSymbol.String),
        bool value => new BoundLiteralExpression(value, TypeSymbol.Bool),
        _ => new BoundErrorExpression(),
    };

    private BoundExpression BindInterpolatedString(InterpolatedStringExpressionSyntax syntax)
    {
        var parts = syntax.Parts.Select(part => part switch
        {
            InterpolatedTextSyntax text => new BoundLiteralExpression(text.Text, TypeSymbol.String),
            InterpolationSyntax hole => RequireValue(BindExpression(hole.Expression), hole.Expression.Span),
            _ => throw new InvalidOperationException(),
        });
        return new BoundInterpolatedStringExpression([.. parts]);
    }

    private BoundExpression BindName(NameExpressionSyntax syntax, TypeSymbol? expected = null)
    {
        var name = syntax.Identifier;
        if (name.IsMissing)
            return new BoundErrorExpression();

        return BindSymbolAsValue(_scope.Lookup(name.Text), name, syntax.Span, expected, syntax.TypeArguments);
    }

    /// <summary>
    /// `{ ...p, y: 5, height }`: a copy of the record `p` with the named fields replaced, as C#'s `p with { … }`.
    /// The result has p's type; each value converts to its field's type (for a generic record, as instantiated).
    /// </summary>
    private BoundExpression BindRecordSpread(RecordSpreadExpressionSyntax syntax, TypeSymbol? expected)
    {
        var source = RequireValue(BindExpression(syntax.Source, expected), syntax.Source.Span);
        var record = source.Type.OriginalDefinition as RecordSymbol;
        if (record is null && source.Type != TypeSymbol.Error)
            _diagnostics.ReportSpreadNotRecord(syntax.Source.Span, source.Type);

        var updates = ImmutableArray.CreateBuilder<(FieldSymbol, BoundExpression)>();
        var seen = new HashSet<string>();
        foreach (var update in syntax.Updates)
        {
            // `height` alone means `height: height`.
            var valueSyntax = update.Value ?? new NameExpressionSyntax(update.Name.Span, update.Name);
            var field = update.Name.IsMissing ? null : record?.Fields.FirstOrDefault(f => f.Name == update.Name.Text);
            if (field is null)
            {
                BindExpression(valueSyntax);
                if (record is not null && !update.Name.IsMissing)
                    _diagnostics.ReportUnknownMember(update.Name.Span, source.Type, update.Name.Text);
                continue;
            }
            if (!seen.Add(field.Name))
                _diagnostics.ReportFieldUpdatedTwice(update.Name.Span, field.Name);

            var fieldType = TypeSubstitution.FieldType(field, source.Type);
            var value = RequireValue(BindExpression(valueSyntax, fieldType), valueSyntax.Span);
            updates.Add((field, Convert(value, fieldType, valueSyntax.Span)));
        }

        return record is null ? new BoundErrorExpression() : new BoundRecordSpreadExpression(source, updates.ToImmutable());
    }
    /// <summary>The type arguments written in <c>f&lt;Int&gt;</c>, or null when there are none.</summary>
    private ImmutableArray<TypeSymbol>? ExplicitTypeArguments(ImmutableArray<TypeSyntax> syntax) =>
        syntax.IsEmpty ? null : [.. syntax.Select(t => BindValueType(t, "type argument"))];

    /// <summary>
    /// Puts explicit type arguments for <paramref name="parameters"/> into an inference map; reports and returns
    /// false if their number is wrong. Does nothing when none were written.
    /// </summary>
    private bool ApplyExplicitTypeArguments(
        ImmutableArray<TypeSymbol>? explicitArguments,
        ImmutableArray<TypeParameterSymbol> parameters,
        Dictionary<TypeParameterSymbol, TypeSymbol> map,
        TextSpan span,
        string name)
    {
        if (explicitArguments is not { } arguments)
            return true;
        if (arguments.Length != parameters.Length)
        {
            if (parameters.IsEmpty)
                _diagnostics.ReportNotGeneric(span, name);
            else
                _diagnostics.ReportTypeArgumentCount(span, name, parameters.Length, arguments.Length);
            return false;
        }
        for (int i = 0; i < parameters.Length; i++)
            map[parameters[i]] = arguments[i];
        return true;
    }

    /// <summary>Uses a resolved name as a value: a variable, or a fieldless built-in variant such as <c>None</c>.</summary>
    private BoundExpression BindSymbolAsValue(
        Symbol? symbol,
        Token name,
        TextSpan span,
        TypeSymbol? expected,
        ImmutableArray<TypeSyntax> typeArguments = default)
    {
        var explicitArguments = ExplicitTypeArguments(typeArguments.IsDefault ? [] : typeArguments);
        if (explicitArguments is not null && symbol is not (FunctionSymbol or null or ErrorSymbol or AmbiguousSymbol))
        {
            _diagnostics.ReportNotGeneric(span, name.Text);
            return new BoundErrorExpression();
        }

        switch (symbol)
        {
            case VariableSymbol variable:
                return new BoundVariableExpression(variable);
            case VariantSymbol variant:
                // A built-in variant used bare: `None`.
                return BindVariantCreation(variant.Enum, variant, null, span, expected);
            case FunctionSymbol function when explicitArguments is not null:
                return BindFunctionReference(function, expected, explicitArguments, span) ?? new BoundErrorExpression();
            case FunctionSymbol function when BindFunctionReference(function, expected, null, span) is { } reference:
                return reference;
            case FunctionSymbol:
                _diagnostics.ReportNotAValue(name.Span, name.Text, "function");
                return new BoundErrorExpression();
            case TypeSymbol or ClrTypeGroupSymbol:
                _diagnostics.ReportNotAValue(name.Span, name.Text, "type");
                return new BoundErrorExpression();
            case NamespaceSymbol:
                _diagnostics.ReportNotAValue(name.Span, name.Text, "namespace");
                return new BoundErrorExpression();
            case AmbiguousSymbol ambiguous:
                ReportAmbiguous(name, ambiguous);
                return new BoundErrorExpression();
            case ErrorSymbol:
                return new BoundErrorExpression();
            default:
                if (name.Kind == TokenKind.ThisKeyword)
                    _diagnostics.ReportThisOutsideInstanceMethod(name.Span);
                else if (!ReportIfPrivateElsewhere(name))
                    _diagnostics.ReportUndefinedName(name.Span, name.Text);
                return new BoundErrorExpression();
        }
    }

    private BoundExpression BindUnary(UnaryExpressionSyntax syntax)
    {
        var operand = RequireValue(BindExpression(syntax.Operand), syntax.Operand.Span);
        if (operand.Type == TypeSymbol.Error)
            return new BoundErrorExpression();

        var kind = Operators.GetUnaryKind(syntax.Operator.Kind)!.Value;
        var type = Operators.ResolveUnary(kind, operand.Type);
        if (type is null)
        {
            _diagnostics.ReportUndefinedUnaryOperator(syntax.Operator.Span, syntax.Operator.Text, operand.Type);
            return new BoundErrorExpression();
        }
        if (kind == BoundUnaryOperatorKind.Negate && ConstantFolding.Value(operand) is { } constant &&
            ConstantFolding.Negate(constant, out _) == ConstantFolding.Failure.Overflow)
        {
            _diagnostics.ReportConstantOverflow(syntax.Span);
        }
        // `-b` on a Byte is an Int, as in C#.
        return new BoundUnaryExpression(kind, Widen(operand, type), type);
    }

    private BoundExpression BindBinary(BinaryExpressionSyntax syntax)
    {
        if (syntax.Operator.Kind == TokenKind.QuestionQuestion)
            return BindNullCoalescing(syntax);

        var kind = Operators.GetBinaryKind(syntax.Operator.Kind)!.Value;
        bool isEquality = kind is BoundBinaryOperatorKind.Equals or BoundBinaryOperatorKind.NotEquals;

        // `x == null`: bind the other side first so `null` knows which optional type it is.
        BoundExpression left, right;
        if (isEquality && syntax.Left is NullLiteralExpressionSyntax)
        {
            right = RequireValue(BindExpression(syntax.Right), syntax.Right.Span);
            left = BindExpression(syntax.Left, right.Type);
        }
        else
        {
            left = RequireValue(BindExpression(syntax.Left), syntax.Left.Span);
            right = RequireValue(BindExpression(syntax.Right, isEquality ? left.Type : null), syntax.Right.Span);
        }
        if (left.Type == TypeSymbol.Error || right.Type == TypeSymbol.Error)
            return new BoundErrorExpression();

        // `maybe == 5` compares with Some(5), as C# compares an int? with an int.
        if (isEquality && left.Type != right.Type)
        {
            if (left.Type.IsOption && IsConvertible(right.Type, left.Type))
                right = Convert(right, left.Type, syntax.Right.Span);
            else if (right.Type.IsOption && IsConvertible(left.Type, right.Type))
                left = Convert(left, right.Type, syntax.Left.Span);
            // `shape == circle`: a value compares with a trait it implements, as that trait.
            else if (left.Type is TraitSymbol && right.Type is not TraitSymbol && IsConvertible(right.Type, left.Type))
                right = Convert(right, left.Type, syntax.Right.Span);
            else if (right.Type is TraitSymbol && left.Type is not TraitSymbol && IsConvertible(left.Type, right.Type))
                left = Convert(left, right.Type, syntax.Left.Span);
            // `(1, 2) == pair`: tuples whose names or element types differ compare at their common type.
            else if (left.Type is TupleTypeSymbol && right.Type is TupleTypeSymbol &&
                     CommonType([left.Type, right.Type], null, out _) is TupleTypeSymbol common)
            {
                left = Convert(left, common, syntax.Left.Span);
                right = Convert(right, common, syntax.Right.Span);
            }
        }

        var resolution = Operators.ResolveBinary(kind, left.Type, right.Type);
        if (resolution is null)
        {
            _diagnostics.ReportUndefinedBinaryOperator(syntax.Operator.Span, syntax.Operator.Text, left.Type, right.Type);
            return new BoundErrorExpression();
        }

        var binary = new BoundBinaryExpression(
            Widen(left, resolution.OperandType), kind, Widen(right, resolution.OperandType), resolution.ResultType);
        CheckConstantArithmetic(binary, syntax.Span);
        return binary;
    }

    /// <summary>As C# would at compile time: arithmetic on constants mustn't overflow or divide by zero.</summary>
    private void CheckConstantArithmetic(BoundBinaryExpression binary, TextSpan span)
    {
        if (ConstantFolding.Value(binary.Left) is not { } left || ConstantFolding.Value(binary.Right) is not { } right)
            return;
        switch (ConstantFolding.Binary(binary.Operator, left, right, out _))
        {
            case ConstantFolding.Failure.Overflow:
                _diagnostics.ReportConstantOverflow(span);
                break;
            case ConstantFolding.Failure.DivideByZero:
                _diagnostics.ReportConstantDivisionByZero(span);
                break;
        }
    }

    private BoundExpression BindRange(RangeExpressionSyntax syntax)
    {
        BindExpression(syntax.Start);
        BindExpression(syntax.End);
        _diagnostics.ReportRangeOutsideFor(syntax.Span);
        return new BoundErrorExpression();
    }

    private BoundExpression BindAssignment(AssignmentExpressionSyntax syntax)
    {
        // `(a, b) = (b, a)`: deconstruction into existing variables.
        if (syntax.Target is TupleExpressionSyntax tupleTarget)
            return BindDeconstructionAssignment(syntax, tupleTarget);

        // .NET properties and indexers can be assigned: `sb.Length = 0`, `ages["Ada"] = 37`.
        if (syntax.Target is IndexExpressionSyntax or MemberAccessExpressionSyntax && BindClrAssignment(syntax) is { } clrAssignment)
            return clrAssignment;

        if (syntax.Target is not NameExpressionSyntax name)
        {
            BindExpression(syntax.Value);
            BindExpression(syntax.Target);
            if (syntax.Target is MemberAccessExpressionSyntax member)
                _diagnostics.ReportImmutableField(member.Member.Span, member.Member.Text);
            else
                _diagnostics.ReportInvalidAssignmentTarget(syntax.Target.Span);
            return new BoundErrorExpression();
        }

        if (_scope.Lookup(name.Identifier.Text) is not VariableSymbol variable)
        {
            BindExpression(syntax.Value);
            if (_scope.Lookup(name.Identifier.Text) is null)
                BindName(name);
            else
                _diagnostics.ReportInvalidAssignmentTarget(syntax.Target.Span);
            return new BoundErrorExpression();
        }

        if (!variable.IsMutable)
            _diagnostics.ReportCannotAssignImmutable(name.Span, variable);

        var value = RequireValue(BindExpression(syntax.Value, variable.Type), syntax.Value.Span);

        if (SyntaxFacts.GetCompoundAssignmentOperator(syntax.Operator.Kind) is not { } compound)
            return new BoundAssignmentExpression(variable, null, Convert(value, variable.Type, syntax.Value.Span));

        var kind = Operators.GetBinaryKind(compound)!.Value;
        if (value.Type == TypeSymbol.Error || variable.Type == TypeSymbol.Error)
            return new BoundAssignmentExpression(variable, kind, value);

        var resolution = Operators.ResolveBinary(kind, variable.Type, value.Type);
        if (resolution is null)
            _diagnostics.ReportUndefinedBinaryOperator(syntax.Operator.Span, syntax.Operator.Text, variable.Type, value.Type);
        else if (!CompoundResultFits(resolution.ResultType, variable.Type, value))
            _diagnostics.ReportCannotConvert(syntax.Value.Span, resolution.ResultType, variable.Type);
        else
            value = Widen(value, resolution.OperandType);

        return new BoundAssignmentExpression(variable, kind, value);
    }

    // Calls

    /// <summary><c>x |> f(a)</c> is <c>f(x, a)</c>, and <c>x |> f</c> is <c>f(x)</c>: the left side becomes the first argument.</summary>
    private BoundExpression BindPipe(PipeExpressionSyntax syntax, TypeSymbol? expected)
    {
        var call = syntax.Right is CallExpressionSyntax right
            ? right with { Span = syntax.Span, Arguments = [syntax.Left, .. right.Arguments] }
            : new CallExpressionSyntax(syntax.Span, syntax.Right, [syntax.Left]);
        return BindCall(call, expected);
    }

    private BoundExpression BindCall(CallExpressionSyntax syntax, TypeSymbol? expected)
    {
        switch (syntax.Callee)
        {
            case MemberAccessExpressionSyntax member when ResolveContainer(member.Target) is { } container:
                return container switch
                {
                    // Geometry.area(c), System.Text.StringBuilder()
                    NamespaceSymbol @namespace when !member.Member.IsMissing =>
                        BindCallTo(MemberOf(@namespace, member.Member.Text) ?? NotInNamespace(member.Member, @namespace), member.Member, syntax, expected, member.TypeArguments),
                    // Point.origin(), Shape.Circle(1.0), Math.Sqrt(2.0), Enumerable.Empty<Int>()
                    TypeSymbol type => BindStaticMember(type, member.Member, syntax.Arguments, syntax.Span, expected, member.TypeArguments),
                    ClrTypeGroupSymbol group when NonGenericMember(group) is { } type =>
                        BindStaticMember(type, member.Member, syntax.Arguments, syntax.Span, expected, member.TypeArguments),
                    _ => BindArgumentsForDiagnostics(syntax.Arguments),
                };

            case MemberAccessExpressionSyntax member:
            {
                var receiver = RequireValue(BindExpression(member.Target), member.Target.Span);
                return BindMethodCall(receiver, member.Member, syntax, expected, member.TypeArguments);
            }

            case NameExpressionSyntax name:
                return name.Identifier.IsMissing
                    ? BindArgumentsForDiagnostics(syntax.Arguments)
                    : BindCallTo(_scope.Lookup(name.Identifier.Text), name.Identifier, syntax, expected, name.TypeArguments);

            default:
            {
                // `makeAdder(1)(2)`: calling the function value an expression produces.
                var callee = BindExpression(syntax.Callee);
                if (DelegateSignature(callee.Type) is not null)
                    return BindDelegateInvocation(callee, syntax, "function");
                if (callee.Type != TypeSymbol.Error)
                    _diagnostics.ReportNotCallable(syntax.Callee.Span, "This expression");
                return BindArgumentsForDiagnostics(syntax.Arguments);
            }
        }
    }

    /// <summary>
    /// If <paramref name="syntax"/> names a namespace or type (rather than a value), returns it:
    /// a <see cref="NamespaceSymbol"/>, <see cref="TypeSymbol"/> or <see cref="ClrTypeGroupSymbol"/>, or
    /// <see cref="ErrorSymbol"/> once a bad qualified name has been reported. Null means "bind it as a value".
    /// </summary>
    private Symbol? ResolveContainer(ExpressionSyntax syntax)
    {
        switch (syntax)
        {
            // `Box<Int>.empty()`, `List<Int>[3]`: a type name with type arguments.
            case NameExpressionSyntax { Identifier: { IsMissing: false } name, TypeArguments.IsEmpty: false } generic:
                return _scope.Lookup(name.Text) is (TypeSymbol or ClrTypeGroupSymbol) and var genericType
                    ? Constructed(genericType, name, name.Text, generic.TypeArguments, generic.Span, isQualified: false)
                    : null;

            case NameExpressionSyntax { Identifier: { IsMissing: false } name }:
                return _scope.Lookup(name.Text) switch
                {
                    (NamespaceSymbol or ClrTypeGroupSymbol or ErrorSymbol) and var symbol => symbol,
                    TypeSymbol type when type != TypeSymbol.Never && type != TypeSymbol.Error => type,
                    AmbiguousSymbol ambiguous => Reported(() => ReportAmbiguous(name, ambiguous)),
                    _ => null,
                };

            case MemberAccessExpressionSyntax { Member: { IsMissing: false } member } access
                when ResolveContainer(access.Target) is NamespaceSymbol @namespace:
                return MemberOf(@namespace, member.Text) switch
                {
                    (TypeSymbol or ClrTypeGroupSymbol) and var symbol when !access.TypeArguments.IsEmpty =>
                        Constructed(symbol, member, $"{@namespace.FullName}.{member.Text}", access.TypeArguments, access.Span, isQualified: true),
                    (NamespaceSymbol or TypeSymbol or ClrTypeGroupSymbol) and var symbol => symbol,
                    null => NotInNamespace(member, @namespace),
                    _ => null,
                };

            case MemberAccessExpressionSyntax access when ResolveContainer(access.Target) is ErrorSymbol:
                return ErrorSymbol.Instance;

            default:
                return null;
        }
    }

    /// <summary>A generic type named in an expression with its type arguments, or <see cref="ErrorSymbol"/> once reported.</summary>
    private Symbol Constructed(Symbol symbol, Token name, string displayName, ImmutableArray<TypeSyntax> arguments, TextSpan span, bool isQualified) =>
        BindNamedType(symbol, name, displayName, arguments, span, isQualified) is var type && type != TypeSymbol.Error
            ? type
            : ErrorSymbol.Instance;

    private static ErrorSymbol Reported(Action report)
    {
        report();
        return ErrorSymbol.Instance;
    }

    private ErrorSymbol NotInNamespace(Token member, NamespaceSymbol @namespace) =>
        Reported(() =>
        {
            if (@namespace != _file.Namespace || !ReportIfPrivateElsewhere(member))
                _diagnostics.ReportNotInNamespace(member.Span, member.Text, @namespace.IsGlobal ? "<global>" : @namespace.FullName);
        });

    /// <summary>Static members of a name shared by several generic arities come from its non-generic type, if any.</summary>
    private TypeSymbol? NonGenericMember(ClrTypeGroupSymbol group) =>
        group.WithArity(0) is { } type ? ClrTypes.FromClr(type, new Dictionary<Type, TypeSymbol>(), _option) : null;

    /// <summary>Calls whatever <paramref name="symbol"/> (named by <paramref name="name"/>) is: a function, a record or .NET type (construction), or a variant.</summary>
    private BoundExpression BindCallTo(
        Symbol? symbol,
        Token name,
        CallExpressionSyntax syntax,
        TypeSymbol? expected,
        ImmutableArray<TypeSyntax> typeArgumentSyntax = default)
    {
        var calleeName = name.Text;
        typeArgumentSyntax = typeArgumentSyntax.IsDefault ? [] : typeArgumentSyntax;

        // `List<Int>()`, `Pair<Int, String>(1, "a")`: construct the type with the written type arguments.
        if (!typeArgumentSyntax.IsEmpty && symbol is TypeSymbol or ClrTypeGroupSymbol)
        {
            var type = BindNamedType(symbol, name, calleeName, typeArgumentSyntax, syntax.Callee.Span, isQualified: false);
            return type switch
            {
                ConstructedTypeSymbol { Definition: RecordSymbol record } => BindRecordCreation(record, syntax, type),
                ClrTypeSymbol clr => BindClrConstruction(clr, syntax.Arguments, syntax.Span, expected),
                _ when type == TypeSymbol.Error => BindArgumentsForDiagnostics(syntax.Arguments),
                _ => BindCallTo(type, name, syntax, expected),
            };
        }

        var explicitArguments = ExplicitTypeArguments(typeArgumentSyntax);
        switch (symbol)
        {
            case not FunctionSymbol and not (null or ErrorSymbol or AmbiguousSymbol) when explicitArguments is not null:
            case FunctionSymbol when explicitArguments is not null && symbol == BuiltinFunctions.Print:
                BindArgumentsForDiagnostics(syntax.Arguments);
                _diagnostics.ReportNotGeneric(syntax.Callee.Span, calleeName);
                return new BoundErrorExpression();

            case FunctionSymbol function when function == BuiltinFunctions.Print:
            {
                if (syntax.Arguments.Length != 1)
                {
                    BindArgumentsForDiagnostics(syntax.Arguments);
                    _diagnostics.ReportWrongArgumentCount(syntax.Span, calleeName, 1, syntax.Arguments.Length);
                    return new BoundErrorExpression();
                }
                var argument = RequireValue(BindExpression(syntax.Arguments[0]), syntax.Arguments[0].Span);
                return new BoundCallExpression(function, [], [argument], TypeSymbol.Unit);
            }

            // A variable holding a function: `f(1)`.
            case VariableSymbol variable when DelegateSignature(variable.Type) is not null:
                return BindDelegateInvocation(new BoundVariableExpression(variable), syntax, calleeName);

            case FunctionSymbol function:
            {
                var map = new Dictionary<TypeParameterSymbol, TypeSymbol>();
                if (!ApplyExplicitTypeArguments(explicitArguments, function.TypeParameters, map, syntax.Callee.Span, calleeName))
                    return BindArgumentsForDiagnostics(syntax.Arguments);
                Seed(function.ReturnType, expected, map, function.TypeParameters);
                var arguments = BindArguments(syntax.Span, calleeName, function.Parameters.Select(p => p.Type), function.TypeParameters, map, syntax.Arguments);
                if (arguments is null)
                    return new BoundErrorExpression();

                var typeArguments = TypeArgumentsFrom(function.TypeParameters, map, syntax.Span);
                return new BoundCallExpression(function, typeArguments, arguments.Value, TypeSubstitution.Substitute(function.ReturnType, map));
            }

            case RecordSymbol record:
                return BindRecordCreation(record, syntax, expected);

            case VariantSymbol variant:
                // A built-in variant used bare: `Some(1)`, `Ok(x)`, `Err(e)`.
                return BindVariantCreation(variant.Enum, variant, syntax.Arguments, syntax.Span, expected);

            // StringBuilder(), Random(42), List() (type arguments from the expected type)
            case ClrTypeSymbol clr:
                return BindClrConstruction(clr, syntax.Arguments, syntax.Span, expected);

            // `String('#', 3)`: a builtin type's .NET constructors (C#'s `new string('#', 3)`).
            case TypeSymbol builtin when ClrTypes.BuiltinTypes.TryGetValue(builtin, out var clrType) && clrType.GetConstructors().Length > 0:
                return BindClrInvocation(
                    null, builtin, clrType.GetConstructors(), builtin.Name, syntax.Arguments, [], syntax.Span, new Dictionary<Type, TypeSymbol>());

            case ClrTypeGroupSymbol group:
            {
                var chosen = expected is ClrTypeSymbol wanted && group.Types.Contains(wanted.Definition.Type)
                    ? wanted.Definition
                    : group.WithArity(0) is { } plain ? ClrTypeSymbol.For(plain) : ClrTypeSymbol.For(group.Types[0]);
                return BindClrConstruction(chosen, syntax.Arguments, syntax.Span, expected);
            }

            case AmbiguousSymbol ambiguous:
                BindArgumentsForDiagnostics(syntax.Arguments);
                ReportAmbiguous(name, ambiguous);
                return new BoundErrorExpression();

            case ErrorSymbol:
                return BindArgumentsForDiagnostics(syntax.Arguments);

            case null:
                BindArgumentsForDiagnostics(syntax.Arguments);
                if (!ReportIfPrivateElsewhere(name))
                    _diagnostics.ReportUndefinedName(name.Span, calleeName);
                return new BoundErrorExpression();

            default:
                BindArgumentsForDiagnostics(syntax.Arguments);
                _diagnostics.ReportNotCallable(name.Span, $"'{calleeName}'");
                return new BoundErrorExpression();
        }
    }

    private BoundExpression BindRecordCreation(RecordSymbol record, CallExpressionSyntax syntax, TypeSymbol? expected)
    {
        var map = new Dictionary<TypeParameterSymbol, TypeSymbol>();
        Seed(record.SelfType, expected, map, record.TypeParameters);
        var arguments = BindArguments(syntax.Span, record.Name, record.Fields.Select(f => f.Type), record.TypeParameters, map, syntax.Arguments);
        if (arguments is null)
            return new BoundErrorExpression();

        var type = record.Construct(TypeArgumentsFrom(record.TypeParameters, map, syntax.Span));
        return new BoundRecordCreationExpression(record, arguments.Value, type);
    }

    /// <summary><c>Shape.Circle(1.0)</c>, <c>Shape.Empty</c>, or a static method call <c>Point.origin()</c>.</summary>
    /// <param name="arguments">Null when there is no argument list, as in <c>Shape.Empty</c>.</param>
    private BoundExpression BindStaticMember(
        TypeSymbol type,
        Token member,
        ImmutableArray<ExpressionSyntax>? arguments,
        TextSpan span,
        TypeSymbol? expected,
        ImmutableArray<TypeSyntax> typeArgumentSyntax = default)
    {
        if (member.IsMissing)
            return BindArgumentsForDiagnostics(arguments);

        var explicitArguments = ExplicitTypeArguments(typeArgumentSyntax.IsDefault ? [] : typeArgumentSyntax);

        // `Shape.Circle(1.0)`; `Option<Int>.None` takes the enum's type arguments from its name.
        if (type.OriginalDefinition is EnumSymbol @enum && @enum.FindVariant(member.Text) is { } variant)
        {
            if (explicitArguments is not null)
            {
                BindArgumentsForDiagnostics(arguments);
                _diagnostics.ReportNotGeneric(member.Span, variant.QualifiedName);
                return new BoundErrorExpression();
            }
            return BindVariantCreation(@enum, variant, arguments, span, type == @enum ? expected : type);
        }

        var method = FindMethod(type, member.Text);
        if (method is null)
        {
            // Math.Sqrt(2.0), Math.PI, Int.MaxValue, Enumerable.Empty<Int>()
            if (IsClrBacked(type))
                return BindClrStaticMember(type, member, arguments, span, explicitArguments);

            BindArgumentsForDiagnostics(arguments);
            if (type is EnumSymbol enumType)
                _diagnostics.ReportUnknownVariant(member.Span, enumType, member.Text);
            else
                _diagnostics.ReportUnknownMethod(member.Span, type, member.Text);
            return new BoundErrorExpression();
        }

        CheckAccessible(method, member);
        if (arguments is null)
        {
            _diagnostics.ReportNotAValue(member.Span, member.Text, "method");
            return new BoundErrorExpression();
        }

        if (!method.IsStatic)
        {
            BindArgumentsForDiagnostics(arguments);
            _diagnostics.ReportInstanceMethodCalledStatically(member.Span, method.Name);
            return new BoundErrorExpression();
        }

        // `Stack.empty()` infers the type's own type arguments as well as the method's; `Stack<Int>.empty()` names them.
        var definition = type.OriginalDefinition as NamedTypeDefinition;
        ImmutableArray<TypeParameterSymbol> inferred = [.. definition?.TypeParameters ?? [], .. method.TypeParameters];
        var map = TypeSubstitution.MapFor(type);
        if (!ApplyExplicitTypeArguments(explicitArguments, method.TypeParameters, map, member.Span, method.Name))
            return BindArgumentsForDiagnostics(arguments);
        Seed(method.ReturnType, expected, map, inferred);
        var boundArguments = BindArguments(span, $"{type}.{method.Name}", method.Parameters.Select(p => p.Type), inferred, map, arguments.Value);
        if (boundArguments is null)
            return new BoundErrorExpression();

        var receiverType = definition is { IsGeneric: true }
            ? definition.Construct(TypeArgumentsFrom(definition.TypeParameters, map, span))
            : type;
        var typeArguments = TypeArgumentsFrom(method.TypeParameters, map, span);
        return new BoundMethodCallExpression(
            null, receiverType, method, typeArguments, boundArguments.Value, TypeSubstitution.Substitute(method.ReturnType, map));
    }

    private BoundExpression BindMethodCall(
        BoundExpression receiver,
        Token member,
        CallExpressionSyntax syntax,
        TypeSymbol? expected,
        ImmutableArray<TypeSyntax> typeArgumentSyntax)
    {
        if (receiver.Type == TypeSymbol.Error || member.IsMissing)
            return BindArgumentsForDiagnostics(syntax.Arguments);

        var explicitArguments = ExplicitTypeArguments(typeArgumentSyntax);
        var method = FindMethod(receiver.Type, member.Text);

        // A record field holding a function: `handler.onClick(event)`.
        if (method is null && FindField(receiver.Type, member.Text) is { } delegateField &&
            DelegateSignature(TypeSubstitution.FieldType(delegateField, receiver.Type)) is not null)
        {
            var field = new BoundFieldAccessExpression(receiver, delegateField, TypeSubstitution.FieldType(delegateField, receiver.Type));
            return BindDelegateInvocation(field, syntax, member.Text);
        }

        if (method is null && IsClrBacked(receiver.Type))
            return BindClrInstanceCall(receiver, member, syntax.Arguments, syntax.Span, explicitArguments);

        if (method is not null)
            CheckAccessible(method, member);
        else
        {
            BindArgumentsForDiagnostics(syntax.Arguments);
            if (FindField(receiver.Type, member.Text) is not null)
                _diagnostics.ReportNotCallable(member.Span, $"Field '{member.Text}'");
            else
                _diagnostics.ReportUnknownMethod(member.Span, receiver.Type, member.Text);
            return new BoundErrorExpression();
        }

        if (method.IsStatic)
        {
            BindArgumentsForDiagnostics(syntax.Arguments);
            _diagnostics.ReportStaticMethodOnInstance(member.Span, method.Name, receiver.Type);
            return new BoundErrorExpression();
        }

        // The receiver fixes the type's own type arguments (Box<Int> → T = Int); infer the method's, unless written.
        var map = TypeSubstitution.MapFor(receiver.Type);
        if (!ApplyExplicitTypeArguments(explicitArguments, method.TypeParameters, map, member.Span, method.Name))
            return BindArgumentsForDiagnostics(syntax.Arguments);
        Seed(method.ReturnType, expected, map, method.TypeParameters);
        var arguments = BindArguments(syntax.Span, method.Name, method.Parameters.Select(p => p.Type), method.TypeParameters, map, syntax.Arguments);
        if (arguments is null)
            return new BoundErrorExpression();

        var typeArguments = TypeArgumentsFrom(method.TypeParameters, map, syntax.Span);
        return new BoundMethodCallExpression(
            receiver, receiver.Type, method, typeArguments, arguments.Value, TypeSubstitution.Substitute(method.ReturnType, map));
    }

    /// <summary>Methods available on a type: its extend methods, then default methods of the traits it implements.</summary>
    private MethodSymbol? FindMethod(TypeSymbol type, string name)
    {
        IEnumerable<MethodSymbol> candidates = type switch
        {
            TypeParameterSymbol parameter => parameter.Constraints.SelectMany(t => t.Methods),
            TraitSymbol trait => trait.Methods,
            _ when type.OriginalDefinition is NamedTypeDefinition definition =>
                definition.Methods.Concat(definition.Traits.SelectMany(t => t.Methods)),
            _ => _builtinMethods.TryGetValue(type, out var methods) ? methods : [],
        };
        return candidates.FirstOrDefault(m => m.Name == name);
    }

    private static FieldSymbol? FindField(TypeSymbol type, string name) =>
        (type.OriginalDefinition as RecordSymbol)?.Fields.FirstOrDefault(f => f.Name == name);

    /// <summary>
    /// <c>Option.Some(1)</c> or <c>Option.None</c>. Fieldless variants are written without parentheses,
    /// variants with fields always with them. A generic enum's type arguments come from the fields' values
    /// or, failing that, from the expected type.
    /// </summary>
    private BoundExpression BindVariantCreation(
        EnumSymbol @enum,
        VariantSymbol variant,
        ImmutableArray<ExpressionSyntax>? arguments,
        TextSpan span,
        TypeSymbol? expected)
    {
        if (arguments is null && !variant.Fields.IsEmpty)
        {
            var fields = string.Join(", ", variant.Fields.Select(f => $"{f.Name}: {f.Type}"));
            _diagnostics.ReportVariantRequiresArguments(span, variant.QualifiedName, fields);
            return new BoundErrorExpression();
        }

        if (arguments is not null && variant.Fields.IsEmpty)
        {
            BindArgumentsForDiagnostics(arguments);
            _diagnostics.ReportVariantHasNoFields(span, variant.QualifiedName);
            return new BoundErrorExpression();
        }

        var map = new Dictionary<TypeParameterSymbol, TypeSymbol>();
        Seed(@enum.SelfType, expected, map, @enum.TypeParameters);
        var boundArguments = BindArguments(span, variant.QualifiedName, variant.Fields.Select(f => f.Type), @enum.TypeParameters, map, arguments ?? []);
        if (boundArguments is null)
            return new BoundErrorExpression();

        var type = @enum.Construct(TypeArgumentsFrom(@enum.TypeParameters, map, span));
        return new BoundVariantCreationExpression(variant, boundArguments.Value, type);
    }

    private BoundErrorExpression BindArgumentsForDiagnostics(ImmutableArray<ExpressionSyntax>? arguments)
    {
        foreach (var argument in arguments ?? [])
        {
            if (argument is not OutArgumentSyntax)
                BindExpression(argument);
        }
        return new BoundErrorExpression();
    }

    /// <summary><c>out</c> outside an argument for a .NET out parameter (Piper functions and function values have none).</summary>
    private BoundErrorExpression ReportMisplacedOut(OutArgumentSyntax syntax)
    {
        _diagnostics.ReportMisplacedOut(syntax.Span);
        return new BoundErrorExpression();
    }

    /// <summary>
    /// Binds call arguments, inferring the type parameters in <paramref name="inferred"/> as it goes: an
    /// argument whose parameter type is already known is bound with that as its expected type; otherwise
    /// its type is unified with the parameter type. Arguments are then converted to the parameter types.
    /// </summary>
    private ImmutableArray<BoundExpression>? BindArguments(
        TextSpan span,
        string calleeName,
        IEnumerable<TypeSymbol> parameterTypes,
        IReadOnlyList<TypeParameterSymbol> inferred,
        Dictionary<TypeParameterSymbol, TypeSymbol> map,
        ImmutableArray<ExpressionSyntax> arguments)
    {
        var declared = parameterTypes.ToList();
        if (declared.Count != arguments.Length)
        {
            BindArgumentsForDiagnostics(arguments);
            _diagnostics.ReportWrongArgumentCount(span, calleeName, declared.Count, arguments.Length);
            return null;
        }

        // Lambdas are bound after the other arguments: those usually fix the lambda's parameter types
        // (map(xs, x => x * 2) learns T from xs), and the lambda's body then tells us the rest (U).
        var bound = new BoundExpression[declared.Count];
        foreach (bool lambdas in new[] { false, true })
        {
            for (int i = 0; i < declared.Count; i++)
            {
                if (arguments[i] is LambdaExpressionSyntax != lambdas)
                    continue;

                var unresolved = inferred.Where(p => !map.ContainsKey(p)).ToHashSet();
                var parameterType = TypeSubstitution.Substitute(declared[i], map);
                bool known = !TypeSubstitution.Mentions(parameterType, unresolved);

                BoundExpression argument;
                if (!known && arguments[i] is LambdaExpressionSyntax lambda &&
                    DelegateSignature(parameterType) is { } signature &&
                    !signature.Parameters.Any(p => TypeSubstitution.Mentions(p, unresolved)))
                {
                    // Parameter types known, result type not: infer it from the body.
                    argument = BindLambdaWith(lambda, signature.Parameters, returnType: null, delegateType: null);
                }
                else
                {
                    argument = BindExpression(arguments[i], known ? parameterType : null);
                }

                argument = RequireValue(argument, arguments[i].Span);
                if (!known)
                    Infer(declared[i], argument.Type, map, unresolved);
                bound[i] = argument;
            }
        }

        foreach (var parameter in inferred)
        {
            if (!map.ContainsKey(parameter))
            {
                _diagnostics.ReportCannotInferTypeArgument(span, parameter.Name, calleeName);
                map[parameter] = TypeSymbol.Error;
            }
        }

        return [.. bound.Select((argument, i) =>
            Convert(argument, TypeSubstitution.Substitute(declared[i], map), arguments[i].Span))];
    }

    /// <summary>Uses the expected type of a call's result to fix type parameters before looking at the arguments.</summary>
    private void Seed(
        TypeSymbol declaredResult,
        TypeSymbol? expected,
        Dictionary<TypeParameterSymbol, TypeSymbol> map,
        IReadOnlyList<TypeParameterSymbol> inferred)
    {
        if (expected is not null && inferred.Count > 0)
            Infer(declaredResult, expected, map, inferred.ToHashSet());
    }

    /// <summary>Matches <paramref name="declared"/> against <paramref name="actual"/>, binding type parameters it finds.</summary>
    private void Infer(
        TypeSymbol declared,
        TypeSymbol actual,
        Dictionary<TypeParameterSymbol, TypeSymbol> map,
        IReadOnlySet<TypeParameterSymbol> inferred)
    {
        if (!actual.IsValueType || actual == TypeSymbol.Error)
            return;

        switch (declared)
        {
            case TypeParameterSymbol parameter when inferred.Contains(parameter):
                map.TryAdd(parameter, actual);
                break;
            case ConstructedTypeSymbol constructed when actual is ConstructedTypeSymbol other && other.Definition == constructed.Definition:
                for (int i = 0; i < constructed.TypeArguments.Length; i++)
                    Infer(constructed.TypeArguments[i], other.TypeArguments[i], map, inferred);
                break;
            case ConstructedTypeSymbol { IsOption: true } optional:
                // A plain value passed for a T? parameter (implicit Some) still tells us T.
                Infer(optional.TypeArguments[0], actual, map, inferred);
                break;
            case TupleTypeSymbol tuple when actual is TupleTypeSymbol other && other.ElementTypes.Length == tuple.ElementTypes.Length:
                for (int i = 0; i < tuple.ElementTypes.Length; i++)
                    Infer(tuple.ElementTypes[i], other.ElementTypes[i], map, inferred);
                break;
            case ClrTypeSymbol { TypeArguments.IsEmpty: false } clr:
            {
                // List<T> against List<Int>, IEnumerable<T> against Int[].
                var match = SelfAndBaseTypes(actual).OfType<ClrTypeSymbol>().FirstOrDefault(t => t.Definition == clr.Definition);
                if (match is not null)
                {
                    for (int i = 0; i < clr.TypeArguments.Length; i++)
                        Infer(clr.TypeArguments[i], match.TypeArguments[i], map, inferred);
                }
                break;
            }
        }
    }

    private ImmutableArray<TypeSymbol> TypeArgumentsFrom(
        ImmutableArray<TypeParameterSymbol> parameters,
        Dictionary<TypeParameterSymbol, TypeSymbol> map,
        TextSpan span)
    {
        var arguments = parameters.Select(p => map.TryGetValue(p, out var t) ? t : TypeSymbol.Error).ToImmutableArray();
        CheckConstraints(parameters, arguments, span);
        return arguments;
    }

    private BoundExpression BindMemberAccess(MemberAccessExpressionSyntax syntax, TypeSymbol? expected)
    {
        switch (ResolveContainer(syntax.Target))
        {
            case NamespaceSymbol @namespace when !syntax.Member.IsMissing:
            {
                var member = MemberOf(@namespace, syntax.Member.Text) ?? NotInNamespace(syntax.Member, @namespace);
                return BindSymbolAsValue(member, syntax.Member, syntax.Span, expected, syntax.TypeArguments);
            }
            // Only functions and types take type arguments: `p.x<Int>` and `Int.MaxValue<Long>` are errors.
            case var _ when !syntax.TypeArguments.IsEmpty:
                _diagnostics.ReportNotGeneric(syntax.Member.Span, syntax.Member.Text);
                return new BoundErrorExpression();
            case TypeSymbol type:
                return BindStaticMember(type, syntax.Member, arguments: null, syntax.Span, expected);
            case ClrTypeGroupSymbol group when NonGenericMember(group) is { } nonGeneric:
                return BindStaticMember(nonGeneric, syntax.Member, arguments: null, syntax.Span, expected);
            case NamespaceSymbol or ClrTypeGroupSymbol or ErrorSymbol:
                return new BoundErrorExpression();
        }

        var target = RequireValue(BindExpression(syntax.Target), syntax.Target.Span);
        if (target.Type == TypeSymbol.Error || syntax.Member.IsMissing)
            return new BoundErrorExpression();

        if (FindField(target.Type, syntax.Member.Text) is { } field)
            return new BoundFieldAccessExpression(target, field, TypeSubstitution.FieldType(field, target.Type));

        // `pair.Item1`, `point.x`.
        if (target.Type is TupleTypeSymbol tuple && tuple.IndexOf(syntax.Member.Text) is >= 0 and var index)
            return new BoundTupleElementExpression(target, index, tuple.ElementTypes[index]);

        // sb.Length, list.Count, "abc".Length
        if (FindMethod(target.Type, syntax.Member.Text) is null && IsClrBacked(target.Type))
            return BindClrInstanceMember(target, syntax.Member);

        if (FindMethod(target.Type, syntax.Member.Text) is not null)
            _diagnostics.ReportNotAValue(syntax.Member.Span, syntax.Member.Text, "method");
        else
            _diagnostics.ReportUnknownMember(syntax.Member.Span, target.Type, syntax.Member.Text);
        return new BoundErrorExpression();
    }

    // Control flow

    private BoundExpression BindIf(IfExpressionSyntax syntax, TypeSymbol? expected)
    {
        // Variables bound by `is` in the condition are in scope in the then-block, but not the else.
        var outer = _scope;
        _scope = new Scope(outer);
        var outerOutScope = _outScope;
        _outScope = outer;
        var condition = BindCondition(syntax.Condition, allowPatternBindings: true);
        _outScope = outerOutScope;
        var then = BindBlock(syntax.Then, expected);
        _scope = outer;
        if (syntax.Else is null)
            return new BoundIfExpression(condition, then, null, TypeSymbol.Unit);

        var @else = BindExpression(syntax.Else, expected);
        var type = CommonType([then.Type, @else.Type], expected, out _);
        if (type is null)
        {
            _diagnostics.ReportIncompatibleBranches(new TextSpan(syntax.Span.Start, 2), then.Type, @else.Type);
            type = TypeSymbol.Error;
        }

        if (NeedsBranchConversion(type))
            return new BoundIfExpression(condition, ConvertBlock(then, type), ConvertBranch(@else, type), type);
        return new BoundIfExpression(condition, then, @else, type);
    }

    /// <summary>
    /// The type of an expression whose value comes from one of several branches: Never is ignored and
    /// numeric types widen. If the branches have no common type but all convert to the expected type
    /// (e.g. two records implementing a trait), that is used. Null if there is no common type.
    /// </summary>
    private TypeSymbol? CommonType(IReadOnlyList<TypeSymbol> types, TypeSymbol? expected, out int failedIndex)
    {
        failedIndex = -1;
        var common = TypeSymbol.Never;
        for (int i = 0; i < types.Count; i++)
        {
            var unified = Unify(common, types[i]);
            if (unified is null)
            {
                failedIndex = i;
                break;
            }
            common = unified;
        }

        if (failedIndex < 0)
            return common;

        if (expected is not null && types.All(t => t == TypeSymbol.Never || IsConvertible(t, expected)))
        {
            failedIndex = -1;
            return expected;
        }
        return null;
    }

    private TypeSymbol? Unify(TypeSymbol a, TypeSymbol b)
    {
        if (a == TypeSymbol.Error || b == TypeSymbol.Error)
            return TypeSymbol.Error;
        if (a == TypeSymbol.Never || a == b)
            return b;
        if (b == TypeSymbol.Never)
            return a;
        if (IsConvertible(a, b))
            return b;
        if (IsConvertible(b, a))
            return a;
        return null;
    }

    private static bool NeedsBranchConversion(TypeSymbol type) => type.IsNumeric || type is TraitSymbol or TupleTypeSymbol || type.IsOption;

    /// <param name="allowPatternBindings">
    /// True for if/while conditions and match guards: an <c>is</c> pattern there may bind variables, which
    /// are then in scope for the rest of a <c>&amp;&amp;</c> chain and the body, as with C#'s definite assignment.
    /// </param>
    private BoundExpression BindCondition(ExpressionSyntax syntax, bool allowPatternBindings = false)
    {
        var condition = allowPatternBindings ? BindBindingCondition(syntax) : BindExpression(syntax);
        if (condition.Type == TypeSymbol.Error || condition.Type == TypeSymbol.Bool)
            return condition;
        _diagnostics.ReportConditionNotBool(syntax.Span, condition.Type);
        return new BoundErrorExpression();
    }

    private BoundExpression BindBindingCondition(ExpressionSyntax syntax)
    {
        switch (syntax)
        {
            case IsExpressionSyntax test:
                return BindIs(test, allowBindings: true);

            case ParenthesizedExpressionSyntax parenthesized:
                return BindBindingCondition(parenthesized.Expression);

            case BinaryExpressionSyntax { Operator.Kind: TokenKind.AmpersandAmpersand } and:
            {
                var left = BindCondition(and.Left, allowPatternBindings: true);
                var right = BindCondition(and.Right, allowPatternBindings: true);
                if (left.Type == TypeSymbol.Error || right.Type == TypeSymbol.Error)
                    return new BoundErrorExpression();

                // Lowering would evaluate a right side with statements after storing the left in a temporary,
                // which loses C#'s knowledge that the pattern variables are assigned.
                if (BindsVariables(left) && ContainsStatements(right))
                    _diagnostics.ReportIsBindingBeforeStatements(and.Right.Span);
                return new BoundBinaryExpression(left, BoundBinaryOperatorKind.LogicalAnd, right, TypeSymbol.Bool);
            }

            default:
                return BindExpression(syntax);
        }
    }

    private static bool BindsVariables(BoundExpression condition) => condition switch
    {
        BoundIsPatternExpression test => test.Pattern.HasBindings,
        BoundBinaryExpression { Operator: BoundBinaryOperatorKind.LogicalAnd } and => BindsVariables(and.Left) || BindsVariables(and.Right),
        _ => false,
    };

    // Null safety

    /// <summary><c>value is Pattern</c>. Bindings are declared in the current scope, which the caller has set up.</summary>
    private BoundExpression BindIs(IsExpressionSyntax syntax, bool allowBindings)
    {
        var value = RequireValue(BindExpression(syntax.Expression), syntax.Expression.Span);
        var pattern = BindPattern(syntax.Pattern, value.Type, allowBindings: true);
        if (!allowBindings && pattern.HasBindings)
            _diagnostics.ReportIsBindingOutsideCondition(syntax.Pattern.Span);
        if (value.Type == TypeSymbol.Error)
            return new BoundErrorExpression();
        return new BoundIsPatternExpression(value, pattern);
    }

    /// <summary><c>null</c> is the None of the optional type the context expects.</summary>
    private BoundExpression BindNull(NullLiteralExpressionSyntax syntax, TypeSymbol? expected)
    {
        if (expected is { IsOption: true })
            return new BoundVariantCreationExpression(_option.FindVariant("None")!, [], expected);
        if (expected != TypeSymbol.Error)
            _diagnostics.ReportNullWithoutOptionalType(syntax.Span);
        return new BoundErrorExpression();
    }

    /// <summary><c>left ?? right</c>: the left's value if it has one, otherwise <c>right</c> (only evaluated then).</summary>
    private BoundExpression BindNullCoalescing(BinaryExpressionSyntax syntax)
    {
        var left = RequireValue(BindExpression(syntax.Left), syntax.Left.Span);
        if (left.Type == TypeSymbol.Error || !left.Type.IsOption)
        {
            if (left.Type != TypeSymbol.Error)
                _diagnostics.ReportRequiresOptional(syntax.Operator.Span, "??", left.Type);
            BindExpression(syntax.Right);
            return new BoundErrorExpression();
        }

        var valueType = left.Type.TypeArguments[0];
        // `x ?? throw …`: a right side that never completes is fine.
        var right = BindExpression(syntax.Right, valueType);
        if (right.Type == TypeSymbol.Never)
            return new BoundNullCoalescingExpression(left, right, valueType);
        right = RequireValue(right, syntax.Right.Span);

        // `a ?? b ?? c` where b is optional too keeps the result optional.
        if (right.Type == left.Type)
            return new BoundNullCoalescingExpression(left, right, left.Type);

        return new BoundNullCoalescingExpression(left, Convert(right, valueType, syntax.Right.Span), valueType);
    }

    /// <summary>
    /// <c>target?.chain</c>: the chain is bound against the unwrapped target. The result is optional
    /// (flattened if the chain already is), or Unit when the chain is a call made for its effect.
    /// </summary>
    private BoundExpression BindConditionalAccess(ConditionalAccessExpressionSyntax syntax)
    {
        var target = RequireValue(BindExpression(syntax.Target), syntax.Target.Span);
        bool valid = target.Type.IsOption;
        if (!valid && target.Type != TypeSymbol.Error)
            _diagnostics.ReportRequiresOptional(syntax.WhenNotNull.Span, "?.", target.Type);

        var receiverType = valid ? target.Type.TypeArguments[0] : target.Type;
        var receiver = new VariableSymbol("value", receiverType, isMutable: false, VariableKind.Temporary);
        _implicitReceivers.Push(receiver);
        var whenNotNull = BindExpression(syntax.WhenNotNull);
        _implicitReceivers.Pop();

        if (!valid || whenNotNull.Type == TypeSymbol.Error)
            return new BoundErrorExpression();

        var type = whenNotNull.Type switch
        {
            { IsValueType: false } => TypeSymbol.Unit,
            { IsOption: true } optional => optional,
            var value => _option.Construct([value]),
        };
        return new BoundConditionalAccessExpression(target, receiver, whenNotNull, type);
    }

    /// <summary><c>operand?</c>: unwraps, or returns the None/Err to the caller (the function must return the same kind).</summary>
    private BoundExpression BindTry(TryExpressionSyntax syntax)
    {
        var operand = RequireValue(BindExpression(syntax.Operand), syntax.Operand.Span);
        if (operand.Type == TypeSymbol.Error)
            return new BoundErrorExpression();
        // `?` returns early, which a finally (or defer) block can't do.
        if (_finallyLoopDepth >= 0)
        {
            _diagnostics.ReportJumpOutOfFinally(syntax.Span, "?");
            return new BoundErrorExpression();
        }

        var returnType = _return!.Type;
        if (operand.Type.IsOption)
        {
            if (returnType.IsOption)
                return new BoundTryExpression(operand, operand.Type.TypeArguments[0], returnType);
            if (returnType != TypeSymbol.Error)
                _diagnostics.ReportTryOutsideOptionalFunction(syntax.Span, "Option");
            return new BoundErrorExpression();
        }

        if (operand.Type.IsResult)
        {
            if (!returnType.IsResult)
            {
                if (returnType != TypeSymbol.Error)
                    _diagnostics.ReportTryOutsideOptionalFunction(syntax.Span, "Result");
                return new BoundErrorExpression();
            }

            var error = operand.Type.TypeArguments[1];
            var returnedError = returnType.TypeArguments[1];
            if (!IsConvertible(error, returnedError))
            {
                _diagnostics.ReportCannotConvert(syntax.Span, error, returnedError);
                return new BoundErrorExpression();
            }
            return new BoundTryExpression(operand, operand.Type.TypeArguments[0], returnType);
        }

        _diagnostics.ReportTryOperandNotOptional(syntax.Span, operand.Type);
        return new BoundErrorExpression();
    }

    // Conversions

    /// <summary>
    /// Numeric widening; a value of a type that implements a trait used as that trait; or a plain value
    /// used as an optional one (implicit Some, like C#'s <c>int</c> to <c>int?</c>).
    /// </summary>
    private bool IsConvertible(TypeSymbol from, TypeSymbol to) =>
        Operators.IsImplicitlyConvertible(from, to) ||
        (to is TraitSymbol trait && from.IsValueType && Implements(from, trait)) ||
        (to.IsOption && from.IsValueType && IsConvertible(from, to.TypeArguments[0])) ||
        // .NET reference conversions: List<Int> to IEnumerable<Int>, String to IEnumerable<Char>, anything to object.
        (to is ClrTypeSymbol && from.IsValueType && (ClrTypes.ToClr(to) == typeof(object) || SelfAndBaseTypes(from).Contains(to))) ||
        (from is TupleTypeSymbol fromTuple && to is TupleTypeSymbol toTuple && IsTupleConvertible(fromTuple, toTuple)) ||
        // List<(Int, Int)> to List<(a: Int, b: Int)>: tuple names don't matter, at any depth.
        (from.IsValueType && TypeSubstitution.WithoutTupleNames(from) == TypeSubstitution.WithoutTupleNames(to));

    /// <summary>
    /// Whether `x op= y` can store its result in x (of type <paramref name="target"/>), as in C#: when the result converts
    /// implicitly, or when it's numeric and y itself converts implicitly to x's type, so `b += 1` works on a Byte.
    /// </summary>
    private bool CompoundResultFits(TypeSymbol result, TypeSymbol target, BoundExpression value) =>
        result == target || IsConvertible(result, target) ||
        (target.IsNumeric && result.IsNumeric && (IsConvertible(value.Type, target) || ConstantFolding.FitsImplicitly(value, target)));

    /// <summary>
    /// <c>(T)x</c>, as in C#: any implicit conversion; between numeric types (and Char), truncating or wrapping as C#'s
    /// unchecked casts do, though a constant that doesn't fit is an error; and from a trait, a .NET base type or object
    /// to something more specific, which throws InvalidCastException if the value isn't one.
    /// </summary>
    private BoundExpression BindCast(CastExpressionSyntax syntax)
    {
        var type = BindValueType(syntax.Type, "cast");
        var operand = RequireValue(BindExpression(syntax.Operand, type), syntax.Operand.Span);
        if (type == TypeSymbol.Error || operand.Type == TypeSymbol.Error)
            return new BoundErrorExpression();
        if (operand.Type == type)
            return operand;

        if (Operators.IsArithmetic(operand.Type) && Operators.IsArithmetic(type))
        {
            if (ConstantFolding.Value(operand) is { } constant && ConstantFolding.Cast(constant, type, out _) == ConstantFolding.Failure.Overflow)
                _diagnostics.ReportConstantOverflow(syntax.Span);
            return new BoundCastExpression(type, operand);
        }

        // Implicit conversions are allowed too; an implicit Some needs no cast in C#.
        if (IsConvertible(operand.Type, type))
            return type.IsOption ? Widen(operand, type) : new BoundCastExpression(type, operand);

        if (IsDowncast(operand.Type, type))
            return new BoundCastExpression(type, operand);

        _diagnostics.ReportCannotCast(syntax.Span, operand.Type, type);
        return new BoundErrorExpression();
    }

    /// <summary>A cast C# allows that may fail at run time: from a trait, object or .NET base type to a more specific type.</summary>
    private bool IsDowncast(TypeSymbol from, TypeSymbol to)
    {
        if (from is TraitSymbol trait && Implements(to, trait))
            return true;
        if (ClrTypes.ToClr(from) is not { } source)
            return false;
        if (source == typeof(object))
            return to.IsValueType;
        return ClrTypes.ToClr(to) is { } target && (source.IsAssignableFrom(target) || (source.IsInterface && !target.IsSealed));
    }

    /// <summary>Converts to <paramref name="type"/> implicitly, or reports an error.</summary>
    private BoundExpression Convert(BoundExpression expression, TypeSymbol type, TextSpan span)
    {
        if (expression.Type == type || expression.Type == TypeSymbol.Error || type == TypeSymbol.Error ||
            expression.Type == TypeSymbol.Never)
            return expression;

        if (IsConvertible(expression.Type, type))
            return Widen(expression, type);

        // `let b: Byte = 7;`: a constant that fits converts implicitly, as in C#.
        if (ConstantFolding.FitsImplicitly(expression, type))
            return new BoundConversionExpression(type, expression);

        _diagnostics.ReportCannotConvert(span, expression.Type, type);
        return new BoundErrorExpression();
    }

    /// <summary>Unit and Never have no value, so they can't be stored, passed or operated on.</summary>
    private BoundExpression RequireValue(BoundExpression expression, TextSpan span)
    {
        if (expression.Type.IsValueType)
            return expression;
        _diagnostics.ReportExpressionHasNoValue(span);
        return new BoundErrorExpression();
    }

    private static BoundExpression Widen(BoundExpression expression, TypeSymbol type)
    {
        if (expression.Type == type || expression.Type == TypeSymbol.Never || expression.Type == TypeSymbol.Error)
            return expression;

        // Implicit Some: wrap (after converting to the optional's value type, e.g. Int → Some(Long)).
        if (type.IsOption)
        {
            var some = ((EnumSymbol)type.OriginalDefinition).FindVariant("Some")!;
            return new BoundVariantCreationExpression(some, [Widen(expression, type.TypeArguments[0])], type);
        }

        // A tuple literal converts element by element, so `(1, 2)` can become `(Long, Int?)`.
        if (type is TupleTypeSymbol tuple && expression is BoundTupleExpression literal)
            return new BoundTupleExpression([.. literal.Elements.Select((e, i) => Widen(e, tuple.ElementTypes[i]))], tuple);

        return new BoundConversionExpression(type, expression);
    }

    /// <summary>Pushes a conversion into a block's trailing value so the branches of an if or match agree.</summary>
    private static BoundBlockExpression ConvertBlock(BoundBlockExpression block, TypeSymbol type) =>
        block.Type == type || block.Trailing is null
            ? block
            : new BoundBlockExpression(block.Statements, Widen(block.Trailing, type), type);

    private static BoundExpression ConvertBranch(BoundExpression branch, TypeSymbol type) => branch switch
    {
        BoundBlockExpression block => ConvertBlock(block, type),
        BoundIfExpression { Else: not null } nested when nested.Type != type =>
            new BoundIfExpression(nested.Condition, ConvertBlock(nested.Then, type), ConvertBranch(nested.Else, type), type),
        _ => Widen(branch, type),
    };
}
