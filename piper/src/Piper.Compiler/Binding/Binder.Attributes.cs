using System.Collections.Immutable;
using System.Reflection;
using Piper.Syntax;

namespace Piper.Binding;

public sealed partial class Binder
{
    /// <summary>
    /// Binds the attributes of every type, field, function, method and parameter. They run after all signatures are
    /// bound, since their arguments can name any type (<c>AttributeTargets.Method</c>), and before bodies, which
    /// report uses of <c>[Obsolete]</c> declarations.
    /// </summary>
    private void BindAttributes(IEnumerable<TypeSymbol> types, IEnumerable<FunctionInfo> functions)
    {
        foreach (var type in types)
        {
            var (declaration, fields) = type switch
            {
                RecordSymbol record => ((DeclarationSyntax?)record.Declaration, record.Declaration.Fields.Select(f => (record.Fields, f))),
                EnumSymbol { Declaration: { } syntax } @enum => (syntax, syntax.Variants.Zip(@enum.Variants, (v, symbol) => (v, symbol))
                    .SelectMany(pair => pair.v.Fields.Select(f => (pair.symbol.Fields, f)))),
                TraitSymbol trait => (trait.Declaration, []),
                ClassSymbol @class => (@class.Declaration, []),
                _ => (null, []),
            };
            if (declaration is null)
                continue;
            InFile(FileOf(type));
            type.Attributes = BindAttributeList(declaration.Attributes, type is TraitSymbol ? AttributeTargets.Interface : AttributeTargets.Class, "a type");
            if (type is ClassSymbol classType)
                BindClassMemberAttributes(classType);
            foreach (var (symbols, syntax) in fields)
            {
                if (symbols.FirstOrDefault(f => f.Name == syntax.Name.Text) is { } field)
                    field.Attributes = BindAttributeList(syntax.Attributes, AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter, "a field");
            }

            // Required trait methods have no body, so they aren't among the functions below.
            if (type is TraitSymbol required)
            {
                foreach (var syntax in required.Declaration.Methods.Where(m => !m.HasBody))
                {
                    if (required.Methods.FirstOrDefault(m => m.Declaration == syntax) is { } method)
                        BindFunctionAttributes(method, syntax);
                }
            }
        }

        foreach (var function in functions)
        {
            InFile(function.File);
            BindFunctionAttributes(function.Symbol, function.Syntax);
        }
    }

    /// <summary>
    /// A class's field, property and indexer attributes. A public field or an automatic property is a C# property with a
    /// backing field, so an attribute goes on the property if it applies to properties, otherwise on the field
    /// (<c>[field: …]</c>); a property with accessors has no field; a private or protected field is a C# field.
    /// </summary>
    private void BindClassMemberAttributes(ClassSymbol @class)
    {
        foreach (var syntax in @class.Declaration.Fields.Where(f => !f.Attributes.IsEmpty))
        {
            if (syntax.IsIndexer)
            {
                if (@class.Indexers.FirstOrDefault(i => ReferenceEquals(i.Getter.Declaration!.Parameters.FirstOrDefault(), syntax.Parameters.FirstOrDefault())) is { } indexer)
                    indexer.Attributes = BindAttributeList(syntax.Attributes, AttributeTargets.Property, "an indexer");
                continue;
            }
            if (@class.Fields.Concat(@class.StaticFields).FirstOrDefault(f => f.Name == syntax.Name.Text) is not { } field)
                continue;
            var (targets, what) = field switch
            {
                { IsProperty: false, IsPublic: false } => (AttributeTargets.Field, "a field"),
                { IsProperty: true, IsAutomatic: false } => (AttributeTargets.Property, "a property"),
                _ => (AttributeTargets.Property | AttributeTargets.Field, field.IsProperty ? "a property" : "a field"),
            };
            field.Attributes = BindAttributeList(syntax.Attributes, targets, what);
        }
    }

    private void BindFunctionAttributes(FunctionSymbol function, FunctionDeclarationSyntax syntax)
    {
        function.Attributes = BindAttributeList(syntax.Attributes, AttributeTargets.Method, "a function");
        foreach (var (parameter, parameterSyntax) in function.Parameters.Zip(syntax.Parameters))
            parameter.Attributes = BindAttributeList(parameterSyntax.Attributes, AttributeTargets.Parameter, "a parameter");
    }

    /// <param name="targets">
    /// Where the attributes go in C#. A field allows several: its attribute goes on the record's property if it applies to
    /// properties, otherwise its backing field, otherwise its constructor parameter.
    /// </param>
    private ImmutableArray<BoundAttribute> BindAttributeList(ImmutableArray<AttributeSyntax> syntax, AttributeTargets targets, string what)
    {
        var attributes = ImmutableArray.CreateBuilder<BoundAttribute>();
        foreach (var attribute in syntax)
        {
            if (BindAttribute(attribute) is not { } bound)
                continue;
            var type = ClrTypes.ToClr(bound.Type)!;
            var usage = type.GetCustomAttribute<AttributeUsageAttribute>(inherit: true) ?? new AttributeUsageAttribute(AttributeTargets.All);
            var target = (usage.ValidOn & targets) switch
            {
                0 => (AttributeTargets?)null,
                var valid when targets.HasFlag(AttributeTargets.Property) && valid.HasFlag(AttributeTargets.Property) => AttributeTargets.Property,
                var valid when targets.HasFlag(AttributeTargets.Field) && valid.HasFlag(AttributeTargets.Field) => AttributeTargets.Field,
                var valid => valid,
            };
            if (target is null)
            {
                _diagnostics.ReportAttributeTarget(attribute.Name.Span, bound.Type.Name, what, usage.ValidOn);
                continue;
            }
            if (!usage.AllowMultiple && attributes.Any(a => a.Type == bound.Type))
            {
                _diagnostics.ReportDuplicateAttribute(attribute.Name.Span, bound.Type.Name);
                continue;
            }
            attributes.Add(target switch
            {
                AttributeTargets.Property when targets.HasFlag(AttributeTargets.Parameter) => bound with { Target = "property" },
                // On a C# property, an attribute for its backing field says so.
                AttributeTargets.Field when targets.HasFlag(AttributeTargets.Property) => bound with { Target = "field" },
                _ => bound,
            });
        }
        return attributes.ToImmutable();
    }

    private BoundAttribute? BindAttribute(AttributeSyntax syntax)
    {
        if (syntax.Name.IsMissing)
            return null;
        if (ResolveAttributeType(syntax.Name) is not { } type)
        {
            _diagnostics.ReportUnknownAttribute(syntax.Name.Span, syntax.Name.Text);
            BindArgumentsForDiagnostics(syntax.Arguments);
            return null;
        }
        var clr = ClrTypes.ToClr(type)!;

        // `[A(1, Name = 2)]`: constructor arguments, then property and field assignments.
        static bool IsNamed(ExpressionSyntax argument) =>
            argument is AssignmentExpressionSyntax { Target: NameExpressionSyntax, Operator.Kind: TokenKind.Equals };
        int firstNamed = syntax.Arguments.IndexOf(syntax.Arguments.FirstOrDefault(IsNamed)!);
        if (firstNamed < 0)
            firstNamed = syntax.Arguments.Length;
        var positional = syntax.Arguments[..firstNamed];
        foreach (var misplaced in syntax.Arguments[firstNamed..].Where(a => !IsNamed(a)))
            _diagnostics.ReportAttributeArgument(misplaced.Span, "Constructor arguments must come before 'Name = value' assignments.");

        var constructors = clr.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        if (ResolveClrOverload(constructors, type, type.Name, positional, [], syntax.Span, new Dictionary<Type, TypeSymbol>()) is not { } resolved)
            return null;
        if (!resolved.Names.IsEmpty)
            _diagnostics.ReportNamedArgument(syntax.Span, "An attribute's constructor arguments can't be named; set its properties with 'Name = value'.");
        foreach (var argument in resolved.Arguments.Where(a => !IsAttributeConstant(a)))
            _diagnostics.ReportAttributeArgument(argument.SourceSpan ?? syntax.Span, "Attribute arguments must be constants: literals, .NET enum members or constant fields.");

        var named = ImmutableArray.CreateBuilder<(string, BoundExpression)>();
        foreach (var assignment in syntax.Arguments[firstNamed..].OfType<AssignmentExpressionSyntax>().Where(IsNamed))
        {
            var name = ((NameExpressionSyntax)assignment.Target).Identifier;
            Type? memberType = clr.GetMember(name.Text, BindingFlags.Public | BindingFlags.Instance).FirstOrDefault() switch
            {
                PropertyInfo { SetMethod.IsPublic: true } property => property.PropertyType,
                FieldInfo { IsInitOnly: false, IsLiteral: false } field => field.FieldType,
                _ => null,
            };
            if (memberType is null || ClrTypes.FromClr(memberType, new Dictionary<Type, TypeSymbol>(), _option) is not { } valueType)
            {
                _diagnostics.ReportUnknownMember(name.Span, type, name.Text);
                BindExpression(assignment.Value);
                continue;
            }
            var value = Convert(RequireValue(BindExpression(assignment.Value, valueType), assignment.Value.Span), valueType, assignment.Value.Span);
            if (!IsAttributeConstant(value))
                _diagnostics.ReportAttributeArgument(assignment.Value.Span, "Attribute arguments must be constants: literals, .NET enum members or constant fields.");
            named.Add((name.Text, value));
        }
        return new BoundAttribute(type, resolved.Arguments, named.ToImmutable());
    }

    /// <summary>
    /// The attribute type <paramref name="name"/> means: <c>Obsolete</c> is <c>ObsoleteAttribute</c> (tried first) or
    /// <c>Obsolete</c>, as in C#. It must be a non-abstract .NET type deriving from <see cref="Attribute"/>.
    /// </summary>
    private TypeSymbol? ResolveAttributeType(QualifiedNameSyntax name)
    {
        var last = name.Parts[^1];
        foreach (var candidate in new[] { last.Text + "Attribute", last.Text })
        {
            Symbol? symbol;
            if (name.Parts.Length == 1)
            {
                symbol = _scope.Lookup(candidate);
            }
            else
            {
                var container = _scope.Lookup(name.Parts[0].Text) as NamespaceSymbol;
                foreach (var part in name.Parts[1..^1])
                    container = container is null ? null : MemberOf(container, part.Text) as NamespaceSymbol;
                symbol = container is null ? null : MemberOf(container, candidate);
            }
            var type = symbol switch
            {
                ClrTypeGroupSymbol group => NonGenericMember(group),
                TypeSymbol t => t,
                _ => null,
            };
            if (type is not null && ClrTypes.ToClr(type) is { IsAbstract: false, ContainsGenericParameters: false } clr && typeof(Attribute).IsAssignableFrom(clr))
            {
                Record(last, type);
                return type;
            }
        }
        return null;
    }

    /// <summary>What C# accepts as an attribute argument: a literal, an enum member or const field, or an operator on those.</summary>
    private static bool IsAttributeConstant(BoundExpression expression) => expression switch
    {
        BoundLiteralExpression => true,
        BoundClrMemberExpression { Receiver: null, Member: FieldInfo { IsLiteral: true } } => true,
        BoundConversionExpression conversion => IsAttributeConstant(conversion.Expression),
        BoundUnaryExpression unary => IsAttributeConstant(unary.Operand),
        BoundBinaryExpression binary => IsAttributeConstant(binary.Left) && IsAttributeConstant(binary.Right),
        _ => false,
    };

    private readonly HashSet<(Piper.Text.SourceText, int)> _obsoleteReported = [];

    /// <summary>Warns about a use of a declaration marked <c>[Obsolete]</c> (an error with <c>[Obsolete("…", true)]</c>), as C# does.</summary>
    private void CheckObsolete(Token name, Symbol symbol)
    {
        // A .NET type: Piper's own types (and Option and Result) carry their attributes as symbols do.
        if (symbol is TypeSymbol type && type.OriginalDefinition is not GenericTypeDefinition && ClrTypes.ToClr(type) is { } clr)
        {
            CheckClrObsolete(name.Span, clr);
            return;
        }
        if (symbol.Attributes.FirstOrDefault(a => ClrTypes.ToClr(a.Type) == typeof(ObsoleteAttribute)) is not { } obsolete ||
            !_obsoleteReported.Add((_file.Source, name.Span.Start)))
        {
            return;
        }
        var message = obsolete.Arguments.FirstOrDefault() is BoundLiteralExpression { Value: string text } ? text : null;
        bool isError = obsolete.Arguments.Length > 1 && obsolete.Arguments[1] is BoundLiteralExpression { Value: true };
        _diagnostics.ReportObsolete(name.Span, symbol.Name, message, isError);
    }

    /// <summary>
    /// The same for a .NET type, method, constructor, property or field. Otherwise C# would report it in the generated
    /// code: a warning nobody sees, or for an error an internal compiler error.
    /// </summary>
    private void CheckClrObsolete(Piper.Text.TextSpan span, MemberInfo member)
    {
        if (member.GetCustomAttribute<ObsoleteAttribute>(inherit: false) is not { } obsolete ||
            RequiredMembers.IsObsoleteOnlyForOlderCompilers(member) || !_obsoleteReported.Add((_file.Source, span.Start)))
            return;
        var name = member switch
        {
            Type type => type.Name.Split('`')[0],
            ConstructorInfo constructor => constructor.DeclaringType!.Name.Split('`')[0],
            _ => $"{member.DeclaringType?.Name.Split('`')[0]}.{member.Name}",
        };
        _diagnostics.ReportObsolete(span, name, obsolete.Message, obsolete.IsError);
    }
}
