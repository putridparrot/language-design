using System.Collections.Immutable;
using Piper.Diagnostics;
using Piper.Syntax;
using Piper.Text;

namespace Piper.Binding;

/// <summary>
/// Resolves names, checks types and enforces Piper's own rules (immutability, Bool conditions,
/// definite returns) that C# could not check for us. Binding happens in passes so declarations
/// can appear in any order: record names, record fields, function signatures, then bodies.
/// After an error the offending expression gets the Error type, which suppresses follow-on errors.
/// </summary>
public sealed class Binder
{
    private readonly DiagnosticBag _diagnostics;
    private readonly Scope _globalScope;
    private Scope _scope;
    private FunctionSymbol? _function;
    private int _loopDepth;

    private Binder(DiagnosticBag diagnostics)
    {
        _diagnostics = diagnostics;

        var builtins = new Scope(parent: null);
        foreach (var type in TypeSymbol.Builtins)
            builtins.TryDeclare(type);
        foreach (var function in BuiltinFunctions.All)
            builtins.TryDeclare(function);

        // User declarations live in their own scope so they may shadow builtins.
        _globalScope = new Scope(builtins);
        _scope = _globalScope;
    }

    public static BoundProgram BindProgram(CompilationUnitSyntax unit, DiagnosticBag diagnostics) =>
        new Binder(diagnostics).Bind(unit);

    private BoundProgram Bind(CompilationUnitSyntax unit)
    {
        var records = new List<RecordSymbol>();
        foreach (var declaration in unit.Declarations.OfType<RecordDeclarationSyntax>())
        {
            if (declaration.Name.IsMissing)
                continue;
            var record = new RecordSymbol(declaration);
            if (_globalScope.TryDeclare(record))
                records.Add(record);
            else
                _diagnostics.ReportDuplicateDeclaration(declaration.Name.Span, record.Name);
        }

        foreach (var record in records)
            BindRecordFields(record);

        var functions = new List<(FunctionSymbol Symbol, FunctionDeclarationSyntax Syntax)>();
        foreach (var declaration in unit.Declarations.OfType<FunctionDeclarationSyntax>())
        {
            var symbol = BindFunctionSignature(declaration);
            if (!declaration.Name.IsMissing && !_globalScope.TryDeclare(symbol))
                _diagnostics.ReportDuplicateDeclaration(declaration.Name.Span, symbol.Name);
            functions.Add((symbol, declaration));
        }

        var boundFunctions = functions.Select(f => BindFunctionBody(f.Symbol, f.Syntax)).ToImmutableArray();

        var main = functions.Select(f => f.Symbol).FirstOrDefault(f => f.Name == "main");
        if (main is not null && (main.Parameters.Length != 0 || main.ReturnType != TypeSymbol.Unit))
            _diagnostics.ReportInvalidMain(main.Declaration!.Name.Span);

        return new BoundProgram([.. records], boundFunctions, main);
    }

    private void BindRecordFields(RecordSymbol record)
    {
        var fields = ImmutableArray.CreateBuilder<FieldSymbol>();
        var names = new HashSet<string>();
        foreach (var field in record.Declaration.Fields)
        {
            var type = BindValueType(field.Type, "field");
            if (field.Name.IsMissing)
                continue;

            if (!names.Add(field.Name.Text))
                _diagnostics.ReportDuplicateDeclaration(field.Name.Span, field.Name.Text);
            else if (field.Name.Text == record.Name)
                _diagnostics.ReportFieldNamedAsRecord(field.Name.Span, record.Name);
            else
                fields.Add(new FieldSymbol(field.Name.Text, type));
        }
        record.Fields = fields.ToImmutable();
    }

    private FunctionSymbol BindFunctionSignature(FunctionDeclarationSyntax declaration)
    {
        var parameters = ImmutableArray.CreateBuilder<VariableSymbol>();
        var names = new HashSet<string>();
        foreach (var parameter in declaration.Parameters)
        {
            var type = BindValueType(parameter.Type, "parameter");
            if (parameter.Name.IsMissing)
                continue;
            if (!names.Add(parameter.Name.Text))
                _diagnostics.ReportDuplicateDeclaration(parameter.Name.Span, parameter.Name.Text);
            parameters.Add(new VariableSymbol(parameter.Name.Text, type, isMutable: false, VariableKind.Parameter));
        }

        var returnType = declaration.ReturnType is null ? TypeSymbol.Unit : BindType(declaration.ReturnType);
        return new FunctionSymbol(declaration.Name.Text, parameters.ToImmutable(), returnType, declaration);
    }

    private BoundFunction BindFunctionBody(FunctionSymbol function, FunctionDeclarationSyntax declaration)
    {
        _function = function;
        _loopDepth = 0;
        _scope = new Scope(_globalScope);
        foreach (var parameter in function.Parameters)
            _scope.TryDeclare(parameter);

        BoundBlockExpression body;
        ExpressionSyntax? trailingSyntax;
        if (declaration.ExpressionBody is { } expressionBody)
        {
            var expression = BindExpression(expressionBody);
            body = new BoundBlockExpression([], expression, expression.Type);
            trailingSyntax = expressionBody;
        }
        else
        {
            body = BindBlock(declaration.BlockBody!);
            trailingSyntax = declaration.BlockBody!.Trailing;
        }

        body = CheckFunctionResult(function, declaration, body, trailingSyntax);

        _scope = _globalScope;
        _function = null;
        return new BoundFunction(function, body, declaration.ExpressionBody is not null);
    }

    /// <summary>A function with a return type must end in a value (trailing expression) or never complete normally.</summary>
    private BoundBlockExpression CheckFunctionResult(
        FunctionSymbol function,
        FunctionDeclarationSyntax declaration,
        BoundBlockExpression body,
        ExpressionSyntax? trailingSyntax)
    {
        var returnType = function.ReturnType;
        if (returnType == TypeSymbol.Unit || returnType == TypeSymbol.Error)
            return body;

        if (body.Trailing is { Type: var trailingType } trailing && trailingType != TypeSymbol.Unit)
        {
            var converted = Convert(trailing, returnType, trailingSyntax!.Span);
            return new BoundBlockExpression(body.Statements, converted, returnType);
        }

        if (body.Type != TypeSymbol.Never)
            _diagnostics.ReportNotAllPathsReturn(declaration.Name.Span, function.Name);
        return body;
    }

    private TypeSymbol BindType(TypeSyntax syntax)
    {
        var name = ((NamedTypeSyntax)syntax).Name;
        if (name.IsMissing)
            return TypeSymbol.Error;

        if (_scope.Lookup(name.Text) is TypeSymbol type && type != TypeSymbol.Never && type != TypeSymbol.Error)
            return type;

        _diagnostics.ReportUndefinedType(name.Span, name.Text);
        return TypeSymbol.Error;
    }

    private TypeSymbol BindValueType(TypeSyntax syntax, string what)
    {
        var type = BindType(syntax);
        if (type != TypeSymbol.Unit)
            return type;
        _diagnostics.ReportInvalidUnitUsage(syntax.Span, what);
        return TypeSymbol.Error;
    }

    // Statements

    private BoundBlockExpression BindBlock(BlockExpressionSyntax syntax)
    {
        var outer = _scope;
        _scope = new Scope(outer);

        var statements = syntax.Statements.Select(BindStatement).ToImmutableArray();
        var trailing = syntax.Trailing is null ? null : BindExpression(syntax.Trailing);

        _scope = outer;

        var type = trailing?.Type ?? (statements.Any(Diverges) ? TypeSymbol.Never : TypeSymbol.Unit);
        return new BoundBlockExpression(statements, trailing, type);
    }

    /// <summary>True if control never flows past <paramref name="statement"/>.</summary>
    private static bool Diverges(BoundStatement statement) => statement switch
    {
        BoundReturnStatement or BoundBreakStatement or BoundContinueStatement => true,
        BoundExpressionStatement e => e.Expression.Type == TypeSymbol.Never,
        _ => false,
    };

    private BoundStatement BindStatement(StatementSyntax syntax) => syntax switch
    {
        VariableDeclarationSyntax s => BindVariableDeclaration(s),
        ExpressionStatementSyntax s => new BoundExpressionStatement(BindExpression(s.Expression)),
        WhileStatementSyntax s => BindWhile(s),
        ForStatementSyntax s => BindFor(s),
        ReturnStatementSyntax s => BindReturn(s),
        BreakStatementSyntax s => BindLoopJump(s.Keyword, new BoundBreakStatement()),
        ContinueStatementSyntax s => BindLoopJump(s.Keyword, new BoundContinueStatement()),
        _ => throw new InvalidOperationException($"Unexpected statement {syntax.GetType().Name}"),
    };

    private BoundStatement BindVariableDeclaration(VariableDeclarationSyntax syntax)
    {
        var initializer = BindExpression(syntax.Initializer);
        TypeSymbol type;
        if (syntax.Type is not null)
        {
            type = BindValueType(syntax.Type, "variable");
            initializer = Convert(initializer, type, syntax.Initializer.Span);
        }
        else
        {
            initializer = RequireValue(initializer, syntax.Initializer.Span);
            type = initializer.Type;
        }

        // Declared after binding the initializer, so `let x = x + 1;` refers to the outer x.
        var variable = new VariableSymbol(syntax.Name.Text, type, syntax.IsMutable, VariableKind.Local);
        if (!syntax.Name.IsMissing && !_scope.TryDeclare(variable))
            _diagnostics.ReportDuplicateDeclaration(syntax.Name.Span, variable.Name);

        return new BoundVariableDeclaration(variable, initializer);
    }

    private BoundStatement BindWhile(WhileStatementSyntax syntax)
    {
        var condition = BindCondition(syntax.Condition);
        _loopDepth++;
        var body = BindBlock(syntax.Body);
        _loopDepth--;
        return new BoundWhileStatement(condition, new BoundExpressionStatement(body));
    }

    private BoundStatement BindFor(ForStatementSyntax syntax)
    {
        BoundExpression lower, upper;
        TypeSymbol type;
        bool inclusive = false;
        if (syntax.Range is RangeExpressionSyntax range)
        {
            inclusive = range.IsInclusive;
            lower = BindExpression(range.Start);
            upper = BindExpression(range.End);
            type = lower.Type == TypeSymbol.Long || upper.Type == TypeSymbol.Long ? TypeSymbol.Long : TypeSymbol.Int;
            lower = BindRangeBound(lower, type, range.Start.Span);
            upper = BindRangeBound(upper, type, range.End.Span);
        }
        else
        {
            BindExpression(syntax.Range);
            _diagnostics.ReportForRequiresRange(syntax.Range.Span);
            lower = upper = new BoundErrorExpression();
            type = TypeSymbol.Error;
        }

        var outer = _scope;
        _scope = new Scope(outer);
        var variable = new VariableSymbol(syntax.Variable.Text, type, isMutable: false, VariableKind.LoopVariable);
        if (!syntax.Variable.IsMissing)
            _scope.TryDeclare(variable);

        _loopDepth++;
        var body = BindBlock(syntax.Body);
        _loopDepth--;
        _scope = outer;

        return new BoundForStatement(variable, lower, upper, inclusive, new BoundExpressionStatement(body));
    }

    private BoundExpression BindRangeBound(BoundExpression bound, TypeSymbol type, TextSpan span)
    {
        if (bound.Type == TypeSymbol.Error || bound.Type.IsIntegral)
            return Convert(bound, type, span);
        _diagnostics.ReportCannotConvert(span, bound.Type, type);
        return new BoundErrorExpression();
    }

    private BoundStatement BindReturn(ReturnStatementSyntax syntax)
    {
        var function = _function!;
        if (syntax.Value is null)
        {
            if (function.ReturnType != TypeSymbol.Unit && function.ReturnType != TypeSymbol.Error)
                _diagnostics.ReportMissingReturnValue(syntax.Keyword.Span, function.Name, function.ReturnType);
            return new BoundReturnStatement(null);
        }

        var value = BindExpression(syntax.Value);
        if (function.ReturnType == TypeSymbol.Unit)
        {
            if (value.Type != TypeSymbol.Error)
                _diagnostics.ReportUnexpectedReturnValue(syntax.Value.Span, function.Name);
            return new BoundReturnStatement(null);
        }

        value = Convert(RequireValue(value, syntax.Value.Span), function.ReturnType, syntax.Value.Span);
        return new BoundReturnStatement(value);
    }

    private BoundStatement BindLoopJump(Token keyword, BoundStatement statement)
    {
        if (_loopDepth == 0)
            _diagnostics.ReportOutsideLoop(keyword.Span, keyword.Text);
        return statement;
    }

    // Expressions

    private BoundExpression BindExpression(ExpressionSyntax syntax) => syntax switch
    {
        LiteralExpressionSyntax s => BindLiteral(s),
        InterpolatedStringExpressionSyntax s => BindInterpolatedString(s),
        NameExpressionSyntax s => BindName(s),
        UnaryExpressionSyntax s => BindUnary(s),
        BinaryExpressionSyntax s => BindBinary(s),
        RangeExpressionSyntax s => BindRange(s),
        AssignmentExpressionSyntax s => BindAssignment(s),
        CallExpressionSyntax s => BindCall(s),
        MemberAccessExpressionSyntax s => BindMemberAccess(s),
        ParenthesizedExpressionSyntax s => BindExpression(s.Expression),
        IfExpressionSyntax s => BindIf(s),
        BlockExpressionSyntax s => BindBlock(s),
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
        var value = BindExpression(syntax.Value);

        if (syntax.Target is not NameExpressionSyntax name)
        {
            BindExpression(syntax.Target);
            if (syntax.Target is MemberAccessExpressionSyntax member)
                _diagnostics.ReportImmutableField(member.Member.Span, member.Member.Text);
            else
                _diagnostics.ReportInvalidAssignmentTarget(syntax.Target.Span);
            return new BoundErrorExpression();
        }

        if (_scope.Lookup(name.Identifier.Text) is not VariableSymbol variable)
        {
            BindName(name);
            return new BoundErrorExpression();
        }

        if (!variable.IsMutable)
            _diagnostics.ReportCannotAssignImmutable(name.Span, variable);

        value = RequireValue(value, syntax.Value.Span);

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

    private BoundExpression BindCall(CallExpressionSyntax syntax)
    {
        var arguments = syntax.Arguments.Select(a => (Syntax: a, Bound: BindExpression(a))).ToList();

        if (syntax.Callee is not NameExpressionSyntax name)
        {
            if (BindExpression(syntax.Callee).Type != TypeSymbol.Error)
                _diagnostics.ReportNotCallable(syntax.Callee.Span, "This expression");
            return new BoundErrorExpression();
        }

        if (name.Identifier.IsMissing)
            return new BoundErrorExpression();

        var calleeName = name.Identifier.Text;
        switch (_scope.Lookup(calleeName))
        {
            case FunctionSymbol function when function == BuiltinFunctions.Print:
                if (arguments.Count != 1)
                {
                    _diagnostics.ReportWrongArgumentCount(syntax.Span, calleeName, 1, arguments.Count);
                    return new BoundErrorExpression();
                }
                return new BoundCallExpression(function, [RequireValue(arguments[0].Bound, arguments[0].Syntax.Span)]);

            case FunctionSymbol function:
                return BindArguments(syntax, calleeName, function.Parameters.Select(p => p.Type), arguments) is { } args
                    ? new BoundCallExpression(function, args)
                    : new BoundErrorExpression();

            case RecordSymbol record:
                return BindArguments(syntax, calleeName, record.Fields.Select(f => f.Type), arguments) is { } fieldArgs
                    ? new BoundRecordCreationExpression(record, fieldArgs)
                    : new BoundErrorExpression();

            case null:
                _diagnostics.ReportUndefinedName(name.Span, calleeName);
                return new BoundErrorExpression();

            default:
                _diagnostics.ReportNotCallable(name.Span, $"'{calleeName}'");
                return new BoundErrorExpression();
        }
    }

    private ImmutableArray<BoundExpression>? BindArguments(
        CallExpressionSyntax syntax,
        string calleeName,
        IEnumerable<TypeSymbol> parameterTypes,
        List<(ExpressionSyntax Syntax, BoundExpression Bound)> arguments)
    {
        var types = parameterTypes.ToList();
        if (types.Count != arguments.Count)
        {
            _diagnostics.ReportWrongArgumentCount(syntax.Span, calleeName, types.Count, arguments.Count);
            return null;
        }

        return [.. arguments.Zip(types, (argument, type) =>
            Convert(RequireValue(argument.Bound, argument.Syntax.Span), type, argument.Syntax.Span))];
    }

    private BoundExpression BindMemberAccess(MemberAccessExpressionSyntax syntax)
    {
        var target = RequireValue(BindExpression(syntax.Target), syntax.Target.Span);
        if (target.Type == TypeSymbol.Error || syntax.Member.IsMissing)
            return new BoundErrorExpression();

        var field = (target.Type as RecordSymbol)?.Fields.FirstOrDefault(f => f.Name == syntax.Member.Text);
        if (field is null)
        {
            _diagnostics.ReportUnknownMember(syntax.Member.Span, target.Type, syntax.Member.Text);
            return new BoundErrorExpression();
        }
        return new BoundFieldAccessExpression(target, field);
    }

    private BoundExpression BindIf(IfExpressionSyntax syntax)
    {
        var condition = BindCondition(syntax.Condition);
        var then = BindBlock(syntax.Then);
        if (syntax.Else is null)
            return new BoundIfExpression(condition, then, null, TypeSymbol.Unit);

        var @else = BindExpression(syntax.Else);
        var thenType = then.Type;
        var elseType = @else.Type;

        TypeSymbol type;
        if (thenType == TypeSymbol.Error || elseType == TypeSymbol.Error)
            type = TypeSymbol.Error;
        else if (thenType == TypeSymbol.Never)
            type = elseType;
        else if (elseType == TypeSymbol.Never || thenType == elseType)
            type = thenType;
        else if (Operators.IsImplicitlyConvertible(thenType, elseType))
            type = elseType;
        else if (Operators.IsImplicitlyConvertible(elseType, thenType))
            type = thenType;
        else
        {
            _diagnostics.ReportIncompatibleBranches(new TextSpan(syntax.Span.Start, 2), thenType, elseType);
            type = TypeSymbol.Error;
        }

        if (type.IsNumeric)
            return new BoundIfExpression(condition, WidenBlock(then, type), WidenBranch(@else, type), type);
        return new BoundIfExpression(condition, then, @else, type);
    }

    private BoundExpression BindCondition(ExpressionSyntax syntax)
    {
        var condition = BindExpression(syntax);
        if (condition.Type == TypeSymbol.Error || condition.Type == TypeSymbol.Bool)
            return condition;
        _diagnostics.ReportConditionNotBool(syntax.Span, condition.Type);
        return new BoundErrorExpression();
    }

    // Conversions

    /// <summary>Converts to <paramref name="type"/> implicitly, or reports an error.</summary>
    private BoundExpression Convert(BoundExpression expression, TypeSymbol type, TextSpan span)
    {
        if (expression.Type == type || expression.Type == TypeSymbol.Error || type == TypeSymbol.Error ||
            expression.Type == TypeSymbol.Never)
            return expression;

        if (Operators.IsImplicitlyConvertible(expression.Type, type))
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
        expression.Type == type || expression.Type == TypeSymbol.Never
            ? expression
            : new BoundConversionExpression(type, expression);

    /// <summary>Pushes a widening conversion into a block's trailing value so the if expression's branches agree.</summary>
    private static BoundBlockExpression WidenBlock(BoundBlockExpression block, TypeSymbol type) =>
        block.Type == type || block.Trailing is null
            ? block
            : new BoundBlockExpression(block.Statements, Widen(block.Trailing, type), type);

    private static BoundExpression WidenBranch(BoundExpression branch, TypeSymbol type) => branch switch
    {
        BoundBlockExpression block => WidenBlock(block, type),
        BoundIfExpression { Else: not null } nested when nested.Type != type =>
            new BoundIfExpression(nested.Condition, WidenBlock(nested.Then, type), WidenBranch(nested.Else, type), type),
        _ => Widen(branch, type),
    };
}
