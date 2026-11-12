namespace Piper.Binding;

/// <summary>
/// Pattern-match analysis based on the "usefulness" algorithm from Luc Maranget, "Warnings for pattern
/// matching" (JFP 2007). A pattern matrix has one row per (unguarded) arm. A pattern vector is useful
/// with respect to the matrix if some value matches it but no row: an arm is unreachable when its
/// pattern is not useful against the arms before it, and a match is exhaustive when a wildcard is not
/// useful against all of its arms.
///
/// Constructors are enum variants, record types (a record has exactly one), tuple types (likewise) and true/false.
/// Integers and chars are intervals instead: a column's values are split, at the bounds of the literals and ranges in
/// it, into pieces that each pattern either covers completely or not at all, and each piece is checked like a
/// constructor. So `1..5` makes a later `3` unreachable, and `0..127, 128..255` exhausts a byte. Any other
/// literal (strings, doubles) is one constructor of an infinite type, never exhausted without a wildcard or binding.
/// Relational patterns (`< 0`) are intervals too on integers and chars, and opaque tests elsewhere, as are property
/// patterns on .NET types.
///
/// A list pattern's constructors are lengths: with L the most elements any list pattern in the column names, each length
/// from 0 to L, and "more than L" (checked as L + 1, which every pattern with a `...` matches and none without does).
///
/// `not` and `and` patterns are first rewritten without them where that's possible (see <see cref="Simplify"/>); any
/// left are opaque tests.
/// </summary>
public static class Exhaustiveness
{
    /// <summary>Returns a description of a value no pattern matches, or null if the patterns are exhaustive.</summary>
    public static string? FindMissingPattern(TypeSymbol type, IEnumerable<BoundPattern> patterns)
    {
        var rows = patterns.Select(p => new[] { Simplify(p) }).ToList();
        return FindWitness(rows, [type])?[0];
    }

    /// <summary>
    /// Patterns for arms that would make a match on <paramref name="type"/> exhaustive, for an editor to add: each variant
    /// (or true/false) that isn't fully covered, as <c>Rect(_, _)</c>, then <c>_</c> if anything is still missing.
    /// </summary>
    public static IReadOnlyList<string> MissingArms(TypeSymbol type, IEnumerable<BoundPattern> patterns)
    {
        var covering = patterns.Select(Simplify).ToList();
        var arms = new List<string>();
        foreach (var constructor in AllConstructors(type) ?? [])
        {
            BoundPattern? pattern = constructor switch
            {
                VariantSymbol variant => new BoundConstructorPattern(variant, [.. FieldTypes(variant, type).Select(Wildcard)], type),
                bool value => new BoundLiteralPattern(value, type),
                _ => null,
            };
            if (pattern is null || !IsUseful(covering, pattern))
                continue;
            arms.Add(constructor switch
            {
                VariantSymbol { Fields.IsEmpty: true } variant => variant.Name,
                VariantSymbol variant => $"{variant.Name}({string.Join(", ", variant.Fields.Select(_ => "_"))})",
                _ => Format(constructor, []),
            });
            covering.Add(pattern);
        }
        if (FindMissingPattern(type, covering) is not null)
            arms.Add("_");
        return arms;
    }

    /// <summary>True for a relational pattern no value of its type matches, such as `< 0` on a uint.</summary>
    public static bool IsEmpty(BoundRelationalPattern pattern) => IntervalOf(pattern) is { } interval && interval.Low > interval.High;

    /// <summary>True if some value matches <paramref name="pattern"/> but none of <paramref name="previous"/>.</summary>
    public static bool IsUseful(IEnumerable<BoundPattern> previous, BoundPattern pattern)
    {
        var rows = previous.Select(p => new[] { Simplify(p) }).ToList();
        return IsUseful(rows, [Simplify(pattern)]);
    }

    /// <summary>
    /// The pattern with its `not` and `and` patterns rewritten as the other kinds, where that's possible: `not null` is
    /// `Some(_)`, `not Red` the enum's other variants, `not 1..5` the integers below 1 and above 5, and `>= 0 and <= 9`
    /// is `0..9`. A part that can't be rewritten (`not "a"`, `[x, ...] and [_, y]`) stays as it is, an opaque test.
    /// </summary>
    private static BoundPattern Simplify(BoundPattern pattern)
    {
        if (!HasNotOrAnd(pattern))
            return pattern;
        return pattern switch
        {
            BoundNotPattern not => Complement(Simplify(not.Pattern)) ?? not,
            BoundAndPattern both => Intersect([.. both.Patterns.Select(Simplify)], both.Type) ?? both,
            BoundOrPattern or => new BoundOrPattern([.. or.Alternatives.Select(Simplify)], or.Type),
            BoundConstructorPattern constructor =>
                new BoundConstructorPattern(constructor.Constructor, [.. constructor.Arguments.Select(Simplify)], constructor.Type),
            BoundTuplePattern tuple => new BoundTuplePattern([.. tuple.Elements.Select(Simplify)], (TupleTypeSymbol)tuple.Type),
            BoundListPattern list => new BoundListPattern([.. list.Elements.Select(Simplify)], list.SliceIndex, list.Slice, list.ElementType, list.Type),
            BoundPropertyPattern properties =>
                new BoundPropertyPattern([.. properties.Properties.Select(p => p with { Pattern = Simplify(p.Pattern) })], properties.Type),
            _ => pattern,
        };
    }

    private static bool HasNotOrAnd(BoundPattern pattern) => pattern switch
    {
        BoundNotPattern or BoundAndPattern => true,
        BoundOrPattern or => or.Alternatives.Any(HasNotOrAnd),
        BoundConstructorPattern constructor => constructor.Arguments.Any(HasNotOrAnd),
        BoundTuplePattern tuple => tuple.Elements.Any(HasNotOrAnd),
        BoundListPattern list => list.Elements.Any(HasNotOrAnd),
        BoundPropertyPattern properties => properties.Properties.Any(p => HasNotOrAnd(p.Pattern)),
        _ => false,
    };

    /// <summary>A pattern matching exactly the values <paramref name="pattern"/> (already simplified) doesn't, or null if there's no such pattern.</summary>
    private static BoundPattern? Complement(BoundPattern pattern)
    {
        var type = pattern.Type;
        if (IsWildcard(pattern))
            return Nothing(type);
        switch (pattern)
        {
            // not (A | B) is (not A) and (not B), and not (A and B) is (not A) | (not B).
            case BoundOrPattern or:
            {
                var complements = or.Alternatives.Select(Complement).ToList();
                return complements.Any(c => c is null) ? null : Intersect([.. complements.OfType<BoundPattern>()], type);
            }
            case BoundAndPattern both:
            {
                var complements = both.Patterns.Select(p => Complement(Simplify(p))).ToList();
                return complements.Any(c => c is null) ? null : Either([.. complements.OfType<BoundPattern>()], type);
            }
            case BoundNotPattern not:
                return Simplify(not.Pattern);
            case BoundLiteralPattern { Value: bool value }:
                return new BoundLiteralPattern(!value, type);
            case var _ when IntervalOf(pattern) is { } interval && Domain(type) is { } domain:
            {
                var pieces = new List<BoundPattern>();
                if (interval.Low > domain.Low)
                    pieces.Add(new IntervalPattern(new(domain.Low, interval.Low - 1), type));
                if (interval.High < domain.High)
                    pieces.Add(new IntervalPattern(new(interval.High + 1, domain.High), type));
                return Either(pieces, type);
            }
            // not C(p, q) is any other constructor, or C(not p, _), or C(_, not q).
            case BoundConstructorPattern or BoundTuplePattern when AllConstructors(type) is { } all:
            {
                var key = KeyOf(pattern)!;
                var arguments = Arguments(pattern).ToList();
                var fieldTypes = FieldTypes(key, type).ToList();
                var alternatives = all.Where(c => !Equals(c, key))
                    .Select(c => Construct(c, [.. FieldTypes(c, type).Select(Wildcard)], type))
                    .ToList();
                for (int i = 0; i < arguments.Count; i++)
                {
                    if (IsWildcard(arguments[i]))
                        continue;
                    if (Complement(arguments[i]) is not { } complement)
                        return null;
                    alternatives.Add(Construct(key, [.. fieldTypes.Select((t, j) => j == i ? complement : Wildcard(t))], type));
                }
                return Either(alternatives, type);
            }
            default:
                return null;
        }
    }

    /// <summary>A pattern matching exactly the values all of <paramref name="patterns"/> (already simplified) match, or null if there's no such pattern.</summary>
    private static BoundPattern? Intersect(List<BoundPattern> patterns, TypeSymbol type)
    {
        var parts = patterns.Where(p => !IsWildcard(p)).ToList();
        if (parts.Count == 0)
            return Wildcard(type);
        if (parts.Count == 1)
            return parts[0];

        // (A | B) and C is (A and C) | (B and C).
        if (parts.OfType<BoundOrPattern>().FirstOrDefault() is { } or)
        {
            var others = parts.Where(p => p != or).ToList();
            var alternatives = new List<BoundPattern>();
            foreach (var alternative in or.Alternatives)
            {
                if (Intersect([alternative, .. others], type) is not { } intersection)
                    return null;
                alternatives.Add(intersection);
            }
            return Either(alternatives, type);
        }

        if (parts.All(p => IntervalOf(p) is not null))
        {
            var intervals = parts.Select(p => IntervalOf(p)!.Value).ToList();
            var low = intervals.Max(i => i.Low);
            var high = intervals.Min(i => i.High);
            return low > high ? Nothing(type) : new IntervalPattern(new(low, high), type);
        }

        // C(p1, q1) and C(p2, q2) is C(p1 and p2, q1 and q2); different constructors match nothing in common.
        if (parts.All(p => p is BoundConstructorPattern or BoundTuplePattern or BoundLiteralPattern { Value: bool }))
        {
            var key = KeyOf(parts[0])!;
            if (parts.Any(p => !Equals(KeyOf(p), key)))
                return Nothing(type);
            var columns = parts.Select(p => Arguments(p).ToList()).ToList();
            var arguments = new List<BoundPattern>();
            for (int i = 0; i < columns[0].Count; i++)
            {
                if (Intersect([.. columns.Select(c => c[i])], columns[0][i].Type) is not { } argument)
                    return null;
                arguments.Add(argument);
            }
            return Construct(key, arguments, type);
        }
        return null;
    }

    /// <summary>A pattern no value matches: an `|` with no alternatives.</summary>
    private static BoundPattern Nothing(TypeSymbol type) => new BoundOrPattern([], type);

    private static BoundPattern Either(List<BoundPattern> alternatives, TypeSymbol type) =>
        alternatives.Count == 1 ? alternatives[0] : new BoundOrPattern([.. alternatives], type);

    /// <summary>A pattern for constructor <paramref name="key"/> of <paramref name="type"/> (see <see cref="KeyOf"/>) with these arguments.</summary>
    private static BoundPattern Construct(object key, List<BoundPattern> arguments, TypeSymbol type) => key switch
    {
        TupleTypeSymbol tuple => new BoundTuplePattern([.. arguments], tuple),
        VariantSymbol or RecordSymbol => new BoundConstructorPattern((Symbol)key, [.. arguments], type),
        _ => new BoundLiteralPattern(key, type),
    };

    /// <summary>The integers or chars in an interval: made by <see cref="Simplify"/>, which may need intervals no pattern names.</summary>
    private sealed class IntervalPattern(Interval interval, TypeSymbol type) : BoundPattern(type)
    {
        public Interval Interval { get; } = interval;
    }

    private static bool IsUseful(List<BoundPattern[]> rows, BoundPattern[] vector)
    {
        if (vector.Length == 0)
            return rows.Count == 0;

        var head = vector[0];
        var rest = vector[1..];

        if (head is BoundOrPattern alternatives)
            return alternatives.Alternatives.Any(a => IsUseful(rows, [a, .. rest]));

        // An interval (or, for a wildcard, the type's whole range) is useful if any piece of it is.
        if ((IntervalOf(head) ?? (IsWildcard(head) && HasIntervalHeads(rows) ? Domain(head.Type) : null)) is { } interval)
            return Split(interval, rows).Any(piece => IsUseful(SpecializeInterval(rows, piece), rest));

        if (head is BoundListPattern || (IsWildcard(head) && HasListHeads(rows)))
        {
            var element = ElementTypeOf(head, rows);
            int longest = LongestList(rows, head);
            return Lengths(head, longest).Any(length =>
                IsUseful(SpecializeList(rows, length, element), [.. ExpandList(head, length, element), .. rest]));
        }

        if (!IsWildcard(head))
        {
            var key = KeyOf(head)!;
            return IsUseful(Specialize(rows, key, head.Type), [.. Arguments(head), .. rest]);
        }

        var all = AllConstructors(head.Type);
        var heads = HeadConstructors(rows);
        if (all is not null && all.All(heads.Contains))
        {
            return all.Any(c =>
                IsUseful(Specialize(rows, c, head.Type), [.. FieldTypes(c, head.Type).Select(Wildcard), .. rest]));
        }

        return IsUseful(Default(rows), rest);
    }

    /// <summary>Returns one example pattern per column that no row matches, or null if the rows cover everything.</summary>
    private static string[]? FindWitness(List<BoundPattern[]> rows, TypeSymbol[] types)
    {
        if (types.Length == 0)
            return rows.Count == 0 ? [] : null;

        var type = types[0];
        var rest = types[1..];

        if (HasListHeads(rows))
        {
            var element = ElementTypeOf(null, rows);
            int longest = LongestList(rows, null);
            for (int length = 0; length <= longest + 1; length++)
            {
                if (FindWitness(SpecializeList(rows, length, element), [.. Enumerable.Repeat(element, length), .. rest]) is not { } witness)
                    continue;
                var elements = witness[..length].Append(length > longest ? "..." : null).OfType<string>();
                return [$"[{string.Join(", ", elements)}]", .. witness[length..]];
            }
            return null;
        }

        if (HasIntervalHeads(rows) && Domain(type) is { } domain)
        {
            // Prefer a piece we can name ('5' or '3..4') over one that runs to the end of the type ('_').
            string[]? found = null;
            foreach (var piece in Split(domain, rows))
            {
                if (FindWitness(SpecializeInterval(rows, piece), rest) is not { } witness)
                    continue;
                bool named = piece.Low == piece.High || (piece.Low > domain.Low && piece.High < domain.High);
                if (named)
                    return [Format(piece, type), .. witness];
                found ??= ["_", .. witness];
            }
            return found;
        }

        var all = AllConstructors(type);
        var heads = HeadConstructors(rows);

        if (all is not null && all.All(heads.Contains))
        {
            foreach (var constructor in all)
            {
                int arity = Arity(constructor);
                var witness = FindWitness(Specialize(rows, constructor, type), [.. FieldTypes(constructor, type), .. rest]);
                if (witness is not null)
                    return [Format(constructor, witness[..arity]), .. witness[arity..]];
            }
            return null;
        }

        var remaining = FindWitness(Default(rows), rest);
        if (remaining is null)
            return null;

        // Name a specific missing constructor when we can (e.g. 'Shape.Rect(_, _)' or 'false'), otherwise '_'.
        var missing = all?.FirstOrDefault(c => !heads.Contains(c));
        var head = missing is not null && heads.Count > 0
            ? Format(missing, Enumerable.Repeat("_", Arity(missing)).ToArray())
            : "_";
        return [head, .. remaining];
    }

    /// <summary>Rows that can match constructor <paramref name="key"/>, with its fields expanded into columns.</summary>
    private static List<BoundPattern[]> Specialize(List<BoundPattern[]> rows, object key, TypeSymbol columnType)
    {
        var result = new List<BoundPattern[]>();
        foreach (var row in ExpandOrHeads(rows))
        {
            var head = row[0];
            if (IsWildcard(head))
                result.Add([.. FieldTypes(key, columnType).Select(Wildcard), .. row[1..]]);
            else if (Equals(KeyOf(head), key))
                result.Add([.. Arguments(head), .. row[1..]]);
        }
        return result;
    }

    /// <summary>Rows that match any value in the first column (wildcards), with that column removed.</summary>
    private static List<BoundPattern[]> Default(List<BoundPattern[]> rows) =>
        ExpandOrHeads(rows).Where(row => IsWildcard(row[0])).Select(row => row[1..]).ToList();

    private static IEnumerable<BoundPattern[]> ExpandOrHeads(List<BoundPattern[]> rows)
    {
        foreach (var row in rows)
        {
            if (row[0] is BoundOrPattern alternatives)
            {
                foreach (var expanded in ExpandOrHeads(alternatives.Alternatives.Select(a => (BoundPattern[])[a, .. row[1..]]).ToList()))
                    yield return expanded;
            }
            else
            {
                yield return row;
            }
        }
    }

    private static List<object> HeadConstructors(List<BoundPattern[]> rows) =>
        ExpandOrHeads(rows).Select(row => KeyOf(row[0])).OfType<object>().Distinct().ToList();

    private static bool HasListHeads(List<BoundPattern[]> rows) => ExpandOrHeads(rows).Any(row => row[0] is BoundListPattern);

    private static TypeSymbol ElementTypeOf(BoundPattern? head, List<BoundPattern[]> rows) =>
        (head as BoundListPattern ?? ExpandOrHeads(rows).Select(row => row[0]).OfType<BoundListPattern>().First()).ElementType;

    /// <summary>The most elements any list pattern among the rows' heads (and <paramref name="head"/>) names.</summary>
    private static int LongestList(List<BoundPattern[]> rows, BoundPattern? head) =>
        ExpandOrHeads(rows).Select(row => row[0]).Append(head).OfType<BoundListPattern>().Max(list => list.Elements.Length);

    /// <summary>The lengths <paramref name="head"/> matches, up to the "longer than <paramref name="longest"/>" one.</summary>
    private static IEnumerable<int> Lengths(BoundPattern head, int longest) => head switch
    {
        BoundListPattern { Slice: null } list => [list.Elements.Length],
        BoundListPattern list => Enumerable.Range(list.Elements.Length, longest + 2 - list.Elements.Length),
        _ => Enumerable.Range(0, longest + 2),
    };

    private static bool MatchesLength(BoundListPattern list, int length) =>
        list.Slice is null ? list.Elements.Length == length : list.Elements.Length <= length;

    /// <summary>A pattern matching lists of <paramref name="length"/> as one pattern per element.</summary>
    private static BoundPattern[] ExpandList(BoundPattern pattern, int length, TypeSymbol element) => pattern switch
    {
        BoundListPattern { Slice: null } list => [.. list.Elements],
        BoundListPattern list =>
        [
            .. list.Elements[..list.SliceIndex],
            .. Enumerable.Repeat(Wildcard(element), length - list.Elements.Length),
            .. list.Elements[list.SliceIndex..],
        ],
        _ => [.. Enumerable.Repeat(Wildcard(element), length)],
    };

    /// <summary>Rows that can match a list of <paramref name="length"/>, with its elements expanded into columns.</summary>
    private static List<BoundPattern[]> SpecializeList(List<BoundPattern[]> rows, int length, TypeSymbol element) =>
        ExpandOrHeads(rows)
            .Where(row => row[0] is not BoundListPattern list || MatchesLength(list, length))
            .Select(row => (BoundPattern[])[.. ExpandList(row[0], length, element), .. row[1..]])
            .ToList();

    /// <summary>An inclusive range of integer or char values.</summary>
    private readonly record struct Interval(Int128 Low, Int128 High)
    {
        public bool Contains(Interval other) => Low <= other.Low && other.High <= High;
    }

    private static Interval? IntervalOf(BoundPattern pattern) => pattern switch
    {
        IntervalPattern interval => interval.Interval,
        BoundLiteralPattern literal when Domain(literal.Type) is not null => new(ToInt128(literal.Value), ToInt128(literal.Value)),
        BoundRangePattern range => new(ToInt128(range.Lower), ToInt128(range.Upper) - (range.IsInclusive ? 0 : 1)),
        BoundRelationalPattern relational when Domain(relational.Type) is { } domain => relational.Operator switch
        {
            "<" => new(domain.Low, ToInt128(relational.Value) - 1),
            "<=" => new(domain.Low, ToInt128(relational.Value)),
            ">" => new(ToInt128(relational.Value) + 1, domain.High),
            _ => new(ToInt128(relational.Value), domain.High),
        },
        _ => null,
    };

    private static Int128 ToInt128(object value) => value switch
    {
        char c => c,
        ulong u => u,
        _ => System.Convert.ToInt64(value),
    };

    /// <summary>All the values of an integer type or char, or null for any other type.</summary>
    private static Interval? Domain(TypeSymbol type) =>
        type == TypeSymbol.Int ? new(int.MinValue, int.MaxValue) :
        type == TypeSymbol.Long ? new(long.MinValue, long.MaxValue) :
        type == TypeSymbol.Byte ? new(byte.MinValue, byte.MaxValue) :
        type == TypeSymbol.SByte ? new(sbyte.MinValue, sbyte.MaxValue) :
        type == TypeSymbol.Short ? new(short.MinValue, short.MaxValue) :
        type == TypeSymbol.UShort ? new(ushort.MinValue, ushort.MaxValue) :
        type == TypeSymbol.UInt ? new(uint.MinValue, uint.MaxValue) :
        type == TypeSymbol.ULong ? new(ulong.MinValue, ulong.MaxValue) :
        type == TypeSymbol.Char ? new(char.MinValue, char.MaxValue) :
        null;

    private static bool HasIntervalHeads(List<BoundPattern[]> rows) =>
        ExpandOrHeads(rows).Any(row => IntervalOf(row[0]) is not null);

    /// <summary>
    /// Splits <paramref name="interval"/> at the bounds of the rows' intervals, so that each row's head covers every
    /// piece completely or not at all.
    /// </summary>
    private static List<Interval> Split(Interval interval, List<BoundPattern[]> rows)
    {
        var cuts = new SortedSet<Int128> { interval.Low, interval.High + 1 };
        foreach (var row in ExpandOrHeads(rows))
        {
            if (IntervalOf(row[0]) is not { } head)
                continue;
            if (head.Low > interval.Low && head.Low <= interval.High)
                cuts.Add(head.Low);
            if (head.High >= interval.Low && head.High < interval.High)
                cuts.Add(head.High + 1);
        }
        var bounds = cuts.ToList();
        return [.. bounds.Zip(bounds.Skip(1), (low, next) => new Interval(low, next - 1))];
    }

    /// <summary>Rows that match every value in <paramref name="piece"/>, with the first column removed.</summary>
    private static List<BoundPattern[]> SpecializeInterval(List<BoundPattern[]> rows, Interval piece) =>
        ExpandOrHeads(rows)
            .Where(row => IsWildcard(row[0]) || (IntervalOf(row[0]) is { } head && head.Contains(piece)))
            .Select(row => row[1..])
            .ToList();

    private static string Format(Interval piece, TypeSymbol type)
    {
        string Value(Int128 value) => type == TypeSymbol.Char ? $"'{(char)value}'" : value.ToString();
        return piece.Low == piece.High ? Value(piece.Low) : $"{Value(piece.Low)}..{Value(piece.High)}";
    }

    /// <summary>Patterns every value matches: a property pattern only tests what it contains, since values aren't null.</summary>
    private static bool IsWildcard(BoundPattern pattern) => pattern switch
    {
        BoundWildcardPattern or BoundBindingPattern or BoundTypePattern { AlwaysMatches: true } => true,
        BoundPropertyPattern properties => properties.Properties.All(p => IsWildcard(p.Pattern)),
        _ => false,
    };

    /// <summary>
    /// The constructor a pattern tests for: a variant or record symbol, a boxed literal, or the pattern itself for a range
    /// or a test the analysis can't see into (a relational pattern on a double, a property pattern).
    /// </summary>
    private static object? KeyOf(BoundPattern pattern) => pattern switch
    {
        _ when IsWildcard(pattern) => null,
        BoundRelationalPattern or BoundPropertyPattern or BoundTypePattern or BoundNotPattern or BoundAndPattern or IntervalPattern => pattern,
        BoundConstructorPattern constructor => constructor.Constructor,
        BoundTuplePattern tuple => tuple.Type,
        BoundLiteralPattern literal => literal.Value,
        BoundRangePattern range => range,
        _ => null,
    };

    private static IEnumerable<BoundPattern> Arguments(BoundPattern pattern) =>
        pattern switch { BoundConstructorPattern constructor => constructor.Arguments, BoundTuplePattern tuple => tuple.Elements, _ => [] };

    private static int Arity(object key) => key switch
    {
        VariantSymbol v => v.Fields.Length,
        RecordSymbol r => r.Fields.Length,
        TupleTypeSymbol t => t.ElementTypes.Length,
        _ => 0,
    };

    /// <summary>Field types of a constructor of <paramref name="columnType"/>, e.g. Int for <c>Some</c> of <c>Option&lt;Int&gt;</c>.</summary>
    private static IEnumerable<TypeSymbol> FieldTypes(object key, TypeSymbol columnType) => key switch
    {
        VariantSymbol variant => variant.Fields.Select(f => TypeSubstitution.FieldType(f, columnType)),
        RecordSymbol record => record.Fields.Select(f => TypeSubstitution.FieldType(f, columnType)),
        TupleTypeSymbol tuple => tuple.ElementTypes,
        _ => [],
    };

    /// <summary>The complete set of constructors for types with finitely many, or null.</summary>
    private static List<object>? AllConstructors(TypeSymbol type) => type.OriginalDefinition switch
    {
        EnumSymbol @enum => [.. @enum.Variants],
        RecordSymbol record => [record],
        TupleTypeSymbol tuple => [tuple],
        _ when type == TypeSymbol.Bool => [true, false],
        _ => null,
    };

    private static BoundPattern Wildcard(TypeSymbol type) => new BoundWildcardPattern(type);

    private static string Format(object key, string[] arguments) => key switch
    {
        VariantSymbol variant => arguments.Length == 0
            ? variant.QualifiedName
            : $"{variant.QualifiedName}({string.Join(", ", arguments)})",
        RecordSymbol record => $"{record.Name}({string.Join(", ", arguments)})",
        TupleTypeSymbol => $"({string.Join(", ", arguments)})",
        bool b => b ? "true" : "false",
        _ => "_",
    };
}
