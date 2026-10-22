using System.Collections.Immutable;
using Piper.Syntax;

namespace Piper.Binding;

public abstract class Symbol(string name)
{
    public string Name { get; } = name;

    public override string ToString() => Name;
}

public sealed class FieldSymbol(string name, TypeSymbol type) : Symbol(name)
{
    public TypeSymbol Type { get; } = type;
}

public enum VariableKind
{
    Local,
    Parameter,
    LoopVariable,

    /// <summary>Introduced by the lowerer; never visible to Piper code.</summary>
    Temporary,
}

/// <summary>
/// A local, parameter or loop variable. Variables are compared by identity, so two variables with
/// the same name (shadowing) are distinct symbols; the emitter gives each a unique C# name.
/// </summary>
public sealed class VariableSymbol(string name, TypeSymbol type, bool isMutable, VariableKind kind) : Symbol(name)
{
    public TypeSymbol Type { get; } = type;

    public bool IsMutable { get; } = isMutable;

    public VariableKind Kind { get; } = kind;
}

public sealed class FunctionSymbol(
    string name,
    ImmutableArray<VariableSymbol> parameters,
    TypeSymbol returnType,
    FunctionDeclarationSyntax? declaration) : Symbol(name)
{
    public ImmutableArray<VariableSymbol> Parameters { get; } = parameters;

    public TypeSymbol ReturnType { get; } = returnType;

    /// <summary>Null for builtins.</summary>
    public FunctionDeclarationSyntax? Declaration { get; } = declaration;
}

public static class BuiltinFunctions
{
    /// <summary><c>print(value)</c> accepts a single argument of any type, so its parameter is typed as Error (accepts anything).</summary>
    public static readonly FunctionSymbol Print = new(
        "print",
        [new VariableSymbol("value", TypeSymbol.Error, isMutable: false, VariableKind.Parameter)],
        TypeSymbol.Unit,
        declaration: null);

    public static readonly ImmutableArray<FunctionSymbol> All = [Print];
}
