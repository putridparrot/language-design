using System.Collections.Immutable;
using System.Reflection;
using Piper.Syntax;
using Piper.Text;

namespace Piper.Binding;

/// <summary>
/// Calling .NET: static and instance methods, extension methods (such as LINQ), constructors, properties
/// and fields, found by reflection. Overloads are resolved with a simplified version of C#'s rules: identity
/// beats numeric widening, which beats a reference conversion, which beats <c>object</c>; then a normal form
/// beats a params expansion, fewer default arguments beat more, and a non-generic method beats a generic one.
/// Generic methods infer their type arguments from the arguments, including from lambda bodies, and lambdas
/// are bound against each candidate's delegate type, as in C#. Where .NET may produce null, Piper sees an
/// Option; where .NET accepts null, an Option may be passed (None becomes null).
/// Not supported (yet): ref/out parameters and events.
/// </summary>
public sealed partial class Binder
{
    private sealed record ClrParameter(TypeSymbol Type, ClrBoundary Boundary);

    /// <summary>A .NET method or constructor as Piper sees it. <see cref="ParamsElement"/> is set for a <c>params</c> array.</summary>
    private sealed record ClrCandidate(
        MethodBase Member,
        TypeSymbol Container,
        ImmutableArray<ClrParameter> Parameters,
        int RequiredCount,
        ClrParameter? ParamsElement,
        TypeSymbol ReturnType,
        ClrBoundary ReturnBoundary,
        ImmutableArray<TypeSymbol> TypeArguments);

    /// <summary>An argument: bound, or (for lambdas) bound later against each candidate's parameter type.</summary>
    private sealed record ClrArgument(BoundExpression? Bound, LambdaExpressionSyntax? Lambda, TextSpan Span)
    {
        public TypeSymbol? Type => Bound?.Type;
    }

    private sealed record ResolvedClrCall(ClrCandidate Candidate, ImmutableArray<BoundExpression> Arguments);

    private static bool IsClrBacked(TypeSymbol type) => type is ClrTypeSymbol || ClrTypes.BuiltinTypes.ContainsKey(type);

    private void CheckAccessible(MethodSymbol method, Token name)
    {
        if (method.IsPrivate && method.DeclaringFile != _file.Source)
            _diagnostics.ReportPrivateDeclaration(name.Span, method.Name, method.DeclaringFile!.FileName);
    }

    /// <summary>The reflection type whose members to search, and how its generic parameters map to Piper types.</summary>
    private static (Type Type, IReadOnlyDictionary<Type, TypeSymbol> Map) ClrContext(TypeSymbol type) => type switch
    {
        ClrTypeSymbol clr => (clr.Type, clr.GenericMap),
        _ => (ClrTypes.BuiltinTypes[type], new Dictionary<Type, TypeSymbol>()),
    };

    private BoundExpression BindClrStaticMember(TypeSymbol type, Token member, ImmutableArray<ExpressionSyntax>? arguments, TextSpan span)
    {
        var (clrType, map) = ClrContext(type);
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
        if (arguments is null)
            return BindClrMemberRead(null, type, member, clrType.GetMember(member.Text, flags), map);

        var methods = WithoutHidden(clrType.GetMethods(flags | BindingFlags.FlattenHierarchy).Where(m => m.Name == member.Text)).ToList();
        if (methods.Count == 0)
        {
            BindArgumentsForDiagnostics(arguments);
            _diagnostics.ReportUnknownMethod(member.Span, type, member.Text);
            return new BoundErrorExpression();
        }
        return BindClrInvocation(null, type, methods, $"{type}.{member.Text}", arguments.Value, [], span, map);
    }

    private BoundExpression BindClrInstanceCall(BoundExpression receiver, Token member, ImmutableArray<ExpressionSyntax> arguments, TextSpan span)
    {
        var (clrType, map) = ClrContext(receiver.Type);
        var methods = InstanceMethods(clrType).Where(m => m.Name == member.Text).ToList();
        if (methods.Count > 0)
            return BindClrInvocation(receiver, receiver.Type, methods, member.Text, arguments, [], span, map);

        // Extension methods from the namespaces this file imports: `numbers.Where(n => n > 2)`.
        var extensions = ImportedExtensionMethods(member.Text);
        if (extensions.Count > 0)
            return BindClrInvocation(null, receiver.Type, extensions, member.Text, arguments, [receiver], span, new Dictionary<Type, TypeSymbol>());

        BindArgumentsForDiagnostics(arguments);
        _diagnostics.ReportUnknownMethod(member.Span, receiver.Type, member.Text);
        return new BoundErrorExpression();
    }

    private List<MethodInfo> ImportedExtensionMethods(string name) =>
        [.. _file.Usings.Append(_file.Namespace)
            .Where(n => !n.IsGlobal)
            .Select(n => n.FullName)
            .Distinct()
            .SelectMany(n => ClrTypeIndex.ExtensionMethods(n, name))];

    /// <summary>Public instance methods, including those an interface type inherits from its base interfaces.</summary>
    private static IEnumerable<MethodInfo> InstanceMethods(Type type)
    {
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance).AsEnumerable();
        if (type.IsInterface)
            methods = methods.Concat(type.GetInterfaces().SelectMany(i => i.GetMethods())).Concat(typeof(object).GetMethods());
        return WithoutHidden(methods);
    }

    /// <summary>
    /// Drops methods hidden by a same-signature method of a more derived type (C#'s <c>new</c>), which reflection
    /// still returns: <c>Exception.GetType()</c> hides <c>object.GetType()</c>.
    /// </summary>
    private static IEnumerable<MethodInfo> WithoutHidden(IEnumerable<MethodInfo> methods) => methods
        .GroupBy(m => (m.Name, m.IsGenericMethodDefinition ? m.GetGenericArguments().Length : 0,
            string.Join(",", m.GetParameters().Select(p => p.ParameterType))))
        .SelectMany(group => group.Where(m => !group.Any(other =>
            other.DeclaringType != m.DeclaringType && m.DeclaringType!.IsAssignableFrom(other.DeclaringType))));

    private BoundExpression BindClrInstanceMember(BoundExpression receiver, Token member)
    {
        var (clrType, map) = ClrContext(receiver.Type);
        return BindClrMemberRead(receiver, receiver.Type, member, clrType.GetMember(member.Text, BindingFlags.Public | BindingFlags.Instance), map);
    }

    /// <summary>Reads a property or field (<c>s.Length</c>, <c>Math.PI</c>); null-returning ones become optional.</summary>
    private BoundExpression BindClrMemberRead(
        BoundExpression? receiver,
        TypeSymbol container,
        Token name,
        MemberInfo[] members,
        IReadOnlyDictionary<Type, TypeSymbol> map)
    {
        var nullability = new NullabilityInfoContext();
        foreach (var member in members)
        {
            (Type Type, NullabilityInfo Info)? read = member switch
            {
                PropertyInfo { CanRead: true } property when property.GetIndexParameters().Length == 0 && property.GetMethod!.IsPublic =>
                    (property.PropertyType, nullability.Create(property)),
                FieldInfo field => (field.FieldType, nullability.Create(field)),
                _ => null,
            };
            if (read is not { } value ||
                MapValue(value.Type, GenericAware(value.Info.ReadState, value.Type, member.CustomAttributes, member, "MaybeNullAttribute"), map) is not { } mapped)
            {
                continue;
            }

            var access = new BoundClrMemberExpression(receiver, container, member, BoundaryInnerType(mapped));
            return FromClrBoundary(access, mapped);
        }

        if (members.OfType<MethodInfo>().Any())
            _diagnostics.ReportNotAValue(name.Span, name.Text, "method");
        else
            _diagnostics.ReportUnknownMember(name.Span, container, name.Text);
        return new BoundErrorExpression();
    }

    /// <summary>
    /// <c>StringBuilder()</c>, <c>Random(42)</c>; a generic type such as <c>List</c> takes its type
    /// arguments from the expected type: <c>let names: List&lt;String&gt; = List();</c>.
    /// </summary>
    private BoundExpression BindClrConstruction(ClrTypeSymbol type, ImmutableArray<ExpressionSyntax> arguments, TextSpan span, TypeSymbol? expected)
    {
        TypeSymbol constructed = type;
        if (type.IsGenericDefinition)
        {
            if (expected is ClrTypeSymbol wanted && wanted.Definition == type)
            {
                constructed = wanted;
            }
            else
            {
                BindArgumentsForDiagnostics(arguments);
                _diagnostics.ReportCannotInferTypeArgument(span, type.Type.GetGenericArguments()[0].Name, type.Name);
                return new BoundErrorExpression();
            }
        }

        var constructors = type.Type.GetConstructors();
        if (constructors.Length == 0 || type.Type.IsAbstract)
        {
            BindArgumentsForDiagnostics(arguments);
            _diagnostics.ReportCannotConstruct(span, constructed);
            return new BoundErrorExpression();
        }
        return BindClrInvocation(null, constructed, constructors, constructed.Name, arguments, [], span, ((ClrTypeSymbol)constructed).GenericMap);
    }

    /// <param name="leadingArguments">The receiver of an extension method, passed as its first argument.</param>
    private BoundExpression BindClrInvocation(
        BoundExpression? receiver,
        TypeSymbol container,
        IEnumerable<MethodBase> members,
        string displayName,
        ImmutableArray<ExpressionSyntax> argumentSyntax,
        ImmutableArray<BoundExpression> leadingArguments,
        TextSpan span,
        IReadOnlyDictionary<Type, TypeSymbol> map)
    {
        if (ResolveClrOverload(members, container, displayName, argumentSyntax, leadingArguments, span, map) is not { } resolved)
            return new BoundErrorExpression();

        var chosen = resolved.Candidate;
        var call = new BoundClrInvocationExpression(
            receiver, chosen.Container, chosen.Member, resolved.Arguments,
            BoundaryInnerType((chosen.ReturnType, chosen.ReturnBoundary)), chosen.TypeArguments);
        return FromClrBoundary(call, (chosen.ReturnType, chosen.ReturnBoundary));
    }

    /// <summary>Picks the best overload for the arguments and binds them against it; reports and returns null if there's none.</summary>
    private ResolvedClrCall? ResolveClrOverload(
        IEnumerable<MethodBase> members,
        TypeSymbol container,
        string displayName,
        ImmutableArray<ExpressionSyntax> argumentSyntax,
        ImmutableArray<BoundExpression> leadingArguments,
        TextSpan span,
        IReadOnlyDictionary<Type, TypeSymbol> map)
    {
        var arguments = leadingArguments.Select(a => new ClrArgument(a, null, span))
            .Concat(argumentSyntax.Select(a => a is LambdaExpressionSyntax lambda
                ? new ClrArgument(null, lambda, a.Span)
                : new ClrArgument(RequireValue(BindExpression(a), a.Span), null, a.Span)))
            .ToList();
        if (arguments.Any(a => a.Type == TypeSymbol.Error))
            return null;

        var applicable = new List<(ClrCandidate Candidate, int[] Scores, bool Expanded)>();
        foreach (var member in members)
        {
            var memberMap = map;
            if (member is MethodInfo { IsGenericMethodDefinition: true } generic)
            {
                if (InferMethodTypeArguments(generic, arguments, map) is not { } inferred)
                    continue;
                memberMap = inferred;
            }

            if (Describe(member, container, memberMap) is not { } candidate)
                continue;
            if (Score(candidate, arguments, expanded: false) is { } normal)
                applicable.Add((candidate, normal, false));
            else if (candidate.ParamsElement is not null && Score(candidate, arguments, expanded: true) is { } expanded)
                applicable.Add((candidate, expanded, true));
        }

        if (applicable.Count == 0)
        {
            var described = arguments.Select(a => a.Type ?? (TypeSymbol)ClrTypeSymbol.For(typeof(Delegate)));
            _diagnostics.ReportNoApplicableOverload(span, displayName, described);
            return null;
        }

        // Keep the candidates no other candidate beats on every argument.
        var best = applicable
            .Where(a => !applicable.Any(b => !ReferenceEquals(a.Candidate, b.Candidate) && Beats(b, a)))
            .ToList();
        if (best.Count != 1)
        {
            _diagnostics.ReportAmbiguousOverload(span, displayName, best.Select(b => b.Candidate.Member.ToString() ?? displayName));
            return null;
        }

        var (chosen, _, isExpanded) = best[0];
        var converted = arguments.Select((argument, i) =>
        {
            var parameter = ParameterFor(chosen, i, isExpanded);
            return argument.Lambda is { } lambda
                ? BindLambda(lambda, parameter.Type)
                : ConvertClrArgument(argument.Bound!, parameter);
        }).ToImmutableArray();
        return new ResolvedClrCall(chosen, converted);
    }

    private static bool Beats((ClrCandidate Candidate, int[] Scores, bool Expanded) a, (ClrCandidate Candidate, int[] Scores, bool Expanded) b)
    {
        bool allAtLeastAsGood = a.Scores.Zip(b.Scores).All(p => p.First <= p.Second);
        // As in C#: a better conversion wins; then a normal form beats a params expansion, a candidate that
        // needs fewer default arguments beats one that needs more, and a non-generic method beats a generic one.
        bool someBetter = a.Scores.Zip(b.Scores).Any(p => p.First < p.Second) ||
                          (!a.Expanded && b.Expanded) ||
                          (a.Expanded == b.Expanded && a.Candidate.Parameters.Length < b.Candidate.Parameters.Length) ||
                          (a.Candidate.TypeArguments.IsEmpty && !b.Candidate.TypeArguments.IsEmpty);
        return allAtLeastAsGood && someBetter;
    }

    private static ClrParameter ParameterFor(ClrCandidate candidate, int index, bool expanded) =>
        expanded && index >= candidate.Parameters.Length - 1 ? candidate.ParamsElement! : candidate.Parameters[index];

    /// <summary>One score per argument (lower is a better conversion), or null if the candidate can't take these arguments.</summary>
    private int[]? Score(ClrCandidate candidate, List<ClrArgument> arguments, bool expanded)
    {
        int fixedCount = expanded ? candidate.Parameters.Length - 1 : candidate.Parameters.Length;
        if (expanded ? arguments.Count < fixedCount : arguments.Count < candidate.RequiredCount || arguments.Count > fixedCount)
            return null;

        var scores = new int[arguments.Count];
        for (int i = 0; i < arguments.Count; i++)
        {
            var parameter = ParameterFor(candidate, i, expanded);
            int? score = arguments[i].Lambda is { } lambda
                ? LambdaScore(lambda, parameter.Type)
                : ConversionScore(arguments[i].Type!, parameter);
            if (score is not { } value)
                return null;
            scores[i] = value;
        }
        return scores;
    }

    /// <summary>
    /// Binds the lambda against a candidate's delegate type without reporting anything; null if that fails.
    /// Ranks by how well the body's value fits the delegate's return type (C# prefers Func&lt;int&gt; to Func&lt;long&gt; for <c>() =&gt; 1</c>).
    /// </summary>
    private int? LambdaScore(LambdaExpressionSyntax lambda, TypeSymbol delegateType)
    {
        if (DelegateSignature(delegateType) is not { } signature || signature.Parameters.Length != lambda.Parameters.Length)
            return null;
        var bound = Trial(() => BindLambda(lambda, delegateType), out bool failed);
        if (failed || bound is not BoundLambdaExpression { BodyType: var body, ReturnType: var returns })
            return null;
        if (body == returns || body == TypeSymbol.Never)
            return 0;
        if (returns == TypeSymbol.Unit)
            return 5;
        if (body.IsNumeric && returns.IsNumeric)
            return returns == TypeSymbol.Long ? 1 : 2;
        return 3;
    }

    private int? ConversionScore(TypeSymbol argument, ClrParameter parameter)
    {
        var target = parameter.Type;
        if (argument == target)
            return 0;
        // An optional value passed where .NET accepts null (or a Nullable<T>).
        if (parameter.Boundary == ClrBoundary.NullableReference && argument.IsOption && argument.TypeArguments[0] == target)
            return 0;
        if (parameter.Boundary == ClrBoundary.NullableValue && argument == target.TypeArguments[0])
            return 1;
        if (argument.IsNumeric && target.IsNumeric && Operators.IsImplicitlyConvertible(argument, target))
            return target == TypeSymbol.Long ? 1 : 2;

        var targetClr = ClrTypes.ToClr(target);
        if (targetClr == typeof(object))
            return argument.IsValueType ? 20 : null;
        if (target is ClrTypeSymbol && SelfAndBaseTypes(argument).Contains(target))
            return 10;
        if (targetClr is not null && ClrTypes.ToClr(argument) is { } argumentClr && targetClr.IsAssignableFrom(argumentClr))
            return 10;
        return null;
    }

    private static BoundExpression ConvertClrArgument(BoundExpression argument, ClrParameter parameter)
    {
        if (argument.Type.IsOption && parameter.Boundary == ClrBoundary.NullableReference)
            return new BoundNullableBoundaryExpression(argument, NullableBoundaryKind.ToNullableReference, parameter.Type);
        if (argument.Type.IsOption && parameter.Boundary == ClrBoundary.NullableValue)
            return new BoundNullableBoundaryExpression(argument, NullableBoundaryKind.ToNullableValue, parameter.Type);

        // Numeric widening; other conversions (to object, to a base type or interface) are implicit in C# too.
        if (argument.Type.IsNumeric && parameter.Type.IsNumeric && argument.Type != parameter.Type)
            return new BoundConversionExpression(parameter.Type, argument);
        return argument;
    }

    /// <summary>
    /// Infers a generic method's type arguments (adding them to <paramref name="map"/>): first from the
    /// ordinary arguments, then from lambdas whose parameter types are known by then (their bodies give the
    /// result type, as in <c>Select(x =&gt; x * 2)</c>). Null if some type argument can't be inferred.
    /// </summary>
    private Dictionary<Type, TypeSymbol>? InferMethodTypeArguments(MethodInfo method, List<ClrArgument> arguments, IReadOnlyDictionary<Type, TypeSymbol> map)
    {
        var generics = method.GetGenericArguments();
        var result = new Dictionary<Type, TypeSymbol>(map);
        var parameters = method.GetParameters();
        int count = Math.Min(parameters.Length, arguments.Count);

        for (int i = 0; i < count; i++)
        {
            if (arguments[i].Bound is { } bound)
                InferClr(parameters[i].ParameterType, bound.Type, result, generics);
        }

        bool progress = true;
        while (progress)
        {
            progress = false;
            for (int i = 0; i < count; i++)
            {
                if (arguments[i].Lambda is not { } lambda || !parameters[i].ParameterType.IsSubclassOf(typeof(MulticastDelegate)))
                    continue;
                var invoke = parameters[i].ParameterType.GetMethod("Invoke")!;
                if (!Unresolved(invoke.ReturnType, generics, result))
                    continue;

                var lambdaParameters = invoke.GetParameters().Select(p => ClrTypes.FromClr(p.ParameterType, result, _option)).ToList();
                if (lambdaParameters.Any(p => p is null) || lambdaParameters.Count != lambda.Parameters.Length)
                    continue;

                var bound = Trial(() => BindLambdaWith(lambda, lambdaParameters!, returnType: null, delegateType: null), out bool failed);
                if (failed || bound is not BoundLambdaExpression typed)
                    continue;
                InferClr(invoke.ReturnType, typed.ReturnType, result, generics);
                progress = !Unresolved(invoke.ReturnType, generics, result);
            }
        }

        return generics.All(result.ContainsKey) ? result : null;
    }

    private static bool Unresolved(Type type, Type[] generics, Dictionary<Type, TypeSymbol> result) =>
        type.IsGenericParameter
            ? generics.Contains(type) && !result.ContainsKey(type)
            : type.HasElementType ? Unresolved(type.GetElementType()!, generics, result)
            : type.IsGenericType && type.GetGenericArguments().Any(a => Unresolved(a, generics, result));

    /// <summary>Matches a .NET parameter type (mentioning the method's type parameters) against an argument's Piper type.</summary>
    private void InferClr(Type parameter, TypeSymbol argument, Dictionary<Type, TypeSymbol> result, Type[] generics)
    {
        if (!argument.IsValueType || argument == TypeSymbol.Error)
            return;

        if (parameter.IsGenericParameter)
        {
            if (generics.Contains(parameter))
                result.TryAdd(parameter, argument);
            return;
        }

        if (!parameter.ContainsGenericParameters)
            return;

        if (parameter.IsArray)
        {
            if (argument is ClrTypeSymbol { Type.IsArray: true } array &&
                ClrTypes.FromClr(array.Type.GetElementType()!, new Dictionary<Type, TypeSymbol>(), _option) is { } element)
            {
                InferClr(parameter.GetElementType()!, element, result, generics);
            }
            return;
        }

        if (Nullable.GetUnderlyingType(parameter) is { } underlying)
        {
            InferClr(underlying, argument.IsOption ? argument.TypeArguments[0] : argument, result, generics);
            return;
        }

        if (!parameter.IsGenericType)
            return;

        // IEnumerable<TSource> against List<Point>: find the matching interface among the argument's base types.
        var definition = parameter.GetGenericTypeDefinition();
        var parameterArguments = parameter.GetGenericArguments();
        var match = SelfAndBaseTypes(argument)
            .OfType<ClrTypeSymbol>()
            .FirstOrDefault(t => t.Definition.Type == definition && t.TypeArguments.Length == parameterArguments.Length);
        if (match is null)
            return;
        for (int i = 0; i < parameterArguments.Length; i++)
            InferClr(parameterArguments[i], match.TypeArguments[i], result, generics);
    }

    /// <summary>Describes a .NET member in Piper types, or returns null if Piper can't call it.</summary>
    /// <param name="map">Maps the containing type's generic parameters, and a generic method's own, to Piper types.</param>
    private ClrCandidate? Describe(MethodBase member, TypeSymbol container, IReadOnlyDictionary<Type, TypeSymbol> map)
    {
        if (!member.IsPublic)
            return null;

        var typeArguments = ImmutableArray<TypeSymbol>.Empty;
        if (member is MethodInfo { IsGenericMethodDefinition: true } generic)
        {
            if (generic.GetGenericArguments().Any(g => !map.ContainsKey(g)))
                return null;
            typeArguments = [.. generic.GetGenericArguments().Select(g => map[g])];
        }

        // Extension methods are called through their declaring static class.
        if (member.IsStatic && member is MethodInfo && member.DeclaringType is { } declaring && ClrTypes.ToClr(container) != declaring &&
            member.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false))
        {
            container = ClrTypeSymbol.For(declaring);
        }

        var nullability = new NullabilityInfoContext();
        var parameters = ImmutableArray.CreateBuilder<ClrParameter>();
        ClrParameter? paramsElement = null;
        int required = 0;
        var infos = member.GetParameters();
        for (int i = 0; i < infos.Length; i++)
        {
            var info = infos[i];
            if (info.ParameterType.IsByRef || info.IsOut)
                return null;
            var accepts = GenericAware(nullability.Create(info).WriteState, info.ParameterType, info.CustomAttributes, member, "AllowNullAttribute");
            if (MapValue(info.ParameterType, accepts, map) is not { } mapped)
                return null;

            var (type, boundary) = mapped;
            // A parameter that accepts null takes the plain type; an Option argument is converted at the call.
            parameters.Add(new ClrParameter(boundary == ClrBoundary.NullableReference ? type.TypeArguments[0] : type, boundary));
            if (!info.IsOptional)
                required = i + 1;

            if (i == infos.Length - 1 && info.IsDefined(typeof(ParamArrayAttribute)) && info.ParameterType.GetElementType() is { } element)
            {
                if (MapValue(element, NullabilityState.NotNull, map) is not { } elementType)
                    return null;
                paramsElement = new ClrParameter(elementType.Type, ClrBoundary.None);
                required = i;
            }
        }

        (TypeSymbol, ClrBoundary)? result = member switch
        {
            ConstructorInfo => (container, ClrBoundary.None),
            MethodInfo method => MapValue(
                method.ReturnType,
                GenericAware(nullability.Create(method.ReturnParameter).ReadState, method.ReturnType, method.ReturnParameter.CustomAttributes, method, "MaybeNullAttribute"),
                map),
            _ => null,
        };
        if (result is not { } returned)
            return null;

        return new ClrCandidate(member, container, parameters.ToImmutable(), required, paramsElement, returned.Item1, returned.Item2, typeArguments);
    }

    /// <summary>
    /// NullabilityInfoContext calls any unconstrained generic <c>T</c> nullable, since it could be instantiated with a
    /// nullable type. Piper substitutes the actual type argument, so only <c>T?</c> (nullable flag 2) or
    /// <paramref name="nullAttribute"/> (<c>[MaybeNull]</c>/<c>[AllowNull]</c>) mean null: <c>First()</c> isn't, <c>FirstOrDefault()</c> is.
    /// </summary>
    private static NullabilityState GenericAware(
        NullabilityState state,
        Type declared,
        IEnumerable<CustomAttributeData> attributes,
        MemberInfo context,
        string nullAttribute)
    {
        if (state != NullabilityState.Nullable || !declared.IsGenericParameter)
            return state;
        if (attributes.Any(a => a.AttributeType.Name == nullAttribute))
            return state;

        var flag = NullableFlag(attributes, "NullableAttribute");
        for (var scope = context; flag is null && scope is not null; scope = scope.DeclaringType)
            flag = NullableFlag(scope.CustomAttributes, "NullableContextAttribute");
        return flag == 2 ? NullabilityState.Nullable : NullabilityState.NotNull;
    }

    /// <summary>The compiler's nullable flag (0 oblivious, 1 not null, 2 nullable) from a [Nullable]/[NullableContext] attribute.</summary>
    private static byte? NullableFlag(IEnumerable<CustomAttributeData> attributes, string attributeName) =>
        attributes.FirstOrDefault(a => a.AttributeType.Name == attributeName)?.ConstructorArguments[0].Value switch
        {
            byte flag => flag,
            IReadOnlyList<CustomAttributeTypedArgument> flags => (byte)flags[0].Value!,
            _ => null,
        };

    /// <summary>
    /// The Piper view of a .NET value: <c>Nullable&lt;T&gt;</c> and possibly-null references become <c>T?</c>
    /// (with the boundary recorded so the conversion can be emitted). Null if Piper can't represent the type.
    /// </summary>
    private (TypeSymbol Type, ClrBoundary Boundary)? MapValue(Type type, NullabilityState state, IReadOnlyDictionary<Type, TypeSymbol> map)
    {
        if (ClrTypes.FromClr(type, map, _option) is not { } mapped)
            return null;
        if (Nullable.GetUnderlyingType(type) is not null)
            return (mapped, ClrBoundary.NullableValue);
        if (state == NullabilityState.Nullable && ClrTypes.IsReferenceType(mapped))
            return (_option.Construct([mapped]), ClrBoundary.NullableReference);
        return (mapped, ClrBoundary.None);
    }

    /// <summary>The type of the raw .NET expression before any null-to-Option conversion.</summary>
    private static TypeSymbol BoundaryInnerType((TypeSymbol Type, ClrBoundary Boundary) value) =>
        value.Boundary == ClrBoundary.NullableReference ? value.Type.TypeArguments[0] : value.Type;

    private static BoundExpression FromClrBoundary(BoundExpression raw, (TypeSymbol Type, ClrBoundary Boundary) value) => value.Boundary switch
    {
        ClrBoundary.NullableReference => new BoundNullableBoundaryExpression(raw, NullableBoundaryKind.FromNullableReference, value.Type),
        ClrBoundary.NullableValue => new BoundNullableBoundaryExpression(raw, NullableBoundaryKind.FromNullableValue, value.Type),
        _ => raw,
    };
}
