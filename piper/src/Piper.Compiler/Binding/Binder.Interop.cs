using System.Collections.Immutable;
using System.Reflection;
using Piper.Diagnostics;
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
/// Out and ref parameters take <c>out</c>/<c>ref</c> arguments, and events take handlers with <c>+=</c> and <c>-=</c>.
/// </summary>
public sealed partial class Binder
{
    /// <param name="IsOut">An <c>out</c> parameter: <see cref="Type"/> is what it produces.</param>
    /// <param name="NullWhenFalse">An out parameter marked <c>[MaybeNullWhen(false)]</c>: null only when the method returns false.</param>
    private sealed record ClrParameter(TypeSymbol Type, ClrBoundary Boundary, bool IsOut = false, bool NullWhenFalse = false, bool IsRef = false);

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

    /// <summary>
    /// An argument: bound, or (for lambdas) bound later against each candidate's parameter type. An <c>out</c>
    /// argument (<see cref="Out"/>) is bound once a candidate is chosen; <see cref="OutVariable"/> is the existing
    /// variable it names, if any.
    /// </summary>
    private sealed record ClrArgument(BoundExpression? Bound, LambdaExpressionSyntax? Lambda, TextSpan Span)
    {
        public OutArgumentSyntax? Out { get; init; }

        /// <summary>`ref x`: <see cref="Bound"/> is the variable or array element passed.</summary>
        public RefArgumentSyntax? Ref { get; init; }

        public VariableSymbol? OutVariable { get; init; }

        /// <summary>`count: 2`: the parameter a named argument is for.</summary>
        public string? Name { get; init; }

        public TypeSymbol? Type => Bound?.Type ?? OutVariable?.Type;
    }

    /// <param name="Names">Named arguments' names, by position (empty when all are positional), for C# to match the same way.</param>
    private sealed record ResolvedClrCall(ClrCandidate Candidate, ImmutableArray<BoundExpression> Arguments, ImmutableArray<string?> Names);

    private static bool IsClrBacked(TypeSymbol type) => type is ClrTypeSymbol || ClrTypes.BuiltinTypes.ContainsKey(type);

    private void CheckAccessible(MethodSymbol method, Token name)
    {
        // A class's private method is private to the class; an extend block's, to its file.
        if (method.ContainingType is ClassSymbol owner)
        {
            if (method.IsPrivate && _currentClass != owner)
                _diagnostics.ReportPrivateMember(name.Span, method.Name, owner.Name);
        }
        else if (method.IsPrivate && method.DeclaringFile != _file.Source)
            _diagnostics.ReportPrivateDeclaration(name.Span, method.Name, method.DeclaringFile!.FileName);
    }

    /// <summary>The reflection type whose members to search, and how its generic parameters map to Piper types.</summary>
    private static (Type Type, IReadOnlyDictionary<Type, TypeSymbol> Map) ClrContext(TypeSymbol type) => type switch
    {
        ClrTypeSymbol clr => (clr.Type, clr.GenericMap),
        // A class's .NET members are its .NET base class's: an Exception subclass's Message.
        _ when type.OriginalDefinition is ClassSymbol @class => (@class.ClrBase.Type, @class.ClrBase.GenericMap),
        _ => (ClrTypes.BuiltinTypes[type], new Dictionary<Type, TypeSymbol>()),
    };

    private BoundExpression BindClrStaticMember(
        TypeSymbol type,
        Token member,
        ImmutableArray<ExpressionSyntax>? arguments,
        TextSpan span,
        ImmutableArray<TypeSymbol>? explicitTypeArguments)
    {
        var (clrType, map) = ClrContext(type);
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
        if (arguments is null)
        {
            if (explicitTypeArguments is not null)
            {
                _diagnostics.ReportNotAValue(member.Span, member.Text, "method");
                return new BoundErrorExpression();
            }
            return BindClrMemberRead(null, type, member, clrType.GetMember(member.Text, flags), map);
        }

        var methods = WithoutHidden(clrType.GetMethods(flags | BindingFlags.FlattenHierarchy).Where(m => m.Name == member.Text)).ToList();
        if (methods.Count == 0)
        {
            BindArgumentsForDiagnostics(arguments);
            _diagnostics.ReportUnknownMethod(member.Span, type, member.Text);
            return new BoundErrorExpression();
        }
        return BindClrInvocation(null, type, methods, $"{type}.{member.Text}", arguments.Value, [], span, map, explicitTypeArguments, member.Span);
    }

    private BoundExpression BindClrInstanceCall(
        BoundExpression receiver,
        Token member,
        ImmutableArray<ExpressionSyntax> arguments,
        TextSpan span,
        ImmutableArray<TypeSymbol>? explicitTypeArguments)
    {
        var (clrType, map) = ClrContext(receiver.Type);
        var methods = InstanceMethods(clrType, SeesProtected(receiver.Type)).Where(m => m.Name == member.Text).ToList();
        // Extension methods from the namespaces this file imports: `numbers.Where(n => n > 2)`.
        var extensions = ImportedExtensionMethods(member.Text);
        BoundExpression Instance() => BindClrInvocation(receiver, receiver.Type, methods, member.Text, arguments, [], span, map, explicitTypeArguments, member.Span);
        BoundExpression Extension() => BindClrInvocation(
            null, receiver.Type, extensions, member.Text, arguments, [receiver], span, new Dictionary<Type, TypeSymbol>(), explicitTypeArguments);

        if (methods.Count > 0 && extensions.Count > 0)
        {
            // As in C#, an extension method is only considered when no instance method applies: Proto.Actor's
            // `root.RequestAsync<T>(pid, message, timeout)` is an extension beside instance overloads of the same name.
            BoundExpression instance = null!, extension = null!;
            var instanceDiagnostics = TrialDiagnostics(() => instance = Instance());
            if (instanceDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error) &&
                TrialDiagnostics(() => extension = Extension()) is var extensionDiagnostics &&
                !extensionDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            {
                _diagnostics.AddRange(extensionDiagnostics);
                return extension;
            }
            _diagnostics.AddRange(instanceDiagnostics);
            return instance;
        }
        if (methods.Count > 0)
            return Instance();
        if (extensions.Count > 0)
            return Extension();

        BindArgumentsForDiagnostics(arguments);
        _diagnostics.ReportUnknownMethod(member.Span, receiver.Type, member.Text);
        return new BoundErrorExpression();
    }

    private List<MethodInfo> ImportedExtensionMethods(string name) =>
        [.. _file.Usings.Append(_file.Namespace)
            .Where(n => !n.IsGlobal)
            .Select(n => n.FullName)
            .Distinct()
            .SelectMany(n => _globalNamespace.Clr.ExtensionMethods(n, name))];

    /// <summary>
    /// Public instance methods, including those an interface type inherits from its base interfaces, and protected ones
    /// when <paramref name="protectedToo"/>.
    /// </summary>
    private static IEnumerable<MethodInfo> InstanceMethods(Type type, bool protectedToo = false)
    {
        var methods = protectedToo
            ? type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Where(m => m.IsPublic || IsProtected(m))
            : type.GetMethods(BindingFlags.Public | BindingFlags.Instance).AsEnumerable();
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
        var members = SeesProtected(receiver.Type)
            ? clrType.GetMember(member.Text, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Where(IsPublicOrProtected).ToArray()
            : clrType.GetMember(member.Text, BindingFlags.Public | BindingFlags.Instance);
        // An interface's properties include those of the interfaces it inherits (`IContext.Message`), which reflection leaves out.
        if (clrType.IsInterface && members.Length == 0)
            members = [.. clrType.GetInterfaces().SelectMany(i => i.GetMember(member.Text, BindingFlags.Public | BindingFlags.Instance))];
        return BindClrMemberRead(receiver, receiver.Type, member, members, map);
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
                PropertyInfo { CanRead: true } property when property.GetIndexParameters().Length == 0 &&
                                                             (property.GetMethod!.IsPublic || (receiver is not null && SeesProtected(receiver.Type) && IsProtected(property.GetMethod))) =>
                    (property.PropertyType, nullability.Create(property)),
                FieldInfo field => (field.FieldType, nullability.Create(field)),
                _ => null,
            };
            if (read is not { } value ||
                MapValue(value.Type, GenericAware(value.Info.ReadState, value.Type, member.CustomAttributes, member, "MaybeNull"), map,
                    ClrTypes.TupleNames(member.CustomAttributes)) is not { } mapped)
            {
                continue;
            }

            CheckClrObsolete(name.Span, member);
            var access = new BoundClrMemberExpression(receiver, container, member, BoundaryInnerType(mapped));
            return FromClrBoundary(access, mapped);
        }

        if (members.OfType<MethodInfo>().Any())
            _diagnostics.ReportNotAValue(name.Span, name.Text, "method");
        else if (members.OfType<EventInfo>().Any())
            _diagnostics.ReportEventMisused(name.Span, name.Text);
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
        IReadOnlyDictionary<Type, TypeSymbol> map,
        ImmutableArray<TypeSymbol>? explicitTypeArguments = null,
        TextSpan? nameSpan = null)
    {
        if (ResolveClrOverload(members, container, displayName, argumentSyntax, leadingArguments, span, map, explicitTypeArguments) is not { } resolved)
            return new BoundErrorExpression();

        var chosen = resolved.Candidate;
        CheckClrObsolete(nameSpan ?? span, chosen.Member);
        var call = new BoundClrInvocationExpression(
            receiver, chosen.Container, chosen.Member, resolved.Arguments,
            BoundaryInnerType((chosen.ReturnType, chosen.ReturnBoundary)), chosen.TypeArguments)
        {
            ArgumentNames = resolved.Names,
        };
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
        IReadOnlyDictionary<Type, TypeSymbol> map,
        ImmutableArray<TypeSymbol>? explicitTypeArguments = null)
    {
        if (explicitTypeArguments?.Contains(TypeSymbol.Error) == true)
        {
            BindArgumentsForDiagnostics(argumentSyntax);
            return null;
        }

        // `name: value` arguments come after positional ones, as in C#.
        if (argumentSyntax.SkipWhile(a => a is not NamedArgumentSyntax).FirstOrDefault(a => a is not NamedArgumentSyntax) is { } misplaced)
        {
            _diagnostics.ReportNamedArgument(misplaced.Span, "Positional arguments must come before named ones.");
            BindArgumentsForDiagnostics([.. argumentSyntax.Select(a => a is NamedArgumentSyntax n ? n.Value : a)]);
            return null;
        }
        var arguments = leadingArguments.Select(a => new ClrArgument(a, null, span))
            .Concat(argumentSyntax.Select(written =>
            {
                var (name, a) = written is NamedArgumentSyntax named ? (named.Name.Text, named.Value) : (null, written);
                return (a switch
                {
                    LambdaExpressionSyntax lambda => new ClrArgument(null, lambda, a.Span),
                    OutArgumentSyntax output => new ClrArgument(null, null, a.Span) { Out = output, OutVariable = OutTarget(output) },
                    RefArgumentSyntax reference => new ClrArgument(BindRefTarget(reference), null, a.Span) { Ref = reference },
                    _ => new ClrArgument(RequireArgument(BindExpression(a), a.Span), null, a.Span),
                }) with { Name = name };
            }))
            .ToList();
        if (arguments.Any(a => a.Type == TypeSymbol.Error || (a.Out is { Declaration: null, IsDiscard: false } && a.OutVariable is null)))
            return null;

        List<(ClrCandidate Candidate, int[] Scores, bool Expanded, int[]? Positions)> Applicable(bool lambdaShapesOnly)
        {
            var applicable = new List<(ClrCandidate Candidate, int[] Scores, bool Expanded, int[]? Positions)>();
            foreach (var member in members)
            {
                var memberMap = map;
                if (explicitTypeArguments is { } written)
                {
                    // `Enumerable.Empty<Int>()`: only generic methods with that many type parameters, and nothing to infer.
                    if (member is not MethodInfo { IsGenericMethodDefinition: true } generic || generic.GetGenericArguments().Length != written.Length)
                        continue;
                    memberMap = new Dictionary<Type, TypeSymbol>(map.Concat(generic.GetGenericArguments().Zip(written, KeyValuePair.Create)));
                }
                else if (member is MethodInfo { IsGenericMethodDefinition: true } inferable)
                {
                    if (InferMethodTypeArguments(inferable, arguments, map) is not { } inferred)
                        continue;
                    memberMap = inferred;
                }

                if (Describe(member, container, memberMap) is not { } candidate)
                    continue;
                if (!Positions(candidate, arguments, out var positions))
                    continue;
                if (Score(candidate, arguments, expanded: false, positions, lambdaShapesOnly) is { } normal)
                    applicable.Add((candidate, normal, false, positions));
                else if (positions is null && candidate.ParamsElement is not null && Score(candidate, arguments, expanded: true, null, lambdaShapesOnly) is { } expanded)
                    applicable.Add((candidate, expanded, true, null));
            }
            return applicable;
        }

        var applicable = Applicable(lambdaShapesOnly: false);
        if (applicable.Count == 0)
        {
            if (!ReportLambdaErrors(Applicable(lambdaShapesOnly: true), arguments))
            {
                var described = arguments.Select(a => a.Type ?? (TypeSymbol)ClrTypeSymbol.For(typeof(Delegate)));
                _diagnostics.ReportNoApplicableOverload(span, displayName, described);
            }
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

        var (chosen, _, isExpanded, chosenPositions) = best[0];
        var converted = arguments.Select((argument, i) =>
        {
            var parameter = ParameterFor(chosen, chosenPositions?[i] ?? i, isExpanded);
            if (argument.Out is { } output)
                return BindOutArgument(output, argument.OutVariable, parameter);
            if (argument.Ref is not null)
                return new BoundRefArgument(argument.Bound!);
            return argument.Lambda is { } lambda
                ? BindLambda(lambda, parameter.Type)
                : ConvertClrArgument(argument.Bound!, parameter);
        }).ToImmutableArray();
        var names = arguments.Any(a => a.Name is not null) ? arguments.Select(a => a.Name).ToImmutableArray() : [];
        return new ResolvedClrCall(chosen, converted, names);
    }

    /// <summary>
    /// Which of the candidate's parameters each argument goes to: null when all are positional (each to its own index),
    /// otherwise positional ones in order and named ones by name. False if the names don't fit the candidate: one it
    /// doesn't have, a parameter given twice, or a required one left out.
    /// </summary>
    private static bool Positions(ClrCandidate candidate, List<ClrArgument> arguments, out int[]? positions)
    {
        positions = null;
        if (!arguments.Any(a => a.Name is not null))
            return true;
        var names = candidate.Member.GetParameters().Select(p => p.Name).ToList();
        var result = new int[arguments.Count];
        var used = new HashSet<int>();
        for (int i = 0; i < arguments.Count; i++)
        {
            int index = arguments[i].Name is { } name ? names.IndexOf(name) : i;
            if (index < 0 || index >= candidate.Parameters.Length || !used.Add(index))
                return false;
            result[i] = index;
        }
        if (Enumerable.Range(0, candidate.RequiredCount).Any(i => !used.Contains(i)))
            return false;
        positions = result;
        return true;
    }

    /// <summary>The existing variable named by <c>out n</c>, or null (after reporting, unless it's <c>out _</c> or <c>out let n</c>).</summary>
    private VariableSymbol? OutTarget(OutArgumentSyntax syntax)
    {
        if (syntax.Declaration is not null || syntax.IsDiscard || syntax.Name.IsMissing)
            return null;
        if (_scope.Lookup(syntax.Name.Text) is VariableSymbol variable)
            return variable;
        if (!ReportIfPrivateElsewhere(syntax.Name))
            _diagnostics.ReportUndefinedName(syntax.Name.Span, syntax.Name.Text);
        return null;
    }

    /// <summary><c>ref x</c>: x must be something .NET can write to, a <c>var</c> variable or an array element.</summary>
    private BoundExpression BindRefTarget(RefArgumentSyntax syntax)
    {
        var target = BindExpression(syntax.Target);
        if (target is BoundVariableExpression { Variable.IsMutable: true } or BoundIndexExpression { Indexer: null } || target.Type == TypeSymbol.Error)
            return target;
        _diagnostics.ReportInvalidRefTarget(syntax.Target.Span);
        return new BoundErrorExpression();
    }


    /// <summary>
    /// <c>extend Money: IComparable&lt;Money&gt;</c>: each abstract method of the .NET interface needs a public Piper method of
    /// the same name whose signature is the .NET one as Piper sees it, with a parameter or return type that .NET marks as
    /// possibly null being an Option (<c>fn CompareTo(other: Money?) => int</c>). Where that makes the C# signatures
    /// differ, a bridge is recorded for the emitter. Interfaces with properties, events, static or generic methods, or
    /// ref and out parameters, can't be implemented; false after reporting such an interface.
    /// </summary>
    private bool CheckClrConformance(NamedTypeDefinition definition, TypeSymbol @interface, TypeSyntax syntax)
    {
        var (clrType, map) = ClrContext(@interface);
        var members = clrType.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m is not MethodInfo { IsAbstract: false })
            .ToList();
        if (members.Any(m => m is not MethodInfo { IsStatic: false, IsSpecialName: false, IsGenericMethodDefinition: false }))
        {
            _diagnostics.ReportUnsupportedInterface(syntax.Span, @interface);
            return false;
        }

        foreach (var required in members.Cast<MethodInfo>())
        {
            if (Describe(required, @interface, map) is not { } candidate || candidate.Parameters.Any(p => p.IsOut || p.IsRef))
            {
                _diagnostics.ReportUnsupportedInterface(syntax.Span, @interface);
                return false;
            }

            var implementation = definition.Methods.FirstOrDefault(m => m.Name == required.Name);
            if (implementation is null)
            {
                _diagnostics.ReportMissingTraitMethod(syntax.Span, definition, required.Name, @interface);
                continue;
            }

            var parameterTypes = candidate.Parameters.Select(p => p.Boundary == ClrBoundary.NullableReference ? _option.Construct([p.Type]) : p.Type).ToList();
            bool matches = !implementation.IsStatic && !implementation.IsPrivate && implementation.TypeParameters.IsEmpty &&
                           implementation.ReturnType == candidate.ReturnType &&
                           implementation.Parameters.Select(p => p.Type).SequenceEqual(parameterTypes);
            if (!matches)
            {
                var expected = $"fn {required.Name}({string.Join(", ", required.GetParameters().Zip(parameterTypes, (p, t) => $"{p.Name}: {t}"))})" +
                               (candidate.ReturnType == TypeSymbol.Unit ? "" : $" => {candidate.ReturnType}");
                _diagnostics.CurrentSource = implementation.DeclaringFile;
                _diagnostics.ReportTraitSignatureMismatch(implementation.Declaration!.Name.Span, required.Name, @interface, $"a public {expected}");
                _diagnostics.CurrentSource = _file.Source;
                continue;
            }

            // C#'s own types: an Option stands for a .NET `T?`.
            static TypeSymbol Inner(TypeSymbol type, ClrBoundary boundary) => boundary == ClrBoundary.None ? type : type.TypeArguments[0];
            var parameters = candidate.Parameters
                .Select(p => (p.Boundary == ClrBoundary.NullableValue ? p.Type.TypeArguments[0] : p.Type, p.Boundary))
                .ToImmutableArray();
            if (candidate.ReturnBoundary != ClrBoundary.None || parameters.Any(p => p.Boundary != ClrBoundary.None))
            {
                definition.InterfaceBridges.Add(new ClrInterfaceBridge(
                    implementation, @interface, parameters, Inner(candidate.ReturnType, candidate.ReturnBoundary), candidate.ReturnBoundary));
            }
        }
        return true;
    }
    /// <summary>The Piper type of what an <c>out</c> parameter produces: an Option where .NET may produce null.</summary>
    private TypeSymbol OutType(ClrParameter parameter) =>
        parameter.Boundary == ClrBoundary.NullableReference ? _option.Construct([parameter.Type]) : parameter.Type;

    /// <summary>
    /// <c>out let n</c> declares n, typed by the parameter, in the enclosing statement's scope (for an if condition, the
    /// scope around the if, so a guard like <c>if !Int.TryParse(s, out let n) { return; }</c> leaves n usable), as in C#.
    /// </summary>
    private BoundExpression BindOutArgument(OutArgumentSyntax syntax, VariableSymbol? existing, ClrParameter parameter)
    {
        var type = OutType(parameter);
        if (syntax.IsDiscard)
            return new BoundOutArgument(null, declares: false, parameter.Type, ClrBoundary.None, type);

        if (syntax.Declaration is { } keyword)
        {
            // Where the call is known to have returned true, `[MaybeNullWhen(false)] out` wrote a value.
            var scope = _outScope ?? _scope;
            var boundary = parameter.Boundary;
            if (parameter.NullWhenFalse && _outsKnownTrue is { } knownTrue && knownTrue.Arguments.Any(a => ReferenceEquals(a, syntax)))
                (type, boundary, scope) = (parameter.Type, ClrBoundary.None, knownTrue.Scope);

            var variable = Declared(new VariableSymbol(syntax.Name.Text, type, keyword.Kind == TokenKind.MutKeyword, VariableKind.Local), syntax.Name);
            if (!syntax.Name.IsMissing && !scope.TryDeclare(variable))
                _diagnostics.ReportDuplicateDeclaration(syntax.Name.Span, variable.Name);
            return new BoundOutArgument(variable, declares: true, parameter.Type, boundary, type);
        }

        if (!existing!.IsMutable)
            _diagnostics.ReportCannotAssignImmutable(syntax.Name.Span, existing);
        return new BoundOutArgument(existing, declares: false, parameter.Type, parameter.Boundary, type);
    }

    private static bool Beats(
        (ClrCandidate Candidate, int[] Scores, bool Expanded, int[]? Positions) a, (ClrCandidate Candidate, int[] Scores, bool Expanded, int[]? Positions) b)
    {
        // Per argument, negative when a's conversion is better: by score, then (as C#'s "better conversion target") the
        // more specific parameter type, so a List<Task<int>> goes to IEnumerable<Task<int>> rather than IEnumerable<Task>.
        var comparisons = a.Scores.Select((score, i) => score != b.Scores[i] ? score.CompareTo(b.Scores[i]) : TargetComparison(a, b, i)).ToList();
        bool allAtLeastAsGood = comparisons.All(c => c <= 0);
        // As in C#: a better conversion wins; then a normal form beats a params expansion, a candidate that
        // needs fewer default arguments beats one that needs more, and a non-generic method beats a generic one.
        bool someBetter = comparisons.Any(c => c < 0) ||
                          (!a.Expanded && b.Expanded) ||
                          (a.Expanded == b.Expanded && a.Candidate.Parameters.Length < b.Candidate.Parameters.Length) ||
                          (a.Candidate.TypeArguments.IsEmpty && !b.Candidate.TypeArguments.IsEmpty) ||
                          IsMoreSpecific(a.Candidate, b.Candidate);
        return allAtLeastAsGood && someBetter;
    }

    /// <summary>-1 if a's parameter type for argument <paramref name="index"/> converts to b's but not back (so is more specific), 1 if the reverse, else 0.</summary>
    private static int TargetComparison(
        (ClrCandidate Candidate, int[] Scores, bool Expanded, int[]? Positions) a, (ClrCandidate Candidate, int[] Scores, bool Expanded, int[]? Positions) b, int index)
    {
        if (ClrTypes.ToClr(ParameterFor(a.Candidate, a.Positions?[index] ?? index, a.Expanded).Type) is not { } first ||
            ClrTypes.ToClr(ParameterFor(b.Candidate, b.Positions?[index] ?? index, b.Expanded).Type) is not { } second || first == second)
        {
            return 0;
        }
        bool firstToSecond = second.IsAssignableFrom(first), secondToFirst = first.IsAssignableFrom(second);
        return firstToSecond && !secondToFirst ? -1 : secondToFirst && !firstToSecond ? 1 : 0;
    }

    /// <summary>
    /// C#'s last tie-break: when two candidates take the same parameter types once their type arguments are filled in,
    /// the one whose declared parameter types are less generic wins. So <c>Task.Run(async () =&gt; 1)</c> picks
    /// <c>Run&lt;T&gt;(Func&lt;Task&lt;T&gt;&gt;)</c> over <c>Run&lt;T&gt;(Func&lt;T&gt;)</c>.
    /// </summary>
    private static bool IsMoreSpecific(ClrCandidate a, ClrCandidate b)
    {
        if (!a.Parameters.Select(p => p.Type).SequenceEqual(b.Parameters.Select(p => p.Type)))
            return false;
        var comparisons = a.Member.GetParameters().Zip(b.Member.GetParameters(), (x, y) => Specificity(x.ParameterType, y.ParameterType)).ToList();
        return comparisons.All(c => c >= 0) && comparisons.Any(c => c > 0);
    }

    /// <summary>1 if <paramref name="a"/> is more specific than <paramref name="b"/> (a type parameter is the least specific), -1 if less, 0 if neither.</summary>
    private static int Specificity(Type a, Type b)
    {
        if (a.IsGenericParameter || b.IsGenericParameter)
            return a.IsGenericParameter == b.IsGenericParameter ? 0 : a.IsGenericParameter ? -1 : 1;
        if (a.HasElementType && b.HasElementType)
            return Specificity(a.GetElementType()!, b.GetElementType()!);
        if (!a.IsGenericType || !b.IsGenericType || a.GetGenericTypeDefinition() != b.GetGenericTypeDefinition())
            return 0;
        var parts = a.GetGenericArguments().Zip(b.GetGenericArguments(), Specificity).ToList();
        return parts.All(p => p >= 0) && parts.Any(p => p > 0) ? 1 : parts.All(p => p <= 0) && parts.Any(p => p < 0) ? -1 : 0;
    }

    private static ClrParameter ParameterFor(ClrCandidate candidate, int index, bool expanded) =>
        expanded && index >= candidate.Parameters.Length - 1 ? candidate.ParamsElement! : candidate.Parameters[index];

    /// <summary>One score per argument (lower is a better conversion), or null if the candidate can't take these arguments.</summary>
    /// <summary>
    /// When no overload accepts the arguments because a lambda's body has errors (<paramref name="byShape"/> are the
    /// overloads that would accept them judging lambdas by their parameters alone), reports the lambdas' errors where
    /// they are rather than "no overload". A lambda that could be one of several delegate types (`Task.Run(() => …)` is
    /// an Action or a Func&lt;Task&gt;) gets the errors it has as every one of them, which are the user's, not a mismatch
    /// with one overload. False if there are none to report.
    /// </summary>
    private bool ReportLambdaErrors(List<(ClrCandidate Candidate, int[] Scores, bool Expanded, int[]? Positions)> byShape, List<ClrArgument> arguments)
    {
        if (byShape.Count == 0)
            return false;

        var errors = new List<Diagnostic>();
        for (int i = 0; i < arguments.Count; i++)
        {
            if (arguments[i].Lambda is not { } lambda)
                continue;
            var delegateTypes = byShape.Select(c => ParameterFor(c.Candidate, c.Positions?[i] ?? i, c.Expanded).Type).Distinct();
            var common = delegateTypes
                .Select(type => TrialDiagnostics(() => BindLambda(lambda, type)).Where(d => d.Severity == DiagnosticSeverity.Error))
                .Aggregate((all, next) => all.Intersect(next).ToList());
            errors.AddRange(common);
        }
        _diagnostics.AddRange(errors);
        return errors.Count > 0;
    }

    /// <summary>The diagnostics <paramref name="bind"/> reports, without reporting them.</summary>
    private List<Diagnostic> TrialDiagnostics(Action bind)
    {
        var real = _diagnostics;
        _diagnostics = new DiagnosticBag { CurrentSource = real.CurrentSource };
        try
        {
            bind();
            return [.. _diagnostics];
        }
        finally
        {
            _diagnostics = real;
        }
    }

    /// <param name="lambdaShapesOnly">Accept a lambda whose parameters fit the delegate, without binding its body.</param>
    private int[]? Score(ClrCandidate candidate, List<ClrArgument> arguments, bool expanded, int[]? positions, bool lambdaShapesOnly = false)
    {
        int fixedCount = expanded ? candidate.Parameters.Length - 1 : candidate.Parameters.Length;
        if (positions is null && (expanded ? arguments.Count < fixedCount : arguments.Count < candidate.RequiredCount || arguments.Count > fixedCount))
            return null;

        var scores = new int[arguments.Count];
        for (int i = 0; i < arguments.Count; i++)
        {
            var parameter = ParameterFor(candidate, positions?[i] ?? i, expanded);
            // An out argument goes only to an out parameter (an existing variable must have exactly its type), and vice versa.
            if (parameter.IsOut != arguments[i].Out is not null)
                return null;
            // Likewise ref: a ref argument must have exactly the parameter's type, since .NET may write to it.
            if (parameter.IsRef != arguments[i].Ref is not null)
                return null;
            int? score = arguments[i] switch
            {
                { Out: not null, OutVariable: { } variable } => variable.Type == OutType(parameter) ? 0 : null,
                { Out: not null } => 0,
                { Ref: not null, Bound: var target } => target!.Type == parameter.Type ? 0 : null,
                { Lambda: { } lambda } when lambdaShapesOnly => FitsShape(lambda, parameter.Type) ? 0 : null,
                { Lambda: { } lambda } => LambdaScore(lambda, parameter.Type),
                var argument => ConversionScore(argument.Bound!, parameter),
            };
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
        // `app.MapGet("/", (id: int) => …)`: as in C#, a lambda whose parameters are all typed has a natural type (a Func
        // or Action), so it can be any Delegate. A specific delegate type matches better.
        if (TakesAnyDelegate(delegateType, lambda))
            return Trial(() => BindLambda(lambda, null), out bool natural) is BoundLambdaExpression && !natural ? 15 : null;
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

    /// <summary>
    /// Whether the lambda's parameters fit <paramref name="delegateType"/>, ignoring its body: as many of them, and any
    /// types they're annotated with the delegate's (so `(clock: Clock) => …` isn't a RequestDelegate's HttpContext).
    /// </summary>
    private bool FitsShape(LambdaExpressionSyntax lambda, TypeSymbol delegateType)
    {
        if (TakesAnyDelegate(delegateType, lambda))
            return true;
        if (DelegateSignature(delegateType) is not { } signature || signature.Parameters.Length != lambda.Parameters.Length)
            return false;
        return lambda.Parameters.Zip(signature.Parameters).All(pair =>
            pair.First.Type is not { } annotation || Trial(() => BindType(annotation), out _) is var type && (type == pair.Second || type == TypeSymbol.Error));
    }

    /// <summary>Whether <paramref name="type"/> is <c>System.Delegate</c> (or MulticastDelegate) and the lambda has a natural type there.</summary>
    private static bool TakesAnyDelegate(TypeSymbol type, LambdaExpressionSyntax lambda) =>
        ClrTypes.ToClr(type) is { } clr && (clr == typeof(Delegate) || clr == typeof(MulticastDelegate)) &&
        lambda.Parameters.All(p => p.Type is not null);

    private int? ConversionScore(BoundExpression bound, ClrParameter parameter)
    {
        var argument = bound.Type;
        var target = parameter.Type;
        // An argument that never completes (`{ throw … }`) fits any parameter.
        if (argument == target || argument == TypeSymbol.Never)
            return 0;
        // An optional value passed where .NET accepts null (or a Nullable<T>).
        if (parameter.Boundary == ClrBoundary.NullableReference && argument.IsOption && argument.TypeArguments[0] == target)
            return 0;
        if (parameter.Boundary == ClrBoundary.NullableValue && argument == target.TypeArguments[0])
            return 1;
        // Numeric widening, preferring the narrowest target as C# does (Int → Long beats Int → Double).
        if (Operators.IsArithmetic(argument) && target.IsNumeric && Operators.IsImplicitlyConvertible(argument, target))
            return 1 + Operators.NumericRank(target);
        // A constant that fits: `stream.WriteByte(7)`.
        if (ConstantFolding.FitsImplicitly(bound, target))
            return 1 + Operators.NumericRank(target);

        var targetClr = ClrTypes.ToClr(target);
        if (targetClr == typeof(object))
            return argument.IsValueType ? 20 : null;
        if (target is ClrTypeSymbol && SelfAndBaseTypes(argument).Contains(target))
            return 12;
        if (targetClr is not null && ClrTypes.ToClr(argument) is { } argumentClr && targetClr.IsAssignableFrom(argumentClr))
            return 12;
        // A Piper type implementing a .NET interface with extend: `words.Sort(ByLength())`.
        if (target is ClrTypeSymbol { Type.IsInterface: true } && Implements(argument, target))
            return 12;
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

        // `params T[] items`: arguments past the fixed ones infer from the element type (`WhenAll(task1, task2)`).
        bool hasParams = parameters.Length > 0 && parameters[^1].IsDefined(typeof(ParamArrayAttribute)) && parameters[^1].ParameterType.IsArray;
        for (int i = 0; i < arguments.Count; i++)
        {
            if (arguments[i].Bound is not { } bound)
                continue;
            if (hasParams && i >= parameters.Length - 1 && !(bound.Type is ClrTypeSymbol { IsArray: true } && arguments.Count == parameters.Length))
                InferClr(parameters[^1].ParameterType.GetElementType()!, bound.Type, result, generics);
            else if (i < parameters.Length)
                InferClr(parameters[i].ParameterType is { IsByRef: true } byRef ? byRef.GetElementType()! : parameters[i].ParameterType, bound.Type, result, generics);
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
                // The delegate's return type: for an async lambda, the Task rather than its result.
                InferClr(invoke.ReturnType, DelegateSignature(typed.Type)?.Return ?? typed.ReturnType, result, generics);
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
            if (argument is ClrTypeSymbol { IsArray: true } array && array.Rank == parameter.GetArrayRank())
                InferClr(parameter.GetElementType()!, array.TypeArguments[0], result, generics);
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
            bool isOut = info.ParameterType.IsByRef && info.IsOut;
            // An `in` or `ref readonly` parameter takes its argument like a value parameter; a `ref` one takes `ref x`.
            bool isRef = info.ParameterType.IsByRef && !isOut && !info.IsIn &&
                         !info.CustomAttributes.Any(a => a.AttributeType.Name == "RequiresLocationAttribute");
            var parameterType = info.ParameterType.IsByRef ? info.ParameterType.GetElementType()! : info.ParameterType;

            NullabilityState state;
            bool nullWhenFalse = false;
            if (isOut)
            {
                // What an out parameter produces may be null: `out string? s`, or `[MaybeNullWhen(false)] out TValue value`.
                state = GenericAware(nullability.Create(info).ReadState, parameterType, info.CustomAttributes, member, "MaybeNull");
                if (info.CustomAttributes.FirstOrDefault(a => a.AttributeType.Name == "MaybeNullWhenAttribute") is { } maybeNullWhen)
                {
                    nullWhenFalse = state != NullabilityState.Nullable && maybeNullWhen.ConstructorArguments is [{ Value: false }];
                    state = NullabilityState.Nullable;
                }
            }
            else if (isRef)
            {
                // A ref argument is a variable of exactly the parameter's type, so .NET's nullability for it is not used:
                // `Array.Resize(ref items, 10)` takes an `int[]` though its parameter is `T[]?`.
                state = NullabilityState.NotNull;
            }
            else
            {
                state = GenericAware(nullability.Create(info).WriteState, parameterType, info.CustomAttributes, member, "AllowNull");
            }
            if (MapValue(parameterType, state, map, ClrTypes.TupleNames(info.CustomAttributes)) is not { } mapped)
                return null;

            var (type, boundary) = mapped;
            // A parameter that accepts (or produces) null takes the plain type; the Option is converted at the call.
            parameters.Add(new ClrParameter(boundary == ClrBoundary.NullableReference ? type.TypeArguments[0] : type, boundary, isOut, nullWhenFalse, isRef));
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
                GenericAware(nullability.Create(method.ReturnParameter).ReadState, method.ReturnType, method.ReturnParameter.CustomAttributes, method, "MaybeNull"),
                map,
                ClrTypes.TupleNames(method.ReturnParameter.CustomAttributes)),
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
        if (attributes.Any(a => a.AttributeType.Name == nullAttribute + "Attribute"))
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
    private (TypeSymbol Type, ClrBoundary Boundary)? MapValue(
        Type type, NullabilityState state, IReadOnlyDictionary<Type, TypeSymbol> map, IReadOnlyList<string?>? tupleNames = null)
    {
        if (ClrTypes.FromClr(type, map, _option, tupleNames) is not { } mapped)
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
