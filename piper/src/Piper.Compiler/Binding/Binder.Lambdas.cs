using System.Collections.Immutable;
using Piper.Diagnostics;
using Piper.Syntax;
using Piper.Text;

namespace Piper.Binding;

/// <summary>
/// Lambdas and function values, collections (for, indexing, literals) and exceptions. Function types are
/// .NET delegates: <c>(int) =&gt; bool</c> is <c>Func&lt;int, bool&gt;</c> and a function returning unit is an <c>Action</c>.
/// </summary>
public sealed partial class Binder
{
    private static readonly Type[] FuncTypes =
        [.. Enumerable.Range(1, 17).Select(n => typeof(Func<>).Assembly.GetType($"System.Func`{n}")!)];

    private static readonly Type[] ActionTypes =
        [typeof(Action), .. Enumerable.Range(1, 16).Select(n => typeof(Action).Assembly.GetType($"System.Action`{n}")!)];

    private static readonly HashSet<Type> CollectionTypes =
    [
        typeof(List<>), typeof(IEnumerable<>), typeof(IReadOnlyList<>), typeof(IReadOnlyCollection<>), typeof(IList<>), typeof(ICollection<>),
    ];

    /// <summary>The delegate type for a function type: <c>Func&lt;…, R&gt;</c>, or <c>Action&lt;…&gt;</c> when it returns Unit.</summary>
    private static TypeSymbol FunctionType(IReadOnlyList<TypeSymbol> parameters, TypeSymbol returnType)
    {
        if (parameters.Contains(TypeSymbol.Error) || returnType == TypeSymbol.Error || parameters.Count > 16)
            return TypeSymbol.Error;
        if (returnType == TypeSymbol.Unit || returnType == TypeSymbol.Never)
        {
            var action = ClrTypeSymbol.For(ActionTypes[parameters.Count]);
            return parameters.Count == 0 ? action : action.Construct([.. parameters]);
        }
        return ClrTypeSymbol.For(FuncTypes[parameters.Count]).Construct([.. parameters, returnType]);
    }

    /// <summary>The parameter and return types of a delegate type, or null if <paramref name="type"/> isn't one Piper can call.</summary>
    private (ImmutableArray<TypeSymbol> Parameters, TypeSymbol Return)? DelegateSignature(TypeSymbol type)
    {
        if (type is not ClrTypeSymbol clr || !clr.Type.IsSubclassOf(typeof(MulticastDelegate)) || clr.IsGenericDefinition)
            return null;
        if (clr.Type.GetMethod("Invoke") is not { } invoke)
            return null;

        var map = clr.GenericMap;
        var parameters = ImmutableArray.CreateBuilder<TypeSymbol>();
        foreach (var parameter in invoke.GetParameters())
        {
            if (ClrTypes.FromClr(parameter.ParameterType, map, _option) is not { } mapped)
                return null;
            parameters.Add(mapped);
        }
        return ClrTypes.FromClr(invoke.ReturnType, map, _option) is { } returned ? (parameters.ToImmutable(), returned) : null;
    }

    /// <summary>Binds speculatively: diagnostics are discarded and <paramref name="failed"/> says whether there were errors.</summary>
    private T Trial<T>(Func<T> bind, out bool failed)
    {
        var real = _diagnostics;
        _diagnostics = new DiagnosticBag { CurrentSource = real.CurrentSource };
        try
        {
            var result = bind();
            failed = _diagnostics.HasErrors;
            return result;
        }
        finally
        {
            _diagnostics = real;
        }
    }

    // Lambdas

    /// <summary>A lambda takes its types from the delegate type the context expects, or else from its annotations.</summary>
    private BoundExpression BindLambda(LambdaExpressionSyntax syntax, TypeSymbol? expected)
    {
        if (expected is not null && DelegateSignature(expected) is { } signature)
        {
            if (signature.Parameters.Length != syntax.Parameters.Length)
            {
                _diagnostics.ReportLambdaParameterCount(syntax.Span, expected, signature.Parameters.Length, syntax.Parameters.Length);
                return new BoundErrorExpression();
            }
            return BindLambdaWith(syntax, signature.Parameters, signature.Return, expected);
        }

        if (syntax.Parameters.Any(p => p.Type is null))
        {
            _diagnostics.ReportCannotInferLambda(syntax.Span);
            return new BoundErrorExpression();
        }
        return BindLambdaWith(syntax, null, null, null);
    }

    /// <param name="parameterTypes">Null to use the parameters' annotations.</param>
    /// <param name="returnType">Null to infer it from the body.</param>
    /// <param name="delegateType">Null for the lambda's natural type (Func/Action).</param>
    private BoundExpression BindLambdaWith(
        LambdaExpressionSyntax syntax,
        IReadOnlyList<TypeSymbol>? parameterTypes,
        TypeSymbol? returnType,
        TypeSymbol? delegateType)
    {
        if (parameterTypes is not null && parameterTypes.Count != syntax.Parameters.Length)
            return new BoundErrorExpression();

        var parameters = new List<VariableSymbol>();
        for (int i = 0; i < syntax.Parameters.Length; i++)
        {
            var parameter = syntax.Parameters[i];
            var annotated = parameter.Type is null ? null : BindValueType(parameter.Type, "parameter");
            var type = parameterTypes?[i] ?? annotated ?? TypeSymbol.Error;
            if (parameterTypes is not null && annotated is not null && annotated != type && annotated != TypeSymbol.Error)
                _diagnostics.ReportLambdaParameterType(parameter.Span, parameter.Name.Text, type, annotated);
            parameters.Add(new VariableSymbol(parameter.Name.Text, type, isMutable: false, VariableKind.Parameter));
        }

        if (returnType is null)
        {
            // Probe the body to see what it returns, then bind it for real with that return type.
            var returned = new List<TypeSymbol>();
            var probe = Trial(() => BindLambdaBody(syntax, parameters, new ReturnContext(TypeSymbol.Error, "lambda", returned), null), out _);
            if (probe.Body.Trailing is { } trailing)
                returned.Add(trailing.Type);
            var values = returned.Where(t => t != TypeSymbol.Never).ToList();
            returnType = values.Count == 0 ? TypeSymbol.Unit : CommonType(values, null, out _) ?? values[0];
            if (returnType == TypeSymbol.Never || !returnType.IsValueType)
                returnType = TypeSymbol.Unit;
        }

        var (body, bodyType) = BindLambdaBody(syntax, parameters, new ReturnContext(returnType, "lambda"), returnType);
        delegateType ??= FunctionType([.. parameters.Select(p => p.Type)], returnType);
        return new BoundLambdaExpression([.. parameters], body, returnType, bodyType, syntax.Body is not BlockExpressionSyntax, delegateType);
    }

    /// <summary>Binds the body; <c>NaturalType</c> is the type of its value before conversion to the return type.</summary>
    private (BoundBlockExpression Body, TypeSymbol NaturalType) BindLambdaBody(
        LambdaExpressionSyntax syntax,
        List<VariableSymbol> parameters,
        ReturnContext context,
        TypeSymbol? returnType)
    {
        var (outerScope, outerReturn, outerLoopDepth, outerFinally, outerOutScope) = (_scope, _return, _loopDepth, _finallyLoopDepth, _outScope);
        _outScope = null;
        _scope = new Scope(_scope);
        foreach (var (parameter, syntaxParameter) in parameters.Zip(syntax.Parameters))
        {
            if (!syntaxParameter.Name.IsMissing && !_scope.TryDeclare(parameter))
                _diagnostics.ReportDuplicateDeclaration(syntaxParameter.Name.Span, parameter.Name);
        }
        _return = context;
        _loopDepth = 0;
        _finallyLoopDepth = -1;

        try
        {
            var expected = returnType is null || returnType == TypeSymbol.Unit ? null : returnType;
            BoundBlockExpression body;
            if (syntax.Body is BlockExpressionSyntax block)
            {
                body = BindBlock(block, expected);
            }
            else
            {
                var expression = BindExpression(syntax.Body, expected);
                body = new BoundBlockExpression([], expression, expression.Type);
            }

            var naturalType = body.Trailing?.Type ?? body.Type;
            if (returnType is null || returnType == TypeSymbol.Unit || returnType == TypeSymbol.Error)
                return (body, naturalType);

            if (body.Trailing is { } trailing && trailing.Type != TypeSymbol.Unit)
            {
                var trailingSpan = syntax.Body is BlockExpressionSyntax b ? b.Trailing!.Span : syntax.Body.Span;
                return (new BoundBlockExpression(body.Statements, Convert(trailing, returnType, trailingSpan), returnType), naturalType);
            }
            if (body.Type != TypeSymbol.Never)
                _diagnostics.ReportNotAllPathsReturn(syntax.Body.Span, "lambda");
            return (body, naturalType);
        }
        finally
        {
            (_scope, _return, _loopDepth, _finallyLoopDepth, _outScope) = (outerScope, outerReturn, outerLoopDepth, outerFinally, outerOutScope);
        }
    }

    /// <summary>
    /// A top-level function used as a value becomes a delegate (its natural type, or the expected one). A generic
    /// function needs its type arguments written (<c>identity&lt;Int&gt;</c>) or inferable from the expected function type.
    /// Null if the function can't be a value (the caller reports that).
    /// </summary>
    private BoundExpression? BindFunctionReference(
        FunctionSymbol function,
        TypeSymbol? expected,
        ImmutableArray<TypeSymbol>? explicitArguments,
        TextSpan span)
    {
        if (function.Declaration is null)
            return null;

        var signature = expected is null ? null : DelegateSignature(expected);
        var map = new Dictionary<TypeParameterSymbol, TypeSymbol>();
        if (!ApplyExplicitTypeArguments(explicitArguments, function.TypeParameters, map, span, function.Name))
            return new BoundErrorExpression();
        if (!function.TypeParameters.IsEmpty && explicitArguments is null)
        {
            // `let f: (int) => int = identity;`
            var unresolved = function.TypeParameters.ToHashSet();
            if (signature is { } wanted && wanted.Parameters.Length == function.Parameters.Length)
            {
                foreach (var (parameter, type) in function.Parameters.Zip(wanted.Parameters))
                    Infer(parameter.Type, type, map, unresolved);
                Infer(function.ReturnType, wanted.Return, map, unresolved);
            }
            if (function.TypeParameters.FirstOrDefault(p => !map.ContainsKey(p)) is { } missing)
            {
                _diagnostics.ReportCannotInferTypeArgument(span, missing.Name, function.Name);
                return new BoundErrorExpression();
            }
        }

        var typeArguments = TypeArgumentsFrom(function.TypeParameters, map, span);
        var parameters = function.Parameters.Select(p => TypeSubstitution.Substitute(p.Type, map)).ToImmutableArray();
        var returnType = TypeSubstitution.Substitute(function.ReturnType, map);
        if (signature is { } exact && exact.Parameters.SequenceEqual(parameters) && exact.Return == returnType)
            return new BoundFunctionReferenceExpression(function, typeArguments, expected!);

        var natural = FunctionType(parameters, returnType);
        return natural == TypeSymbol.Error ? null : new BoundFunctionReferenceExpression(function, typeArguments, natural);
    }

    /// <summary>Calling a function value: <c>f(1)</c>.</summary>
    private BoundExpression BindDelegateInvocation(BoundExpression target, CallExpressionSyntax syntax, string name)
    {
        var signature = DelegateSignature(target.Type)!.Value;
        var arguments = BindArguments(syntax.Span, name, signature.Parameters, [], [], syntax.Arguments);
        return arguments is null
            ? new BoundErrorExpression()
            : new BoundDelegateInvocationExpression(target, arguments.Value, signature.Return);
    }

    // Collections

    /// <summary>The type itself, then (for .NET-backed types) its base classes and interfaces, in Piper terms.</summary>
    private IEnumerable<TypeSymbol> SelfAndBaseTypes(TypeSymbol type)
    {
        yield return type;

        (Type Type, IReadOnlyDictionary<Type, TypeSymbol> Map)? clr = type switch
        {
            ClrTypeSymbol symbol => (symbol.Type, symbol.GenericMap),
            _ when ClrTypes.BuiltinTypes.TryGetValue(type, out var builtin) => (builtin, new Dictionary<Type, TypeSymbol>()),
            _ => null,
        };
        if (clr is not { } context)
            yield break;

        for (var baseType = context.Type.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            if (ClrTypes.FromClr(baseType, context.Map, _option) is { } mapped)
                yield return mapped;
        }
        foreach (var @interface in context.Type.GetInterfaces())
        {
            if (ClrTypes.FromClr(@interface, context.Map, _option) is { } mapped)
                yield return mapped;
        }
    }

    /// <summary>T for anything that implements <c>IEnumerable&lt;T&gt;</c> (lists, arrays, strings, LINQ results).</summary>
    private TypeSymbol? ElementTypeOf(TypeSymbol type) =>
        // A multi-dimensional array is only a non-generic IEnumerable, but C#'s foreach still knows its element type.
        type is ClrTypeSymbol { IsArray: true } array ? array.TypeArguments[0] :
        SelfAndBaseTypes(type)
            .OfType<ClrTypeSymbol>()
            .FirstOrDefault(t => t.Definition.Type == typeof(IEnumerable<>) && t.TypeArguments.Length == 1)
            ?.TypeArguments[0];

    private static TypeSymbol ListOf(TypeSymbol element) => ClrTypeSymbol.For(typeof(List<>)).Construct([element]);

    /// <summary><c>[1, 2, 3]</c>: a <c>List&lt;T&gt;</c>, or an array when one is expected. The element type comes from the context or the elements.</summary>
    private BoundExpression BindCollection(CollectionExpressionSyntax syntax, TypeSymbol? expected)
    {
        TypeSymbol? elementType = null;
        TypeSymbol? arrayType = null;
        if (expected is ClrTypeSymbol { IsArray: true, Rank: 1 } array)
        {
            (elementType, arrayType) = (array.TypeArguments[0], array);
        }
        else if (expected is ClrTypeSymbol { TypeArguments.Length: 1 } collection && CollectionTypes.Contains(collection.Definition.Type))
        {
            elementType = collection.TypeArguments[0];
        }

        var elements = syntax.Elements.Select(e => RequireValue(BindExpression(e, elementType), e.Span)).ToList();
        if (elementType is null)
        {
            if (elements.Count == 0)
            {
                _diagnostics.ReportCannotInferCollection(syntax.Span);
                return new BoundErrorExpression();
            }
            elementType = CommonType([.. elements.Select(e => e.Type)], null, out int failed);
            if (elementType is null)
            {
                _diagnostics.ReportIncompatibleArms(syntax.Elements[failed].Span, elements[0].Type, elements[failed].Type);
                return new BoundErrorExpression();
            }
        }

        var converted = elements.Select((e, i) => Convert(e, elementType, syntax.Elements[i].Span)).ToImmutableArray();
        return new BoundCollectionExpression(converted, arrayType ?? ListOf(elementType));
    }

    /// <summary><c>for x in collection</c> over anything that implements <c>IEnumerable&lt;T&gt;</c>.</summary>
    private BoundStatement BindForEach(ForStatementSyntax syntax)
    {
        var collection = RequireValue(BindExpression(syntax.Range), syntax.Range.Span);
        var elementType = collection.Type == TypeSymbol.Error ? TypeSymbol.Error : ElementTypeOf(collection.Type);
        if (elementType is null)
        {
            _diagnostics.ReportNotIterable(syntax.Range.Span, collection.Type);
            elementType = TypeSymbol.Error;
        }

        var outer = _scope;
        _scope = new Scope(outer);
        var variable = new VariableSymbol(syntax.Variable.Text, elementType, isMutable: false, VariableKind.LoopVariable);
        BoundParenthesizedDesignation? deconstruction = null;
        if (syntax.Deconstruction is { } designation)
        {
            // `for (key, value) in dictionary`
            var declared = new List<(VariableSymbol, Token)>();
            deconstruction = BindDesignation(designation, elementType, isMutable: false, VariableKind.LoopVariable, declared);
            DeclareAll(declared);
        }
        else if (!syntax.Variable.IsMissing)
        {
            _scope.TryDeclare(variable);
        }

        var body = BindLoopBody(syntax.Body, out bool breaks);
        _scope = outer;

        return new BoundForEachStatement(variable, collection, new BoundExpressionStatement(body), deconstruction);
    }

    /// <summary><c>array[i]</c>, <c>text[i]</c>, <c>list[i]</c>, <c>dictionary[key]</c>.</summary>
    private BoundExpression BindIndex(IndexExpressionSyntax syntax)
    {
        // `Int[5]`: indexing a type creates an array, as calling one constructs it. `String?[5]` is an array of Nones.
        if (syntax.Target is TryExpressionSyntax { Operand: var operand } && ResolveContainer(operand) is TypeSymbol optionalElement)
            return BindArrayCreation(optionalElement.IsValueType ? _option.Construct([optionalElement]) : optionalElement, syntax);

        switch (ResolveContainer(syntax.Target))
        {
            case TypeSymbol element:
                return BindArrayCreation(element, syntax);
            case ClrTypeGroupSymbol group when NonGenericMember(group) is { } element:
                return BindArrayCreation(element, syntax);
            case ErrorSymbol:
                return BindArgumentsForDiagnostics(syntax.Arguments);
            case (NamespaceSymbol or ClrTypeGroupSymbol) and var container:
                _diagnostics.ReportNotAValue(syntax.Target.Span, container.Name, container is NamespaceSymbol ? "namespace" : "type");
                return BindArgumentsForDiagnostics(syntax.Arguments);
        }

        var target = RequireValue(BindExpression(syntax.Target), syntax.Target.Span);
        if (target.Type == TypeSymbol.Error)
            return BindArgumentsForDiagnostics(syntax.Arguments);

        if (target.Type is ClrTypeSymbol { IsArray: true } array && array.Rank == syntax.Arguments.Length)
        {
            var element = array.TypeArguments[0];
            var indices = syntax.Arguments.Select(a => Convert(RequireValue(BindExpression(a, TypeSymbol.Int), a.Span), TypeSymbol.Int, a.Span));
            return new BoundIndexExpression(target, null, [.. indices], element);
        }

        if (IsClrBacked(target.Type))
        {
            var (clrType, map) = ClrContext(target.Type);
            var indexers = clrType.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Where(p => p.GetIndexParameters().Length > 0 && p.GetMethod is { IsPublic: true })
                .ToList();
            if (indexers.Count > 0)
            {
                var resolved = ResolveClrOverload(indexers.Select(p => (System.Reflection.MethodBase)p.GetMethod!), target.Type, $"{target.Type}[]", syntax.Arguments, [], syntax.Span, map);
                if (resolved is not { } chosen)
                    return new BoundErrorExpression();
                var indexer = indexers.First(p => p.GetMethod == chosen.Candidate.Member);
                var value = (chosen.Candidate.ReturnType, chosen.Candidate.ReturnBoundary);
                return FromClrBoundary(new BoundIndexExpression(target, indexer, chosen.Arguments, BoundaryInnerType(value)), value);
            }
        }

        BindArgumentsForDiagnostics(syntax.Arguments);
        _diagnostics.ReportNotIndexable(syntax.Target.Span, target.Type);
        return new BoundErrorExpression();
    }

    /// <summary>
    /// <c>Int[5]</c>, <c>Double[2, 3]</c>: an array of default values. Only for element types whose default isn't
    /// null (numbers, Bool, Char, optionals, .NET structs), since Piper has no null references; a <c>String[]</c> comes
    /// from a collection literal or .NET instead.
    /// </summary>
    private BoundExpression BindArrayCreation(TypeSymbol element, IndexExpressionSyntax syntax)
    {
        var sizes = syntax.Arguments
            .Select(a => Convert(RequireValue(BindExpression(a, TypeSymbol.Int), a.Span), TypeSymbol.Int, a.Span))
            .ToImmutableArray();
        if (element == TypeSymbol.Error)
            return new BoundErrorExpression();
        if (!element.IsValueType || element is ClrTypeSymbol { IsGenericDefinition: true } || element is NamedTypeDefinition { IsGeneric: true })
        {
            _diagnostics.ReportNotAValue(syntax.Target.Span, element.Name, "type");
            return new BoundErrorExpression();
        }
        if (!HasNonNullDefault(element))
        {
            _diagnostics.ReportArrayOfNullDefaults(syntax.Span, element);
            return new BoundErrorExpression();
        }
        return new BoundArrayCreationExpression(sizes, ClrTypeSymbol.ArrayOf(element, sizes.Length));
    }

    /// <summary>True if C#'s <c>default</c> of the type is a valid Piper value (not a null reference).</summary>
    private static bool HasNonNullDefault(TypeSymbol type) =>
        type.IsNumeric || type == TypeSymbol.Bool || type == TypeSymbol.Char || type.IsOption ||
        type is ClrTypeSymbol { IsReferenceType: false } ||
        (type is TupleTypeSymbol tuple && tuple.ElementTypes.All(HasNonNullDefault));

    /// <summary>Assigning a .NET property or indexer: <c>sb.Length = 0</c>, <c>scores["Ada"] += 1</c>.</summary>
    private BoundExpression? BindClrAssignment(AssignmentExpressionSyntax syntax)
    {
        BoundExpression target;
        string name;
        switch (syntax.Target)
        {
            case IndexExpressionSyntax index:
                target = BindIndex(index);
                name = "indexer";
                break;
            case MemberAccessExpressionSyntax member when ResolveContainer(member.Target) is null:
            {
                var receiver = RequireValue(BindExpression(member.Target), member.Target.Span);
                if (receiver.Type == TypeSymbol.Error || member.Member.IsMissing)
                    return new BoundErrorExpression();
                if (FindField(receiver.Type, member.Member.Text) is not null || !IsClrBacked(receiver.Type))
                    return null;
                target = BindClrInstanceMember(receiver, member.Member);
                name = member.Member.Text;
                break;
            }
            default:
                return null;
        }

        if (target is BoundNullableBoundaryExpression boundary)
            target = boundary.Operand;
        if (target.Type == TypeSymbol.Error)
        {
            BindExpression(syntax.Value);
            return new BoundErrorExpression();
        }

        bool writable = target switch
        {
            BoundIndexExpression { Indexer: null } => true,
            BoundIndexExpression { Indexer: { } indexer } => indexer.SetMethod is { IsPublic: true },
            BoundClrMemberExpression { Member: System.Reflection.PropertyInfo property } => property.SetMethod is { IsPublic: true },
            BoundClrMemberExpression { Member: System.Reflection.FieldInfo field } => !field.IsInitOnly && !field.IsLiteral,
            _ => false,
        };
        if (!writable)
        {
            BindExpression(syntax.Value);
            _diagnostics.ReportReadOnlyMember(syntax.Target.Span, name);
            return new BoundErrorExpression();
        }

        var value = RequireValue(BindExpression(syntax.Value, target.Type), syntax.Value.Span);
        if (SyntaxFacts.GetCompoundAssignmentOperator(syntax.Operator.Kind) is not { } compound)
        {
            // None assigned to a .NET property that accepts null.
            if (value.Type.IsOption && value.Type.TypeArguments[0] == target.Type && ClrTypes.IsReferenceType(target.Type))
                return new BoundClrAssignmentExpression(target, null, new BoundNullableBoundaryExpression(value, NullableBoundaryKind.ToNullableReference, target.Type));
            return new BoundClrAssignmentExpression(target, null, Convert(value, target.Type, syntax.Value.Span));
        }

        var kind = Operators.GetBinaryKind(compound)!.Value;
        var resolution = value.Type == TypeSymbol.Error ? null : Operators.ResolveBinary(kind, target.Type, value.Type);
        if (resolution is null || !CompoundResultFits(resolution.ResultType, target.Type, value))
        {
            if (value.Type != TypeSymbol.Error)
                _diagnostics.ReportUndefinedBinaryOperator(syntax.Operator.Span, syntax.Operator.Text, target.Type, value.Type);
            return new BoundErrorExpression();
        }
        return new BoundClrAssignmentExpression(target, kind, Widen(value, resolution.OperandType));
    }

    // Exceptions

    private static bool IsException(TypeSymbol type) =>
        type is ClrTypeSymbol clr && typeof(Exception).IsAssignableFrom(clr.Type);

    private BoundExpression BindThrow(ThrowExpressionSyntax syntax)
    {
        var exception = RequireValue(BindExpression(syntax.Exception), syntax.Exception.Span);
        if (exception.Type != TypeSymbol.Error && !IsException(exception.Type))
            _diagnostics.ReportNotAnException(syntax.Exception.Span, exception.Type);
        return new BoundThrowExpression(exception);
    }

    private BoundStatement BindTryCatch(TryStatementSyntax syntax)
    {
        var body = BindBlock(syntax.Body);

        var catches = ImmutableArray.CreateBuilder<BoundCatchClause>();
        var caught = new List<TypeSymbol?>();
        foreach (var clause in syntax.Catches)
        {
            TypeSymbol? exceptionType = null;
            if (clause.Type is not null)
            {
                exceptionType = BindType(clause.Type);
                if (exceptionType != TypeSymbol.Error && !IsException(exceptionType))
                    _diagnostics.ReportNotAnException(clause.Type.Span, exceptionType);
            }
            else if (clause.Name is not null)
            {
                exceptionType = ClrTypeSymbol.For(typeof(Exception));
            }

            // C# rejects a catch that an earlier one already handles completely.
            var exceptionClr = exceptionType is ClrTypeSymbol c ? c.Type : null;
            if (caught.Any(earlier => earlier is null ||
                                      (earlier is ClrTypeSymbol e && (exceptionClr is null ? e.Type == typeof(Exception) : e.Type.IsAssignableFrom(exceptionClr)))))
            {
                _diagnostics.ReportUnreachableCatch(clause.Keyword.Span);
            }
            caught.Add(exceptionType);

            var outer = _scope;
            _scope = new Scope(outer);
            VariableSymbol? variable = null;
            if (clause.Name is { IsMissing: false } name && name.Text != "_")
            {
                variable = new VariableSymbol(name.Text, exceptionType ?? TypeSymbol.Error, isMutable: false, VariableKind.Local);
                _scope.TryDeclare(variable);
            }
            var clauseBody = BindBlock(clause.Body);
            _scope = outer;
            catches.Add(new BoundCatchClause(exceptionType, variable, new BoundExpressionStatement(clauseBody)));
        }

        BoundStatement? @finally = null;
        if (syntax.Finally is not null)
        {
            var outerFinally = _finallyLoopDepth;
            _finallyLoopDepth = _loopDepth;
            @finally = new BoundExpressionStatement(BindBlock(syntax.Finally));
            _finallyLoopDepth = outerFinally;
        }

        return new BoundTryCatchStatement(new BoundExpressionStatement(body), catches.ToImmutable(), @finally);
    }
}
