namespace Piper.Binding;

/// <summary>
/// Pattern-match analysis based on the "usefulness" algorithm from Luc Maranget, "Warnings for pattern
/// matching" (JFP 2007). A pattern matrix has one row per (unguarded) arm. A pattern vector is useful
/// with respect to the matrix if some value matches it but no row: an arm is unreachable when its
/// pattern is not useful against the arms before it, and a match is exhaustive when a wildcard is not
/// useful against all of its arms.
///
/// Constructors are enum variants, record types (a record has exactly one) and true/false. Every
/// other literal or range is treated as one constructor of an infinite type, so those types are never
/// exhausted without a wildcard or binding. That is conservative but sound: overlapping ranges are
/// not detected as unreachable.
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

        if (!IsWildcard(head))
        {
            var key = KeyOf(head)!;
            return IsUseful(Specialize(rows, key, Arity(key)), [.. Arguments(head), .. rest]);
        }

        var all = AllConstructors(head.Type);
        var heads = HeadConstructors(rows);
        if (all is not null && all.All(heads.Contains))
        {
            return all.Any(c =>
                IsUseful(Specialize(rows, c, Arity(c)), [.. FieldTypes(c).Select(Wildcard), .. rest]));
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
        var all = AllConstructors(type);
        var heads = HeadConstructors(rows);

        if (all is not null && all.All(heads.Contains))
        {
            foreach (var constructor in all)
            {
                int arity = Arity(constructor);
                var witness = FindWitness(Specialize(rows, constructor, arity), [.. FieldTypes(constructor), .. rest]);
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
    private static List<BoundPattern[]> Specialize(List<BoundPattern[]> rows, object key, int arity)
    {
        var result = new List<BoundPattern[]>();
        foreach (var row in ExpandOrHeads(rows))
        {
            var head = row[0];
            if (IsWildcard(head))
                result.Add([.. FieldTypes(key).Select(Wildcard), .. row[1..]]);
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

    private static bool IsWildcard(BoundPattern pattern) => pattern is BoundWildcardPattern or BoundBindingPattern;

    /// <summary>The constructor a pattern tests for: a variant or record symbol, a boxed literal, or the range pattern itself.</summary>
    private static object? KeyOf(BoundPattern pattern) => pattern switch
    {
        BoundConstructorPattern constructor => constructor.Constructor,
        BoundLiteralPattern literal => literal.Value,
        BoundRangePattern range => range,
        _ => null,
    };

    private static IEnumerable<BoundPattern> Arguments(BoundPattern pattern) =>
        pattern is BoundConstructorPattern constructor ? constructor.Arguments : [];

    private static int Arity(object key) => FieldTypes(key).Count();

    private static IEnumerable<TypeSymbol> FieldTypes(object key) => key switch
    {
        VariantSymbol variant => variant.Fields.Select(f => f.Type),
        RecordSymbol record => record.Fields.Select(f => f.Type),
        _ => [],
    };

    /// <summary>The complete set of constructors for types with finitely many, or null.</summary>
    private static List<object>? AllConstructors(TypeSymbol type) => type switch
    {
        EnumSymbol @enum => [.. @enum.Variants],
        RecordSymbol record => [record],
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
        bool b => b ? "true" : "false",
        _ => "_",
    };
}
