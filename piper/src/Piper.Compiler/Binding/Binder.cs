using System.Collections.Immutable;
using System.Reflection;
using Piper.Diagnostics;
using Piper.Syntax;
using Piper.Text;

namespace Piper.Binding;

/// <summary>
/// Resolves names, checks types and enforces Piper's own rules (immutability, Bool conditions,
/// definite returns, exhaustive matches, trait conformance) that C# could not check for us.
/// Binding happens in passes so declarations can appear in any order: type names, type parameters,
/// trait conformances, fields, method and function signatures, then bodies. After an error the
/// offending expression gets the Error type, which suppresses follow-on errors.
/// This file holds declarations and statements; see Binder.Expressions.cs and Binder.Patterns.cs.
/// </summary>
public sealed partial class Binder
{
    /// <summary>Names every C# record already has as members; a field, variant or method with one of these names wouldn't compile.</summary>
    private static readonly HashSet<string> ReservedMemberNames =
    [
        "Equals", "GetHashCode", "ToString", "GetType", "EqualityContract", "PrintMembers", "Deconstruct",
        "MemberwiseClone", "Finalize", "ReferenceEquals",
    ];

    private static readonly Dictionary<TypeSymbol, Type> ClrTypes = new()
    {
        [TypeSymbol.Int] = typeof(int),
        [TypeSymbol.Long] = typeof(long),
        [TypeSymbol.Double] = typeof(double),
        [TypeSymbol.Bool] = typeof(bool),
        [TypeSymbol.String] = typeof(string),
        [TypeSymbol.Char] = typeof(char),
    };

    private readonly DiagnosticBag _diagnostics;
    private readonly Scope _globalScope;

    /// <summary>Methods added to builtin types with <c>extend Int { … }</c>; user types keep theirs on the symbol.</summary>
    private readonly Dictionary<TypeSymbol, List<MethodSymbol>> _builtinMethods = [];

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

    private sealed record ExtendInfo(ExtendDeclarationSyntax Syntax, TypeSymbol Target, Scope TypeParameterScope);

    private sealed record FunctionInfo(FunctionSymbol Symbol, FunctionDeclarationSyntax Syntax, Scope SignatureScope);

    private BoundProgram Bind(CompilationUnitSyntax unit)
    {
        var types = DeclareTypes(unit);

        // Type parameters (with their trait constraints) come next: everything below may mention them.
        var typeParameterScopes = new Dictionary<NamedTypeDefinition, Scope>();
        foreach (var definition in types.OfType<NamedTypeDefinition>())
        {
            var (syntax, reserved) = definition switch
            {
                RecordSymbol r => (r.Declaration.TypeParameters, new HashSet<string> { r.Name }),
                EnumSymbol e => (e.Declaration.TypeParameters, [e.Name, .. e.Declaration.Variants.Select(v => v.Name.Text)]),
                _ => throw new InvalidOperationException(),
            };
            var scope = new Scope(_globalScope);
            definition.TypeParameters = BindTypeParameters(syntax, scope, reserved);
            typeParameterScopes[definition] = scope;
        }

        // Conformances must be known before any generic type is instantiated, to check constraints.
        var extends = BindExtendHeaders(unit, typeParameterScopes);

        foreach (var type in types)
        {
            if (type is RecordSymbol record)
                WithScope(typeParameterScopes[record], () => record.Fields = BindFields(record.Declaration.Fields, record.Name));
            else if (type is EnumSymbol @enum)
                WithScope(typeParameterScopes[@enum], () => BindVariants(@enum));
        }

        var methods = new List<FunctionInfo>();
        foreach (var trait in types.OfType<TraitSymbol>())
            methods.AddRange(BindTraitMethods(trait));
        foreach (var extend in extends)
            methods.AddRange(BindExtendMethods(extend));
        CheckConformances(extends);

        var functions = new List<FunctionInfo>();
        foreach (var declaration in unit.Declarations.OfType<FunctionDeclarationSyntax>())
        {
            var info = BindFunctionSignature(declaration, _globalScope, containingType: null, selfType: null);
            if (!declaration.Name.IsMissing && !_globalScope.TryDeclare(info.Symbol))
                _diagnostics.ReportDuplicateDeclaration(declaration.Name.Span, info.Symbol.Name);
            functions.Add(info);
        }

        var boundFunctions = functions.Select(BindFunctionBody).OfType<BoundFunction>().ToImmutableArray();
        var boundMethods = methods.Select(BindFunctionBody).OfType<BoundFunction>().ToImmutableArray();

        var main = functions.Select(f => f.Symbol).FirstOrDefault(f => f.Name == "main");
        if (main is not null && (main.Parameters.Length != 0 || main.ReturnType != TypeSymbol.Unit || main.TypeParameters.Length != 0))
            _diagnostics.ReportInvalidMain(main.Declaration!.Name.Span);

        return new BoundProgram([.. types], boundFunctions, boundMethods, main);
    }

    private List<TypeSymbol> DeclareTypes(CompilationUnitSyntax unit)
    {
        var types = new List<TypeSymbol>();
        foreach (var declaration in unit.Declarations)
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

            if (_globalScope.TryDeclare(type))
                types.Add(type);
            else
                _diagnostics.ReportDuplicateDeclaration(declaration.Name.Span, type.Name);
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
        var declaration = @enum.Declaration;
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
    private List<ExtendInfo> BindExtendHeaders(CompilationUnitSyntax unit, Dictionary<NamedTypeDefinition, Scope> typeParameterScopes)
    {
        var extends = new List<ExtendInfo>();
        foreach (var syntax in unit.Declarations.OfType<ExtendDeclarationSyntax>())
        {
            var targetSyntax = (NamedTypeSyntax)syntax.Target;
            if (syntax.Name.IsMissing)
                continue;

            var symbol = _globalScope.Lookup(syntax.Name.Text);
            TypeSymbol? target = symbol switch
            {
                NamedTypeDefinition definition => definition,
                TypeSymbol builtin when ClrTypes.ContainsKey(builtin) => builtin,
                _ => null,
            };

            if (target is null)
            {
                if (symbol is TraitSymbol)
                    _diagnostics.ReportInvalidExtendTarget(targetSyntax.Span, "Traits cannot be extended (yet); add default methods to the trait instead.");
                else if (symbol is TypeSymbol other)
                    _diagnostics.ReportInvalidExtendTarget(targetSyntax.Span, $"'{other}' cannot be extended.");
                else
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

                if (target is not NamedTypeDefinition)
                {
                    _diagnostics.ReportInvalidExtendTarget(traitSyntax.Span,
                        $"Only records and enums can implement traits; '{target}' is a builtin type.");
                    continue;
                }

                if (traits.Contains(trait) || ((NamedTypeDefinition)target).Traits.Contains(trait))
                    _diagnostics.ReportDuplicateDeclaration(traitSyntax.Span, trait.Name);
                else
                    traits.Add(trait);
            }

            if (target is NamedTypeDefinition named)
                named.Traits = [.. named.Traits, .. traits];

            var scope = target is NamedTypeDefinition def ? typeParameterScopes[def] : _globalScope;
            extends.Add(new ExtendInfo(syntax, target, scope));
        }
        return extends;
    }

    private IEnumerable<FunctionInfo> BindTraitMethods(TraitSymbol trait)
    {
        var infos = new List<FunctionInfo>();
        var methods = ImmutableArray.CreateBuilder<MethodSymbol>();
        var names = new HashSet<string>();
        foreach (var syntax in trait.Declaration.Methods)
        {
            if (syntax.Self is null)
                _diagnostics.ReportInvalidTraitMethod(syntax.Name.Span, $"Trait method '{syntax.Name.Text}' must take 'self'; static trait methods aren't supported (yet).");
            if (!syntax.TypeParameters.IsEmpty)
                _diagnostics.ReportInvalidTraitMethod(syntax.Name.Span, $"Trait method '{syntax.Name.Text}' cannot be generic (yet).");

            var info = BindFunctionSignature(syntax, _globalScope, trait, trait);
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
        var target = extend.Target;
        var selfType = target is NamedTypeDefinition definition ? definition.SelfType : target;
        var infos = new List<FunctionInfo>();
        foreach (var syntax in extend.Syntax.Methods)
        {
            var info = BindFunctionSignature(syntax, extend.TypeParameterScope, target, selfType);
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
        if (ReservedMemberNames.Contains(method.Name))
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
        if (ClrTypes[target].GetMember(method.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Length > 0)
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

            foreach (var traitSyntax in extend.Syntax.Traits)
            {
                if (_globalScope.Lookup(((NamedTypeSyntax)traitSyntax).Name.Text) is not TraitSymbol trait)
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
                        _diagnostics.ReportTraitSignatureMismatch(implementation.Declaration!.Name.Span, required.Name, trait, Signature(required));
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
        var parameters = string.Join(", ", ["self", .. method.Parameters.Select(p => $"{p.Name}: {p.Type}")]);
        var result = method.ReturnType == TypeSymbol.Unit ? "" : $" -> {method.ReturnType}";
        return $"fn {method.Name}({parameters}){result}";
    }

    // Functions and methods

    /// <param name="containingType">The trait, record/enum definition or builtin a method belongs to; null for a top-level function.</param>
    /// <param name="selfType">The type of <c>self</c> inside the method.</param>
    private FunctionInfo BindFunctionSignature(
        FunctionDeclarationSyntax declaration,
        Scope outer,
        TypeSymbol? containingType,
        TypeSymbol? selfType)
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

        var returnType = declaration.ReturnType is null ? TypeSymbol.Unit : BindType(declaration.ReturnType);
        _scope = outerScope;

        if (containingType is null)
        {
            if (declaration.Self is { } self)
                _diagnostics.ReportSelfOutsideMethod(self.Span);
            if (!declaration.HasBody)
                _diagnostics.ReportMissingBody(declaration.Name.Span, declaration.Name.Text);
            var function = new FunctionSymbol(declaration.Name.Text, typeParameters, parameters.ToImmutable(), returnType, declaration);
            return new FunctionInfo(function, declaration, scope);
        }

        var selfVariable = declaration.Self is null
            ? null
            : new VariableSymbol("self", selfType!, isMutable: false, VariableKind.Self);
        var method = new MethodSymbol(
            declaration.Name.Text, typeParameters, parameters.ToImmutable(), returnType, declaration, containingType, selfVariable);
        return new FunctionInfo(method, declaration, scope);
    }

    private BoundFunction? BindFunctionBody(FunctionInfo info)
    {
        var (function, declaration, signatureScope) = info;
        if (!declaration.HasBody)
            return null;

        _function = function;
        _loopDepth = 0;
        _scope = new Scope(signatureScope);
        if (function is MethodSymbol { Self: { } self })
            _scope.TryDeclare(self);
        foreach (var parameter in function.Parameters)
            _scope.TryDeclare(parameter);

        var expected = function.ReturnType == TypeSymbol.Unit ? null : function.ReturnType;
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

    // Types

    private TypeSymbol BindType(TypeSyntax syntax)
    {
        var named = (NamedTypeSyntax)syntax;
        var name = named.Name;
        if (name.IsMissing)
            return TypeSymbol.Error;

        var symbol = _scope.Lookup(name.Text);
        var arguments = named.TypeArguments.Select(a => BindValueType(a, "type argument")).ToImmutableArray();

        switch (symbol)
        {
            case NamedTypeDefinition definition:
                if (definition.TypeParameters.Length != arguments.Length)
                {
                    if (definition.IsGeneric)
                        _diagnostics.ReportTypeArgumentCount(syntax.Span, definition.Name, definition.TypeParameters.Length, arguments.Length);
                    else
                        _diagnostics.ReportNotGeneric(syntax.Span, definition.Name);
                    return TypeSymbol.Error;
                }
                if (arguments.Contains(TypeSymbol.Error))
                    return TypeSymbol.Error;
                CheckConstraints(definition.TypeParameters, arguments, syntax.Span);
                return definition.Construct(arguments);

            case TypeSymbol type when type != TypeSymbol.Never && type != TypeSymbol.Error:
                if (!arguments.IsEmpty)
                {
                    _diagnostics.ReportNotGeneric(syntax.Span, type.Name);
                    return TypeSymbol.Error;
                }
                return type;

            default:
                _diagnostics.ReportUndefinedType(name.Span, name.Text);
                return TypeSymbol.Error;
        }
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

        if (function.ReturnType == TypeSymbol.Unit)
        {
            if (BindExpression(syntax.Value).Type != TypeSymbol.Error)
                _diagnostics.ReportUnexpectedReturnValue(syntax.Value.Span, function.Name);
            return new BoundReturnStatement(null);
        }

        var value = BindExpression(syntax.Value, function.ReturnType);
        value = Convert(RequireValue(value, syntax.Value.Span), function.ReturnType, syntax.Value.Span);
        return new BoundReturnStatement(value);
    }

    private BoundStatement BindLoopJump(Token keyword, BoundStatement statement)
    {
        if (_loopDepth == 0)
            _diagnostics.ReportOutsideLoop(keyword.Span, keyword.Text);
        return statement;
    }
}
