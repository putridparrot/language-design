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
    private BoundExpression BindExpression(ExpressionSyntax syntax, TypeSymbol? expected = null) => syntax switch
    {
        LiteralExpressionSyntax s => BindLiteral(s),
        InterpolatedStringExpressionSyntax s => BindInterpolatedString(s),
        NameExpressionSyntax s => BindName(s),
        UnaryExpressionSyntax s => BindUnary(s),
        BinaryExpressionSyntax s => BindBinary(s),
        RangeExpressionSyntax s => BindRange(s),
        AssignmentExpressionSyntax s => BindAssignment(s),
        CallExpressionSyntax s => BindCall(s, expected),
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

    private BoundExpression BindName(NameExpressionSyntax syntax)
    {
        var name = syntax.Identifier;
        if (name.IsMissing)
            return new BoundErrorExpression();

        switch (_scope.Lookup(name.Text))
        {
            case VariableSymbol variable:
                return new BoundVariableExpression(variable);
            case FunctionSymbol:
                _diagnostics.ReportNotAValue(name.Span, name.Text, "function");
                return new BoundErrorExpression();
            case TypeSymbol:
                _diagnostics.ReportNotAValue(name.Span, name.Text, "type");
                return new BoundErrorExpression();
            default:
                if (name.Kind == TokenKind.SelfKeyword)
                    _diagnostics.ReportSelfOutsideMethod(name.Span);
                else
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
        return new BoundUnaryExpression(kind, operand, type);
    }

    private BoundExpression BindBinary(BinaryExpressionSyntax syntax)
    {
        var left = RequireValue(BindExpression(syntax.Left), syntax.Left.Span);
        var right = RequireValue(BindExpression(syntax.Right), syntax.Right.Span);
        if (left.Type == TypeSymbol.Error || right.Type == TypeSymbol.Error)
            return new BoundErrorExpression();

        var kind = Operators.GetBinaryKind(syntax.Operator.Kind)!.Value;
        var resolution = Operators.ResolveBinary(kind, left.Type, right.Type);
        if (resolution is null)
        {
            _diagnostics.ReportUndefinedBinaryOperator(syntax.Operator.Span, syntax.Operator.Text, left.Type, right.Type);
            return new BoundErrorExpression();
        }

        return new BoundBinaryExpression(
            Widen(left, resolution.OperandType), kind, Widen(right, resolution.OperandType), resolution.ResultType);
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
            BindName(name);
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
        else if (resolution.ResultType != variable.Type)
            _diagnostics.ReportCannotConvert(syntax.Value.Span, resolution.ResultType, variable.Type);
        else
            value = Widen(value, resolution.OperandType);

        return new BoundAssignmentExpression(variable, kind, value);
    }

    // Calls

    private BoundExpression BindCall(CallExpressionSyntax syntax, TypeSymbol? expected)
    {
        switch (syntax.Callee)
        {
            case MemberAccessExpressionSyntax { Target: NameExpressionSyntax typeName } member when LookupType(typeName) is { } type:
                return BindStaticMember(type, member.Member, syntax.Arguments, syntax.Span, expected);

            case MemberAccessExpressionSyntax member:
            {
                var receiver = RequireValue(BindExpression(member.Target), member.Target.Span);
                return BindMethodCall(receiver, member.Member, syntax, expected);
            }

            case NameExpressionSyntax name:
                return BindNamedCall(name, syntax, expected);

            default:
                if (BindExpression(syntax.Callee).Type != TypeSymbol.Error)
                    _diagnostics.ReportNotCallable(syntax.Callee.Span, "This expression");
                return BindArgumentsForDiagnostics(syntax.Arguments);
        }
    }

    /// <summary>A name that refers to a type (not a variable that happens to share its name).</summary>
    private TypeSymbol? LookupType(NameExpressionSyntax name) =>
        !name.Identifier.IsMissing &&
        _scope.Lookup(name.Identifier.Text) is TypeSymbol type && type != TypeSymbol.Never && type != TypeSymbol.Error
            ? type
            : null;

    private BoundExpression BindNamedCall(NameExpressionSyntax name, CallExpressionSyntax syntax, TypeSymbol? expected)
    {
        if (name.Identifier.IsMissing)
            return BindArgumentsForDiagnostics(syntax.Arguments);

        var calleeName = name.Identifier.Text;
        switch (_scope.Lookup(calleeName))
        {
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

            case FunctionSymbol function:
            {
                var map = new Dictionary<TypeParameterSymbol, TypeSymbol>();
                Seed(function.ReturnType, expected, map, function.TypeParameters);
                var arguments = BindArguments(syntax.Span, calleeName, function.Parameters.Select(p => p.Type), function.TypeParameters, map, syntax.Arguments);
                if (arguments is null)
                    return new BoundErrorExpression();

                var typeArguments = TypeArgumentsFrom(function.TypeParameters, map, syntax.Span);
                return new BoundCallExpression(function, typeArguments, arguments.Value, TypeSubstitution.Substitute(function.ReturnType, map));
            }

            case RecordSymbol record:
                return BindRecordCreation(record, syntax, expected);

            case null:
                BindArgumentsForDiagnostics(syntax.Arguments);
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
        TypeSymbol? expected)
    {
        if (member.IsMissing)
            return BindArgumentsForDiagnostics(arguments);

        if (type is EnumSymbol @enum && @enum.FindVariant(member.Text) is { } variant)
            return BindVariantCreation(@enum, variant, arguments, span, expected);

        var method = FindMethod(type, member.Text);
        if (method is null)
        {
            BindArgumentsForDiagnostics(arguments);
            if (type is EnumSymbol enumType)
                _diagnostics.ReportUnknownVariant(member.Span, enumType, member.Text);
            else
                _diagnostics.ReportUnknownMethod(member.Span, type, member.Text);
            return new BoundErrorExpression();
        }

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

        // `Stack.empty()` infers the type's own type arguments as well as the method's.
        var definition = type as NamedTypeDefinition;
        ImmutableArray<TypeParameterSymbol> inferred = [.. definition?.TypeParameters ?? [], .. method.TypeParameters];
        var map = new Dictionary<TypeParameterSymbol, TypeSymbol>();
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

    private BoundExpression BindMethodCall(BoundExpression receiver, Token member, CallExpressionSyntax syntax, TypeSymbol? expected)
    {
        if (receiver.Type == TypeSymbol.Error || member.IsMissing)
            return BindArgumentsForDiagnostics(syntax.Arguments);

        var method = FindMethod(receiver.Type, member.Text);
        if (method is null)
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

        // The receiver fixes the type's own type arguments (Box<Int> → T = Int); infer the method's.
        var map = TypeSubstitution.MapFor(receiver.Type);
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
            BindExpression(argument);
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

        var bound = new List<BoundExpression>();
        for (int i = 0; i < declared.Count; i++)
        {
            var unresolved = inferred.Where(p => !map.ContainsKey(p)).ToHashSet();
            var parameterType = TypeSubstitution.Substitute(declared[i], map);
            bool known = !TypeSubstitution.Mentions(parameterType, unresolved);

            var argument = RequireValue(BindExpression(arguments[i], known ? parameterType : null), arguments[i].Span);
            if (!known)
                Infer(declared[i], argument.Type, map, unresolved);
            bound.Add(argument);
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
    private static void Seed(
        TypeSymbol declaredResult,
        TypeSymbol? expected,
        Dictionary<TypeParameterSymbol, TypeSymbol> map,
        IReadOnlyList<TypeParameterSymbol> inferred)
    {
        if (expected is not null && inferred.Count > 0)
            Infer(declaredResult, expected, map, inferred.ToHashSet());
    }

    /// <summary>Matches <paramref name="declared"/> against <paramref name="actual"/>, binding type parameters it finds.</summary>
    private static void Infer(
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
        if (syntax.Target is NameExpressionSyntax typeName && LookupType(typeName) is { } type)
            return BindStaticMember(type, syntax.Member, arguments: null, syntax.Span, expected);

        var target = RequireValue(BindExpression(syntax.Target), syntax.Target.Span);
        if (target.Type == TypeSymbol.Error || syntax.Member.IsMissing)
            return new BoundErrorExpression();

        if (FindField(target.Type, syntax.Member.Text) is { } field)
            return new BoundFieldAccessExpression(target, field, TypeSubstitution.FieldType(field, target.Type));

        if (FindMethod(target.Type, syntax.Member.Text) is not null)
            _diagnostics.ReportNotAValue(syntax.Member.Span, syntax.Member.Text, "method");
        else
            _diagnostics.ReportUnknownMember(syntax.Member.Span, target.Type, syntax.Member.Text);
        return new BoundErrorExpression();
    }

    // Control flow

    private BoundExpression BindIf(IfExpressionSyntax syntax, TypeSymbol? expected)
    {
        var condition = BindCondition(syntax.Condition);
        var then = BindBlock(syntax.Then, expected);
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

    private static bool NeedsBranchConversion(TypeSymbol type) => type.IsNumeric || type is TraitSymbol;

    private BoundExpression BindCondition(ExpressionSyntax syntax)
    {
        var condition = BindExpression(syntax);
        if (condition.Type == TypeSymbol.Error || condition.Type == TypeSymbol.Bool)
            return condition;
        _diagnostics.ReportConditionNotBool(syntax.Span, condition.Type);
        return new BoundErrorExpression();
    }

    // Conversions

    /// <summary>Numeric widening, or a value of a type that implements a trait used as that trait.</summary>
    private static bool IsConvertible(TypeSymbol from, TypeSymbol to) =>
        Operators.IsImplicitlyConvertible(from, to) ||
        (to is TraitSymbol trait && from.IsValueType && Implements(from, trait));

    /// <summary>Converts to <paramref name="type"/> implicitly, or reports an error.</summary>
    private BoundExpression Convert(BoundExpression expression, TypeSymbol type, TextSpan span)
    {
        if (expression.Type == type || expression.Type == TypeSymbol.Error || type == TypeSymbol.Error ||
            expression.Type == TypeSymbol.Never)
            return expression;

        if (IsConvertible(expression.Type, type))
            return Widen(expression, type);

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

    private static BoundExpression Widen(BoundExpression expression, TypeSymbol type) =>
        expression.Type == type || expression.Type == TypeSymbol.Never || expression.Type == TypeSymbol.Error
            ? expression
            : new BoundConversionExpression(type, expression);

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
