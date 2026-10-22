using System.Collections.Immutable;
using Piper.Syntax;
using Piper.Text;

namespace Piper.Binding;

/// <summary>
/// A type. Builtins are singletons; user types are per-compilation symbols. Types are compared by
/// reference: generic instantiations are interned by <see cref="NamedTypeDefinition.Construct"/>, so
/// <c>Option&lt;Int&gt;</c> is always the same object.
/// </summary>
public class TypeSymbol : Symbol
{
    public static readonly TypeSymbol Int = new("Int");
    public static readonly TypeSymbol Long = new("Long");
    public static readonly TypeSymbol Double = new("Double");
    public static readonly TypeSymbol Bool = new("Bool");
    public static readonly TypeSymbol String = new("String");
    public static readonly TypeSymbol Char = new("Char");

    /// <summary>The type of expressions evaluated only for their effect; emitted as C# <c>void</c>.</summary>
    public static readonly TypeSymbol Unit = new("Unit");

    /// <summary>
    /// The type of a block that never completes normally (it always returns, breaks or continues).
    /// Never converts to every type, so <c>let x = if c { 1 } else { return; };</c> type-checks. Not nameable in source.
    /// </summary>
    public static readonly TypeSymbol Never = new("Never");

    /// <summary>Produced after an error has been reported; converts to anything so errors don't cascade.</summary>
    public static readonly TypeSymbol Error = new("?");

    public static readonly ImmutableArray<TypeSymbol> Builtins = [Int, Long, Double, Bool, String, Char, Unit];

    protected TypeSymbol(string name) : base(name)
    {
    }

    public bool IsNumeric => this == Int || this == Long || this == Double;

    public bool IsIntegral => this == Int || this == Long;

    /// <summary>False for types that cannot be stored in a variable, field or parameter.</summary>
    public bool IsValueType => this != Unit && this != Never;

    /// <summary>For <c>Option&lt;Int&gt;</c>, the generic definition <c>Option</c>; otherwise the type itself.</summary>
    public virtual TypeSymbol OriginalDefinition => this;

    public virtual ImmutableArray<TypeSymbol> TypeArguments => [];

    /// <summary>An instantiation of the built-in <c>Option&lt;T&gt;</c> (written <c>T?</c>).</summary>
    public bool IsOption => OriginalDefinition is EnumSymbol { BuiltinKind: BuiltinEnumKind.Option };

    /// <summary>An instantiation of the built-in <c>Result&lt;T, E&gt;</c>.</summary>
    public bool IsResult => OriginalDefinition is EnumSymbol { BuiltinKind: BuiltinEnumKind.Result };

    public override string ToString() => Name;
}

/// <summary>A type parameter such as <c>T</c> in <c>fn id&lt;T&gt;(x: T) -> T</c>.</summary>
public sealed class TypeParameterSymbol(string name) : TypeSymbol(name)
{
    public ImmutableArray<TraitSymbol> Constraints { get; internal set; } = [];
}

/// <summary>A user-declared record or enum: possibly generic, possibly extended with methods and traits.</summary>
public abstract class NamedTypeDefinition : TypeSymbol
{
    private readonly List<ConstructedTypeSymbol> _instantiations = [];
    private readonly List<MethodSymbol> _methods = [];

    protected NamedTypeDefinition(string name) : base(name)
    {
    }

    public ImmutableArray<TypeParameterSymbol> TypeParameters { get; internal set; } = [];

    public bool IsGeneric => TypeParameters.Length > 0;

    /// <summary>Traits this type implements, declared with <c>extend Type: Trait { … }</c>.</summary>
    public ImmutableArray<TraitSymbol> Traits => [.. Conformances.Select(c => c.Trait)];

    /// <summary>Each implemented trait with the file whose extend block declared it (it may not be this type's file).</summary>
    public ImmutableArray<(TraitSymbol Trait, SourceText? File)> Conformances { get; internal set; } = [];

    /// <summary>Methods declared in <c>extend</c> blocks.</summary>
    public IReadOnlyList<MethodSymbol> Methods => _methods;

    internal void AddMethod(MethodSymbol method) => _methods.Add(method);

    /// <summary>The type as seen from inside its own declaration: <c>Option&lt;T&gt;</c> for a generic <c>Option</c>.</summary>
    public TypeSymbol SelfType => IsGeneric ? Construct([.. TypeParameters]) : this;

    /// <summary>Returns the (interned) instantiation of this definition with <paramref name="arguments"/>.</summary>
    public TypeSymbol Construct(ImmutableArray<TypeSymbol> arguments)
    {
        if (!IsGeneric)
            return this;

        var existing = _instantiations.FirstOrDefault(c =>
            c.TypeArguments.Length == arguments.Length &&
            c.TypeArguments.Zip(arguments).All(pair => ReferenceEquals(pair.First, pair.Second)));
        if (existing is not null)
            return existing;

        var constructed = new ConstructedTypeSymbol(this, arguments);
        _instantiations.Add(constructed);
        return constructed;
    }
}

public sealed class RecordSymbol : NamedTypeDefinition
{
    public RecordSymbol(RecordDeclarationSyntax declaration) : base(declaration.Name.Text)
    {
        Declaration = declaration;
    }

    public RecordDeclarationSyntax Declaration { get; }

    /// <summary>Field types are written in terms of the record's own type parameters; see <see cref="TypeSubstitution"/>.</summary>
    public ImmutableArray<FieldSymbol> Fields { get; internal set; } = [];
}

public enum BuiltinEnumKind
{
    None,
    Option,
    Result,
}

/// <summary>A sum type: a value is exactly one of its <see cref="Variants"/>.</summary>
public sealed class EnumSymbol : NamedTypeDefinition
{
    public EnumSymbol(EnumDeclarationSyntax declaration) : base(declaration.Name.Text)
    {
        Declaration = declaration;
    }

    private EnumSymbol(string name, BuiltinEnumKind kind) : base(name)
    {
        BuiltinKind = kind;
    }

    /// <summary>Null for the built-in <c>Option</c> and <c>Result</c>.</summary>
    public EnumDeclarationSyntax? Declaration { get; }

    public BuiltinEnumKind BuiltinKind { get; }

    /// <summary>
    /// Creates the built-in <c>enum Option&lt;T&gt; { Some(value: T), None }</c> and
    /// <c>enum Result&lt;T, E&gt; { Ok(value: T), Err(error: E) }</c>. They are created per compilation
    /// because extend blocks add methods to them.
    /// </summary>
    public static (EnumSymbol Option, EnumSymbol Result) CreateBuiltins()
    {
        var t = new TypeParameterSymbol("T");
        var option = new EnumSymbol("Option", BuiltinEnumKind.Option) { TypeParameters = [t] };
        option.Variants =
        [
            new VariantSymbol("Some", option) { Fields = [new FieldSymbol("value", t)] },
            new VariantSymbol("None", option),
        ];

        var value = new TypeParameterSymbol("T");
        var error = new TypeParameterSymbol("E");
        var result = new EnumSymbol("Result", BuiltinEnumKind.Result) { TypeParameters = [value, error] };
        result.Variants =
        [
            new VariantSymbol("Ok", result) { Fields = [new FieldSymbol("value", value)] },
            new VariantSymbol("Err", result) { Fields = [new FieldSymbol("error", error)] },
        ];

        return (option, result);
    }

    public ImmutableArray<VariantSymbol> Variants { get; internal set; } = [];

    public VariantSymbol? FindVariant(string name) => Variants.FirstOrDefault(v => v.Name == name);
}

/// <summary>One case of an enum. Variants are not types themselves: <c>Shape.Circle(1.0)</c> has type <c>Shape</c>.</summary>
public sealed class VariantSymbol(string name, EnumSymbol @enum) : Symbol(name)
{
    public EnumSymbol Enum { get; } = @enum;

    public ImmutableArray<FieldSymbol> Fields { get; internal set; } = [];

    public string QualifiedName => $"{Enum.Name}.{Name}";
}

/// <summary>A generic record or enum applied to type arguments, e.g. <c>Option&lt;Int&gt;</c>.</summary>
public sealed class ConstructedTypeSymbol(NamedTypeDefinition definition, ImmutableArray<TypeSymbol> arguments)
    : TypeSymbol(definition is EnumSymbol { BuiltinKind: BuiltinEnumKind.Option } ? $"{arguments[0]}?" : $"{definition.Name}<{string.Join(", ", arguments)}>")
{
    public NamedTypeDefinition Definition { get; } = definition;

    public override TypeSymbol OriginalDefinition => Definition;

    public override ImmutableArray<TypeSymbol> TypeArguments { get; } = arguments;
}

/// <summary>An interface-like set of methods. A trait is also a type: a value of any type that implements it.</summary>
public sealed class TraitSymbol : TypeSymbol
{
    public TraitSymbol(TraitDeclarationSyntax declaration) : base(declaration.Name.Text)
    {
        Declaration = declaration;
    }

    public TraitDeclarationSyntax Declaration { get; }

    public ImmutableArray<MethodSymbol> Methods { get; internal set; } = [];
}

public static class TypeSubstitution
{
    /// <summary>Replaces type parameters in <paramref name="type"/> according to <paramref name="map"/>.</summary>
    public static TypeSymbol Substitute(TypeSymbol type, IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol> map)
    {
        if (map.Count == 0)
            return type;
        return type switch
        {
            TypeParameterSymbol parameter => map.TryGetValue(parameter, out var replacement) ? replacement : parameter,
            ConstructedTypeSymbol constructed => constructed.Definition.Construct([.. constructed.TypeArguments.Select(a => Substitute(a, map))]),
            _ => type,
        };
    }

    /// <summary>For <c>Option&lt;Int&gt;</c> the map <c>T → Int</c>; empty for non-generic types.</summary>
    public static Dictionary<TypeParameterSymbol, TypeSymbol> MapFor(TypeSymbol type)
    {
        var map = new Dictionary<TypeParameterSymbol, TypeSymbol>();
        if (type.OriginalDefinition is NamedTypeDefinition { IsGeneric: true } definition && type != definition)
        {
            for (int i = 0; i < definition.TypeParameters.Length; i++)
                map[definition.TypeParameters[i]] = type.TypeArguments[i];
        }
        return map;
    }

    /// <summary>The type of <paramref name="field"/> when its record or enum is instantiated as <paramref name="owner"/>.</summary>
    public static TypeSymbol FieldType(FieldSymbol field, TypeSymbol owner) => Substitute(field.Type, MapFor(owner));

    public static bool Mentions(TypeSymbol type, IReadOnlySet<TypeParameterSymbol> parameters) => type switch
    {
        TypeParameterSymbol parameter => parameters.Contains(parameter),
        ConstructedTypeSymbol constructed => constructed.TypeArguments.Any(a => Mentions(a, parameters)),
        _ => false,
    };
}
