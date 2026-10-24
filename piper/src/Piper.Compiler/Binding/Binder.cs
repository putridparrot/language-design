using System.Collections.Immutable;
using System.Reflection;
using Piper.Diagnostics;
using Piper.Syntax;
using Piper.Text;

namespace Piper.Binding;

/// <summary>
/// Resolves names, checks types and enforces Piper's own rules (immutability, Bool conditions,
/// definite returns, exhaustive matches, trait conformance, visibility) that C# could not check for us.
/// A program is any number of files; binding happens in passes over all of them so declarations can
/// appear in any order and any file: type names, type parameters, trait conformances, fields, method and
/// function signatures, then bodies. After an error the offending expression gets the Error type, which
/// suppresses follow-on errors.
/// This file holds declarations and statements; see Binder.Expressions.cs, Binder.Patterns.cs and Binder.Interop.cs.
/// </summary>
public sealed partial class Binder
{
    /// <summary>Names every C# record already has as members; a field, variant or method with one of these names wouldn't compile.</summary>
    private static readonly HashSet<string> ReservedMemberNames =
    [
        "Equals", "GetHashCode", "ToString", "GetType", "EqualityContract", "PrintMembers", "Deconstruct",
        "MemberwiseClone", "Finalize", "ReferenceEquals",
    ];

    private DiagnosticBag _diagnostics;
    private readonly Scope _builtins;
    private readonly NamespaceSymbol _globalNamespace;
    private readonly List<FileContext> _files = [];

    /// <summary>Methods added to builtin and .NET types with <c>extend Int { … }</c>; user types keep theirs on the symbol.</summary>
    private readonly Dictionary<TypeSymbol, List<MethodSymbol>> _builtinMethods = [];

    /// <summary>The built-in <c>Option&lt;T&gt;</c> (<c>T?</c>) and <c>Result&lt;T, E&gt;</c>.</summary>
    private readonly EnumSymbol _option;
    private readonly EnumSymbol _result;

    /// <summary>The unwrapped values that <c>?.</c> chains currently being bound refer to.</summary>
    private readonly Stack<VariableSymbol> _implicitReceivers = new();

    private FileContext _file = null!;
    private Scope _scope = null!;
    /// <summary>What `return` returns from: the current function or lambda.</summary>
    private ReturnContext? _return;

    /// <summary>Inside a finally block, control can't jump out (C# forbids it); loops opened inside are fine.</summary>
    private int _finallyLoopDepth = -1;

    /// <summary>
    /// Where <c>out let n</c> declares n, when not the current scope: while binding an if condition, the scope around
    /// the if (as in C#, where such variables outlive the if statement).
    /// </summary>
    private Scope? _outScope;

    /// <summary>For each loop being bound, innermost last: whether a `break` leaves it.</summary>
    private readonly Stack<bool> _loopBreaks = new();

    /// <summary>
    /// <paramref name="Inferred"/> is set while binding a lambda whose return type isn't known yet: returned values
    /// are collected (not converted) so their common type can become the return type.
    /// </summary>
    /// <param name="IteratorElement">In an iterator, the type it yields; `return` isn't allowed there.</param>
    private sealed record ReturnContext(
        TypeSymbol Type,
        string Name,
        List<TypeSymbol>? Inferred = null,
        bool IsAsync = false,
        TypeSymbol? IteratorElement = null);
    private int _loopDepth;

    /// <summary>Everything about the file being bound: its namespace, private declarations and the scope chain its code sees.</summary>
    private sealed class FileContext(CompilationUnitSyntax unit, NamespaceSymbol @namespace)
    {
        public CompilationUnitSyntax Unit { get; } = unit;

        public SourceText Source => Unit.Source;

        public NamespaceSymbol Namespace { get; } = @namespace;

        public List<NamespaceSymbol> Usings { get; } = [];

        /// <summary>The file's private declarations: the head of its scope chain.</summary>
        public Scope Private { get; set; } = null!;

        /// <summary>
        /// Lookup chain, nearest first: private declarations → the namespace and each enclosing one →
        /// builtins → names imported by using directives → top-level namespaces (e.g. System).
        /// </summary>
        public Scope Scope => Private;
    }

    /// <summary>Resolution reached a dead end that has already been reported.</summary>
    private sealed class ErrorSymbol() : Symbol("?")
    {
        public static readonly ErrorSymbol Instance = new();
    }

    /// <summary>A name imported by more than one using directive.</summary>
    private sealed class AmbiguousSymbol(string name, IReadOnlyList<string> sources) : Symbol(name)
    {
        public IReadOnlyList<string> Sources { get; } = sources;
    }

    private Binder(DiagnosticBag diagnostics)
    {
        _diagnostics = diagnostics;
        (_option, _result) = EnumSymbol.CreateBuiltins();

        _builtins = new Scope(parent: null);
        foreach (var type in TypeSymbol.Builtins)
            _builtins.TryDeclare(type);
        // `object` is a keyword for System.Object, as in C#.
        _builtins.TryDeclare("object", ClrTypeSymbol.For(typeof(object)));
        foreach (var function in BuiltinFunctions.All)
            _builtins.TryDeclare(function);

        // Option and Result are part of the language: their variants can be used unqualified (Some(1), None, Ok(x), Err(e)).
        foreach (var @enum in new[] { _option, _result })
        {
            _builtins.TryDeclare(@enum);
            foreach (var variant in @enum.Variants)
                _builtins.TryDeclare(variant);
        }

        _globalNamespace = new NamespaceSymbol("", null, new Scope(null));
    }

    public static BoundProgram BindProgram(ImmutableArray<CompilationUnitSyntax> units, DiagnosticBag diagnostics) =>
        new Binder(diagnostics).Bind(units);

    private sealed record ExtendInfo(ExtendDeclarationSyntax Syntax, FileContext File, TypeSymbol Target, Scope TypeParameterScope);

    private sealed record FunctionInfo(FunctionSymbol Symbol, FunctionDeclarationSyntax Syntax, FileContext File, Scope SignatureScope);

    private void InFile(FileContext file)
    {
        _file = file;
        _scope = file.Scope;
        _diagnostics.CurrentSource = file.Source;
    }

    private BoundProgram Bind(ImmutableArray<CompilationUnitSyntax> units)
    {
        foreach (var unit in units)
            _files.Add(CreateFile(unit));
        foreach (var file in _files)
            BindUsings(file);

        var types = new List<TypeSymbol>();
        foreach (var file in _files)
            types.AddRange(DeclareTypes(file));

        // Type parameters (with their trait constraints) come next: everything below may mention them.
        foreach (var definition in types.OfType<NamedTypeDefinition>())
        {
            InFile(FileOf(definition));
            var (syntax, reserved) = definition switch
            {
                RecordSymbol r => (r.Declaration.TypeParameters, new HashSet<string> { r.Name }),
                EnumSymbol e => (e.Declaration!.TypeParameters, [e.Name, .. e.Declaration.Variants.Select(v => v.Name.Text)]),
                _ => throw new InvalidOperationException(),
            };
            definition.TypeParameters = BindTypeParameters(syntax, new Scope(_file.Scope), reserved);
        }

        // Conformances must be known before any generic type is instantiated, to check constraints.
        var extends = _files.SelectMany(BindExtendHeaders).ToList();

        foreach (var type in types)
        {
            if (type is not NamedTypeDefinition definition)
                continue;
            InFile(FileOf(definition));
            WithScope(TypeParameterScope(definition, _file), () =>
            {
                if (definition is RecordSymbol record)
                    record.Fields = BindFields(record.Declaration.Fields, record.Name);
                else
                    BindVariants((EnumSymbol)definition);
            });
        }

        var methods = new List<FunctionInfo>();
        foreach (var trait in types.OfType<TraitSymbol>())
            methods.AddRange(BindTraitMethods(trait));
        foreach (var extend in extends)
            methods.AddRange(BindExtendMethods(extend));
        CheckConformances(extends);

        var functions = new List<FunctionInfo>();
        foreach (var file in _files)
        {
            InFile(file);
            foreach (var declaration in file.Unit.Declarations.OfType<FunctionDeclarationSyntax>())
            {
                var info = BindFunctionSignature(declaration, file, file.Scope, containingType: null, thisType: null);
                if (!declaration.Name.IsMissing)
                    Declare(info.Symbol, declaration, file);
                functions.Add(info);
            }
        }

        CheckExposure(types, functions, methods);

        var boundFunctions = functions.Select(BindFunctionBody).OfType<BoundFunction>().ToImmutableArray();
        var boundMethods = methods.Select(BindFunctionBody).OfType<BoundFunction>().ToImmutableArray();

        var mains = functions.Select(f => f.Symbol).Where(f => f.Name == "main").ToList();
        foreach (var extra in mains.Skip(1))
        {
            InFile(FileOf(extra));
            _diagnostics.ReportDuplicateMain(extra.Declaration!.Name.Span);
        }
        var main = mains.FirstOrDefault();
        // `fn main()`, or `async fn main()` (returning a plain Task).
        if (main is not null && (main.Parameters.Length != 0 || main.BodyReturnType != TypeSymbol.Unit || main.TypeParameters.Length != 0 ||
                                 main.IteratorElementType is not null))
        {
            InFile(FileOf(main));
            _diagnostics.ReportInvalidMain(main.Declaration!.Name.Span);
        }

        return new BoundProgram([.. units.Select(u => u.Source)], [.. _files.Select(f => f.Namespace)], [.. types], [_option, _result], boundFunctions, boundMethods, main);
    }

    private FileContext FileOf(Symbol symbol) => _files.First(f => f.Source == symbol.DeclaringFile);

    // Files, namespaces and using directives

    private FileContext CreateFile(CompilationUnitSyntax unit)
    {
        var @namespace = _globalNamespace;
        if (unit.Namespace is { Name: { IsMissing: false } name })
        {
            foreach (var part in name.Parts)
            {
                @namespace = @namespace.GetOrAddChild(part.Text, () => new Scope(null));
                @namespace.IsDeclaredInPiper = true;
            }
        }

        var file = new FileContext(unit, @namespace);

        // Build the chain from the far end: top-level namespaces ← usings ← builtins ← namespaces ← private.
        var roots = Scope.WithFallback(null, name => _globalNamespace.LookupMember(name, _option, NewMembers) as NamespaceSymbol);
        var imports = Scope.WithFallback(roots, name => LookupImported(file, name));
        Scope chain = Scope.View(_builtins, imports);
        var enclosing = new List<NamespaceSymbol>();
        for (var ns = @namespace; ns is not null; ns = ns.Parent)
            enclosing.Add(ns);
        for (int i = enclosing.Count - 1; i >= 0; i--)
            chain = Scope.View(enclosing[i].Members, chain);
        file.Private = new Scope(chain);
        return file;
    }

    private static Scope NewMembers() => new(null);

    private void BindUsings(FileContext file)
    {
        InFile(file);
        foreach (var directive in file.Unit.Usings)
        {
            if (directive.Name.IsMissing)
                continue;
            if (ResolveNamespace(directive.Name.Parts) is { } @namespace)
                file.Usings.Add(@namespace);
            else
                _diagnostics.ReportUnknownNamespace(directive.Name.Span, directive.Name.Text);
        }
    }

    private NamespaceSymbol? ResolveNamespace(ImmutableArray<Token> parts)
    {
        var current = _globalNamespace;
        foreach (var part in parts)
        {
            if (current.LookupMember(part.Text, _option, NewMembers) is not NamespaceSymbol next)
                return null;
            current = next;
        }
        return current;
    }

    /// <summary>A name imported into <paramref name="file"/> by its using directives (types and functions, not namespaces).</summary>
    private Symbol? LookupImported(FileContext file, string name)
    {
        var found = new List<(Symbol Symbol, NamespaceSymbol From)>();
        foreach (var @namespace in file.Usings)
        {
            var symbol = @namespace.LookupMember(name, _option, NewMembers);
            if (symbol is null or NamespaceSymbol || found.Any(f => ReferenceEquals(f.Symbol, symbol)))
                continue;
            found.Add((symbol, @namespace));
        }

        return found.Count switch
        {
            0 => null,
            1 => found[0].Symbol,
            _ => new AmbiguousSymbol(name, [.. found.Select(f => f.From.FullName)]),
        };
    }

    /// <summary>A member of <paramref name="namespace"/>, including this file's private declarations if it's the file's own namespace.</summary>
    private Symbol? MemberOf(NamespaceSymbol @namespace, string name)
    {
        if (@namespace == _file.Namespace && _file.Private.LookupLocal(name) is { } own)
            return own;
        return @namespace.LookupMember(name, _option, NewMembers);
    }

    /// <summary>Declares a top-level symbol in its file's namespace, or in the file's private scope.</summary>
    private void Declare(Symbol symbol, DeclarationSyntax declaration, FileContext file)
    {
        symbol.DeclaringFile = file.Source;
        symbol.Namespace = file.Namespace;
        symbol.IsPrivate = declaration.Private is not null;

        var name = symbol.Name;
        bool clash = file.Private.LookupLocal(name) is not null || file.Namespace.Members.LookupLocal(name) is not null;
        var scope = symbol.IsPrivate ? file.Private : file.Namespace.Members;
        if (clash || !scope.TryDeclare(symbol))
            _diagnostics.ReportDuplicateDeclaration(declaration.Name.Span, name);
    }

    /// <summary>For an undefined name: a private declaration elsewhere explains it better than "does not exist".</summary>
    private bool ReportIfPrivateElsewhere(Token name)
    {
        var owner = _files.FirstOrDefault(f => f != _file && f.Private.LookupLocal(name.Text) is not null);
        if (owner is null)
            return false;
        _diagnostics.ReportPrivateDeclaration(name.Span, name.Text, owner.Source.FileName);
        return true;
    }

    private void ReportAmbiguous(Token name, AmbiguousSymbol ambiguous) =>
        _diagnostics.ReportAmbiguousImport(name.Span, name.Text, ambiguous.Sources);

    private List<TypeSymbol> DeclareTypes(FileContext file)
    {
        InFile(file);
        var types = new List<TypeSymbol>();
        foreach (var declaration in file.Unit.Declarations)
        {
            TypeSymbol? type = declaration switch
            {
                RecordDeclarationSyntax record => new RecordSymbol(record),
                EnumDeclarationSyntax @enum => new EnumSymbol(@enum),
                TraitDeclarationSyntax trait => new TraitSymbol(trait),
                _ => null,
            };
            if (type is null || declaration.Name.IsMissing)
                continue;

            if (type.Name is "Option" or "Result")
            {
                _diagnostics.ReportBuiltinTypeRedeclared(declaration.Name.Span, type.Name);
                continue;
            }

            Declare(type, declaration, file);
            types.Add(type);
        }
        return types;
    }

    private void WithScope(Scope scope, Action action)
    {
        var outer = _scope;
        _scope = scope;
        try
        {
            action();
        }
        finally
        {
            _scope = outer;
        }
    }

    /// <summary>A scope inside <paramref name="file"/> in which <paramref name="target"/>'s type parameters are visible.</summary>
    private static Scope TypeParameterScope(TypeSymbol target, FileContext file)
    {
        var scope = new Scope(file.Scope);
        if (target is NamedTypeDefinition definition)
        {
            foreach (var parameter in definition.TypeParameters)
                scope.TryDeclare(parameter);
        }
        return scope;
    }

    /// <summary>Declares type parameters in <paramref name="scope"/> and binds their trait constraints.</summary>
    private ImmutableArray<TypeParameterSymbol> BindTypeParameters(
        ImmutableArray<TypeParameterSyntax> syntax,
        Scope scope,
        IReadOnlySet<string> reservedNames)
    {
        var parameters = ImmutableArray.CreateBuilder<TypeParameterSymbol>();
        foreach (var parameterSyntax in syntax)
        {
            var name = parameterSyntax.Name;
            var parameter = new TypeParameterSymbol(name.IsMissing ? "?" : name.Text);
            if (!name.IsMissing && (reservedNames.Contains(name.Text) || !scope.TryDeclare(parameter)))
                _diagnostics.ReportDuplicateDeclaration(name.Span, name.Text);

            var constraints = ImmutableArray.CreateBuilder<TraitSymbol>();
            foreach (var constraint in parameterSyntax.Constraints)
            {
                var type = BindType(constraint);
                if (type is TraitSymbol trait)
                    constraints.Add(trait);
                else if (type != TypeSymbol.Error)
                    _diagnostics.ReportNotATrait(constraint.Span, type.Name);
            }
            parameter.Constraints = constraints.ToImmutable();
            parameters.Add(parameter);
        }
        return parameters.ToImmutable();
    }

    /// <summary>Binds fields of a record or variant named <paramref name="owner"/> in the current (type-parameter) scope.</summary>
    private ImmutableArray<FieldSymbol> BindFields(ImmutableArray<ParameterSyntax> syntax, string owner)
    {
        var fields = ImmutableArray.CreateBuilder<FieldSymbol>();
        var names = new HashSet<string>();
        foreach (var field in syntax)
        {
            var type = BindValueType(field.Type, "field");
            if (field.Name.IsMissing)
                continue;

            if (!names.Add(field.Name.Text))
                _diagnostics.ReportDuplicateDeclaration(field.Name.Span, field.Name.Text);
            else if (field.Name.Text == owner)
                _diagnostics.ReportFieldNamedAsRecord(field.Name.Span, owner);
            else if (ReservedMemberNames.Contains(field.Name.Text))
                _diagnostics.ReportReservedMemberName(field.Name.Span, field.Name.Text);
            else
                fields.Add(new FieldSymbol(field.Name.Text, type));
        }
        return fields.ToImmutable();
    }

    private void BindVariants(EnumSymbol @enum)
    {
        var declaration = @enum.Declaration!;
        if (declaration.Variants.IsEmpty)
            _diagnostics.ReportEmptyEnum(declaration.Name.Span, @enum.Name);

        var variants = ImmutableArray.CreateBuilder<VariantSymbol>();
        var names = new HashSet<string>();
        foreach (var syntax in declaration.Variants)
        {
            var name = syntax.Name;
            if (name.IsMissing)
                continue;

            if (!names.Add(name.Text))
                _diagnostics.ReportDuplicateDeclaration(name.Span, name.Text);
            else if (name.Text == @enum.Name)
                _diagnostics.ReportVariantNamedAsEnum(name.Span, @enum.Name);
            else if (ReservedMemberNames.Contains(name.Text))
                _diagnostics.ReportReservedMemberName(name.Span, name.Text);
            else
                variants.Add(new VariantSymbol(name.Text, @enum) { Fields = BindFields(syntax.Fields, name.Text) });
        }
        @enum.Variants = variants.ToImmutable();
    }

    // Traits and extend blocks

    /// <summary>Resolves each extend block's target type and records the traits it implements.</summary>
    private List<ExtendInfo> BindExtendHeaders(FileContext file)
    {
        InFile(file);
        var extends = new List<ExtendInfo>();
        foreach (var syntax in file.Unit.Declarations.OfType<ExtendDeclarationSyntax>())
        {
            var targetSyntax = (NamedTypeSyntax)syntax.Target;
            if (syntax.Name.IsMissing)
                continue;

            if (syntax.Private is { } @private)
                _diagnostics.ReportInvalidExtendTarget(@private.Span, "'private' applies to the methods in an extend block, not the block itself.");

            var symbol = ResolveTypeName(targetSyntax);
            TypeSymbol? target = symbol switch
            {
                NamedTypeDefinition definition => definition,
                TypeSymbol builtin when ClrTypes.BuiltinTypes.ContainsKey(builtin) => builtin,
                ClrTypeSymbol { IsGenericDefinition: false } clr when !clr.Type.IsGenericType => clr,
                _ => null,
            };

            if (target is null)
            {
                if (symbol is TraitSymbol)
                    _diagnostics.ReportInvalidExtendTarget(targetSyntax.Span, "Traits cannot be extended (yet); add default methods to the trait instead.");
                else if (symbol is TypeSymbol or ClrTypeGroupSymbol)
                    _diagnostics.ReportInvalidExtendTarget(targetSyntax.Span, $"'{symbol.Name}' cannot be extended.");
                else if (symbol is null && !ReportIfPrivateElsewhere(syntax.Name))
                    _diagnostics.ReportUndefinedType(syntax.Name.Span, syntax.Name.Text);
                continue;
            }

            if (!targetSyntax.TypeArguments.IsEmpty)
            {
                _diagnostics.ReportInvalidExtendTarget(targetSyntax.Span,
                    $"Write 'extend {target.Name}' without type arguments; its type parameters are in scope inside the block.");
            }

            var traits = new List<TraitSymbol>();
            foreach (var traitSyntax in syntax.Traits)
            {
                var type = BindType(traitSyntax);
                if (type is not TraitSymbol trait)
                {
                    if (type != TypeSymbol.Error)
                        _diagnostics.ReportNotATrait(traitSyntax.Span, type.Name);
                    continue;
                }

                if (target is not NamedTypeDefinition || target is NamedTypeDefinition { IsRuntimeType: true })
                {
                    _diagnostics.ReportInvalidExtendTarget(traitSyntax.Span,
                        $"Only your own records and enums can implement traits; '{target.Name}' is a builtin, runtime or .NET type.");
                    continue;
                }

                if (traits.Contains(trait) || ((NamedTypeDefinition)target).Traits.Contains(trait))
                    _diagnostics.ReportDuplicateDeclaration(traitSyntax.Span, trait.Name);
                else
                    traits.Add(trait);
            }

            if (target is NamedTypeDefinition named)
                named.Conformances = [.. named.Conformances, .. traits.Select(t => (t, (SourceText?)file.Source))];

            extends.Add(new ExtendInfo(syntax, file, target, TypeParameterScope(target, file)));
        }
        return extends;
    }

    private IEnumerable<FunctionInfo> BindTraitMethods(TraitSymbol trait)
    {
        var file = FileOf(trait);
        InFile(file);
        var infos = new List<FunctionInfo>();
        var methods = ImmutableArray.CreateBuilder<MethodSymbol>();
        var names = new HashSet<string>();
        foreach (var syntax in trait.Declaration.Methods)
        {
            if (syntax.Static is { } @static)
                _diagnostics.ReportInvalidTraitMethod(@static.Span, $"Trait method '{syntax.Name.Text}' cannot be static (yet).");
            if (!syntax.TypeParameters.IsEmpty)
                _diagnostics.ReportInvalidTraitMethod(syntax.Name.Span, $"Trait method '{syntax.Name.Text}' cannot be generic (yet).");
            if (syntax.Private is { } @private)
                _diagnostics.ReportInvalidTraitMethod(@private.Span, $"Trait method '{syntax.Name.Text}' cannot be private.");

            var info = BindFunctionSignature(syntax, file, file.Scope, trait, trait);
            var method = (MethodSymbol)info.Symbol;
            if (syntax.Name.IsMissing)
                continue;
            if (!names.Add(method.Name) || ReservedMemberNames.Contains(method.Name))
            {
                ReportMemberClash(syntax.Name);
                continue;
            }

            methods.Add(method);
            if (syntax.HasBody)
                infos.Add(info);
        }
        trait.Methods = methods.ToImmutable();
        return infos;
    }

    private IEnumerable<FunctionInfo> BindExtendMethods(ExtendInfo extend)
    {
        InFile(extend.File);
        var target = extend.Target;
        var thisType = target is NamedTypeDefinition definition ? definition.SelfType : target;
        var infos = new List<FunctionInfo>();
        foreach (var syntax in extend.Syntax.Methods)
        {
            var info = BindFunctionSignature(syntax, extend.File, extend.TypeParameterScope, target, thisType);
            var method = (MethodSymbol)info.Symbol;
            if (!syntax.HasBody)
                _diagnostics.ReportMissingBody(syntax.Name.Span, syntax.Name.Text);
            if (syntax.Name.IsMissing || !RegisterMethod(target, method, syntax.Name))
                continue;
            if (syntax.HasBody)
                infos.Add(info);
        }
        return infos;
    }

    /// <summary>Adds an extend method to its type, rejecting names that clash with existing members.</summary>
    private bool RegisterMethod(TypeSymbol target, MethodSymbol method, Token name)
    {
        // The emitted Option/Result structs also have IsSome/IsOk/Value/Error members.
        bool reservedOnBuiltin = target is EnumSymbol { BuiltinKind: not BuiltinEnumKind.None } &&
                                 method.Name is "IsSome" or "IsOk" or "Value" or "Error";
        if (ReservedMemberNames.Contains(method.Name) || reservedOnBuiltin)
        {
            _diagnostics.ReportReservedMemberName(name.Span, method.Name);
            return false;
        }

        if (target is NamedTypeDefinition definition)
        {
            bool clash = method.Name == definition.Name ||
                         definition.Methods.Any(m => m.Name == method.Name) ||
                         definition.TypeParameters.Any(p => p.Name == method.Name) ||
                         (definition is RecordSymbol record && record.Fields.Any(f => f.Name == method.Name)) ||
                         // Variants inherit the enum's methods, so a method can't share a name with a variant's field either.
                         (definition is EnumSymbol @enum &&
                          (@enum.FindVariant(method.Name) is not null || @enum.Variants.Any(v => v.Fields.Any(f => f.Name == method.Name))));
            if (clash)
            {
                ReportMemberClash(name);
                return false;
            }
            definition.AddMethod(method);
            return true;
        }

        var methods = _builtinMethods.TryGetValue(target, out var list) ? list : _builtinMethods[target] = [];
        if (methods.Any(m => m.Name == method.Name))
        {
            ReportMemberClash(name);
            return false;
        }

        // An existing .NET member would win over a C# extension member, silently calling the wrong thing.
        var clrType = ClrTypes.ToClr(target)!;
        if (clrType.GetMember(method.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Length > 0)
        {
            _diagnostics.ReportExistingClrMember(name.Span, target, method.Name);
            return false;
        }

        methods.Add(method);
        return true;
    }

    private void ReportMemberClash(Token name) => _diagnostics.ReportDuplicateDeclaration(name.Span, name.Text);

    /// <summary>Every required trait method must be implemented, with exactly the trait's signature.</summary>
    private void CheckConformances(List<ExtendInfo> extends)
    {
        foreach (var extend in extends)
        {
            if (extend.Target is not NamedTypeDefinition definition)
                continue;

            InFile(extend.File);
            foreach (var traitSyntax in extend.Syntax.Traits)
            {
                if (traitSyntax is not NamedTypeSyntax named || ResolveTypeName(named) is not TraitSymbol trait)
                    continue;

                foreach (var required in trait.Methods)
                {
                    var implementation = definition.Methods.FirstOrDefault(m => m.Name == required.Name);
                    if (implementation is null)
                    {
                        if (required.IsAbstract)
                            _diagnostics.ReportMissingTraitMethod(traitSyntax.Span, definition, required.Name, trait);
                        continue;
                    }

                    if (!SignaturesMatch(implementation, required))
                    {
                        _diagnostics.CurrentSource = implementation.DeclaringFile;
                        _diagnostics.ReportTraitSignatureMismatch(implementation.Declaration!.Name.Span, required.Name, trait, Signature(required));
                        _diagnostics.CurrentSource = extend.File.Source;
                    }
                }
            }
        }
    }

    private static bool SignaturesMatch(MethodSymbol implementation, MethodSymbol required) =>
        !implementation.IsStatic &&
        implementation.TypeParameters.IsEmpty &&
        implementation.ReturnType == required.ReturnType &&
        implementation.Parameters.Select(p => p.Type).SequenceEqual(required.Parameters.Select(p => p.Type));

    private static string Signature(MethodSymbol method)
    {
        var parameters = string.Join(", ", method.Parameters.Select(p => $"{p.Name}: {p.Type}"));
        var result = method.ReturnType == TypeSymbol.Unit ? "" : $" => {method.ReturnType}";
        return $"fn {method.Name}({parameters}){result}";
    }

    /// <summary>
    /// A public declaration must not mention a private type: code in other files could see the declaration
    /// but not the type (C# rejects the same thing for file-local types).
    /// </summary>
    private void CheckExposure(List<TypeSymbol> types, List<FunctionInfo> functions, List<FunctionInfo> methods)
    {
        void Check(Symbol owner, Token name, IEnumerable<TypeSymbol> mentioned)
        {
            if (owner.IsPrivate || mentioned.FirstOrDefault(MentionsPrivateType) is not { } exposed)
                return;
            InFile(FileOf(owner));
            _diagnostics.ReportPrivateTypeExposed(name.Span, name.Text, PrivateTypeIn(exposed)!);
        }

        foreach (var type in types)
        {
            switch (type)
            {
                case RecordSymbol record:
                    Check(record, record.Declaration.Name, record.Fields.Select(f => f.Type));
                    break;
                case EnumSymbol @enum:
                    Check(@enum, @enum.Declaration!.Name, @enum.Variants.SelectMany(v => v.Fields.Select(f => f.Type)));
                    break;
                case TraitSymbol trait:
                    Check(trait, trait.Declaration.Name, trait.Methods.SelectMany(SignatureTypes));
                    break;
            }

            if (type is NamedTypeDefinition definition && !definition.IsPrivate)
            {
                foreach (var (trait, _) in definition.Conformances.Where(c => c.Trait.IsPrivate))
                {
                    InFile(FileOf(definition));
                    _diagnostics.ReportPrivateTypeExposed(NameOf(definition).Span, definition.Name, trait.Name);
                }
            }
        }

        foreach (var info in functions)
            Check(info.Symbol, info.Syntax.Name, SignatureTypes(info.Symbol));

        foreach (var info in methods)
        {
            var method = (MethodSymbol)info.Symbol;
            if (!method.ContainingType.IsPrivate)
                Check(method, info.Syntax.Name, SignatureTypes(method));
        }
    }

    private static Token NameOf(NamedTypeDefinition definition) => definition switch
    {
        RecordSymbol record => record.Declaration.Name,
        EnumSymbol @enum => @enum.Declaration!.Name,
        _ => throw new InvalidOperationException(),
    };

    private static IEnumerable<TypeSymbol> SignatureTypes(FunctionSymbol function) =>
        [.. function.Parameters.Select(p => p.Type), function.ReturnType, .. function.TypeParameters.SelectMany(p => p.Constraints)];

    private static bool MentionsPrivateType(TypeSymbol type) => PrivateTypeIn(type) is not null;

    private static string? PrivateTypeIn(TypeSymbol type) => type switch
    {
        _ when type.OriginalDefinition.IsPrivate => type.OriginalDefinition.Name,
        TupleTypeSymbol tuple => tuple.ElementTypes.Select(PrivateTypeIn).FirstOrDefault(n => n is not null),
        _ => type.TypeArguments.Select(PrivateTypeIn).FirstOrDefault(n => n is not null),
    };

    // Functions and methods

    /// <param name="containingType">The trait, record/enum definition or builtin a method belongs to; null for a top-level function.</param>
    /// <param name="thisType">The type of <c>this</c> inside an instance method.</param>
    private FunctionInfo BindFunctionSignature(
        FunctionDeclarationSyntax declaration,
        FileContext file,
        Scope outer,
        TypeSymbol? containingType,
        TypeSymbol? thisType)
    {
        var scope = new Scope(outer);
        var typeParameters = BindTypeParameters(declaration.TypeParameters, scope, new HashSet<string> { declaration.Name.Text });

        var outerScope = _scope;
        _scope = scope;

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

        // An async function with no return type returns a Task, as a plain one returns unit.
        var returnType = declaration.ReturnType is null
            ? declaration.Async is null ? TypeSymbol.Unit : TaskType
            : BindType(declaration.ReturnType);
        _scope = outerScope;

        var bodyReturnType = returnType;
        TypeSymbol? iteratorElement = null;
        if (declaration.IsIterator)
        {
            // An iterator's body yields its values rather than producing one.
            iteratorElement = IteratorElementType(returnType, declaration.Async is not null);
            if (iteratorElement is null && returnType != TypeSymbol.Error)
                _diagnostics.ReportInvalidIteratorType(declaration.ReturnType?.Span ?? declaration.Name.Span, returnType, declaration.Async is not null);
            iteratorElement ??= TypeSymbol.Error;
            bodyReturnType = TypeSymbol.Unit;
        }
        else if (declaration.Async is { } @async)
        {
            if (!declaration.HasBody)
                _diagnostics.ReportAsyncWithoutBody(@async.Span);
            bodyReturnType = AsyncBodyType(returnType) ?? TypeSymbol.Error;
            if (bodyReturnType == TypeSymbol.Error && returnType != TypeSymbol.Error)
                _diagnostics.ReportInvalidAsyncReturnType(declaration.ReturnType!.Span, "function", returnType);
        }
        bool isAsync = declaration.Async is not null;

        if (containingType is null)
        {
            if (declaration.Static is { } @static)
                _diagnostics.ReportStaticOutsideExtend(@static.Span);
            if (!declaration.HasBody)
                _diagnostics.ReportMissingBody(declaration.Name.Span, declaration.Name.Text);
            var function = new FunctionSymbol(declaration.Name.Text, typeParameters, parameters.ToImmutable(), returnType, declaration)
            {
                IsAsync = isAsync,
                BodyReturnType = bodyReturnType,
                IteratorElementType = iteratorElement,
            };
            return new FunctionInfo(function, declaration, file, scope);
        }

        // As in C#, methods are instance methods with an implicit `this` unless marked static.
        var thisVariable = declaration.Static is null
            ? new VariableSymbol("this", thisType!, isMutable: false, VariableKind.This)
            : null;
        var method = new MethodSymbol(
            declaration.Name.Text, typeParameters, parameters.ToImmutable(), returnType, declaration, containingType, thisVariable)
        {
            DeclaringFile = file.Source,
            Namespace = file.Namespace,
            IsPrivate = declaration.Private is not null,
            IsAsync = isAsync,
            BodyReturnType = bodyReturnType,
            IteratorElementType = iteratorElement,
        };
        return new FunctionInfo(method, declaration, file, scope);
    }

    private BoundFunction? BindFunctionBody(FunctionInfo info)
    {
        var (function, declaration, file, signatureScope) = info;
        if (!declaration.HasBody)
            return null;

        InFile(file);
        _return = new ReturnContext(function.BodyReturnType, function.Name, IsAsync: function.IsAsync, IteratorElement: function.IteratorElementType);
        (_yieldReturnForbidden, _yieldBreakForbidden) = (null, null);
        _sawAwait = false;
        _finallyLoopDepth = -1;
        _loopDepth = 0;
        _scope = new Scope(signatureScope);
        if (function is MethodSymbol { This: { } @this })
            _scope.TryDeclare(@this);
        foreach (var parameter in function.Parameters)
            _scope.TryDeclare(parameter);

        var expected = function.BodyReturnType == TypeSymbol.Unit ? null : function.BodyReturnType;
        BoundBlockExpression body;
        ExpressionSyntax? trailingSyntax;
        if (declaration.ExpressionBody is { } expressionBody)
        {
            var expression = BindExpression(expressionBody, expected);
            body = new BoundBlockExpression([], expression, expression.Type);
            trailingSyntax = expressionBody;
        }
        else
        {
            body = BindBlock(declaration.BlockBody!, expected);
            trailingSyntax = declaration.BlockBody!.Trailing;
        }

        body = CheckFunctionResult(function, declaration, body, trailingSyntax);
        if (function.IsAsync && !_sawAwait && function.BodyReturnType != TypeSymbol.Error)
            _diagnostics.ReportAsyncWithoutAwait(declaration.Name.Span, "function");

        _scope = file.Scope;
        _return = null;
        return new BoundFunction(function, body, declaration.ExpressionBody is not null);
    }

    /// <summary>A function with a return type must end in a value (trailing expression) or never complete normally.</summary>
    private BoundBlockExpression CheckFunctionResult(
        FunctionSymbol function,
        FunctionDeclarationSyntax declaration,
        BoundBlockExpression body,
        ExpressionSyntax? trailingSyntax)
    {
        var returnType = function.BodyReturnType;
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

    // Types

    private TypeSymbol BindType(TypeSyntax syntax)
    {
        if (syntax is OptionalTypeSyntax optional)
        {
            var inner = BindValueType(optional.Inner, "type argument");
            return inner == TypeSymbol.Error ? TypeSymbol.Error : _option.Construct([inner]);
        }

        if (syntax is TupleTypeSyntax tuple)
            return BindTupleType(tuple);

        if (syntax is ArrayTypeSyntax array)
        {
            var element = BindValueType(array.Element, "array element");
            return element == TypeSymbol.Error ? TypeSymbol.Error : ClrTypeSymbol.ArrayOf(element, array.Rank);
        }

        // (int, string) => bool is Func<int, string, bool>; a function returning unit is an Action.
        if (syntax is FunctionTypeSyntax function)
        {
            var parameters = function.Parameters.Select(p => BindValueType(p, "parameter")).ToList();
            return FunctionType(parameters, BindType(function.ReturnType));
        }

        var named = (NamedTypeSyntax)syntax;
        var name = named.Name;
        if (name.IsMissing || named.Qualifier.Any(q => q.IsMissing))
            return TypeSymbol.Error;

        var displayName = named.Qualifier.IsEmpty ? name.Text : $"{string.Join(".", named.Qualifier.Select(q => q.Text))}.{name.Text}";
        return BindNamedType(ResolveTypeName(named), name, displayName, named.TypeArguments, syntax.Span, isQualified: !named.Qualifier.IsEmpty);
    }

    /// <summary>
    /// Applies written type arguments (possibly none) to a resolved type name: <c>List&lt;Int&gt;</c>, <c>Pair&lt;A, B&gt;</c>, <c>Int</c>.
    /// Also used for type names in expressions, such as <c>List&lt;Int&gt;()</c> and <c>Box&lt;Int&gt;.empty()</c>.
    /// </summary>
    private TypeSymbol BindNamedType(
        Symbol? symbol,
        Token name,
        string displayName,
        ImmutableArray<TypeSyntax> argumentSyntax,
        TextSpan span,
        bool isQualified)
    {
        var arguments = argumentSyntax.Select(a => BindValueType(a, "type argument")).ToImmutableArray();

        // `List<Int>`: pick the .NET type with the right number of type parameters.
        if (symbol is ClrTypeGroupSymbol group)
        {
            if (group.WithArity(arguments.Length) is not { } chosen)
            {
                _diagnostics.ReportTypeArgumentCount(span, group.Name, group.Types.Max(t => t.GetGenericArguments().Length), arguments.Length);
                return TypeSymbol.Error;
            }
            symbol = ClrTypes.FromClr(chosen, new Dictionary<Type, TypeSymbol>(), _option) ?? ClrTypeSymbol.For(chosen);
        }

        switch (symbol)
        {
            case ErrorSymbol:
                return TypeSymbol.Error;

            case AmbiguousSymbol ambiguous:
                ReportAmbiguous(name, ambiguous);
                return TypeSymbol.Error;

            case ClrTypeSymbol clr when clr.IsGenericDefinition:
            {
                int arity = clr.Type.GetGenericArguments().Length;
                if (arity != arguments.Length)
                {
                    _diagnostics.ReportTypeArgumentCount(span, clr.Name, arity, arguments.Length);
                    return TypeSymbol.Error;
                }
                return arguments.Contains(TypeSymbol.Error) ? TypeSymbol.Error : clr.Construct(arguments);
            }

            case NamespaceSymbol:
                _diagnostics.ReportNotAType(name.Span, displayName, "namespace");
                return TypeSymbol.Error;
            case NamedTypeDefinition definition:
                if (definition.TypeParameters.Length != arguments.Length)
                {
                    if (definition.IsGeneric)
                        _diagnostics.ReportTypeArgumentCount(span, definition.Name, definition.TypeParameters.Length, arguments.Length);
                    else
                        _diagnostics.ReportNotGeneric(span, definition.Name);
                    return TypeSymbol.Error;
                }
                if (arguments.Contains(TypeSymbol.Error))
                    return TypeSymbol.Error;
                CheckConstraints(definition.TypeParameters, arguments, span);
                return definition.Construct(arguments);

            case TypeSymbol type when type != TypeSymbol.Never && type != TypeSymbol.Error:
                if (!arguments.IsEmpty)
                {
                    _diagnostics.ReportNotGeneric(span, type.Name);
                    return TypeSymbol.Error;
                }
                return type;

            case null when !isQualified && ReportIfPrivateElsewhere(name):
                return TypeSymbol.Error;

            case null:
                _diagnostics.ReportUndefinedType(name.Span, name.Text);
                return TypeSymbol.Error;

            default:
                _diagnostics.ReportNotAType(name.Span, name.Text, symbol is FunctionSymbol ? "function" : "value");
                return TypeSymbol.Error;
        }
    }

    /// <summary>
    /// Resolves a (possibly qualified) type name to a symbol without type arguments applied: a Piper type,
    /// a builtin, a .NET type or type group, or <see cref="ErrorSymbol"/> after reporting a bad qualifier.
    /// </summary>
    private Symbol? ResolveTypeName(NamedTypeSyntax syntax)
    {
        if (syntax.Qualifier.IsEmpty)
            return _scope.Lookup(syntax.Name.Text);

        if (_scope.Lookup(syntax.Qualifier[0].Text) is not NamespaceSymbol @namespace)
        {
            _diagnostics.ReportUnknownNamespace(syntax.Qualifier[0].Span, syntax.Qualifier[0].Text);
            return ErrorSymbol.Instance;
        }

        foreach (var part in syntax.Qualifier.Skip(1))
        {
            if (MemberOf(@namespace, part.Text) is not NamespaceSymbol next)
            {
                _diagnostics.ReportUnknownNamespace(part.Span, $"{@namespace.FullName}.{part.Text}");
                return ErrorSymbol.Instance;
            }
            @namespace = next;
        }

        if (MemberOf(@namespace, syntax.Name.Text) is { } member)
            return member;
        _diagnostics.ReportNotInNamespace(syntax.Name.Span, syntax.Name.Text, @namespace.FullName);
        return ErrorSymbol.Instance;
    }

    private TypeSymbol BindValueType(TypeSyntax syntax, string what)
    {
        var type = BindType(syntax);
        if (type != TypeSymbol.Unit)
            return type;
        _diagnostics.ReportInvalidUnitUsage(syntax.Span, what);
        return TypeSymbol.Error;
    }

    private void CheckConstraints(ImmutableArray<TypeParameterSymbol> parameters, IReadOnlyList<TypeSymbol> arguments, TextSpan span)
    {
        for (int i = 0; i < parameters.Length; i++)
        {
            foreach (var trait in parameters[i].Constraints)
            {
                if (!Implements(arguments[i], trait))
                    _diagnostics.ReportConstraintNotSatisfied(span, arguments[i], trait, parameters[i]);
            }
        }
    }

    /// <summary>True if values of <paramref name="type"/> can be used where <paramref name="trait"/> is expected.</summary>
    private static bool Implements(TypeSymbol type, TraitSymbol trait) => type switch
    {
        _ when type == TypeSymbol.Error || type == trait => true,
        TypeParameterSymbol parameter => parameter.Constraints.Contains(trait),
        _ => type.OriginalDefinition is NamedTypeDefinition definition && definition.Traits.Contains(trait),
    };

    // Statements

    private BoundBlockExpression BindBlock(BlockExpressionSyntax syntax, TypeSymbol? expected = null)
    {
        var outer = _scope;
        _scope = new Scope(outer);

        var statements = syntax.Statements.Select(BindStatement).ToImmutableArray();
        var trailing = syntax.Trailing is null ? null : BindExpression(syntax.Trailing, expected);

        _scope = outer;

        var type = trailing?.Type ?? (statements.Any(Diverges) ? TypeSymbol.Never : TypeSymbol.Unit);
        (statements, trailing) = ApplyDefers(statements, trailing, type);
        return new BoundBlockExpression(statements, trailing, type);
    }

    /// <summary>True if control never flows past <paramref name="statement"/>.</summary>
    private static bool Diverges(BoundStatement statement) => statement switch
    {
        BoundReturnStatement or BoundBreakStatement or BoundContinueStatement or BoundThrowStatement => true,
        BoundTryCatchStatement t => (Diverges(t.Body) && t.Catches.All(c => Diverges(c.Body))) || (t.Finally is { } f && Diverges(f)),
        BoundUsingStatement u => Diverges(u.Body),
        BoundWhileStatement { IsInfinite: true } => true,
        BoundYieldStatement { Value: null } => true,
        BoundExpressionStatement e => e.Expression.Type == TypeSymbol.Never,
        _ => false,
    };

    private BoundStatement BindStatement(StatementSyntax syntax)
    {
        var bound = BindStatementCore(syntax);
        bound.SourceSpan ??= syntax.Span;
        return bound;
    }

    private BoundStatement BindStatementCore(StatementSyntax syntax) => syntax switch
    {
        VariableDeclarationSyntax s => BindVariableDeclaration(s),
        DeconstructionDeclarationSyntax s => BindDeconstructionDeclaration(s),
        ExpressionStatementSyntax s => new BoundExpressionStatement(BindExpression(s.Expression)),
        WhileStatementSyntax s => BindWhile(s),
        ForStatementSyntax s => BindFor(s),
        ReturnStatementSyntax s => BindReturn(s),
        TryStatementSyntax s => BindTryCatch(s),
        YieldStatementSyntax s => BindYield(s),
        UsingStatementSyntax s => BindUsing(s),
        DeferStatementSyntax s => BindDefer(s),
        BreakStatementSyntax s => BindLoopJump(s.Keyword, new BoundBreakStatement()),
        ContinueStatementSyntax s => BindLoopJump(s.Keyword, new BoundContinueStatement()),
        _ => throw new InvalidOperationException($"Unexpected statement {syntax.GetType().Name}"),
    };

    private BoundStatement BindVariableDeclaration(VariableDeclarationSyntax syntax)
    {
        TypeSymbol type;
        BoundExpression initializer;
        if (syntax.Type is not null)
        {
            type = BindValueType(syntax.Type, "variable");
            initializer = Convert(BindExpression(syntax.Initializer, type), type, syntax.Initializer.Span);
        }
        else
        {
            initializer = RequireValue(BindExpression(syntax.Initializer), syntax.Initializer.Span);
            type = initializer.Type;
        }

        if (syntax.Using is not null)
            CheckDisposable(type, syntax.Initializer.Span, syntax.Await);

        // Declared after binding the initializer, so `let x = x + 1;` refers to the outer x.
        var variable = new VariableSymbol(syntax.Name.Text, type, syntax.IsMutable, VariableKind.Local);
        if (!syntax.Name.IsMissing && !_scope.TryDeclare(variable))
            _diagnostics.ReportDuplicateDeclaration(syntax.Name.Span, variable.Name);

        return new BoundVariableDeclaration(variable, initializer, isUsing: syntax.Using is not null) { IsAwait = syntax.Await is not null };
    }

    /// <summary><c>using (let r = open()) { … }</c>: r is in scope (and read-only) in the body only.</summary>
    private BoundStatement BindUsing(UsingStatementSyntax syntax)
    {
        var type = syntax.Type is null ? null : BindValueType(syntax.Type, "variable");
        var resource = RequireValue(BindExpression(syntax.Resource, type), syntax.Resource.Span);
        if (type is not null)
            resource = Convert(resource, type, syntax.Resource.Span);
        CheckDisposable(resource.Type, syntax.Resource.Span, syntax.Await);

        var outer = _scope;
        _scope = new Scope(outer);
        VariableSymbol? variable = null;
        if (syntax.Name is { } name)
        {
            variable = new VariableSymbol(name.Text, type ?? resource.Type, isMutable: false, VariableKind.Local);
            if (!name.IsMissing)
                _scope.TryDeclare(variable);
        }
        var body = BindBlock(syntax.Body);
        _scope = outer;
        return new BoundUsingStatement(variable, resource, new BoundExpressionStatement(body)) { IsAwait = syntax.Await is not null };
    }

    /// <param name="await">`await using`: the value must be an IAsyncDisposable, in an async function.</param>
    private void CheckDisposable(TypeSymbol type, TextSpan span, Token? await = null)
    {
        if (await is not null)
            CheckAwaitAllowed(await);
        var required = await is null ? typeof(IDisposable) : typeof(IAsyncDisposable);
        if (type != TypeSymbol.Error && !(ClrTypes.ToClr(type) is { } clr && required.IsAssignableFrom(clr)))
            _diagnostics.ReportNotDisposable(span, type, required.Name);
    }

    private BoundStatement BindWhile(WhileStatementSyntax syntax)
    {
        // Variables bound by `is` in the condition are in scope in the body.
        var outer = _scope;
        _scope = new Scope(outer);
        var condition = BindCondition(syntax.Condition, allowPatternBindings: true);
        var body = BindLoopBody(syntax.Body, out bool breaks);
        _scope = outer;
        // `while true` with no `break` never finishes, so code after it is unreachable, as in C#.
        bool infinite = condition is BoundLiteralExpression { Value: true } && !breaks;
        return new BoundWhileStatement(condition, new BoundExpressionStatement(body)) { IsInfinite = infinite };
    }

    /// <summary>Binds a loop's body, noting whether a `break` leaves this loop (rather than an inner one).</summary>
    private BoundBlockExpression BindLoopBody(BlockExpressionSyntax body, out bool breaks)
    {
        _loopDepth++;
        _loopBreaks.Push(false);
        var bound = BindBlock(body);
        breaks = _loopBreaks.Pop();
        _loopDepth--;
        return bound;
    }

    private BoundStatement BindFor(ForStatementSyntax syntax)
    {
        BoundExpression lower, upper;
        TypeSymbol type;
        bool inclusive = false;
        if (syntax.Range is RangeExpressionSyntax range && syntax.Deconstruction is null && syntax.Await is null)
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
            return BindForEach(syntax);
        }

        var outer = _scope;
        _scope = new Scope(outer);
        var variable = new VariableSymbol(syntax.Variable.Text, type, isMutable: false, VariableKind.LoopVariable);
        if (!syntax.Variable.IsMissing)
            _scope.TryDeclare(variable);

        var body = BindLoopBody(syntax.Body, out bool breaks);
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
        var function = _return!;
        if (function.IteratorElement is not null)
            _diagnostics.ReportReturnInIterator(syntax.Keyword.Span);
        if (_finallyLoopDepth >= 0)
            _diagnostics.ReportJumpOutOfFinally(syntax.Keyword.Span, "return");

        if (function.Inferred is { } inferred)
        {
            if (syntax.Value is null)
            {
                inferred.Add(TypeSymbol.Unit);
                return new BoundReturnStatement(null);
            }
            var returned = RequireValue(BindExpression(syntax.Value), syntax.Value.Span);
            inferred.Add(returned.Type);
            return new BoundReturnStatement(returned);
        }
        if (syntax.Value is null)
        {
            if (function.Type != TypeSymbol.Unit && function.Type != TypeSymbol.Error)
                _diagnostics.ReportMissingReturnValue(syntax.Keyword.Span, function.Name, function.Type);
            return new BoundReturnStatement(null);
        }

        if (function.Type == TypeSymbol.Unit)
        {
            if (BindExpression(syntax.Value).Type != TypeSymbol.Error)
                _diagnostics.ReportUnexpectedReturnValue(syntax.Value.Span, function.Name);
            return new BoundReturnStatement(null);
        }

        var value = BindExpression(syntax.Value, function.Type);
        value = Convert(RequireValue(value, syntax.Value.Span), function.Type, syntax.Value.Span);
        return new BoundReturnStatement(value);
    }

    private BoundStatement BindLoopJump(Token keyword, BoundStatement statement)
    {
        if (statement is BoundBreakStatement && _loopDepth > 0 && _loopBreaks.Count > 0)
        {
            _loopBreaks.Pop();
            _loopBreaks.Push(true);
        }
        if (_loopDepth == 0)
            _diagnostics.ReportOutsideLoop(keyword.Span, keyword.Text);
        else if (_finallyLoopDepth >= 0 && _loopDepth <= _finallyLoopDepth)
            _diagnostics.ReportJumpOutOfFinally(keyword.Span, keyword.Text);
        return statement;
    }
}
