using System.Collections.Immutable;

namespace Piper.Binding;

/// <summary>A type-checked pattern. <see cref="Type"/> is the type of the value being matched.</summary>
public abstract class BoundPattern(TypeSymbol type)
{
    public TypeSymbol Type { get; } = type;

    public bool HasBindings => this switch
    {
        BoundBindingPattern => true,
        BoundConstructorPattern constructor => constructor.Arguments.Any(a => a.HasBindings),
        BoundOrPattern either => either.Alternatives.Any(a => a.HasBindings),
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

public sealed class BoundOrPattern(ImmutableArray<BoundPattern> alternatives, TypeSymbol type) : BoundPattern(type)
{
    public ImmutableArray<BoundPattern> Alternatives { get; } = alternatives;
}
