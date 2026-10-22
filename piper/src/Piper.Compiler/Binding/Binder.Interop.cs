using System.Collections.Immutable;
using System.Reflection;
using Piper.Syntax;
using Piper.Text;

namespace Piper.Binding;

/// <summary>
/// Calling .NET: static and instance methods, constructors, properties and fields, found by reflection.
/// Overloads are resolved with a simplified version of C#'s rules (identity beats numeric widening, which
/// beats a reference conversion, which beats <c>object</c>). Where .NET may produce null, Piper sees an
/// Option; where .NET accepts null, an Option may be passed (None becomes null).
/// Not supported (yet): generic methods, ref/out parameters, indexers, property setters, delegates.
/// </summary>
public sealed partial class Binder
{
    private sealed record ClrParameter(TypeSymbol Type, ClrBoundary Boundary);

    /// <summary>A .NET method or constructor as Piper sees it. <see cref="ParamsElement"/> is set for a <c>params</c> array.</summary>
    private sealed record ClrCandidate(
        MethodBase Member,
        ImmutableArray<ClrParameter> Parameters,
        int RequiredCount,
        ClrParameter? ParamsElement,
        TypeSymbol ReturnType,
        ClrBoundary ReturnBoundary);

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

        var methods = clrType.GetMethods(flags).Where(m => m.Name == member.Text).ToList();
        if (methods.Count == 0)
        {
            BindArgumentsForDiagnostics(arguments);
            _diagnostics.ReportUnknownMethod(member.Span, type, member.Text);
            return new BoundErrorExpression();
        }
        return BindClrInvocation(null, type, methods, $"{type}.{member.Text}", arguments.Value, span, map);
    }

    private BoundExpression BindClrInstanceCall(BoundExpression receiver, Token member, ImmutableArray<ExpressionSyntax> arguments, TextSpan span)
    {
        var (clrType, map) = ClrContext(receiver.Type);
        var methods = InstanceMethods(clrType).Where(m => m.Name == member.Text).ToList();
        if (methods.Count == 0)
        {
            BindArgumentsForDiagnostics(arguments);
            _diagnostics.ReportUnknownMethod(member.Span, receiver.Type, member.Text);
            return new BoundErrorExpression();
        }
        return BindClrInvocation(receiver, receiver.Type, methods, member.Text, arguments, span, map);
    }

    /// <summary>Public instance methods, including those an interface type inherits from its base interfaces.</summary>
    private static IEnumerable<MethodInfo> InstanceMethods(Type type)
    {
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance).AsEnumerable();
        if (type.IsInterface)
            methods = methods.Concat(type.GetInterfaces().SelectMany(i => i.GetMethods())).Concat(typeof(object).GetMethods());
        return methods;
    }

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
            if (read is not { } value || MapValue(value.Type, value.Info.ReadState, map) is not { } mapped)
                continue;

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
        return BindClrInvocation(null, constructed, constructors, constructed.Name, arguments, span, ((ClrTypeSymbol)constructed).GenericMap);
    }

    private BoundExpression BindClrInvocation(
        BoundExpression? receiver,
        TypeSymbol container,
        IEnumerable<MethodBase> members,
        string displayName,
        ImmutableArray<ExpressionSyntax> argumentSyntax,
        TextSpan span,
        IReadOnlyDictionary<Type, TypeSymbol> map)
    {
        var arguments = argumentSyntax.Select(a => RequireValue(BindExpression(a), a.Span)).ToList();
        if (arguments.Any(a => a.Type == TypeSymbol.Error))
            return new BoundErrorExpression();

        var candidates = members.Select(m => Describe(m, container, map)).OfType<ClrCandidate>().ToList();
        var applicable = new List<(ClrCandidate Candidate, int[] Scores, bool Expanded)>();
        foreach (var candidate in candidates)
        {
            if (Score(candidate, arguments, expanded: false) is { } normal)
                applicable.Add((candidate, normal, false));
            else if (candidate.ParamsElement is not null && Score(candidate, arguments, expanded: true) is { } expanded)
                applicable.Add((candidate, expanded, true));
        }

        if (applicable.Count == 0)
        {
            _diagnostics.ReportNoApplicableOverload(span, displayName, arguments.Select(a => a.Type));
            return new BoundErrorExpression();
        }

        // Keep the candidates no other candidate beats on every argument.
        var best = applicable
            .Where(a => !applicable.Any(b => !ReferenceEquals(a.Candidate, b.Candidate) && Beats(b, a)))
            .ToList();
        if (best.Count != 1)
        {
            _diagnostics.ReportAmbiguousOverload(span, displayName, best.Select(b => b.Candidate.Member.ToString() ?? displayName));
            return new BoundErrorExpression();
        }

        var (chosen, _, isExpanded) = best[0];
        var converted = arguments.Select((argument, i) =>
            ConvertClrArgument(argument, ParameterFor(chosen, i, isExpanded), argumentSyntax[i].Span)).ToImmutableArray();

        var resultType = chosen.ReturnType;
        var call = new BoundClrInvocationExpression(receiver, container, chosen.Member, converted, BoundaryInnerType((resultType, chosen.ReturnBoundary)));
        return FromClrBoundary(call, (resultType, chosen.ReturnBoundary));
    }

    private static bool Beats((ClrCandidate Candidate, int[] Scores, bool Expanded) a, (ClrCandidate Candidate, int[] Scores, bool Expanded) b)
    {
        bool allAtLeastAsGood = a.Scores.Zip(b.Scores).All(p => p.First <= p.Second);
        // As in C#: a better conversion wins; then a normal form beats a params expansion, and a candidate
        // that needs fewer default arguments filled in beats one that needs more.
        bool someBetter = a.Scores.Zip(b.Scores).Any(p => p.First < p.Second) ||
                          (!a.Expanded && b.Expanded) ||
                          (a.Expanded == b.Expanded && a.Candidate.Parameters.Length < b.Candidate.Parameters.Length);
        return allAtLeastAsGood && someBetter;
    }

    private static ClrParameter ParameterFor(ClrCandidate candidate, int index, bool expanded) =>
        expanded && index >= candidate.Parameters.Length - 1 ? candidate.ParamsElement! : candidate.Parameters[index];

    /// <summary>One score per argument (lower is a better conversion), or null if the candidate can't take these arguments.</summary>
    private static int[]? Score(ClrCandidate candidate, List<BoundExpression> arguments, bool expanded)
    {
        int fixedCount = expanded ? candidate.Parameters.Length - 1 : candidate.Parameters.Length;
        if (expanded ? arguments.Count < fixedCount : arguments.Count < candidate.RequiredCount || arguments.Count > fixedCount)
            return null;

        var scores = new int[arguments.Count];
        for (int i = 0; i < arguments.Count; i++)
        {
            if (ConversionScore(arguments[i].Type, ParameterFor(candidate, i, expanded)) is not { } score)
                return null;
            scores[i] = score;
        }
        return scores;
    }

    private static int? ConversionScore(TypeSymbol argument, ClrParameter parameter)
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
        if (targetClr is not null && ClrTypes.ToClr(argument) is { } argumentClr && targetClr.IsAssignableFrom(argumentClr))
            return 10;
        return null;
    }

    private static BoundExpression ConvertClrArgument(BoundExpression argument, ClrParameter parameter, TextSpan span)
    {
        if (argument.Type.IsOption && parameter.Boundary != ClrBoundary.None && argument.Type != parameter.Type ||
            argument.Type.IsOption && parameter.Boundary == ClrBoundary.NullableValue)
        {
            var kind = parameter.Boundary == ClrBoundary.NullableValue ? NullableBoundaryKind.ToNullableValue : NullableBoundaryKind.ToNullableReference;
            return new BoundNullableBoundaryExpression(argument, kind, parameter.Type);
        }

        // Numeric widening; other conversions (to object, to a base type or interface) are implicit in C# too.
        if (argument.Type.IsNumeric && parameter.Type.IsNumeric && argument.Type != parameter.Type)
            return new BoundConversionExpression(parameter.Type, argument);
        return argument;
    }

    /// <summary>Describes a .NET member in Piper types, or returns null if Piper can't call it.</summary>
    private ClrCandidate? Describe(MethodBase member, TypeSymbol container, IReadOnlyDictionary<Type, TypeSymbol> map)
    {
        if (member.IsGenericMethodDefinition || !member.IsPublic)
            return null;

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
            if (MapValue(info.ParameterType, nullability.Create(info).WriteState, map) is not { } mapped)
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
            MethodInfo method => MapValue(method.ReturnType, nullability.Create(method.ReturnParameter).ReadState, map),
            _ => null,
        };
        if (result is not { } returned)
            return null;

        return new ClrCandidate(member, parameters.ToImmutable(), required, paramsElement, returned.Item1, returned.Item2);
    }

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
