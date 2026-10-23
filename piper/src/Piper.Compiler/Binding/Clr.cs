using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;

namespace Piper.Binding;

/// <summary>
/// Every public, top-level .NET type a Piper program can use: those in the runtime's trusted platform
/// assemblies, which is also what <see cref="Runner"/> compiles the generated C# against. Built once per process.
/// </summary>
public static class ClrTypeIndex
{
    private sealed record Index(Dictionary<string, List<Type>> Types, HashSet<string> Namespaces, Dictionary<string, List<Type>> TypesByNamespace);

    private static readonly ConcurrentDictionary<(string Namespace, string Name), MethodInfo[]> ExtensionMethodCache = new();

    private static readonly Lazy<Index> Instance = new(Build);

    /// <summary>True if <paramref name="fullName"/> (e.g. "System.Text") is a .NET namespace.</summary>
    public static bool IsNamespace(string fullName) => Instance.Value.Namespaces.Contains(fullName);

    /// <summary>All types called <paramref name="name"/> in <paramref name="namespace"/>, of any generic arity.</summary>
    public static IReadOnlyList<Type> FindTypes(string @namespace, string name) =>
        Instance.Value.Types.TryGetValue($"{@namespace}.{name}", out var types) ? types : [];

    /// <summary>Extension methods called <paramref name="name"/> declared by static classes in <paramref name="namespace"/> (e.g. LINQ's Where in System.Linq).</summary>
    public static IReadOnlyList<MethodInfo> ExtensionMethods(string @namespace, string name) =>
        ExtensionMethodCache.GetOrAdd((@namespace, name), key =>
            Instance.Value.TypesByNamespace.TryGetValue(key.Namespace, out var types)
                ? [.. types
                    .Where(t => t.IsAbstract && t.IsSealed && t.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false))
                    .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    .Where(m => m.Name == key.Name && m.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false))]
                : []);

    private static Index Build()
    {
        var types = new Dictionary<string, List<Type>>();
        var byNamespace = new Dictionary<string, List<Type>>();
        var namespaces = new HashSet<string>();
        var seen = new HashSet<Type>();
        var paths = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var path in paths)
        {
            Type[] exported;
            try
            {
                var assembly = AssemblyLoadContext.Default.LoadFromAssemblyName(AssemblyName.GetAssemblyName(path));
                exported = assembly.GetExportedTypes();
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException or NotSupportedException)
            {
                continue;
            }

            foreach (var type in exported)
            {
                if (type.IsNested || type.Namespace is null || !seen.Add(type))
                    continue;

                var name = type.Name.Split('`')[0];
                var key = $"{type.Namespace}.{name}";
                if (!types.TryGetValue(key, out var list))
                    types[key] = list = [];
                list.Add(type);
                if (!byNamespace.TryGetValue(type.Namespace, out var inNamespace))
                    byNamespace[type.Namespace] = inNamespace = [];
                inNamespace.Add(type);

                for (var ns = type.Namespace; ns.Length > 0; ns = ns.Contains('.') ? ns[..ns.LastIndexOf('.')] : "")
                    namespaces.Add(ns);
            }
        }
        return new Index(types, namespaces, byNamespace);
    }
}

/// <summary>
/// A .NET type: either non-generic (<c>StringBuilder</c>), a generic definition (<c>List</c>, only usable once
/// instantiated), or an instantiation whose type arguments are Piper types (<c>List&lt;Point&gt;</c>). Piper's builtin
/// types are never represented this way: <c>System.String</c> is <see cref="TypeSymbol.String"/>.
/// Arrays are instantiations too: <c>Point[]</c> is the definition <c>T[]</c> (over <see cref="ElementParameter"/>)
/// with <c>T = Point</c>, so arrays of Piper types work like <c>List&lt;Point&gt;</c> and reflection still finds
/// <c>Length</c> and the <c>IEnumerable&lt;T&gt;</c> interfaces.
/// </summary>
public sealed class ClrTypeSymbol : TypeSymbol
{
    private static readonly ConcurrentDictionary<Type, ClrTypeSymbol> Definitions = new();
    private readonly List<ClrTypeSymbol> _instantiations = [];

    private sealed class ArrayElement<T>;

    /// <summary>The <c>T</c> of the array definitions <c>T[]</c>, <c>T[,]</c>, ….</summary>
    public static readonly Type ElementParameter = typeof(ArrayElement<>).GetGenericArguments()[0];

    /// <summary><c>element[]</c>, or <c>element[,]</c> etc. for a multi-dimensional array.</summary>
    public static TypeSymbol ArrayOf(TypeSymbol element, int rank = 1) =>
        For(rank == 1 ? ElementParameter.MakeArrayType() : ElementParameter.MakeArrayType(rank)).Construct([element]);

    public bool IsArray => Type.IsArray;

    public int Rank => Type.IsArray ? Type.GetArrayRank() : 0;

    private ClrTypeSymbol(Type type, ClrTypeSymbol? definition, ImmutableArray<TypeSymbol> arguments)
        : base(DisplayName(type, arguments))
    {
        Type = type;
        Definition = definition ?? this;
        TypeArguments = arguments;
    }

    /// <summary>The non-generic type, or the generic type definition (e.g. <c>List`1</c>).</summary>
    public Type Type { get; }

    public ClrTypeSymbol Definition { get; }

    public override TypeSymbol OriginalDefinition => Definition;

    public override ImmutableArray<TypeSymbol> TypeArguments { get; }

    public bool IsGenericDefinition => (Type.IsGenericTypeDefinition || Type.IsArray) && TypeArguments.IsEmpty;

    public bool IsReferenceType => !Type.IsValueType;

    /// <summary>The symbol for a non-generic type or generic definition.</summary>
    public static ClrTypeSymbol For(Type type) => Definitions.GetOrAdd(type, t => new ClrTypeSymbol(t, null, []));

    public TypeSymbol Construct(ImmutableArray<TypeSymbol> arguments)
    {
        lock (_instantiations)
        {
            var existing = _instantiations.FirstOrDefault(c =>
                c.TypeArguments.Length == arguments.Length &&
                c.TypeArguments.Zip(arguments).All(pair => ReferenceEquals(pair.First, pair.Second)));
            if (existing is not null)
                return existing;

            var constructed = new ClrTypeSymbol(Type, this, arguments);
            _instantiations.Add(constructed);
            return constructed;
        }
    }

    /// <summary>Maps the generic definition's type parameters (e.g. List's T) to this instantiation's arguments.</summary>
    public IReadOnlyDictionary<Type, TypeSymbol> GenericMap =>
        TypeArguments.IsEmpty ? new Dictionary<Type, TypeSymbol>()
        : Type.IsArray ? new Dictionary<Type, TypeSymbol> { [ElementParameter] = TypeArguments[0] }
        : Type.GetGenericArguments().Zip(TypeArguments).ToDictionary(p => p.First, p => p.Second);

    private static string DisplayName(Type type, ImmutableArray<TypeSymbol> arguments)
    {
        if (type.IsArray)
            return $"{(arguments.IsEmpty ? "T" : arguments[0].Name)}[{new string(',', type.GetArrayRank() - 1)}]";
        var name = type.Name.Split('`')[0];
        return arguments.IsEmpty ? name : $"{name}<{string.Join(", ", arguments)}>";
    }
}

/// <summary>A name that refers to several .NET types of different generic arity (e.g. <c>Tuple</c>); resolved by usage.</summary>
public sealed class ClrTypeGroupSymbol(string name, IReadOnlyList<Type> types) : Symbol(name)
{
    public IReadOnlyList<Type> Types { get; } = types;

    public Type? WithArity(int arity) =>
        Types.FirstOrDefault(t => (t.IsGenericTypeDefinition ? t.GetGenericArguments().Length : 0) == arity);
}

/// <summary>How a .NET value crosses into Piper: nullable values become Option and back.</summary>
public enum ClrBoundary
{
    None,

    /// <summary>A reference type the .NET API annotates as possibly null (<c>string?</c>).</summary>
    NullableReference,

    /// <summary>A <c>Nullable&lt;T&gt;</c> value type (<c>int?</c>).</summary>
    NullableValue,
}

/// <summary>Conversions between .NET <see cref="Type"/>s and Piper <see cref="TypeSymbol"/>s.</summary>
public static class ClrTypes
{
    public static readonly IReadOnlyDictionary<TypeSymbol, Type> BuiltinTypes = new Dictionary<TypeSymbol, Type>
    {
        [TypeSymbol.Int] = typeof(int),
        [TypeSymbol.Long] = typeof(long),
        [TypeSymbol.Double] = typeof(double),
        [TypeSymbol.Bool] = typeof(bool),
        [TypeSymbol.String] = typeof(string),
        [TypeSymbol.Char] = typeof(char),
        [TypeSymbol.Byte] = typeof(byte),
        [TypeSymbol.SByte] = typeof(sbyte),
        [TypeSymbol.Short] = typeof(short),
        [TypeSymbol.UShort] = typeof(ushort),
        [TypeSymbol.UInt] = typeof(uint),
        [TypeSymbol.ULong] = typeof(ulong),
        [TypeSymbol.Float] = typeof(float),
        [TypeSymbol.Decimal] = typeof(decimal),
    };

    /// <summary><c>ValueTuple&lt;T1, T2&gt;</c> … <c>ValueTuple&lt;T1, …, T7&gt;</c>: the .NET types of Piper's tuples.</summary>
    private static readonly Type[] ValueTupleTypes =
    [
        typeof(ValueTuple<,>), typeof(ValueTuple<,,>), typeof(ValueTuple<,,,>), typeof(ValueTuple<,,,,>),
        typeof(ValueTuple<,,,,,>), typeof(ValueTuple<,,,,,,>),
    ];

    /// <summary>
    /// The Piper type for a .NET type, or null if Piper can't represent it (pointers, by-refs, ref structs,
    /// unmapped generic parameters). <paramref name="genericMap"/> supplies the containing type's type arguments.
    /// </summary>
    public static TypeSymbol? FromClr(Type type, IReadOnlyDictionary<Type, TypeSymbol> genericMap, EnumSymbol option)
    {
        if (type.IsByRef || type.IsPointer || type.IsByRefLike || type.IsFunctionPointer)
            return null;
        if (type.IsGenericParameter)
            return genericMap.TryGetValue(type, out var mapped) ? mapped : null;
        if (type == typeof(void))
            return TypeSymbol.Unit;

        foreach (var (piper, clr) in BuiltinTypes)
        {
            if (clr == type)
                return piper;
        }

        if (Nullable.GetUnderlyingType(type) is { } underlying)
            return FromClr(underlying, genericMap, option) is { } inner ? option.Construct([inner]) : null;

        if (type.IsArray)
        {
            return FromClr(type.GetElementType()!, genericMap, option) is { } element
                ? ClrTypeSymbol.ArrayOf(element, type.IsSZArray ? 1 : type.GetArrayRank())
                : null;
        }

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            var arguments = new List<TypeSymbol>();
            foreach (var argument in type.GetGenericArguments())
            {
                if (FromClr(argument, genericMap, option) is not { } piper)
                    return null;
                arguments.Add(piper);
            }
            // (int, string) is a Piper tuple. (Element names from [TupleElementNames] aren't read.)
            return ValueTupleTypes.Contains(definition)
                ? TupleTypeSymbol.Create([.. arguments])
                : ClrTypeSymbol.For(definition).Construct([.. arguments]);
        }

        return ClrTypeSymbol.For(type);
    }

    /// <summary>The .NET type for a Piper type, when one exists (not for Piper records, enums or traits).</summary>
    public static Type? ToClr(TypeSymbol type)
    {
        if (BuiltinTypes.TryGetValue(type, out var builtin))
            return builtin;
        if (type is TupleTypeSymbol tuple)
        {
            var elements = tuple.ElementTypes.Select(ToClr).ToArray();
            return elements.Length < ValueTupleTypes.Length + 2 && elements.All(e => e is not null)
                ? ValueTupleTypes[elements.Length - 2].MakeGenericType(elements!)
                : null;
        }
        if (type is not ClrTypeSymbol clr)
            return null;
        if (clr.TypeArguments.IsEmpty)
            return clr.Type;
        if (clr.IsArray)
            return ToClr(clr.TypeArguments[0]) is { } element ? (clr.Rank == 1 ? element.MakeArrayType() : element.MakeArrayType(clr.Rank)) : null;

        var arguments = clr.TypeArguments.Select(ToClr).ToArray();
        if (arguments.Any(a => a is null))
            return null;
        try
        {
            return clr.Type.MakeGenericType(arguments!);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>True for the Piper types whose values are .NET reference types (String and CLR classes/interfaces).</summary>
    public static bool IsReferenceType(TypeSymbol type) =>
        type == TypeSymbol.String || type is ClrTypeSymbol { IsReferenceType: true };
}
