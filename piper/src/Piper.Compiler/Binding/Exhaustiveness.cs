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
/// constructor. So `1..=5` makes a later `3` unreachable, and `0..=127, 128..=255` exhausts a byte. Any other
/// literal (strings, doubles) is one constructor of an infinite type, never exhausted without a wildcard or binding.
/// </summary>
public static class Exhaustiveness
{
    /// <summary>Returns a description of a value no pattern matches, or null if the patterns are exhaustive.</summary>
    public static string? FindMissingPattern(TypeSymbol type, IEnumerable<BoundPattern> patterns)
    {
        var rows = patterns.Select(p => new[] { p }).ToList();
        return FindWitness(rows, [type])?[0];
    }

    /// <summary>True if some value matches <paramref name="pattern"/> but none of <paramref name="previous"/>.</summary>
    public static bool IsUseful(IEnumerable<BoundPattern> previous, BoundPattern pattern)
    {
        var rows = previous.Select(p => new[] { p }).ToList();
        return IsUseful(rows, [pattern]);
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

        if (HasIntervalHeads(rows) && Domain(type) is { } domain)
        {
            // Prefer a piece we can name ('5' or '3..=4') over one that runs to the end of the type ('_').
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

    /// <summary>An inclusive range of integer or char values.</summary>
    private readonly record struct Interval(Int128 Low, Int128 High)
    {
        public bool Contains(Interval other) => Low <= other.Low && other.High <= High;
    }

    private static Interval? IntervalOf(BoundPattern pattern) => pattern switch
    {
        BoundLiteralPattern literal when Domain(literal.Type) is not null => new(ToInt128(literal.Value), ToInt128(literal.Value)),
        BoundRangePattern range => new(ToInt128(range.Lower), ToInt128(range.Upper) - (range.IsInclusive ? 0 : 1)),
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
        return piece.Low == piece.High ? Value(piece.Low) : $"{Value(piece.Low)}..={Value(piece.High)}";
    }

    private static bool IsWildcard(BoundPattern pattern) => pattern is BoundWildcardPattern or BoundBindingPattern;

    /// <summary>The constructor a pattern tests for: a variant or record symbol, a boxed literal, or the range pattern itself.</summary>
    private static object? KeyOf(BoundPattern pattern) => pattern switch
    {
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
