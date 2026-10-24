using System.Collections.Immutable;
using System.Reflection;

namespace Piper.Binding;

/// <summary>A type-checked pattern. <see cref="Type"/> is the type of the value being matched.</summary>
public abstract class BoundPattern(TypeSymbol type)
{
    public TypeSymbol Type { get; } = type;

    public bool HasBindings => this switch
    {
        BoundBindingPattern => true,
        BoundConstructorPattern constructor => constructor.Arguments.Any(a => a.HasBindings),
        BoundTuplePattern tuple => tuple.Elements.Any(e => e.HasBindings),
        BoundOrPattern either => either.Alternatives.Any(a => a.HasBindings),
        BoundListPattern list => list.Elements.Any(e => e.HasBindings) || list.Slice is { HasBindings: true },
        BoundPropertyPattern properties => properties.Properties.Any(p => p.Pattern.HasBindings),
        _ => false,
    };
}

/// <summary><c>_</c>; also produced for patterns that failed to bind, so they don't cause follow-on errors.</summary>
public sealed class BoundWildcardPattern(TypeSymbol type) : BoundPattern(type);

public sealed class BoundBindingPattern(VariableSymbol variable) : BoundPattern(variable.Type)
{
    public VariableSymbol Variable { get; } = variable;
}

/// <summary>The value has already been converted to <see cref="BoundPattern.Type"/> (e.g. an Int literal matching a Long).</summary>
public sealed class BoundLiteralPattern(object value, TypeSymbol type) : BoundPattern(type)
{
    public object Value { get; } = value;
}

public sealed class BoundRangePattern(object lower, object upper, bool isInclusive, TypeSymbol type) : BoundPattern(type)
{
    public object Lower { get; } = lower;

    public object Upper { get; } = upper;

    public bool IsInclusive { get; } = isInclusive;
}

/// <summary>An enum variant (<see cref="VariantSymbol"/>) or a record deconstruction (<see cref="RecordSymbol"/>).</summary>
public sealed class BoundConstructorPattern(Symbol constructor, ImmutableArray<BoundPattern> arguments, TypeSymbol type)
    : BoundPattern(type)
{
    public Symbol Constructor { get; } = constructor;

    public ImmutableArray<BoundPattern> Arguments { get; } = arguments;
}

/// <summary><c>(0, _)</c>: one pattern per element of a tuple.</summary>
public sealed class BoundTuplePattern(ImmutableArray<BoundPattern> elements, TupleTypeSymbol type) : BoundPattern(type)
{
    public ImmutableArray<BoundPattern> Elements { get; } = elements;
}

public sealed class BoundOrPattern(ImmutableArray<BoundPattern> alternatives, TypeSymbol type) : BoundPattern(type)
{
    public ImmutableArray<BoundPattern> Alternatives { get; } = alternatives;
}

/// <summary>
/// <c>[a, ...rest, z]</c>: <see cref="Elements"/> are the element patterns, and when there's a <c>...</c>,
/// <see cref="Slice"/> (a wildcard, or a binding of the matched type) stands for the elements between
/// <see cref="Elements"/>[..<see cref="SliceIndex"/>] and the rest.
/// </summary>
public sealed class BoundListPattern(ImmutableArray<BoundPattern> elements, int sliceIndex, BoundPattern? slice, TypeSymbol elementType, TypeSymbol type)
    : BoundPattern(type)
{
    public ImmutableArray<BoundPattern> Elements { get; } = elements;

    public int SliceIndex { get; } = sliceIndex;

    public BoundPattern? Slice { get; } = slice;

    public TypeSymbol ElementType { get; } = elementType;
}

/// <summary><c>{ Length: &gt; 5 }</c> on a .NET type (on a Piper record it's a <see cref="BoundConstructorPattern"/>).</summary>
public sealed class BoundPropertyPattern(ImmutableArray<BoundPropertySubpattern> properties, TypeSymbol type) : BoundPattern(type)
{
    public ImmutableArray<BoundPropertySubpattern> Properties { get; } = properties;
}

/// <summary>A property or field of a .NET type, and the pattern its value must match.</summary>
public sealed record BoundPropertySubpattern(MemberInfo Member, BoundPattern Pattern);

/// <summary><c>&lt; 0</c>: <see cref="Operator"/> is <c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c> or <c>&gt;=</c>, and the value is already of the matched type.</summary>
public sealed class BoundRelationalPattern(string @operator, object value, TypeSymbol type) : BoundPattern(type)
{
    public string Operator { get; } = @operator;

    public object Value { get; } = value;
}
