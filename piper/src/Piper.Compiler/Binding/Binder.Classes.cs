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

    /// <summary>Whether a class's `requires` conditions are being bound, which can't use its fields.</summary>
    private bool _bindingClassInvariants;

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
            var accessors = syntax.Fields.Where(f => f.IsProperty).SelectMany(p => PropertyAccessors(p).ToList());
            var extend = new ExtendDeclarationSyntax(syntax.Span, syntax.Name, target, [], [.. syntax.Methods, .. accessors]);
            extends.Add(new ExtendInfo(extend, file, @class, typeParameterScope, traits, @class.MemberScope, @class.StaticScope));
        }
        return extends;
    }

    /// <summary>
    /// A property's getter and setter as the methods they're bound as: <c>get_Name()</c> and <c>set_Name(value)</c>,
    /// .NET's names, so overriding a .NET property's accessors is checked as overriding methods is. A property written
    /// wrongly is reported here. An automatic property has none: C# stores its value.
    /// </summary>
    private IEnumerable<FunctionDeclarationSyntax> PropertyAccessors(ClassFieldSyntax property)
    {
        var name = property.Name;
        // An indexer's accessors are .NET's get_Item and set_Item, taking its parameters (and the setter `value`).
        var shown = property.IsIndexer ? "this[]" : name.Text;
        var accessorName = property.IsIndexer ? "Item" : name.Text;
        bool isAbstract = property.Modifier?.Text == "abstract";
        var gets = property.Accessors.Where(a => !a.IsSetter).ToList();
        var sets = property.Accessors.Where(a => a.IsSetter).ToList();
        void Report(TextSpan span, string message) => _diagnostics.ReportInvalidProperty(span, message);

        if (gets.Count == 0)
            Report(name.Span, $"'{shown}' needs a 'get'; a property can't only be set.");
        foreach (var extra in gets.Skip(1).Concat(sets.Skip(1)))
            Report(extra.Keyword.Span, $"'{shown}' already has a '{extra.Keyword.Text}'.");
        foreach (var get in gets.Where(g => g.Private is not null || g.Protected is not null))
        {
            var access = (get.Private ?? get.Protected)!;
            Report(access.Span, $"Only a property's 'set' can be {access.Text}; to hide '{shown}' altogether, write '{access.Text} prop'.");
        }
        if (property.Private is not null)
        {
            foreach (var set in sets.Where(s => s.Private is not null || s.Protected is not null))
                Report((set.Private ?? set.Protected)!.Span, $"'{shown}' is private, so its 'set' already is.");
        }
        if (property.Protected is not null)
        {
            foreach (var set in sets.Where(s => s.Protected is not null))
                Report(set.Protected!.Span, $"'{shown}' is protected, so its 'set' already is.");
        }

        // What computes its value is written as a function is, `prop Total => decimal = …`; what stores one, as a field
        // is, `prop Count: int { get; set; } = 0`.
        var head = property.IsIndexer ? $"this[{string.Join(", ", property.Parameters.Select(p => _file.Source.ToString(p.Span)))}]" : name.Text;
        var typeText = property.Type is null ? "Type" : _file.Source.ToString(property.Type.Span);
        if (property.Arrow is null && (isAbstract || property.Accessors.Any(a => a.HasBody)))
        {
            Report(property.Type?.Span ?? name.Span,
                $"'{shown}' computes its value, so it's written as a function is: 'prop {head} => {typeText} {(isAbstract ? "{ get; … }" : "= …")}'.");
            if (property.Type is null)
                yield break;
        }

        if (property.IsIndexer)
        {
            if (property.Static is { } @static)
                Report(@static.Span, "An indexer belongs to an instance, so it can't be static.");
            if (property.Parameters.IsEmpty)
                Report(name.Span, "An indexer needs at least one parameter: 'prop this[index: int] => T = …'.");
            if (property.Parameters.FirstOrDefault(p => p.Name.Text == "value") is { } value)
                Report(value.Name.Span, "An indexer's parameter can't be called 'value', which its 'set' is given.");
            if (sets.FirstOrDefault(s => s.IsInit) is { } init)
                Report(init.Keyword.Span, "An indexer can't have an 'init'; give it a 'set'.");
            if (property.IsAutomatic)
            {
                Report(name.Span, "An indexer can't store values itself, so its accessors need bodies.");
                yield break;
            }
        }

        if (property.IsAutomatic)
        {
            if (property.Modifier is { } modifier)
                Report(modifier.Span, $"A property that stores its value can't be '{modifier.Text}'; compute it instead: 'prop {name.Text} => {typeText} = …'.");
            if (property.Initializer is null && gets.Count > 0)
                Report(name.Span, $"'{name.Text}' needs an initial value, written after its accessors: '{{ get; set; }} = …;'.");
            yield break;
        }
        if (property.Initializer is not null)
        {
            Report(property.Initializer.Span, isAbstract
                ? $"'{shown}' is abstract, so it can't have an initial value."
                : $"'{shown}' computes its value, so it can't have an initial value; to store one, write it as a field is: 'prop {head}: {typeText} {{ get; set; }} = …;'.");
        }
        if (!isAbstract && property.Accessors.All(a => !a.HasBody))
        {
            // `prop Count => int;` in a class, or `prop Count => int { get; set; }`: nothing computes it.
            Report(name.Span, $"'{shown}' is written as a function is, so it needs a body: 'prop {head} => {typeText} = …;'. " +
                              $"To store a value, write it as a field is: 'prop {head}: {typeText} {{ get; set; }} = …;'.");
            yield break;
        }
        if (!isAbstract && property.Accessors.FirstOrDefault(a => !a.HasBody) is { } bodiless)
        {
            Report(bodiless.Keyword.Span, $"Either all of a property's accessors have bodies, or none do (a property that stores its value); give this '{bodiless.Keyword.Text}' one.");
            yield break;
        }
        if (property.Type is null)
        {
            Report(name.Span, $"'{shown}' needs a type: 'prop {head} => Type = …'. Only a property that stores its value can take its type from its initial value.");
            yield break;
        }
        foreach (var accessor in gets.Take(1).Concat(sets.Take(1)))
        {
            var keyword = accessor.Keyword.Span;
            ImmutableArray<ParameterSyntax> value = [new ParameterSyntax(keyword, new Token(TokenKind.Identifier, keyword, "value"), property.Type)];
            yield return new FunctionDeclarationSyntax(
                accessor.Span,
                new Token(TokenKind.Identifier, name.Span, (accessor.IsSetter ? "set_" : "get_") + accessorName),
                [],
                property.Static,
                accessor.IsSetter ? [.. property.Parameters, .. value] : property.Parameters,
                accessor.IsSetter ? null : property.Type,
                accessor.BlockBody,
                accessor.ExpressionBody)
            {
                Private = accessor.Private ?? property.Private,
                Protected = accessor.Private is null ? accessor.Protected ?? property.Protected : null,
                Modifier = property.Modifier,
                IsIterator = accessor.IsIterator,
                AccessorOf = shown,
                IsInitAccessor = accessor.IsInit,
            };
        }
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
            var names = new HashSet<string>(@class.Methods.Select(m => m.Name));

            // Static fields first, as C# initializes them before any instance exists. Their initializers run in order,
            // so each sees the static fields before it (and the static methods), but no instance or parameters.
            var staticScope = new Scope(TypeParameterScope(@class, file));
            foreach (var method in @class.Methods.Where(m => m.IsStatic && !m.IsHidden))
                staticScope.TryDeclare(method);
            var staticFields = ImmutableArray.CreateBuilder<FieldSymbol>();
            foreach (var syntax in @class.Declaration.Fields.Where(f => f.Static is not null && !f.IsIndexer))
            {
                if (BindClassField(@class, file, syntax, staticScope, names) is not { } field)
                    continue;
                staticFields.Add(field);
                staticScope.TryDeclare(StaticFieldVariable(field, @class.SelfType));
            }
            @class.StaticFields = staticFields.ToImmutable();

            var parameterScope = new Scope(TypeParameterScope(@class, file));
            foreach (var parameter in @class.Parameters)
                parameterScope.TryDeclare(parameter);
            foreach (var field in @class.StaticFields)
                parameterScope.TryDeclare(StaticFieldVariable(field, @class.SelfType));
            foreach (var method in @class.Methods.Where(m => m.IsStatic && !m.IsHidden))
                parameterScope.TryDeclare(method);

            var fields = ImmutableArray.CreateBuilder<FieldSymbol>();
            foreach (var syntax in @class.Declaration.Fields.Where(f => f.Static is null && !f.IsIndexer))
            {
                if (BindClassField(@class, file, syntax, parameterScope, names) is not { } field)
                    continue;
                fields.Add(field);
                // Later initializers can't use it (C# has no instance yet), which naming it reports, unless a parameter
                // has the name: `let owner = owner; let label = owner;` both mean the parameter.
                if (parameterScope.LookupLocal(field.Name) is null)
                    parameterScope.TryDeclare(FieldVariable(field));
            }
            @class.Fields = fields.ToImmutable();
            BindClassIndexers(@class);
            @class.BaseArguments = BindBaseArguments(@class, parameterScope);
            BindClassConstructors(@class, file);

            // The scope its instance methods are bound in: its fields and static fields, then the parameters no field
            // hides (a field may take a parameter's name: `let owner = owner;`), its methods (called bare as `this.M()`),
            // and what it inherits from its Piper base classes that isn't private.
            var scope = @class.MemberScope!;
            foreach (var field in @class.Fields)
                scope.TryDeclare(FieldVariable(field));
            foreach (var field in @class.StaticFields)
                scope.TryDeclare(StaticFieldVariable(field, @class.SelfType));
            foreach (var parameter in @class.Parameters)
                scope.TryDeclare(parameter);
            foreach (var method in @class.Methods.Where(m => !m.IsHidden))
                scope.TryDeclare(method);
            foreach (var @base in SelfAndBaseTypes(@class.SelfType).Skip(1).Where(b => b.OriginalDefinition is ClassSymbol))
            {
                var baseClass = (ClassSymbol)@base.OriginalDefinition;
                foreach (var field in baseClass.Fields.Where(f => f.IsPublic || f.IsProtected))
                    scope.TryDeclare(FieldVariable(field, TypeSubstitution.FieldType(field, @base)));
                foreach (var field in baseClass.StaticFields.Where(f => f.IsPublic || f.IsProtected))
                    scope.TryDeclare(StaticFieldVariable(field, @base));
                foreach (var method in baseClass.Methods.Where(m => !m.IsPrivate && !m.IsHidden))
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

    /// <summary>
    /// A class's field or property, bound with its initializer in <paramref name="scope"/>; null if its name is missing or
    /// taken. Only fields and automatic properties have initializers; other properties have accessors (see <see cref="PropertyAccessors"/>).
    /// </summary>
    private FieldSymbol? BindClassField(ClassSymbol @class, FileContext file, ClassFieldSyntax syntax, Scope scope, HashSet<string> names)
    {
        var declared = syntax.Type is null ? null : WithScope(scope, () => BindValueType(syntax.Type, syntax.IsProperty ? "property" : "field"));
        BoundExpression? initializer = null;
        if (syntax.Initializer is { } value && (!syntax.IsProperty || syntax.IsAutomatic))
        {
            initializer = WithScope(scope, () => RequireValue(BindExpression(value, declared), value.Span));
            if (declared is not null)
                initializer = Convert(initializer, declared, value.Span);
        }
        var type = declared ?? initializer?.Type ?? TypeSymbol.Error;
        if (syntax.Name.IsMissing)
            return null;
        if (!names.Add(syntax.Name.Text) || syntax.Name.Text == @class.Name || ReservedMemberNames.Contains(syntax.Name.Text))
        {
            ReportMemberClash(syntax.Name);
            return null;
        }
        MethodSymbol? Accessor(string prefix) =>
            @class.Methods.FirstOrDefault(m => m.Declaration?.AccessorOf == syntax.Name.Text && m.Name.StartsWith(prefix, StringComparison.Ordinal));
        var field = new FieldSymbol(syntax.Name.Text, type)
        {
            IsMutable = syntax.IsMutable,
            // Fields are private unless marked public, properties (like methods) public unless marked private or protected.
            IsPublic = syntax.IsProperty ? syntax.Private is null && syntax.Protected is null : syntax.Public is not null,
            IsProtected = syntax.Protected is not null,
            IsStatic = syntax.Static is not null,
            IsProperty = syntax.IsProperty,
            IsAutomatic = syntax.IsAutomatic,
            IsInitOnly = syntax.IsProperty && syntax.IsInitOnly,
            HasPrivateSetter = syntax.IsProperty && syntax.Private is null && syntax.Accessors.Any(a => a.IsSetter && a.Private is not null),
            HasProtectedSetter = syntax.IsProperty && syntax.Private is null && syntax.Protected is null && syntax.Accessors.Any(a => a.IsSetter && a.Protected is not null),
            Getter = syntax.IsProperty ? Accessor("get_") : null,
            Setter = syntax.IsProperty ? Accessor("set_") : null,
            Owner = @class,
            Initializer = initializer,
            Documentation = syntax.Documentation,
            DeclaringFile = file.Source,
        };
        Record(syntax.Name, field, declaration: true);
        return field;
    }

    /// <summary>
    /// A class's indexers, from their accessor methods: overloads must differ in their parameters' types, as C#'s do.
    /// </summary>
    private void BindClassIndexers(ClassSymbol @class)
    {
        var indexers = ImmutableArray.CreateBuilder<IndexerSymbol>();
        foreach (var syntax in @class.Declaration.Fields.Where(f => f.IsIndexer && !f.Parameters.IsEmpty))
        {
            // An indexer's accessors take its parameters, the very syntax nodes, which pairs them with it.
            MethodSymbol? Accessor(string prefix) => @class.Methods.FirstOrDefault(m =>
                m.IsIndexerAccessor && m.Name.StartsWith(prefix, StringComparison.Ordinal) &&
                ReferenceEquals(m.Declaration!.Parameters[0], syntax.Parameters[0]));
            if (Accessor("get_") is not { } getter)
                continue;
            var indexer = new IndexerSymbol(getter, @class) { Setter = Accessor("set_") };
            if (indexers.Any(i => i.Parameters.Select(p => p.Type).SequenceEqual(indexer.Parameters.Select(p => p.Type))))
            {
                _diagnostics.ReportInvalidProperty(syntax.Name.Span,
                    $"'{@class.Name}' already has an indexer taking ({string.Join(", ", indexer.Parameters.Select(p => p.Type))}).");
                continue;
            }
            Record(syntax.Name, indexer, declaration: true);
            indexers.Add(indexer);
        }
        @class.Indexers = indexers.ToImmutable();
    }

    /// <summary>
    /// <c>value[i]</c> on a Piper class with indexers, its own or its Piper base classes' (an override replaces the
    /// indexer it overrides): the one whose parameters the arguments convert to, preferring one they match exactly.
    /// Null if the class has none, so .NET's (from a .NET base) are tried.
    /// </summary>
    private BoundExpression? BindPiperIndex(BoundExpression target, IndexExpressionSyntax syntax)
    {
        var candidates = new List<(IndexerSymbol Indexer, ImmutableArray<TypeSymbol> Parameters, TypeSymbol Type)>();
        foreach (var type in SelfAndBaseTypes(target.Type).Where(t => t.OriginalDefinition is ClassSymbol))
        {
            var map = TypeSubstitution.MapFor(type);
            foreach (var candidate in ((ClassSymbol)type.OriginalDefinition).Indexers)
            {
                ImmutableArray<TypeSymbol> parameters = [.. candidate.Parameters.Select(p => TypeSubstitution.Substitute(p.Type, map))];
                if (!candidates.Any(c => c.Parameters.SequenceEqual(parameters)))
                    candidates.Add((candidate, parameters, TypeSubstitution.Substitute(candidate.Type, map)));
            }
        }
        if (candidates.Count == 0)
            return null;

        ImmutableArray<BoundExpression> Arguments(ImmutableArray<TypeSymbol> parameters, out bool exact)
        {
            var arguments = ImmutableArray.CreateBuilder<BoundExpression>();
            exact = true;
            for (int i = 0; i < syntax.Arguments.Length; i++)
            {
                var argument = RequireValue(BindExpression(syntax.Arguments[i], parameters[i]), syntax.Arguments[i].Span);
                exact &= argument.Type == parameters[i];
                arguments.Add(Convert(argument, parameters[i], syntax.Arguments[i].Span));
            }
            return arguments.ToImmutable();
        }

        var fitting = candidates.Where(c => c.Parameters.Length == syntax.Arguments.Length).ToList();
        var viable = fitting
            .Select(c => (Candidate: c, Exact: Trial(() => { Arguments(c.Parameters, out bool exact); return exact; }, out bool failed), Failed: failed))
            .Where(c => !c.Failed)
            .ToList();
        var chosen = viable.Count(v => v.Exact) == 1 ? viable.First(v => v.Exact).Candidate
            : viable.Count == 1 ? viable[0].Candidate
            : fitting.Count == 1 && viable.Count == 0 ? fitting[0]    // bound below, to report what's wrong with its arguments
            : ((IndexerSymbol, ImmutableArray<TypeSymbol>, TypeSymbol)?)null;
        if (chosen is not var (indexer, parameterTypes, resultType))
        {
            var argumentTypes = syntax.Arguments.Select(a => Trial(() => BindExpression(a), out _).Type).ToList();
            if (viable.Count > 1)
                _diagnostics.ReportAmbiguousOverload(syntax.Span, $"{target.Type}[]", viable.Select(v => $"this[{string.Join(", ", v.Candidate.Parameters)}]"));
            else
                _diagnostics.ReportNoApplicableOverload(syntax.Span, $"{target.Type}[]", argumentTypes);
            return BindArgumentsForDiagnostics(syntax.Arguments);
        }

        if (!indexer.IsPublic)
            CheckMemberAccessible("this[]", indexer.Owner, indexer.IsProtected, target, syntax.Span);
        var bound = Arguments(parameterTypes, out _);
        return new BoundIndexExpression(target, null, bound, resultType) { PiperIndexer = indexer };
    }

    /// <summary>A class field as the variable its methods name it by.</summary>
    private static VariableSymbol FieldVariable(FieldSymbol field, TypeSymbol? type = null) =>
        new(field.Name, type ?? field.Type, field.IsMutable, VariableKind.Field) { Field = field, Documentation = field.Documentation };

    /// <summary>A static field as a variable, reached through <paramref name="container"/>: its class, or one deriving from it.</summary>
    private static VariableSymbol StaticFieldVariable(FieldSymbol field, TypeSymbol container) =>
        new(field.Name, TypeSubstitution.FieldType(field, container), field.IsMutable, VariableKind.StaticField)
        {
            Field = field,
            Container = container,
            Documentation = field.Documentation,
        };

    /// <summary>
    /// The static field <paramref name="name"/> of a class or one of its Piper base classes, with the class (as
    /// <paramref name="type"/> sees it, type arguments substituted) that declares it.
    /// </summary>
    private (FieldSymbol Field, TypeSymbol Container)? FindStaticField(TypeSymbol type, string name)
    {
        foreach (var candidate in SelfAndBaseTypes(type).Where(t => t.OriginalDefinition is ClassSymbol))
        {
            if (((ClassSymbol)candidate.OriginalDefinition).StaticFields.FirstOrDefault(f => f.Name == name) is { } field)
                return (field, candidate);
        }
        return null;
    }

    /// <summary>
    /// <c>Account.count</c>: a static field through its class (or a class deriving from it). A generic class needs its
    /// type arguments (<c>Stack&lt;int&gt;.count</c>), as each instantiation has its own, except inside the class itself.
    /// </summary>
    private BoundExpression? BindStaticFieldAccess(TypeSymbol type, Token member)
    {
        if (FindStaticField(type, member.Text) is not var (field, _))
            return null;
        Record(member, field);
        CheckFieldAccessible(field, member);
        if (type is GenericTypeDefinition { IsGeneric: true } generic)
        {
            if (_currentClass != generic)
            {
                _diagnostics.ReportInvalidClassMember(member.Span,
                    $"'{generic.Name}' is generic, and each instantiation has its own '{field.Name}', so name its type arguments: '{generic.Name}<…>.{field.Name}'.");
                return new BoundErrorExpression();
            }
            // Inside the class, `Stack.count` is its own instantiation's: `Stack<T>.count`.
            type = generic.SelfType;
        }
        var (_, container) = FindStaticField(type, member.Text)!.Value;
        return new BoundVariableExpression(StaticFieldVariable(field, container));
    }

    /// <summary><c>Handlers.onTick(now)</c>: calls the function a static field holds.</summary>
    private BoundExpression BindStaticFieldCall(TypeSymbol type, Token member, CallExpressionSyntax syntax)
    {
        var field = BindStaticFieldAccess(type, member)!;
        if (field.Type != TypeSymbol.Error && DelegateSignature(field.Type) is not null)
            return BindDelegateInvocation(field, syntax, member.Text);
        if (field.Type != TypeSymbol.Error)
            _diagnostics.ReportNotCallable(member.Span, $"'{type.OriginalDefinition.Name}.{member.Text}'");
        return BindArgumentsForDiagnostics(syntax.Arguments);
    }

    /// <summary><c>account.count</c> where <c>count</c> is static: reported, as it belongs to the class. True if it was one.</summary>
    private bool ReportStaticFieldThroughInstance(TypeSymbol type, Token member)
    {
        if (FindStaticField(type, member.Text) is not var (field, container))
            return false;
        Record(member, field);
        _diagnostics.ReportInvalidClassMember(member.Span,
            $"'{field.Name}' is static, so it belongs to the class, not an instance: use '{container.OriginalDefinition.Name}.{field.Name}'.");
        return true;
    }

    /// <summary>
    /// A name in a class's field initializer or static method that the class declares but that isn't in scope there:
    /// reported with the reason, rather than as unknown. True if it was one.
    /// </summary>
    private bool ReportClassMemberOutOfScope(Token name)
    {
        if (_currentClass is not { } @class || _scope.Lookup("this") is not null)
            return false;
        string? reason = null;
        if (_bindingClassInvariants && @class.Declaration.Fields.Any(f => f.Name.Text == name.Text))
            reason = $"'{name.Text}' is a field, which a class's 'requires' can't use, as it's checked before the fields are initialized; it can use the constructor's parameters.";
        else if (@class.Declaration.Fields.FirstOrDefault(f => f.Name.Text == name.Text) is { } field)
            reason = field.Static is not null
                ? $"'{name.Text}' isn't initialized yet: static fields are initialized in the order they're declared, so an initializer can only use the ones before it."
                : $"'{name.Text}' is a field of an instance, which a static method or field initializer has none of; a field initializer can use the class's parameters.";
        else if (@class.Parameters.Any(p => p.Name == name.Text))
            reason = $"'{name.Text}' is a parameter of the class's constructor, which static methods and static fields can't use, as they don't belong to an instance.";
        if (reason is null)
            return false;
        _diagnostics.ReportInvalidClassMember(name.Span, reason);
        return true;
    }

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
            var bound = WithScope(parameterScope, () => ResolveConstruction(baseClass, entry.Span, arguments, [], map, throughBase: true));
            return bound is var (_, values, _, _) ? values : [];
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
        if (ResolveConstruction(@class, syntax.Span, syntax.Arguments, @class.TypeParameters, map, throughBase: false) is not var (constructor, arguments, names, byParameter))
            return new BoundErrorExpression();
        var type = @class.Construct(TypeArgumentsFrom(@class.TypeParameters, map, syntax.Span));
        // Constant arguments that break an invariant are caught here when they go straight to the primary constructor.
        if (constructor is null)
            CheckContracts(@class.Invariants, @class.Parameters, byParameter, syntax.Span, @class.Name, "invariant");
        return new BoundObjectCreationExpression(@class, arguments, type) { ArgumentNames = names };
    }

    /// <summary>
    /// The constructor <paramref name="arguments"/> choose among a class's primary constructor (null) and its others:
    /// the one they fit, preferring one whose parameter types they match exactly, as C# chooses. Reported if none fits,
    /// or several do equally well, or the chosen one can't be used here: a private constructor only in its class, and a
    /// protected one also from a derived class's base list (<paramref name="throughBase"/>), as in C#.
    /// </summary>
    private (MethodSymbol? Constructor, ImmutableArray<BoundExpression> Arguments, ImmutableArray<string?> Names, ImmutableArray<BoundExpression> ByParameter)? ResolveConstruction(
        ClassSymbol @class, TextSpan span, ImmutableArray<ExpressionSyntax> arguments, IReadOnlyList<TypeParameterSymbol> inferred,
        Dictionary<TypeParameterSymbol, TypeSymbol> map, bool throughBase)
    {
        var candidates = new List<(MethodSymbol? Constructor, IReadOnlyList<(string Name, TypeSymbol Type, ParameterDefault? Default)> Parameters)>
        {
            (null, Signature(@class.Parameters)),
        };
        candidates.AddRange(@class.Methods.Where(m => m.IsConstructor).Select(m => ((MethodSymbol?)m, Signature(m.Parameters))));

        var chosen = candidates[0];
        if (candidates.Count > 1)
        {
            // Each candidate is tried on its own copy of what's inferred so far; the arguments' own types decide exactness.
            var naturals = arguments.Select(a => a is NamedArgumentSyntax ? null : Trial(() => BindExpression(a), out _).Type).ToList();
            var viable = new List<((MethodSymbol? Constructor, IReadOnlyList<(string Name, TypeSymbol Type, ParameterDefault? Default)> Parameters) Candidate, bool Exact)>();
            foreach (var candidate in candidates)
            {
                var trialMap = new Dictionary<TypeParameterSymbol, TypeSymbol>(map);
                var bound = Trial(() => BindNamedArguments(span, @class.Name, candidate.Parameters, inferred, trialMap, arguments), out bool failed);
                if (failed || bound is null)
                    continue;
                bool exact = naturals.Count == candidate.Parameters.Count &&
                             naturals.Select((t, i) => t is null || t == TypeSubstitution.Substitute(candidate.Parameters[i].Type, trialMap)).All(e => e);
                viable.Add((candidate, exact));
            }
            var exactOnes = viable.Where(v => v.Exact).ToList();
            if (exactOnes.Count == 1 || viable.Count == 1)
            {
                chosen = exactOnes.Count == 1 ? exactOnes[0].Candidate : viable[0].Candidate;
            }
            else
            {
                string Shown(IReadOnlyList<(string Name, TypeSymbol Type, ParameterDefault? Default)> parameters) =>
                    $"{@class.Name}({string.Join(", ", parameters.Select(p => p.Type))})";
                if (viable.Count > 1)
                    _diagnostics.ReportAmbiguousOverload(span, @class.Name, viable.Select(v => Shown(v.Candidate.Parameters)));
                else
                    _diagnostics.ReportNoApplicableOverload(span, @class.Name, naturals.Select(t => t ?? TypeSymbol.Error));
                BindArgumentsForDiagnostics(arguments);
                return null;
            }
        }

        if (chosen.Constructor is { } constructor && (constructor.IsPrivate || constructor.IsProtected) && _currentClass != @class &&
            !(throughBase && constructor.IsProtected))
        {
            if (constructor.IsPrivate)
                _diagnostics.ReportPrivateMember(span, $"{@class.Name}({string.Join(", ", constructor.Parameters.Select(p => p.Type))})", @class.Name);
            else
                _diagnostics.ReportProtectedMember(span, $"{@class.Name}({string.Join(", ", constructor.Parameters.Select(p => p.Type))})", @class.Name);
        }
        return BindNamedArguments(span, @class.Name, chosen.Parameters, inferred, map, arguments) is var (bound2, names, byParameter)
            ? (chosen.Constructor, bound2, names, byParameter)
            : null;
    }

    /// <summary>
    /// A class's constructors besides the primary one: each must call the primary constructor (<c>: this(…)</c>, left
    /// out only if it needs no arguments), with arguments from its own parameters and the class's static members (there's
    /// no instance yet), and take parameter types no other constructor does.
    /// </summary>
    private void BindClassConstructors(ClassSymbol @class, FileContext file)
    {
        var constructors = ImmutableArray.CreateBuilder<ClassConstructor>();
        var signatures = new List<ImmutableArray<TypeSymbol>> { @class.Parameters.Select(p => p.Type).ToImmutableArray() };
        foreach (var method in @class.Methods.Where(m => m.IsConstructor))
        {
            var chain = method.Declaration!.Constructor!;
            ImmutableArray<TypeSymbol> types = [.. method.Parameters.Select(p => p.Type)];
            if (signatures.Any(s => s.SequenceEqual(types)))
            {
                _diagnostics.ReportInvalidConstructor(chain.ClassName.Span, $"'{@class.Name}' already has a constructor taking ({string.Join(", ", types)}).");
                continue;
            }
            signatures.Add(types);
            if (chain.This is null && @class.Parameters.Any(p => p.Default is null))
            {
                _diagnostics.ReportInvalidConstructor(chain.ClassName.Span,
                    $"A constructor must first call the primary constructor, which takes ({string.Join(", ", @class.Parameters.Select(p => $"{p.Name}: {p.Type}"))}): add ': this(…)'.");
                continue;
            }

            var statics = new Scope(TypeParameterScope(@class, file));
            foreach (var field in @class.StaticFields)
                statics.TryDeclare(StaticFieldVariable(field, @class.SelfType));
            foreach (var staticMethod in @class.Methods.Where(m => m.IsStatic && !m.IsHidden))
                statics.TryDeclare(staticMethod);
            var scope = new Scope(statics);
            foreach (var parameter in method.Parameters)
                scope.TryDeclare(parameter);
            var bound = WithScope(scope, () =>
                BindNamedArguments(chain.This?.Span ?? chain.ClassName.Span, @class.Name, Signature(@class.Parameters), [], [], chain.Arguments));
            constructors.Add(new ClassConstructor(method, bound?.Arguments ?? [], bound?.Names ?? []));
        }
        @class.Constructors = constructors.ToImmutable();
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
            _diagnostics.ReportInvalidClassMember(span, $"'{method.Name}' is an instance method, so it needs an instance, which static methods, field initializers and a class's 'requires' don't have.");
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
    private void CheckFieldAccessible(FieldSymbol field, Token name, BoundExpression? receiver = null)
    {
        if (field.Owner is { } owner && !field.IsPublic)
            CheckMemberAccessible(field.Name, owner, field.IsProtected, field.IsStatic ? null : receiver, name.Span);
    }

    /// <summary>
    /// A private or protected member of <paramref name="owner"/>, used here: a private one only in its class, a
    /// protected one also in classes deriving from it, and (for an instance member, as C# requires) only through an
    /// instance of the class using it, or one deriving from that.
    /// </summary>
    private void CheckMemberAccessible(string member, ClassSymbol owner, bool isProtected, BoundExpression? receiver, TextSpan span)
    {
        if (_currentClass == owner)
            return;
        if (!isProtected)
            _diagnostics.ReportPrivateMember(span, member, owner.Name);
        else if (!CanUseProtected(owner, receiver))
            _diagnostics.ReportProtectedMember(span, member, owner.Name);
    }

    private bool CanUseProtected(ClassSymbol owner, BoundExpression? receiver) =>
        _currentClass is { } current && current.SelfAndBases().Contains(owner) &&
        (receiver is null or BoundVariableExpression { Variable.Kind: VariableKind.This or VariableKind.Base } ||
         (receiver.Type.OriginalDefinition is ClassSymbol type && type.SelfAndBases().Contains(current)));

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
            foreach (var method in @class.Methods.Where(m => !m.IsConstructor))
            {
                var name = method.Declaration!.Name;
                // A property's modifiers are its accessors', so what's wrong with them is reported once, for its getter.
                bool isSetter = method.IsAccessor && method.Name.StartsWith("set_", StringComparison.Ordinal);
                bool reported = isSetter && @class.Methods.Any(m => m != method && m.Declaration?.AccessorOf == method.Declaration.AccessorOf);
                if (!reported)
                    CheckMethodModifiers(@class, method, name);

                var inherited = InheritedMethod(@class, method.Name);
                if (method.IsOverride)
                {
                    if (inherited is null && isSetter && InheritedMethod(@class, "get_" + method.DisplayName) is not null)
                        _diagnostics.ReportInvalidOverride(name.Span, $"'{method.DisplayName}' is marked 'override', but the property it overrides has no 'set' to override.");
                    else if (inherited is null && !reported)
                        _diagnostics.ReportInvalidOverride(name.Span, $"'{method.DisplayName}' is marked 'override', but no base class has a virtual or abstract '{method.DisplayName}'.");
                    else if (inherited is not null && !CanOverride(inherited, method, out var problem))
                    {
                        if (!reported)
                            _diagnostics.ReportInvalidOverride(name.Span, problem);
                    }
                    else
                    {
                        method.Overridden = inherited;
                        bool inheritedInit = inherited switch
                        {
                            MethodSymbol piper => piper.IsInitAccessor,
                            MethodInfo clr => clr.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit"),
                            _ => false,
                        };
                        if (isSetter && inheritedInit != method.IsInitAccessor)
                            _diagnostics.ReportInvalidOverride(name.Span, $"'{method.DisplayName}' must have {(inheritedInit ? "an 'init'" : "a 'set'")}, as the property it overrides does.");
                        // An override keeps the access of what it overrides; it can't make a public method protected.
                        if (method.Declaration.Protected is not null && !reported &&
                            inherited is not (MethodSymbol { IsProtected: true } or MethodInfo { IsFamily: true } or MethodInfo { IsFamilyOrAssembly: true }))
                        {
                            _diagnostics.ReportInvalidOverride(name.Span, $"'{method.DisplayName}' overrides a public member, so it must be public too.");
                        }
                    }
                }
                else if (inherited is not null && !method.IsStatic && !reported)
                {
                    _diagnostics.ReportInvalidOverride(name.Span, $"A base class already has a '{method.DisplayName}'; mark this one 'override' to replace it, or give it another name.");
                }
            }

            if (!@class.IsAbstract)
            {
                foreach (var (missing, owner) in UnimplementedAbstractMethods(@class))
                    _diagnostics.ReportAbstractNotImplemented(@class.Declaration.Name.Span, @class.Name, missing, owner);
            }
        }
    }

    /// <summary><c>static virtual</c>, <c>abstract</c> with a body, a body missing, or one missing outside an abstract class.</summary>
    private void CheckMethodModifiers(ClassSymbol @class, MethodSymbol method, Token name)
    {
        if ((method.IsVirtual || method.IsOverride || method.IsAbstract) && method.IsStatic)
            _diagnostics.ReportInvalidOverride(name.Span, $"'{method.DisplayName}' is static, so it can't be virtual, abstract or an override.");
        else if ((method.IsVirtual || method.IsOverride || method.IsAbstract) && method.Declaration!.Private is not null)
            _diagnostics.ReportInvalidOverride(name.Span, $"'{method.DisplayName}' is private, so derived classes couldn't override it; make it 'protected' instead.");
        else if (!method.IsAbstract && method.Declaration!.Modifier?.Text == "abstract")
            _diagnostics.ReportInvalidOverride(name.Span, $"'{method.DisplayName}' is abstract, so it can't have a body; mark it 'virtual' to give derived classes a default.");
        else if (method.IsAbstract && method.Declaration!.Modifier?.Text != "abstract")
            _diagnostics.ReportInvalidOverride(name.Span, $"'{method.DisplayName}' has no body; mark it 'abstract' if derived classes are to supply it.");
        else if (method.IsAbstract && !@class.IsAbstract)
            _diagnostics.ReportInvalidOverride(name.Span, $"'{method.DisplayName}' has no body, which only an abstract class's methods can do; mark the class 'abstract', or give it a body.");
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
    private static Type ClrBaseType(ClassSymbol @class) =>
        TypeSubstitution.ClrBaseOf(@class.SelfType) is var clrBase && ClrTypes.ToClr(clrBase) is { } constructed ? constructed : clrBase.Type;

    /// <summary>Whether <paramref name="method"/> can override <paramref name="inherited"/>: a virtual or abstract method, with the same signature.</summary>
    /// <summary>The type arguments <paramref name="method"/>'s class gives the base class declaring <paramref name="inherited"/>.</summary>
    private static Dictionary<TypeParameterSymbol, TypeSymbol> BaseMap(MethodSymbol method, MethodSymbol inherited) =>
        method.ContainingType is ClassSymbol @class
            ? TypeSubstitution.MapFor(TypeSubstitution.AsBase(@class.SelfType, inherited.ContainingType as ClassSymbol))
            : [];

    private bool CanOverride(object inherited, MethodSymbol method, out string problem)
    {
        problem = "";
        switch (inherited)
        {
            case MethodSymbol piper when !piper.IsVirtual && !piper.IsAbstract && !piper.IsOverride:
                problem = $"'{method.Name}' isn't virtual or abstract in '{piper.ContainingType!.Name}', so it can't be overridden; mark it 'virtual' there.";
                return false;
            // A generic base's method is compared as the class sees it: `F(x: T)` of `Repository<T>` is `F(x: string)`
            // for `class Names: Repository<string>`.
            case MethodSymbol piper when BaseMap(method, piper) is var map && !SignaturesMatch(method, piper, map):
                problem = piper.IsAccessor
                    ? $"'{method.DisplayName}' must have the type of the property it overrides: {TypeSubstitution.Substitute(AccessorType(piper), map)}."
                    : $"'{method.Name}' must have the signature of the method it overrides: {Signature(piper, map)}.";
                return false;
            case MethodInfo clr:
            {
                var parameters = clr.GetParameters();
                bool matches = parameters.Length == method.Parameters.Length &&
                               parameters.Zip(method.Parameters).All(p => ClrTypes.ToClr(p.Second.Type) == p.First.ParameterType) &&
                               (clr.ReturnType == typeof(void) ? method.ReturnType == TypeSymbol.Unit : ClrTypes.ToClr(method.ReturnType) == clr.ReturnType);
                if (!matches && method.IsAccessor && clr.IsSpecialName)
                {
                    var type = clr.ReturnType == typeof(void) ? parameters[^1].ParameterType : clr.ReturnType;
                    problem = $"'{method.DisplayName}' must have the type of the .NET property it overrides: {type.Name}.";
                }
                else if (!matches)
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

    /// <summary>A property's type, from its getter's return type or its setter's parameter.</summary>
    private static TypeSymbol AccessorType(MethodSymbol accessor) => accessor.Parameters is [var value] ? value.Type : accessor.ReturnType;

    /// <summary>
    /// The abstract methods and properties a class inherits and nothing overrides, with the class or .NET type declaring
    /// each. A property's getter and setter are each reported, as each must be overridden.
    /// </summary>
    private static IEnumerable<(string Method, string Owner)> UnimplementedAbstractMethods(ClassSymbol @class)
    {
        var overridden = new HashSet<string>();
        var reported = new HashSet<string>();
        foreach (var type in @class.SelfAndBases())
        {
            foreach (var method in type.Methods)
            {
                // Its own abstract methods are reported where they're declared.
                if (method.IsAbstract && !overridden.Contains(method.Name) && type != @class && reported.Add(method.DisplayName))
                    yield return (method.DisplayName, type.Name);
                if (method.IsOverride || !method.IsAbstract)
                    overridden.Add(method.Name);
            }
        }
        if (@class.ClrBase is var clr)
        {
            // Property accessors (get_Length) count too; event accessors and the like don't arise in practice.
            var abstracts = ClrBaseType(@class).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(m => m.IsAbstract && (!m.IsSpecialName || m.Name.StartsWith("get_", StringComparison.Ordinal) || m.Name.StartsWith("set_", StringComparison.Ordinal)));
            foreach (var method in abstracts)
            {
                var name = method.IsSpecialName ? method.Name[4..] : method.Name;
                if (!overridden.Contains(method.Name) && reported.Add(name))
                    yield return (name, clr.Name);
            }
        }
    }
}
