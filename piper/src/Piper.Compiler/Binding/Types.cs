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
    public static readonly TypeSymbol Int = new("int");
    public static readonly TypeSymbol Long = new("long");
    public static readonly TypeSymbol Double = new("double");
    public static readonly TypeSymbol Bool = new("bool");
    public static readonly TypeSymbol String = new("string");
    public static readonly TypeSymbol Char = new("char");

    // The other C# numeric types: byte, sbyte, short, ushort, uint, ulong, float and decimal.
    public static readonly TypeSymbol Byte = new("byte");
    public static readonly TypeSymbol SByte = new("sbyte");
    public static readonly TypeSymbol Short = new("short");
    public static readonly TypeSymbol UShort = new("ushort");
    public static readonly TypeSymbol UInt = new("uint");
    public static readonly TypeSymbol ULong = new("ulong");
    public static readonly TypeSymbol Float = new("float");
    public static readonly TypeSymbol Decimal = new("decimal");

    /// <summary>The type of expressions evaluated only for their effect; emitted as C# <c>void</c>.</summary>
    public static readonly TypeSymbol Unit = new("unit");

    /// <summary>
    /// The type of a block that never completes normally (it always returns, breaks or continues).
    /// Never converts to every type, so <c>let x = if c { 1 } else { return; };</c> type-checks. Not nameable in source.
    /// </summary>
    public static readonly TypeSymbol Never = new("Never");

    /// <summary>Produced after an error has been reported; converts to anything so errors don't cascade.</summary>
    public static readonly TypeSymbol Error = new("?");

    public static readonly ImmutableArray<TypeSymbol> Builtins =
        [Int, Long, Double, Bool, String, Char, Unit, Byte, SByte, Short, UShort, UInt, ULong, Float, Decimal];

    protected TypeSymbol(string name) : base(name)
    {
    }

    public bool IsNumeric => IsIntegral || this == Double || this == Float || this == Decimal;

    public bool IsIntegral =>
        this == Int || this == Long || this == Byte || this == SByte || this == Short || this == UShort || this == UInt || this == ULong;

    /// <summary>False for types that cannot be stored in a variable, field or parameter.</summary>
    public bool IsValueType => this != Unit && this != Never;

    /// <summary>For <c>Option&lt;Int&gt;</c>, the generic definition <c>Option</c>; otherwise the type itself.</summary>
    public virtual TypeSymbol OriginalDefinition => this;

    public virtual ImmutableArray<TypeSymbol> TypeArguments => [];

    /// <summary>An instantiation of the built-in <c>Option&lt;T&gt;</c> (written <c>T?</c>).</summary>
    public bool IsOption => OriginalDefinition is EnumSymbol { BuiltinKind: BuiltinEnumKind.Option };

    /// <summary>An instantiation of the built-in <c>Result&lt;T, E&gt;</c>.</summary>
    public bool IsResult => OriginalDefinition is EnumSymbol { BuiltinKind: BuiltinEnumKind.Result };

    /// <summary>The trait this type is (or is an instantiation of, for <c>Convert&lt;string&gt;</c>), if any.</summary>
    public TraitSymbol? TraitDefinition => OriginalDefinition as TraitSymbol;

    public bool IsTrait => TraitDefinition is not null;

    public override string ToString() => Name;
}

/// <summary>A type parameter such as <c>T</c> in <c>fn id&lt;T&gt;(x: T) =&gt; T</c>.</summary>
public sealed class TypeParameterSymbol(string name) : TypeSymbol(name)
{
    /// <summary>The traits it must implement: <c>Shape</c>, or an instantiation such as <c>Comparable&lt;T&gt;</c>.</summary>
    public ImmutableArray<TypeSymbol> Constraints { get; internal set; } = [];
}

/// <summary>A user-declared record, enum or trait, possibly generic.</summary>
public abstract class GenericTypeDefinition : TypeSymbol
{
    private readonly List<ConstructedTypeSymbol> _instantiations = [];

    protected GenericTypeDefinition(string name) : base(name)
    {
    }

    public ImmutableArray<TypeParameterSymbol> TypeParameters { get; internal set; } = [];

    public bool IsGeneric => TypeParameters.Length > 0;

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

/// <summary>A user-declared record or enum: possibly generic, possibly extended with methods and traits.</summary>
public abstract class NamedTypeDefinition : GenericTypeDefinition
{
    private readonly List<MethodSymbol> _methods = [];

    protected NamedTypeDefinition(string name) : base(name)
    {
    }

    /// <summary>Option and Result: defined by the Piper.Runtime assembly rather than emitted with the program.</summary>
    public bool IsRuntimeType => this is EnumSymbol { BuiltinKind: not BuiltinEnumKind.None };

    /// <summary>
    /// Traits this type implements, declared with <c>extend Type: Trait { … }</c>: a trait, or an instantiation such as
    /// <c>Convert&lt;string&gt;</c>, written in terms of this type's own type parameters.
    /// </summary>
    public ImmutableArray<TypeSymbol> Traits => [.. Conformances.Select(c => c.Trait)];

    /// <summary>Each implemented trait with the file whose extend block declared it (it may not be this type's file).</summary>
    public ImmutableArray<(TypeSymbol Trait, SourceText? File)> Conformances { get; internal set; } = [];

    /// <summary>Methods declared in <c>extend</c> blocks.</summary>
    public IReadOnlyList<MethodSymbol> Methods => _methods;

    internal void AddMethod(MethodSymbol method) => _methods.Add(method);

    /// <summary>Methods implementing .NET interface methods whose C# signatures differ from theirs by nullability.</summary>
    public List<ClrInterfaceBridge> InterfaceBridges { get; } = [];

    /// <summary>
    /// The method implementing <c>IComparable&lt;T&gt;</c> for the type itself, when it does: the type then has
    /// <c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c> and <c>&gt;=</c>, emitted as C# operators that call it.
    /// </summary>
    public MethodSymbol? CompareTo { get; internal set; }
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

    /// <summary>Its `requires` conditions, checked when it's constructed, over <see cref="InvariantFields"/>.</summary>
    public ImmutableArray<BoundContract> Invariants { get; internal set; } = [];

    /// <summary>The fields as the variables the invariants are written in terms of (the constructor's parameters).</summary>
    public ImmutableArray<VariableSymbol> InvariantFields { get; internal set; } = [];
}

/// <summary>
/// A class: a mutable reference type with identity. Its primary constructor's <see cref="Parameters"/> are usable in
/// its field initializers, its base's arguments and its methods; its <see cref="Fields"/> are private unless public;
/// its methods (in <see cref="NamedTypeDefinition.Methods"/>) public unless private. It can derive from a Piper or .NET
/// class (<see cref="Base"/>) and implement traits and .NET interfaces.
/// </summary>
public sealed class ClassSymbol : NamedTypeDefinition
{
    public ClassSymbol(ClassDeclarationSyntax declaration) : base(declaration.Name.Text)
    {
        Declaration = declaration;
    }

    public ClassDeclarationSyntax Declaration { get; }

    public bool IsAbstract => Declaration.Abstract is not null;

    public bool IsSealed => Declaration.Sealed is not null;

    /// <summary>The primary constructor's parameters.</summary>
    public ImmutableArray<VariableSymbol> Parameters { get; internal set; } = [];

    public ImmutableArray<FieldSymbol> Fields { get; internal set; } = [];

    /// <summary>Its `requires` conditions, over the primary constructor's <see cref="Parameters"/>, checked when one is made.</summary>
    public ImmutableArray<BoundContract> Invariants { get; internal set; } = [];

    /// <summary>Its properties implementing .NET interface properties that may be null, converting between Option and null.</summary>
    public List<ClrPropertyBridge> PropertyBridges { get; } = [];

    /// <summary>Its indexers' implementations of .NET interface indexers that may take or give null (see <see cref="ClrIndexerBridge"/>).</summary>
    public List<ClrIndexerBridge> IndexerBridges { get; } = [];

    /// <summary>Its constructors besides the primary one, which all call it first (<c>: this(…)</c>).</summary>
    public ImmutableArray<ClassConstructor> Constructors { get; internal set; } = [];

    /// <summary>Its indexers (<c>prop this[i: int]: T</c>), which may be overloaded by their parameters' types.</summary>
    public ImmutableArray<IndexerSymbol> Indexers { get; internal set; } = [];

    /// <summary>Its `static` fields, in the order they're declared (and initialized).</summary>
    public ImmutableArray<FieldSymbol> StaticFields { get; internal set; } = [];

    /// <summary>The class it derives from: a <see cref="ClassSymbol"/>, a .NET class, or null for <c>object</c>.</summary>
    public TypeSymbol? Base { get; internal set; }

    /// <summary>The base constructor's arguments, converted to its parameters' types.</summary>
    public ImmutableArray<BoundExpression> BaseArguments { get; internal set; } = [];

    /// <summary>The constructor of a .NET <see cref="Base"/> it calls, if known.</summary>
    public System.Reflection.ConstructorInfo? ClrBaseConstructor { get; internal set; }

    /// <summary>
    /// Whether the .NET constructor its bases end up calling sets the .NET <c>required</c> members it inherits
    /// (<c>[SetsRequiredMembers]</c>): then so do its own constructors, which C# needs marked too, and making one needs
    /// none of them set.
    /// </summary>
    public bool SetsRequiredMembers => Base?.OriginalDefinition is ClassSymbol piperBase
        ? piperBase.SetsRequiredMembers
        : ClrBaseConstructor is { } constructor && RequiredMembers.AreSetBy(constructor);

    /// <summary>The .NET interfaces it lists (its traits are in <see cref="NamedTypeDefinition.Conformances"/>).</summary>
    public ImmutableArray<TypeSymbol> Interfaces { get; internal set; } = [];

    /// <summary>Where its methods are bound: the fields and primary constructor parameters, by bare name.</summary>
    internal Scope? MemberScope { get; set; }

    /// <summary>Where its static methods are bound: its members by name, so instance ones are reported as needing an instance.</summary>
    internal Scope? StaticScope { get; set; }

    /// <summary>
    /// Itself and its Piper base classes' definitions, nearest first (<c>Repository</c> for a base written
    /// <c>Repository&lt;string&gt;</c>; the binder's <c>SelfAndBaseTypes</c> gives the bases with their type arguments).
    /// </summary>
    public IEnumerable<ClassSymbol> SelfAndBases()
    {
        for (ClassSymbol? type = this; type is not null; type = type.Base?.OriginalDefinition as ClassSymbol)
            yield return type;
    }

    /// <summary>
    /// The .NET class at the root of its bases: <c>Exception</c> for <c>class NotFound: Exception</c>, or <c>object</c>,
    /// which every class derives from (giving it <c>ToString</c>, <c>Equals</c> and <c>GetHashCode</c>).
    /// </summary>
    public ClrTypeSymbol ClrBase => SelfAndBases().Last().Base as ClrTypeSymbol ?? ClrTypeSymbol.For(typeof(object));
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

/// <summary>A generic record, enum or trait applied to type arguments, e.g. <c>Option&lt;Int&gt;</c> or <c>Convert&lt;string&gt;</c>.</summary>
public sealed class ConstructedTypeSymbol(GenericTypeDefinition definition, ImmutableArray<TypeSymbol> arguments)
    : TypeSymbol(definition is EnumSymbol { BuiltinKind: BuiltinEnumKind.Option } ? $"{arguments[0]}?" : $"{definition.Name}<{string.Join(", ", arguments)}>")
{
    public GenericTypeDefinition Definition { get; } = definition;

    public override TypeSymbol OriginalDefinition => Definition;

    public override ImmutableArray<TypeSymbol> TypeArguments { get; } = arguments;
}

/// <summary>
/// <c>(Int, String)</c> or <c>(x: Int, y: Int)</c>: a .NET <c>ValueTuple</c>, as in C#. Element names are part of the
/// type (so <c>t.x</c> works) but, as in C#, not of its identity for conversions: <c>(x: Int, y: Int)</c> and
/// <c>(Int, Int)</c> convert to each other. Interned, so each combination of element types and names is one object.
/// </summary>
public sealed class TupleTypeSymbol : TypeSymbol
{
    private static readonly List<TupleTypeSymbol> Instances = [];

    private TupleTypeSymbol(ImmutableArray<TypeSymbol> elements, ImmutableArray<string?> names)
        : base($"({string.Join(", ", elements.Select((t, i) => names[i] is { } name ? $"{name}: {t}" : t.ToString()))})")
    {
        ElementTypes = elements;
        ElementNames = names;
    }

    public ImmutableArray<TypeSymbol> ElementTypes { get; }

    /// <summary>Null for an unnamed element.</summary>
    public ImmutableArray<string?> ElementNames { get; }

    public static TupleTypeSymbol Create(ImmutableArray<TypeSymbol> elements, ImmutableArray<string?> names = default)
    {
        names = names.IsDefault ? [.. elements.Select(_ => (string?)null)] : names;
        lock (Instances)
        {
            var existing = Instances.FirstOrDefault(t =>
                t.ElementTypes.Length == elements.Length &&
                t.ElementTypes.Zip(elements).All(pair => ReferenceEquals(pair.First, pair.Second)) &&
                t.ElementNames.SequenceEqual(names));
            if (existing is not null)
                return existing;

            var created = new TupleTypeSymbol(elements, names);
            Instances.Add(created);
            return created;
        }
    }

    /// <summary>The same element types without names, the identity C# conversions go by.</summary>
    public TupleTypeSymbol Unnamed => Create(ElementTypes);

    /// <summary>The element called <paramref name="name"/>, or <c>ItemN</c> for any element; -1 if none.</summary>
    public int IndexOf(string name)
    {
        int named = ElementNames.IndexOf(name);
        if (named >= 0)
            return named;
        return name.StartsWith("Item") && int.TryParse(name.AsSpan(4), out int position) && position >= 1 &&
               position <= ElementTypes.Length && name == $"Item{position}"
            ? position - 1
            : -1;
    }
}

/// <summary>An interface-like set of methods. A trait is also a type: a value of any type that implements it.</summary>
public sealed class TraitSymbol : GenericTypeDefinition
{
    public TraitSymbol(TraitDeclarationSyntax declaration) : base(declaration.Name.Text)
    {
        Declaration = declaration;
    }

    public TraitDeclarationSyntax Declaration { get; }

    /// <summary>
    /// The scopes its methods' bodies (and those of `extend` blocks adding to it) are bound in, which name its instance
    /// methods and those it inherits, so a default can call <c>Name()</c> for <c>this.Name()</c>. Filled once its
    /// inheritance is known.
    /// </summary>
    internal List<Scope> MemberScopes { get; } = [];

    /// <summary>The methods it declares itself (not those it inherits, or its overrides of them).</summary>
    public ImmutableArray<MethodSymbol> Methods { get; internal set; } = [];

    /// <summary>Its new bodies for methods it inherits.</summary>
    public ImmutableArray<MethodSymbol> Overrides { get; internal set; } = [];

    /// <summary>The traits it inherits, written in terms of its own type parameters: <c>Named</c>, <c>Equatable&lt;T&gt;</c>.</summary>
    public ImmutableArray<TypeSymbol> Parents { get; internal set; } = [];
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
            TupleTypeSymbol tuple => TupleTypeSymbol.Create([.. tuple.ElementTypes.Select(e => Substitute(e, map))], tuple.ElementNames),
            ClrTypeSymbol { TypeArguments.IsEmpty: false } clr => clr.Definition.Construct([.. clr.TypeArguments.Select(a => Substitute(a, map))]),
            _ => type,
        };
    }

    /// <summary>For <c>Option&lt;Int&gt;</c> the map <c>T → Int</c>; empty for non-generic types.</summary>
    public static Dictionary<TypeParameterSymbol, TypeSymbol> MapFor(TypeSymbol type)
    {
        var map = new Dictionary<TypeParameterSymbol, TypeSymbol>();
        if (type.OriginalDefinition is GenericTypeDefinition { IsGeneric: true } definition && type != definition)
        {
            for (int i = 0; i < definition.TypeParameters.Length; i++)
                map[definition.TypeParameters[i]] = type.TypeArguments[i];
        }
        return map;
    }

    /// <summary>
    /// The type of <paramref name="field"/> when its record, enum or class is instantiated as <paramref name="owner"/>.
    /// A class's field may be reached through a class deriving from it: its <c>T</c> is then what that class passes,
    /// <c>string</c> for an <c>items: List&lt;T&gt;</c> of <c>Repository&lt;T&gt;</c> reached through <c>class Names: Repository&lt;string&gt;</c>.
    /// </summary>
    public static TypeSymbol FieldType(FieldSymbol field, TypeSymbol owner) => Substitute(field.Type, MapFor(AsBase(owner, field.Owner)));

    /// <summary>
    /// The .NET class at the root of a Piper class type's bases, with the type arguments the classes pass down:
    /// <c>List&lt;int&gt;</c> for <c>class Ints: Bag&lt;int&gt;</c> where <c>class Bag&lt;T&gt;: List&lt;T&gt;</c>.
    /// </summary>
    public static ClrTypeSymbol ClrBaseOf(TypeSymbol type)
    {
        var current = type;
        while (current.OriginalDefinition is ClassSymbol { Base: { OriginalDefinition: ClassSymbol } @base })
            current = Substitute(@base, MapFor(current));
        var root = (ClassSymbol)current.OriginalDefinition;
        return root.Base is { } clr && Substitute(clr, MapFor(current)) is ClrTypeSymbol substituted ? substituted : root.ClrBase;
    }

    /// <summary>
    /// <paramref name="type"/>'s Piper base class <paramref name="declaring"/>, with the type arguments
    /// <paramref name="type"/> gives it, or <paramref name="type"/> itself if that's not one of its bases.
    /// </summary>
    public static TypeSymbol AsBase(TypeSymbol type, ClassSymbol? declaring)
    {
        if (declaring is null)
            return type;
        for (TypeSymbol? current = type; current?.OriginalDefinition is ClassSymbol definition;
             current = definition.Base is { } @base ? Substitute(@base, MapFor(current)) : null)
        {
            if (definition == declaring)
                return current;
        }
        return type;
    }

    /// <summary>
    /// The type with every tuple's element names removed, at any depth: C# treats types that differ only in tuple
    /// names (<c>List&lt;(int a, int b)&gt;</c> and <c>List&lt;(int, int)&gt;</c>) as identical for conversions.
    /// </summary>
    public static TypeSymbol WithoutTupleNames(TypeSymbol type) => type switch
    {
        TupleTypeSymbol tuple => TupleTypeSymbol.Create([.. tuple.ElementTypes.Select(WithoutTupleNames)]),
        ConstructedTypeSymbol constructed => constructed.Definition.Construct([.. constructed.TypeArguments.Select(WithoutTupleNames)]),
        ClrTypeSymbol { TypeArguments.IsEmpty: false } clr => clr.Definition.Construct([.. clr.TypeArguments.Select(WithoutTupleNames)]),
        _ => type,
    };

    public static bool Mentions(TypeSymbol type, IReadOnlySet<TypeParameterSymbol> parameters) => type switch
    {
        TypeParameterSymbol parameter => parameters.Contains(parameter),
        ConstructedTypeSymbol constructed => constructed.TypeArguments.Any(a => Mentions(a, parameters)),
        TupleTypeSymbol tuple => tuple.ElementTypes.Any(e => Mentions(e, parameters)),
        ClrTypeSymbol clr => clr.TypeArguments.Any(a => Mentions(a, parameters)),
        _ => false,
    };
}

/// <summary>
/// A Piper method implementing a .NET interface method whose C# signature differs from the method's own by nullability:
/// a parameter or return type .NET marks as possibly null is an Option in Piper, so the emitter adds an explicit interface
/// implementation that converts and forwards, e.g. <c>int IComparable&lt;Money&gt;.CompareTo(Money? other) =&gt;
/// CompareTo(PiperInterop.FromNullableReference(other))</c>. Each type is the C# one, without the <c>?</c>.
/// </summary>
public sealed record ClrInterfaceBridge(
    MethodSymbol Method,
    TypeSymbol Interface,
    ImmutableArray<(TypeSymbol Type, ClrBoundary Boundary)> Parameters,
    TypeSymbol ReturnType,
    ClrBoundary ReturnBoundary);

/// <summary>
/// A class property implementing a .NET interface property that .NET marks as possibly null, which is an Option in Piper:
/// the emitter adds an explicit implementation that converts, e.g. <c>object? IAsyncResult.AsyncState =&gt;
/// PiperInterop.ToNullableReference(AsyncState);</c>. <see cref="Type"/> is the C# one, without the <c>?</c>.
/// </summary>
public sealed record ClrPropertyBridge(FieldSymbol Property, TypeSymbol Interface, TypeSymbol Type, ClrBoundary Boundary, bool Settable);

/// <summary>
/// A class indexer implementing a .NET interface indexer whose result or parameters .NET marks as possibly null, which are
/// Options in Piper: the emitter adds an explicit implementation that converts, e.g. <c>string? IStore.this[string? key]
/// { get =&gt; PiperInterop.ToNullableReference(this[PiperInterop.FromNullableReference(key)]); … }</c>. Types are the C#
/// ones, without the <c>?</c>.
/// </summary>
public sealed record ClrIndexerBridge(
    IndexerSymbol Indexer,
    TypeSymbol Interface,
    ImmutableArray<(TypeSymbol Type, ClrBoundary Boundary)> Parameters,
    TypeSymbol Type,
    ClrBoundary Boundary,
    bool Settable);

/// <summary>
/// A class's constructor besides its primary one: <see cref="Method"/> holds its parameters and body (run after the
/// primary constructor), and <see cref="ThisArguments"/> the primary constructor's arguments, named as written.
/// </summary>
public sealed record ClassConstructor(MethodSymbol Method, ImmutableArray<BoundExpression> ThisArguments, ImmutableArray<string?> ArgumentNames);
