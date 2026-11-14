using System.Collections;
using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;

namespace Piper.Runtime;

/// <summary>
/// The random choices one generated test case is made from. Generators read every value through <see cref="Draw"/>, so
/// a case is exactly its list of choices: replaying the list remakes it, and a smaller list (fewer choices, or lower
/// ones) makes a simpler case. That's how failures shrink, for every type alike (as Hypothesis does), without a
/// shrinker per type. Every generator makes choice 0 its simplest value: 0, empty, None, the first variant.
/// </summary>
public sealed class PiperChoices
{
    /// <summary>Choices one case may make, so a recursive type can't grow without end.</summary>
    internal const int MaxChoices = 20_000;

    private readonly IReadOnlyList<ulong> _prefix;
    private readonly Random? _random;
    private readonly List<ulong> _drawn = [];

    /// <param name="prefix">The choices to replay; once they run out, <paramref name="random"/>'s, or else 0.</param>
    internal PiperChoices(IReadOnlyList<ulong> prefix, Random? random, int size)
    {
        _prefix = prefix;
        _random = random;
        Size = size;
    }

    /// <summary>How large values should be, from 1 (the first case) to 100, growing as cases pass.</summary>
    public int Size { get; }

    /// <summary>How deeply nested the value being made is (a list in a record in a list…), which makes it smaller.</summary>
    internal int Depth { get; set; }

    internal IReadOnlyList<ulong> Drawn => _drawn;

    /// <summary>A choice in [0, <paramref name="bound"/>), or any ulong for a bound of 0.</summary>
    public ulong Draw(ulong bound)
    {
        var value = Shaped(random => (ulong)random.NextInt64() ^ ((ulong)random.Next(2) << 63));
        return bound == 0 ? value : value % bound;
    }

    /// <summary>
    /// A choice that <paramref name="random"/> makes when a new case is generated, and that's otherwise replayed. How it
    /// was made (mostly small, sometimes huge…) doesn't change what it means, so lowering it while shrinking lowers the
    /// value smoothly.
    /// </summary>
    private ulong Shaped(Func<Random, ulong> random)
    {
        if (_drawn.Count >= MaxChoices)
            throw new PiperDiscardException();
        var value = _drawn.Count < _prefix.Count ? _prefix[_drawn.Count] : _random is not null ? random(_random) : 0;
        _drawn.Add(value);
        return value;
    }

    /// <summary>True one time in <paramref name="oneIn"/>; choice 0 is false.</summary>
    internal bool OneIn(ulong oneIn) => Draw(oneIn) == oneIn - 1;

    /// <summary>
    /// An integer in [<paramref name="min"/>, <paramref name="max"/>], nearest 0 when the choices are 0. Mostly within
    /// <paramref name="small"/> of that, sometimes anywhere, and sometimes an edge (min, max, ±1).
    /// </summary>
    internal Int128 Integer(Int128 min, Int128 max, Int128 small)
    {
        var target = min > 0 ? min : max < 0 ? max : 0;
        Int128 above = max - target, below = target - min;
        // Choice 0 is the side above the target, when there is one.
        var downwards = above == 0 || (below > 0 && Draw(2) == 1);
        var span = (ulong)Int128.Min(downwards ? below : above, ulong.MaxValue);
        // The choice is the distance from the target, whichever way it was made.
        var magnitude = (Int128)Math.Min(span, Shaped(random => Magnitude(random, span, small)));
        return downwards ? target - magnitude : target + magnitude;
    }

    /// <summary>A distance up to <paramref name="span"/>: mostly within <paramref name="small"/>, sometimes larger, and sometimes the very edge.</summary>
    private static ulong Magnitude(Random random, ulong span, Int128 small)
    {
        ulong Upto(Int128 limit)
        {
            var any = (ulong)random.NextInt64() ^ ((ulong)random.Next(2) << 63);
            var bound = Int128.Min(limit, span);
            return bound >= ulong.MaxValue ? any : any % ((ulong)bound + 1);
        }

        return random.Next(8) switch
        {
            7 => random.Next(3) switch { 0 => span, 1 => span == 0 ? 0 : span - 1, _ => Math.Min(span, 1) },
            6 => Upto(ulong.MaxValue),
            5 => Upto(small * small * 100),
            _ => Upto(small),
        };
    }

    /// <summary>For a collection: whether to add another item. Choice 0 stops, so shrinking can only shorten it.</summary>
    internal bool More(int count)
    {
        var size = Math.Max(1, Size >> Depth);
        if (count >= size)
            return false;
        // About size / 4 items on average.
        return Draw((ulong)(2 + size / 4)) != 0;
    }
}

/// <summary>Thrown to throw away a generated case: a record's invariant failed while making it, or a test's `requires`.</summary>
public sealed class PiperDiscardException : Exception
{
    public PiperDiscardException() : base("The generated values were discarded.") { }
}

/// <summary>Makes values of one type for property tests: <c>test "name" for (x: int in 1..10) { … }</c>.</summary>
public interface IPiperGen
{
    Type ValueType { get; }

    object? GenerateObject(PiperChoices choices);
}

/// <summary>
/// Makes values of <typeparamref name="T"/> for property tests. Made by <see cref="PiperGen"/>'s functions, and changed
/// and combined as LINQ's names say: <c>PiperGen.For&lt;int&gt;().Where(n =&gt; n % 2 == 0).Select(n =&gt; n * 10)</c>.
/// Whatever's made this way shrinks, as every value is made from the same recorded choices.
/// </summary>
public abstract class PiperGen<T> : IPiperGen
{
    public Type ValueType => typeof(T);

    public abstract T Generate(PiperChoices choices);

    public object? GenerateObject(PiperChoices choices) => Generate(choices);

    /// <summary>Each value made into another: <c>For&lt;int&gt;().Select(n =&gt; LotId(n))</c>.</summary>
    public PiperGen<R> Select<R>(Func<T, R> map) => new PiperGen.FuncGen<R>(c => map(Generate(c)));

    /// <summary>
    /// Only the values <paramref name="keep"/> accepts, trying again (up to 100 times) for those it doesn't, and then
    /// throwing the case away. For rare values, make them directly instead, as with a range.
    /// </summary>
    public PiperGen<T> Where(Func<T, bool> keep) => new PiperGen.FuncGen<T>(c =>
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            var value = Generate(c);
            if (keep(value))
                return value;
        }
        throw new PiperDiscardException();
    });

    /// <summary>
    /// A value made from another's generator: <c>Range(1, 10).SelectMany(n =&gt; ListOf(For&lt;int&gt;(), n, n))</c>, a
    /// count and then a list of that many.
    /// </summary>
    public PiperGen<R> SelectMany<R>(Func<T, PiperGen<R>> next) => new PiperGen.FuncGen<R>(c => next(Generate(c)).Generate(c));
}

/// <summary>The generators a property test's parameters use: by type, or from an <c>in</c> range or collection.</summary>
public static class PiperGen
{
    private static readonly ConcurrentDictionary<Type, IPiperGen> ByType = new();

    /// <summary>Values of any type Piper can make: numbers, text, collections, Options, Results, tuples, records and enums.</summary>
    public static PiperGen<T> For<T>() => (PiperGen<T>)For(typeof(T));

    public static IPiperGen For(Type type)
    {
        if (ByType.TryGetValue(type, out var gen))
            return gen;
        // Lazily, so a record or enum that contains itself doesn't build its generator forever.
        var lazy = (IPiperGen)Activator.CreateInstance(typeof(LazyGen<>).MakeGenericType(type), new Func<IPiperGen>(() => Build(type)))!;
        return ByType.GetOrAdd(type, lazy);
    }

    /// <summary><c>x: int in 1..10</c>: from <paramref name="start"/> to <paramref name="end"/>, both included.</summary>
    public static PiperGen<T> Range<T>(T start, T end) where T : IBinaryInteger<T> =>
        end < start
            ? throw new ArgumentException($"The range {start}..{end} is empty.")
            : new IntegerGen<T>(Int128.CreateTruncating(start), Int128.CreateTruncating(end));

    /// <summary><c>x: int in 0..&lt;10</c>: from <paramref name="start"/> up to, not including, <paramref name="end"/>.</summary>
    public static PiperGen<T> RangeExclusive<T>(T start, T end) where T : IBinaryInteger<T> =>
        end <= start
            ? throw new ArgumentException($"The range {start}..<{end} is empty.")
            : new IntegerGen<T>(Int128.CreateTruncating(start), Int128.CreateTruncating(end) - 1);

    /// <summary><c>x: string in ["a", "b"]</c>: one of the items, shrinking towards the first.</summary>
    public static PiperGen<T> In<T>(IEnumerable<T> items)
    {
        var list = items.ToArray();
        return list.Length == 0 ? throw new ArgumentException("There are no items to choose from.") : new ItemGen<T>(list);
    }

    /// <summary><c>x: T in generator</c>: values from a generator made elsewhere.</summary>
    public static PiperGen<T> In<T>(PiperGen<T> generator) => generator;

    /// <summary>Always <paramref name="value"/>.</summary>
    public static PiperGen<T> Constant<T>(T value) => new FuncGen<T>(_ => value);

    /// <summary>Values from one of the generators, shrinking towards the first.</summary>
    public static PiperGen<T> OneOf<T>(params PiperGen<T>[] generators) =>
        generators.Length == 0
            ? throw new ArgumentException("OneOf needs at least one generator.")
            : new FuncGen<T>(c => generators[c.Draw((ulong)generators.Length)].Generate(c));

    /// <summary>Lists of <paramref name="item"/>'s values, as long as a list of the type would be, shrinking shorter.</summary>
    public static PiperGen<List<T>> ListOf<T>(PiperGen<T> item) => new FuncGen<List<T>>(c => Items(item, c));

    /// <summary>Lists of from <paramref name="least"/> to <paramref name="most"/> of <paramref name="item"/>'s values.</summary>
    public static PiperGen<List<T>> ListOf<T>(PiperGen<T> item, int least, int most)
    {
        if (least < 0 || most < least)
            throw new ArgumentException($"A list can't have from {least} to {most} items.");
        return new FuncGen<List<T>>(c =>
        {
            var count = (int)c.Integer(least, most, c.Size);
            var items = new List<T>(count);
            for (int i = 0; i < count; i++)
                items.Add(Nested(item, c));
            return items;
        });
    }

    /// <summary>
    /// The items in any order, each order alike likely, shrinking towards the order given: for "in any order"
    /// properties.
    /// </summary>
    public static PiperGen<List<T>> Shuffled<T>(IEnumerable<T> items)
    {
        var list = items.ToArray();
        return new FuncGen<List<T>>(c =>
        {
            // Fisher–Yates, each choice the distance to swap with: all 0 is the order given.
            var order = (T[])list.Clone();
            for (int i = 0; i < order.Length - 1; i++)
            {
                var j = i + (int)c.Draw((ulong)(order.Length - i));
                (order[i], order[j]) = (order[j], order[i]);
            }
            return [.. order];
        });
    }

    private static IPiperGen Build(Type type)
    {
        if (Primitive(type) is { } primitive)
            return primitive;
        if (type.IsEnum)
            return Make(typeof(DotNetEnumGen<>), type);
        if (type.IsArray && type.GetArrayRank() == 1)
            return Make(typeof(ArrayGen<>), type.GetElementType()!);
        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            var arguments = type.GetGenericArguments();
            if (definition == typeof(Option<>))
                return Make(typeof(OptionGen<>), arguments);
            if (definition == typeof(Result<,>))
                return Make(typeof(ResultGen<,>), arguments);
            if (definition == typeof(List<>) || definition == typeof(IEnumerable<>) || definition == typeof(IList<>) ||
                definition == typeof(IReadOnlyList<>) || definition == typeof(ICollection<>) || definition == typeof(IReadOnlyCollection<>))
                return Make(typeof(ListGen<,>), [type, arguments[0]]);
            if (definition == typeof(HashSet<>) || definition == typeof(ISet<>) || definition == typeof(IReadOnlySet<>))
                return Make(typeof(SetGen<,>), [type, arguments[0]]);
            if (definition == typeof(Dictionary<,>) || definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>))
                return Make(typeof(DictionaryGen<,,>), [type, arguments[0], arguments[1]]);
            if (type.FullName?.StartsWith("System.ValueTuple`", StringComparison.Ordinal) == true)
                return Make(typeof(ConstructedGen<>), type);
        }
        // A Piper enum: an abstract record whose variants are records nested in it.
        if (type.IsAbstract && IsRecord(type) && Variants(type) is { Length: > 0 })
            return Make(typeof(VariantGen<>), type);
        if (IsRecord(type))
            return Make(typeof(ConstructedGen<>), type);
        throw new NotSupportedException($"Values of type {Display(type)} can't be generated. Give the parameter an `in` range or collection.");
    }

    private static IPiperGen Make(Type generic, params Type[] arguments) =>
        (IPiperGen)Activator.CreateInstance(generic.MakeGenericType(arguments))!;

    private static IPiperGen? Primitive(Type type)
    {
        if (type == typeof(bool)) return new FuncGen<bool>(c => c.Draw(2) == 1);
        if (type == typeof(int)) return Integers<int>();
        if (type == typeof(long)) return Integers<long>();
        if (type == typeof(short)) return Integers<short>();
        if (type == typeof(sbyte)) return Integers<sbyte>();
        if (type == typeof(byte)) return Integers<byte>();
        if (type == typeof(ushort)) return Integers<ushort>();
        if (type == typeof(uint)) return Integers<uint>();
        if (type == typeof(ulong)) return Integers<ulong>();
        if (type == typeof(double)) return new FuncGen<double>(Double);
        if (type == typeof(float)) return new FuncGen<float>(c => (float)Math.Clamp(Double(c), float.MinValue, float.MaxValue));
        if (type == typeof(decimal)) return new FuncGen<decimal>(Decimal);
        if (type == typeof(char)) return new FuncGen<char>(Char);
        if (type == typeof(string)) return new FuncGen<string>(String);
        if (type == typeof(Guid)) return new FuncGen<Guid>(c => c.Draw(4) == 0 ? Guid.Empty : new Guid(BitConverter.GetBytes(c.Draw(0)).Concat(BitConverter.GetBytes(c.Draw(0))).ToArray()));
        if (type == typeof(TimeSpan)) return new FuncGen<TimeSpan>(c => TimeSpan.FromSeconds((long)c.Integer(-10_000_000_000, 10_000_000_000, c.Size * 60L)));
        if (type == typeof(DateTime)) return new FuncGen<DateTime>(c => Epoch.AddSeconds((double)Seconds(c)));
        if (type == typeof(DateTimeOffset)) return new FuncGen<DateTimeOffset>(c => new DateTimeOffset(Epoch.AddSeconds((double)Seconds(c)), TimeSpan.Zero));
        if (type == typeof(DateOnly)) return new FuncGen<DateOnly>(c => DateOnly.FromDateTime(Epoch).AddDays((int)c.Integer(-700_000, 2_900_000, c.Size)));
        if (type == typeof(TimeOnly)) return new FuncGen<TimeOnly>(c => new TimeOnly((long)c.Integer(0, TimeSpan.TicksPerDay - 1, c.Size * TimeSpan.TicksPerSecond)));
        return null;
    }

    private static readonly DateTime Epoch = new(2000, 1, 1);

    /// <summary>Seconds from 2000, between years 1 and 9999.</summary>
    private static Int128 Seconds(PiperChoices c) =>
        c.Integer((Int128)(DateTime.MinValue - Epoch).TotalSeconds, (Int128)(DateTime.MaxValue - Epoch).TotalSeconds - 1, c.Size * 86_400L);

    private static IntegerGen<T> Integers<T>() where T : IBinaryInteger<T>, IMinMaxValue<T> =>
        new(Int128.CreateTruncating(T.MinValue), Int128.CreateTruncating(T.MaxValue));

    /// <summary>
    /// Finite doubles: a whole number, a fraction and a power of two, each shrinking to 0, so failures shrink towards
    /// small whole numbers. Mostly small, sometimes very large or very small. No NaN or infinities.
    /// </summary>
    private static double Double(PiperChoices c)
    {
        var whole = (double)c.Integer(-(1L << 53), 1L << 53, c.Size);
        var fraction = (double)c.Integer(0, (1 << 20) - 1, 1 << 20) / (1 << 20);
        var exponent = (int)c.Integer(-1074, 970, c.Size / 20);
        var value = (whole + (whole < 0 ? -fraction : fraction)) * Math.Pow(2, exponent);
        return double.IsFinite(value) ? value : whole < 0 ? double.MinValue : double.MaxValue;
    }

    /// <summary>Decimals with up to 4 decimal places, as money often has, and occasionally up to 28.</summary>
    private static decimal Decimal(PiperChoices c)
    {
        var scale = (byte)(c.OneIn(16) ? c.Draw(29) : c.Draw(5));
        var limit = (Int128)ulong.MaxValue;
        var units = c.Integer(-limit, limit, c.Size * (Int128)Math.Pow(10, Math.Min((int)scale, 4)));
        var magnitude = (ulong)Int128.Abs(units);
        return new decimal((int)(uint)magnitude, (int)(uint)(magnitude >> 32), 0, units < 0, scale);
    }

    private const string Printable = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 !\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";

    /// <summary>Mostly printable ASCII, from 'a'; sometimes any character outside the surrogates.</summary>
    private static char Char(PiperChoices c)
    {
        // One choice: an index into the printable characters, or past them, any other character.
        const int others = 0x10000 - 0x800;
        var index = (int)c.Integer(0, Printable.Length + others - 1, Printable.Length - 1);
        if (index < Printable.Length)
            return Printable[index];
        var code = index - Printable.Length;
        return (char)(code < 0xD800 ? code : code + 0x800);
    }

    private static string String(PiperChoices c)
    {
        var text = new System.Text.StringBuilder();
        while (c.More(text.Length))
            text.Append(Char(c));
        return text.ToString();
    }

    /// <summary>A Piper enum's variants (its nested classes deriving from it), in declaration order.</summary>
    internal static Type[] Variants(Type type) =>
        [.. type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .Where(t => !t.IsAbstract && type.IsAssignableFrom(t))
            .OrderBy(t => t.MetadataToken)];

    /// <summary>A C# record (class or struct), which a Piper record, variant or newtype compiles to.</summary>
    private static bool IsRecord(Type type) =>
        type.GetMethod("PrintMembers", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) is not null;

    /// <summary>How Piper writes a .NET type: <c>List&lt;int&gt;</c>, <c>int?</c>.</summary>
    internal static string Display(Type type)
    {
        if (type.IsArray)
            return Display(type.GetElementType()!) + "[]";
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Option<>))
            return Display(type.GetGenericArguments()[0]) + "?";
        if (type.IsGenericType)
            return $"{type.Name[..type.Name.IndexOf('`')]}<{string.Join(", ", type.GetGenericArguments().Select(Display))}>";
        return Type.GetTypeCode(type) switch
        {
            TypeCode.Int32 => "int",
            TypeCode.Int64 => "long",
            TypeCode.Boolean => "bool",
            TypeCode.String => "string",
            TypeCode.Double => "double",
            TypeCode.Decimal => "decimal",
            TypeCode.Char => "char",
            _ => type.DeclaringType is { } outer ? $"{Display(outer)}.{type.Name}" : type.Name,
        };
    }

    /// <summary>Generates a value one level deeper, so nested collections and recursive types get smaller.</summary>
    internal static object? Nested(IPiperGen gen, PiperChoices c)
    {
        c.Depth++;
        try
        {
            return gen.GenerateObject(c);
        }
        finally
        {
            c.Depth--;
        }
    }

    internal static T Nested<T>(PiperGen<T> gen, PiperChoices c) => (T)Nested((IPiperGen)gen, c)!;

    private sealed class LazyGen<T>(Func<IPiperGen> build) : PiperGen<T>
    {
        private readonly Lazy<IPiperGen> _gen = new(build);

        public override T Generate(PiperChoices choices) => (T)_gen.Value.GenerateObject(choices)!;
    }

    internal sealed class FuncGen<T>(Func<PiperChoices, T> generate) : PiperGen<T>
    {
        public override T Generate(PiperChoices choices) => generate(choices);
    }

    private sealed class IntegerGen<T>(Int128 min, Int128 max) : PiperGen<T> where T : IBinaryInteger<T>
    {
        public override T Generate(PiperChoices choices) => T.CreateTruncating(choices.Integer(min, max, choices.Size));
    }

    private sealed class ItemGen<T>(T[] items) : PiperGen<T>
    {
        public override T Generate(PiperChoices choices) => items[choices.Draw((ulong)items.Length)];
    }

    private sealed class DotNetEnumGen<T> : PiperGen<T> where T : struct, Enum
    {
        private readonly T[] _values = Enum.GetValues<T>();

        public override T Generate(PiperChoices choices) => _values[choices.Draw((ulong)_values.Length)];
    }

    private sealed class OptionGen<T> : PiperGen<Option<T>>
    {
        private readonly PiperGen<T> _value = For<T>();

        public override Option<T> Generate(PiperChoices choices) =>
            choices.Draw(4) == 0 ? Option<T>.None : Option<T>.Some(Nested(_value, choices));
    }

    private sealed class ResultGen<T, E> : PiperGen<Result<T, E>>
    {
        private readonly PiperGen<T> _value = For<T>();
        private readonly PiperGen<E> _error = For<E>();

        public override Result<T, E> Generate(PiperChoices choices) =>
            choices.Draw(4) == 3 ? Result<T, E>.Err(Nested(_error, choices)) : Result<T, E>.Ok(Nested(_value, choices));
    }

    private static List<T> Items<T>(PiperGen<T> item, PiperChoices choices)
    {
        var items = new List<T>();
        while (choices.More(items.Count))
            items.Add(Nested(item, choices));
        return items;
    }

    private sealed class ArrayGen<T> : PiperGen<T[]>
    {
        private readonly PiperGen<T> _item = For<T>();

        public override T[] Generate(PiperChoices choices) => [.. Items(_item, choices)];
    }

    /// <summary>A List, or an interface a List implements.</summary>
    private sealed class ListGen<TList, T> : PiperGen<TList>
    {
        private readonly PiperGen<T> _item = For<T>();

        public override TList Generate(PiperChoices choices) => (TList)(object)Items(_item, choices);
    }

    private sealed class SetGen<TSet, T> : PiperGen<TSet>
    {
        private readonly PiperGen<T> _item = For<T>();

        public override TSet Generate(PiperChoices choices) => (TSet)(object)new HashSet<T>(Items(_item, choices));
    }

    private sealed class DictionaryGen<TDictionary, K, V> : PiperGen<TDictionary> where K : notnull
    {
        private readonly PiperGen<K> _key = For<K>();
        private readonly PiperGen<V> _value = For<V>();

        public override TDictionary Generate(PiperChoices choices)
        {
            var entries = new Dictionary<K, V>();
            var count = 0;
            while (choices.More(count++))
                entries[Nested(_key, choices)] = Nested(_value, choices);
            return (TDictionary)(object)entries;
        }
    }

    /// <summary>A record or tuple, made by its constructor from generated fields.</summary>
    private sealed class ConstructedGen<T> : PiperGen<T>
    {
        private readonly ConstructorInfo _constructor;
        private readonly IPiperGen[] _fields;

        public ConstructedGen()
        {
            // The primary constructor: the one with most parameters, other than a record's copy constructor.
            _constructor = typeof(T).GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .Where(c => c.GetParameters() is not [{ } only] || only.ParameterType != typeof(T))
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault() ?? throw new NotSupportedException($"Values of type {Display(typeof(T))} can't be generated: it has no public constructor.");
            _fields = [.. _constructor.GetParameters().Select(p => For(p.ParameterType))];
        }

        public override T Generate(PiperChoices choices)
        {
            var arguments = _fields.Select(f => Nested(f, choices)).ToArray();
            try
            {
                return (T)_constructor.Invoke(arguments);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is ArgumentException)
            {
                // A record's `requires` refused the fields: try other values.
                throw new PiperDiscardException();
            }
        }
    }

    /// <summary>A Piper enum: one of its variants, shrinking towards the first.</summary>
    private sealed class VariantGen<T> : PiperGen<T>
    {
        private const int MaxDepth = 4;

        private readonly IPiperGen[] _variants;
        private readonly int[] _shallow;

        public VariantGen()
        {
            var variants = Variants(typeof(T));
            _variants = [.. variants.Select(For)];
            // Deep in a recursive enum, only the variants that don't contain it, so it ends.
            _shallow = [.. Enumerable.Range(0, variants.Length).Where(i => !Mentions(variants[i], typeof(T)))];
        }

        public override T Generate(PiperChoices choices)
        {
            var index = choices.Depth >= MaxDepth && _shallow.Length > 0
                ? _shallow[choices.Draw((ulong)_shallow.Length)]
                : (int)choices.Draw((ulong)_variants.Length);
            // The variant itself isn't a level deeper: its fields are.
            return (T)_variants[index].GenerateObject(choices)!;
        }

        private static bool Mentions(Type variant, Type type) =>
            variant.GetConstructors().SelectMany(c => c.GetParameters()).Any(p => Contains(p.ParameterType, type));

        private static bool Contains(Type candidate, Type type) =>
            type.IsAssignableFrom(candidate) ||
            (candidate.HasElementType && Contains(candidate.GetElementType()!, type)) ||
            (candidate.IsGenericType && candidate.GetGenericArguments().Any(a => Contains(a, type)));
    }
}

/// <summary>A property test failed: the smallest failing values found, and the failure they cause.</summary>
public sealed class PiperPropertyException(string message, Exception? failure) : Exception(message, failure);

/// <summary>
/// Runs a property test (<c>test "name" for (x: int) { … }</c>): the body over generated values, and if it fails, the
/// smallest values found that still fail it.
/// </summary>
public static class PiperProperty
{
    /// <summary>The seed to use, as <c>piper test --seed</c> gives; otherwise a new one each run, shown on a failure.</summary>
    public static int? Seed { get; set; }

    /// <summary>How many cases must pass.</summary>
    public static int Runs { get; set; } = 100;

    /// <summary>Most cases tried while shrinking a failure.</summary>
    private const int MaxShrinks = 2_000;

    /// <summary>A test's `requires`: throws the case away unless <paramref name="condition"/> holds.</summary>
    public static void Assume(bool condition)
    {
        if (!condition)
            throw new PiperDiscardException();
    }

    /// <summary>
    /// Runs <paramref name="test"/> on values from <paramref name="generators"/> until <see cref="Runs"/> cases pass,
    /// or throws a <see cref="PiperPropertyException"/> naming the smallest failing values.
    /// </summary>
    public static void Check(IReadOnlyList<string> names, IReadOnlyList<IPiperGen> generators, Action<object?[]> test)
    {
        var seed = Seed ?? Random.Shared.Next();
        var random = new Random(seed);
        int passed = 0, discarded = 0;
        while (passed < Runs)
        {
            if (discarded > Runs * 10)
                throw new PiperPropertyException(
                    $"Gave up after {passed} passing cases: {discarded} were discarded by a `requires` (seed {seed}).", null);
            var size = Math.Min(passed, 99) + 1;
            var choices = new PiperChoices([], random, size);
            var outcome = Run(choices, generators, test);
            if (outcome.Failure is { } first)
            {
                var (smallest, failure, shrinks) = Shrink([.. choices.Drawn], first, size, generators, test);
                // Remake the smallest values to show them: the test may have changed the ones it was given.
                var values = Generate(new PiperChoices(smallest, null, size), generators);
                var lines = names.Select((name, i) => $"\n  {name} = {PiperAssert.Format(values[i])}");
                var cases = passed == 0 ? "the first case" : $"{passed + 1} cases";
                var shrunk = shrinks == 0 ? "" : $", shrunk {shrinks} {(shrinks == 1 ? "time" : "times")}";
                throw new PiperPropertyException(
                    $"Falsified after {cases}{shrunk} (seed {seed}):{string.Concat(lines)}\n{Describe(failure)}", failure);
            }
            if (outcome.Discarded)
                discarded++;
            else
                passed++;
        }
    }

    private static string Describe(Exception failure) =>
        failure is PiperAssertionException ? failure.Message : $"Unhandled exception: {failure.GetType().FullName}: {failure.Message}";

    /// <summary>A case's outcome: passed (neither set), discarded, or failed with <see cref="Failure"/>.</summary>
    private readonly record struct Outcome(bool Discarded, Exception? Failure);

    private static object?[] Generate(PiperChoices choices, IReadOnlyList<IPiperGen> generators) =>
        [.. generators.Select(g => g.GenerateObject(choices))];

    private static Outcome Run(PiperChoices choices, IReadOnlyList<IPiperGen> generators, Action<object?[]> test)
    {
        // Generating can't fail the test, only discard the case (or say the type can't be generated).
        object?[] values;
        try
        {
            values = Generate(choices, generators);
        }
        catch (PiperDiscardException)
        {
            return new Outcome(true, null);
        }
        try
        {
            test(values);
            return new Outcome(false, null);
        }
        catch (PiperDiscardException)
        {
            return new Outcome(true, null);
        }
        catch (Exception ex)
        {
            return new Outcome(false, ex);
        }
    }

    /// <summary>
    /// Makes the failing choices as small as it can while the test still fails the same way: deleting runs of choices
    /// (which shortens collections and drops fields), zeroing them, and then lowering each one.
    /// </summary>
    private static (List<ulong> Choices, Exception Failure, int Shrinks) Shrink(
        List<ulong> best, Exception failure, int size, IReadOnlyList<IPiperGen> generators, Action<object?[]> test)
    {
        int attempts = 0, shrinks = 0;

        bool Try(List<ulong> candidate)
        {
            if (attempts >= MaxShrinks || !Simpler(candidate, best))
                return false;
            attempts++;
            var choices = new PiperChoices(candidate, null, size);
            var outcome = Run(choices, generators, test);
            // Replaying may read past the candidate (as zeros), so it's what was really drawn that must be simpler.
            List<ulong> drawn = [.. choices.Drawn];
            if (outcome.Failure is not { } failed || failed.GetType() != failure.GetType() || !Simpler(drawn, best))
                return false;
            best = drawn;
            failure = failed;
            shrinks++;
            return true;
        }

        var improved = true;
        while (improved && attempts < MaxShrinks)
        {
            improved = false;
            foreach (var length in (int[])[8, 4, 2, 1])
            {
                for (var start = best.Count - length; start >= 0; start--)
                {
                    if (start + length <= best.Count && Try([.. best.Take(start), .. best.Skip(start + length)]))
                        improved = true;
                }
            }
            foreach (var length in (int[])[8, 4, 2, 1])
            {
                for (var start = 0; start + length <= best.Count; start++)
                {
                    if (best.Skip(start).Take(length).Any(c => c != 0) &&
                        Try([.. best.Take(start), .. Enumerable.Repeat(0UL, length), .. best.Skip(start + length)]))
                        improved = true;
                }
            }
            for (var i = 0; i < best.Count; i++)
            {
                // The lowest value of this choice that still fails, by binary search.
                ulong low = 0, high = best[i];
                while (low < high && i < best.Count)
                {
                    var middle = low + (high - low) / 2;
                    var candidate = new List<ulong>(best) { [i] = middle };
                    if (Try(candidate))
                    {
                        improved = true;
                        high = i < best.Count ? Math.Min(middle, best[i]) : middle;
                    }
                    else
                    {
                        low = middle + 1;
                    }
                }
            }
        }
        return (best, failure, shrinks);
    }

    /// <summary>Fewer choices, or as many and lower at the first that differs: so shrinking always ends.</summary>
    private static bool Simpler(List<ulong> candidate, List<ulong> best)
    {
        if (candidate.Count != best.Count)
            return candidate.Count < best.Count;
        for (var i = 0; i < candidate.Count; i++)
        {
            if (candidate[i] != best[i])
                return candidate[i] < best[i];
        }
        return false;
    }
}
