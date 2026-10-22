using System.Collections.Immutable;
using Piper.Syntax;

namespace Piper.Binding;

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

    public override string ToString() => Name;
}

public sealed class RecordSymbol : TypeSymbol
{
    public RecordSymbol(RecordDeclarationSyntax declaration) : base(declaration.Name.Text)
    {
        Declaration = declaration;
    }

    public RecordDeclarationSyntax Declaration { get; }

    public ImmutableArray<FieldSymbol> Fields { get; internal set; } = [];
}

/// <summary>A sum type: a value is exactly one of its <see cref="Variants"/>.</summary>
public sealed class EnumSymbol : TypeSymbol
{
    public EnumSymbol(EnumDeclarationSyntax declaration) : base(declaration.Name.Text)
    {
        Declaration = declaration;
    }

    public EnumDeclarationSyntax Declaration { get; }

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
