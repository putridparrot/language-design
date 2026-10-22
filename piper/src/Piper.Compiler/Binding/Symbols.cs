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

    /// <summary>Bound by a pattern in a match arm.</summary>
    PatternVariable,

    /// <summary>The receiver of an instance method.</summary>
    Self,

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

public class FunctionSymbol(
    string name,
    ImmutableArray<TypeParameterSymbol> typeParameters,
    ImmutableArray<VariableSymbol> parameters,
    TypeSymbol returnType,
    FunctionDeclarationSyntax? declaration) : Symbol(name)
{
    public ImmutableArray<TypeParameterSymbol> TypeParameters { get; } = typeParameters;

    public ImmutableArray<VariableSymbol> Parameters { get; } = parameters;

    public TypeSymbol ReturnType { get; } = returnType;

    /// <summary>Null for builtins.</summary>
    public FunctionDeclarationSyntax? Declaration { get; } = declaration;
}

/// <summary>
/// A function declared in a trait or an <c>extend</c> block. Instance methods have a <see cref="Self"/>
/// receiver; methods without one are static (<c>Type.name(...)</c>).
/// </summary>
public sealed class MethodSymbol(
    string name,
    ImmutableArray<TypeParameterSymbol> typeParameters,
    ImmutableArray<VariableSymbol> parameters,
    TypeSymbol returnType,
    FunctionDeclarationSyntax declaration,
    TypeSymbol containingType,
    VariableSymbol? self) : FunctionSymbol(name, typeParameters, parameters, returnType, declaration)
{
    /// <summary>The record or enum definition, builtin type or trait the method belongs to.</summary>
    public TypeSymbol ContainingType { get; } = containingType;

    public VariableSymbol? Self { get; } = self;

    public bool IsStatic => Self is null;

    public TraitSymbol? Trait => ContainingType as TraitSymbol;

    /// <summary>A required trait method, declared without a body.</summary>
    public bool IsAbstract => !Declaration!.HasBody;
}

public static class BuiltinFunctions
{
    /// <summary><c>print(value)</c> accepts a single argument of any type, so its parameter is typed as Error (accepts anything).</summary>
    public static readonly FunctionSymbol Print = new(
        "print",
        [],
        [new VariableSymbol("value", TypeSymbol.Error, isMutable: false, VariableKind.Parameter)],
        TypeSymbol.Unit,
        declaration: null);

    public static readonly ImmutableArray<FunctionSymbol> All = [Print];
}
