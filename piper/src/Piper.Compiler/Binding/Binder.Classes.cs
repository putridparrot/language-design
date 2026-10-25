using System.Collections.Immutable;
using System.Reflection;
using Piper.Syntax;
using Piper.Text;

namespace Piper.Binding;

// Classes: their headers (base class, traits and interfaces), primary constructor parameters, fields, and the scope
// their methods are bound in. Their methods go through the same machinery as an extend block's.
public sealed partial class Binder
{
    /// <summary>The class whose method or field initializer is being bound, for its private members; null elsewhere.</summary>
    private ClassSymbol? _currentClass;

    /// <summary>
    /// A class's base list, and the extend through which its methods are bound. The first entry is its base class if it's
    /// a class (Piper or .NET, given the base constructor's arguments); the others, or all, are traits and interfaces.
    /// </summary>
    private List<ExtendInfo> BindClassHeaders(FileContext file, IReadOnlyList<TypeSymbol> types)
    {
        InFile(file);
        var extends = new List<ExtendInfo>();
        foreach (var @class in types.OfType<ClassSymbol>().Where(c => c.DeclaringFile == file.Source))
        {
            var syntax = @class.Declaration;
            var typeParameterScope = TypeParameterScope(@class, file);
            var traits = new List<(TypeSymbol Trait, TypeSyntax Syntax)>();
            for (int i = 0; i < syntax.Bases.Length; i++)
            {
                var entry = syntax.Bases[i];
                var type = TypeSymbol.Error;
                WithScope(typeParameterScope, () => type = BindType(entry.Type));
                if (type == TypeSymbol.Error)
                    continue;

                if (IsClassType(type))
                {
                    if (i > 0)
                        _diagnostics.ReportInvalidBase(entry.Span, $"'{type}' is a class, so it must come first in the base list.");
                    else if (type is ClassSymbol { IsSealed: true } || type is ClrTypeSymbol { Type.IsSealed: true })
                        _diagnostics.ReportInvalidBase(entry.Span, $"'{type}' is sealed, so no class can derive from it.");
                    else if (type is ClrTypeSymbol clr && !HasAccessibleConstructor(clr.Type))
                        _diagnostics.ReportInvalidBase(entry.Span, $"'{type}' has no constructor a derived class can call.");
                    else
                        @class.Base = type;
                    continue;
                }

                if (entry.Arguments is not null)
                    _diagnostics.ReportInvalidBase(entry.Span, $"'{type}' isn't a class, so it has no constructor to pass arguments to.");
                if (!IsInterfaceType(type))
                {
                    _diagnostics.ReportInvalidBase(entry.Type.Span, $"'{type}' is neither a class nor a trait or interface, so a class can't derive from it.");
                    continue;
                }
                if (traits.Any(t => t.Trait == type))
                    _diagnostics.ReportDuplicateDeclaration(entry.Type.Span, type.Name);
                else
                    traits.Add((type, entry.Type));
            }
            @class.Conformances = [.. @class.Conformances, .. traits.Select(t => (t.Trait, (SourceText?)file.Source))];

            // Instance methods see the fields and the primary constructor's parameters by bare name; they're declared
            // here once bound (see BindClassMembers), and the scope is passed to the methods' signatures now.
            @class.MemberScope = new Scope(typeParameterScope);
            @class.StaticScope = new Scope(typeParameterScope);
            var target = new NamedTypeSyntax(syntax.Name.Span, [], syntax.Name, []);
            var extend = new ExtendDeclarationSyntax(syntax.Span, syntax.Name, target, [], syntax.Methods);
            extends.Add(new ExtendInfo(extend, file, @class, typeParameterScope, traits, @class.MemberScope, @class.StaticScope));
        }
        return extends;
    }

    private static bool IsClassType(TypeSymbol type) =>
        type.OriginalDefinition is ClassSymbol ||
        (type is ClrTypeSymbol { Type: { IsClass: true, IsArray: false } clr } && clr != typeof(string) && !clr.IsSubclassOf(typeof(Delegate)) && clr != typeof(object));

    /// <summary>The constructors a derived class can call: public or protected.</summary>
    private static ConstructorInfo[] AccessibleConstructors(Type type) =>
        [.. type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Where(c => c.IsPublic || c.IsFamily || c.IsFamilyOrAssembly)];

    private static bool HasAccessibleConstructor(Type type) => AccessibleConstructors(type).Length > 0;

    /// <summary>A class deriving from itself, directly or not, is reported and given no base.</summary>
    private void CheckClassCycles(IEnumerable<ClassSymbol> classes)
    {
        foreach (var @class in classes)
        {
            var chain = new List<ClassSymbol> { @class };
            for (var type = @class.Base?.OriginalDefinition as ClassSymbol; type is not null; type = type.Base?.OriginalDefinition as ClassSymbol)
            {
                if (chain.Contains(type))
                {
                    if (type == @class)
                    {
                        InFile(FileOf(@class));
                        _diagnostics.ReportInvalidBase(@class.Declaration.Name.Span,
                            $"'{@class.Name}' derives from itself: {string.Join(" → ", chain.Append(@class).Select(c => c.Name))}.");
                        @class.Base = null;
                    }
                    break;
                }
                chain.Add(type);
            }
        }
    }

    /// <summary>A class's primary constructor parameters, which its fields, base arguments and methods can use.</summary>
    private void BindClassParameters(ClassSymbol @class)
    {
        var parameters = ImmutableArray.CreateBuilder<VariableSymbol>();
        var names = new HashSet<string>();
        bool sawDefault = false;
        foreach (var parameter in @class.Declaration.Parameters)
        {
            var type = BindValueType(parameter.Type, "parameter");
            var @default = BindDefault(parameter, type, ref sawDefault, null);
            if (parameter.Name.IsMissing)
                continue;
            if (!names.Add(parameter.Name.Text))
                _diagnostics.ReportDuplicateDeclaration(parameter.Name.Span, parameter.Name.Text);
            parameters.Add(Declared(new VariableSymbol(parameter.Name.Text, type, isMutable: false, VariableKind.ClassParameter)
            {
                Default = @default,
                Documentation = parameter.Documentation,
            }, parameter.Name));
        }
        @class.Parameters = parameters.ToImmutable();
    }

    /// <summary>
    /// A class's fields (with their initializers) and base constructor arguments, bound once every function is declared,
    /// as initializers can call them. Initializers see the primary constructor's parameters but not other fields, as in
    /// C#. Base classes come first, so a derived class's methods can use what they inherit.
    /// </summary>
    private void BindClassMembers(IEnumerable<ClassSymbol> classes)
    {
        foreach (var @class in classes.OrderBy(c => c.SelfAndBases().Count()))
        {
            var file = FileOf(@class);
            InFile(file);
            var outerClass = _currentClass;
            _currentClass = @class;
            var parameterScope = new Scope(TypeParameterScope(@class, file));
            foreach (var parameter in @class.Parameters)
                parameterScope.TryDeclare(parameter);

            var fields = ImmutableArray.CreateBuilder<FieldSymbol>();
            var names = new HashSet<string>(@class.Methods.Select(m => m.Name));
            foreach (var syntax in @class.Declaration.Fields)
            {
                var declared = syntax.Type is null ? null : WithScope(parameterScope, () => BindValueType(syntax.Type, "field"));
                var initializer = WithScope(parameterScope, () => RequireValue(BindExpression(syntax.Initializer, declared), syntax.Initializer.Span));
                var type = declared ?? initializer.Type;
                if (declared is not null)
                    initializer = Convert(initializer, declared, syntax.Initializer.Span);
                if (syntax.Name.IsMissing)
                    continue;
                if (!names.Add(syntax.Name.Text) || syntax.Name.Text == @class.Name || ReservedMemberNames.Contains(syntax.Name.Text))
                {
                    ReportMemberClash(syntax.Name);
                    continue;
                }
                var field = new FieldSymbol(syntax.Name.Text, type)
                {
                    IsMutable = syntax.IsMutable,
                    IsPublic = syntax.Public is not null,
                    Owner = @class,
                    Initializer = initializer,
                    Documentation = syntax.Documentation,
                    DeclaringFile = file.Source,
                };
                Record(syntax.Name, field, declaration: true);
                fields.Add(field);
                // Later initializers can't use it (C# has no instance yet), which naming it reports, unless a parameter
                // has the name: `let owner = owner; let label = owner;` both mean the parameter.
                if (parameterScope.LookupLocal(field.Name) is null)
                    parameterScope.TryDeclare(FieldVariable(field));
            }
            @class.Fields = fields.ToImmutable();
            @class.BaseArguments = BindBaseArguments(@class, parameterScope);

            // The scope its instance methods are bound in: its fields, then the parameters no field hides (a field may
            // take a parameter's name: `let owner = owner;`), its methods (called bare as `this.M()`), and what it
            // inherits from its Piper base classes that isn't private.
            var scope = @class.MemberScope!;
            foreach (var field in @class.Fields)
                scope.TryDeclare(FieldVariable(field));
            foreach (var parameter in @class.Parameters)
                scope.TryDeclare(parameter);
            foreach (var method in @class.Methods)
                scope.TryDeclare(method);
            foreach (var @base in @class.SelfAndBases().Skip(1))
            {
                foreach (var field in @base.Fields.Where(f => f.IsPublic))
                    scope.TryDeclare(FieldVariable(field));
                foreach (var method in @base.Methods.Where(m => !m.IsPrivate))
                    scope.TryDeclare(method);
            }

            // Its static methods see the same members, so a static method can call another bare, and naming an instance
            // member there is reported as needing an instance rather than as unknown.
            var statics = @class.StaticScope!;
            foreach (var symbol in scope.Symbols.Where(s => s is not VariableSymbol { Kind: VariableKind.ClassParameter }))
                statics.TryDeclare(symbol);
            _currentClass = outerClass;
        }
    }

    /// <summary>A class field as the variable its methods name it by.</summary>
    private static VariableSymbol FieldVariable(FieldSymbol field) =>
        new(field.Name, field.Type, field.IsMutable, VariableKind.Field) { Field = field, Documentation = field.Documentation };

    private ImmutableArray<BoundExpression> BindBaseArguments(ClassSymbol @class, Scope parameterScope)
    {
        var entry = @class.Declaration.Bases.FirstOrDefault();
        if (@class.Base is null || entry is null)
            return [];
        // No arguments: the base's parameterless constructor, which C# calls itself (it may be protected).
        if (entry.Arguments is null && @class.Base is ClrTypeSymbol { Type: var noArguments } &&
            AccessibleConstructors(noArguments).Any(c => c.GetParameters().Length == 0))
            return [];
        var arguments = entry.Arguments ?? [];

        if (@class.Base.OriginalDefinition is ClassSymbol baseClass)
        {
            var map = TypeSubstitution.MapFor(@class.Base);
            var bound = WithScope(parameterScope, () =>
                BindNamedArguments(entry.Span, baseClass.Name, Signature(baseClass.Parameters), [], map, arguments));
            return bound is var (values, _, _) ? values : [];
        }

        var clr = (ClrTypeSymbol)@class.Base;
        var resolved = WithScope(parameterScope, () =>
            ResolveClrOverload(AccessibleConstructors(clr.Type), clr, clr.Name, arguments, [], entry.Span, clr.GenericMap));
        return resolved?.Arguments ?? [];
    }

    private T WithScope<T>(Scope scope, Func<T> bind)
    {
        var outer = _scope;
        _scope = scope;
        try
        {
            return bind();
        }
        finally
        {
            _scope = outer;
        }
    }

    /// <summary><c>Account("Ada", 100m)</c>: a new instance, through the primary constructor.</summary>
    private BoundExpression BindClassCreation(ClassSymbol @class, CallExpressionSyntax syntax, TypeSymbol? expected)
    {
        if (@class.IsAbstract)
        {
            BindArgumentsForDiagnostics(syntax.Arguments);
            _diagnostics.ReportAbstractInstantiation(syntax.Callee.Span, @class.Name);
            return new BoundErrorExpression();
        }
        var map = new Dictionary<TypeParameterSymbol, TypeSymbol>();
        Seed(@class.SelfType, expected, map, @class.TypeParameters);
        if (BindNamedArguments(syntax.Span, @class.Name, Signature(@class.Parameters), @class.TypeParameters, map, syntax.Arguments) is not var (arguments, names, _))
            return new BoundErrorExpression();
        var type = @class.Construct(TypeArgumentsFrom(@class.TypeParameters, map, syntax.Span));
        return new BoundObjectCreationExpression(@class, arguments, type) { ArgumentNames = names };
    }

    /// <summary>
    /// <c>Area()</c> inside a class: one of its methods (or an inherited one), called on <c>this</c>, or on the class
    /// for a static one, as C# allows.
    /// </summary>
    private BoundExpression BindBareMethodCall(MethodSymbol method, CallExpressionSyntax syntax, Token name, TypeSymbol? expected)
    {
        var span = name.Span;
        ExpressionSyntax receiver = method.IsStatic
            ? new NameExpressionSyntax(span, new Token(TokenKind.Identifier, span, method.ContainingType!.Name))
            : new NameExpressionSyntax(span, new Token(TokenKind.ThisKeyword, span, "this"));
        if (!method.IsStatic && _scope.Lookup("this") is null)
        {
            BindArgumentsForDiagnostics(syntax.Arguments);
            _diagnostics.ReportInvalidClassMember(span, $"'{method.Name}' is an instance method, so it needs an instance; a static method or field initializer has none.");
            return new BoundErrorExpression();
        }
        var call = syntax with { Callee = new MemberAccessExpressionSyntax(span, receiver, name) { TypeArguments = syntax.Callee is NameExpressionSyntax n ? n.TypeArguments : [] } };
        return BindExpression(call, expected);
    }

    /// <summary>
    /// A .NET member a class inherits, named bare in one of its instance methods (<c>Message</c>, <c>Items.Add(x)</c>),
    /// which means <c>this.Message</c> as in C#: the member access to bind in its place, or null.
    /// </summary>
    private MemberAccessExpressionSyntax? InheritedClrMember(Token name)
    {
        if (_currentClass is not { } @class || _scope.Lookup("this") is null ||
            !ClrBaseType(@class).GetMember(name.Text, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Any(IsPublicOrProtected))
        {
            return null;
        }
        return new MemberAccessExpressionSyntax(name.Span, new NameExpressionSyntax(name.Span, new Token(TokenKind.ThisKeyword, name.Span, "this")), name);
    }

    /// <summary>
    /// <c>base</c>, in a class's instance method: the instance typed as its base class (a Piper class, or the .NET one),
    /// so <c>base.F()</c> calls the base class's version of F, as in C#.
    /// </summary>
    private BoundExpression BindBase(Token name)
    {
        if (_currentClass is not { } @class || _scope.Lookup("this") is null)
        {
            _diagnostics.ReportInvalidClassMember(name.Span, "'base' can only be used in a class's instance methods.");
            return new BoundErrorExpression();
        }
        var type = @class.Base ?? @class.ClrBase;
        return new BoundVariableExpression(new VariableSymbol("base", type, isMutable: false, VariableKind.Base));
    }

    /// <summary>
    /// Whether protected .NET members are visible through a <paramref name="receiver"/>: in a class's methods, on that
    /// class or one derived from it, or through <c>base</c>, as in C#.
    /// </summary>
    private bool SeesProtected(BoundExpression receiver) =>
        _currentClass is { } current &&
        (receiver is BoundVariableExpression { Variable.Kind: VariableKind.Base } ||
         (receiver.Type is ClassSymbol @class && @class.SelfAndBases().Contains(current)));

    private static bool IsProtected(MethodBase method) => method.IsFamily || method.IsFamilyOrAssembly;

    private static bool IsPublicOrProtected(MemberInfo member) => member switch
    {
        MethodBase method => method.IsPublic || IsProtected(method),
        FieldInfo field => field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly,
        PropertyInfo property => property.GetAccessors(nonPublic: true).Any(IsPublicOrProtected),
        EventInfo @event => @event.AddMethod is { } add && IsPublicOrProtected(add),
        _ => false,
    };

    /// <summary>A class field, through an instance: reports a private one used outside its class.</summary>
    private void CheckFieldAccessible(FieldSymbol field, Token name)
    {
        if (field.Owner is { } owner && !field.IsPublic && _currentClass != owner)
            _diagnostics.ReportPrivateMember(name.Span, field.Name, owner.Name);
    }

    /// <summary>
    /// The checks on a class's methods once they're all known: an override must override a virtual or abstract method of
    /// a base class (Piper or .NET) with the same signature, and a class that isn't abstract must implement every
    /// abstract method it inherits.
    /// </summary>
    private void CheckClassMethods(IEnumerable<ClassSymbol> classes)
    {
        foreach (var @class in classes)
        {
            InFile(FileOf(@class));
            foreach (var method in @class.Methods)
            {
                var name = method.Declaration!.Name;
                if ((method.IsVirtual || method.IsOverride || method.IsAbstract) && method.IsStatic)
                    _diagnostics.ReportInvalidOverride(name.Span, $"'{method.Name}' is static, so it can't be virtual, abstract or an override.");
                else if (!method.IsAbstract && method.Declaration.Modifier?.Text == "abstract")
                    _diagnostics.ReportInvalidOverride(name.Span, $"'{method.Name}' is abstract, so it can't have a body; mark it 'virtual' to give derived classes a default.");
                else if (method.IsAbstract && method.Declaration.Modifier?.Text != "abstract")
                    _diagnostics.ReportInvalidOverride(name.Span, $"'{method.Name}' has no body; mark it 'abstract' if derived classes are to supply it.");
                else if (method.IsAbstract && !@class.IsAbstract)
                    _diagnostics.ReportInvalidOverride(name.Span, $"'{method.Name}' has no body, which only an abstract class's methods can do; mark the class 'abstract', or give it a body.");

                var inherited = InheritedMethod(@class, method.Name);
                if (method.IsOverride)
                {
                    if (inherited is null)
                        _diagnostics.ReportInvalidOverride(name.Span, $"'{method.Name}' is marked 'override', but no base class has a virtual or abstract '{method.Name}'.");
                    else if (!CanOverride(inherited, method, out var problem))
                        _diagnostics.ReportInvalidOverride(name.Span, problem);
                    else
                        method.Overridden = inherited;
                }
                else if (inherited is not null && !method.IsStatic)
                {
                    _diagnostics.ReportInvalidOverride(name.Span, $"A base class already has a '{method.Name}'; mark this one 'override' to replace it, or give it another name.");
                }
            }

            if (!@class.IsAbstract)
            {
                // Piper classes can't declare properties, so a .NET base's abstract ones can't be supplied.
                var properties = ClrBaseType(@class).GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(p => p.GetAccessors(nonPublic: true).Any(a => a.IsAbstract)).Select(p => p.Name).ToList();
                if (properties.Count > 0)
                    _diagnostics.ReportInvalidBase(@class.Declaration.Name.Span,
                        $"'{@class.Name}' can't derive from '{@class.ClrBase.Name}': it would have to override its abstract properties ({string.Join(", ", properties)}), and Piper classes can't declare properties yet.");
                foreach (var (missing, owner) in UnimplementedAbstractMethods(@class))
                    _diagnostics.ReportAbstractNotImplemented(@class.Declaration.Name.Span, @class.Name, missing, owner);
            }
        }
    }

    /// <summary>The nearest base class method called <paramref name="name"/>: a Piper method, or a .NET one.</summary>
    private static object? InheritedMethod(ClassSymbol @class, string name)
    {
        foreach (var @base in @class.SelfAndBases().Skip(1))
        {
            if (@base.Methods.FirstOrDefault(m => m.Name == name) is { } method)
                return method;
        }
        return ClrBaseType(@class).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == name && (m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly) && m.IsVirtual && !m.IsFinal);
    }

    /// <summary>The reflection type of a class's .NET base, constructed when its type arguments are .NET types (so <c>Collection&lt;string&gt;</c>'s methods take <c>string</c>, not <c>T</c>).</summary>
    private static Type ClrBaseType(ClassSymbol @class) => ClrTypes.ToClr(@class.ClrBase) ?? @class.ClrBase.Type;

    /// <summary>Whether <paramref name="method"/> can override <paramref name="inherited"/>: a virtual or abstract method, with the same signature.</summary>
    private bool CanOverride(object inherited, MethodSymbol method, out string problem)
    {
        problem = "";
        switch (inherited)
        {
            case MethodSymbol piper when !piper.IsVirtual && !piper.IsAbstract && !piper.IsOverride:
                problem = $"'{method.Name}' isn't virtual or abstract in '{piper.ContainingType!.Name}', so it can't be overridden; mark it 'virtual' there.";
                return false;
            case MethodSymbol piper when !SignaturesMatch(method, piper, new Dictionary<TypeParameterSymbol, TypeSymbol>()):
                problem = $"'{method.Name}' must have the signature of the method it overrides: {Signature(piper, new Dictionary<TypeParameterSymbol, TypeSymbol>())}.";
                return false;
            case MethodInfo clr:
            {
                var parameters = clr.GetParameters();
                bool matches = parameters.Length == method.Parameters.Length &&
                               parameters.Zip(method.Parameters).All(p => ClrTypes.ToClr(p.Second.Type) == p.First.ParameterType) &&
                               (clr.ReturnType == typeof(void) ? method.ReturnType == TypeSymbol.Unit : ClrTypes.ToClr(method.ReturnType) == clr.ReturnType);
                if (!matches)
                {
                    var expected = $"({string.Join(", ", parameters.Select(p => $"{p.Name}: {p.ParameterType.Name}"))}) => {(clr.ReturnType == typeof(void) ? "unit" : clr.ReturnType.Name)}";
                    problem = $"'{method.Name}' must have the signature of the .NET method it overrides: {expected}.";
                }
                return matches;
            }
            default:
                return true;
        }
    }

    /// <summary>The abstract methods a class inherits and nothing overrides, with the class or .NET type declaring each.</summary>
    private static IEnumerable<(string Method, string Owner)> UnimplementedAbstractMethods(ClassSymbol @class)
    {
        var overridden = new HashSet<string>();
        foreach (var type in @class.SelfAndBases())
        {
            foreach (var method in type.Methods)
            {
                // Its own abstract methods are reported where they're declared.
                if (method.IsAbstract && !overridden.Contains(method.Name) && type != @class)
                    yield return (method.Name, type.Name);
                if (method.IsOverride || !method.IsAbstract)
                    overridden.Add(method.Name);
            }
        }
        if (@class.ClrBase is var clr)
        {
            foreach (var method in ClrBaseType(@class).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Where(m => m.IsAbstract && !m.IsSpecialName))
            {
                if (!overridden.Contains(method.Name))
                    yield return (method.Name, clr.Name);
            }
        }
    }
}
