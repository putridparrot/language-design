using System.Collections.Immutable;
using Piper.Syntax;
using Piper.Text;

namespace Piper.Binding;

public abstract class Symbol(string name)
{
    public string Name { get; } = name;

    /// <summary>For top-level declarations (types, traits, functions, methods): the file that declares them.</summary>
    public SourceText? DeclaringFile { get; internal set; }

    /// <summary>For top-level declarations: the namespace they belong to.</summary>
    public NamespaceSymbol? Namespace { get; internal set; }

    /// <summary>Declared <c>private</c>: visible only in <see cref="DeclaringFile"/>.</summary>
    public bool IsPrivate { get; internal set; }

    /// <summary>Its `///` doc comment, for the editor and the generated C#'s XML documentation.</summary>
    public string? Documentation { get; internal set; }

    /// <summary>The .NET attributes written on the declaration, field or parameter.</summary>
    public ImmutableArray<BoundAttribute> Attributes { get; internal set; } = [];

    public override string ToString() => Name;
}

public sealed class FieldSymbol(string name, TypeSymbol type) : Symbol(name)
{
    public TypeSymbol Type { get; } = type;

    /// <summary>The value a construction that leaves this field out gets.</summary>
    public ParameterDefault? Default { get; internal set; }

    /// <summary>A class's `mut` field, which its methods can change.</summary>
    public bool IsMutable { get; init; }

    /// <summary>A class's `public` field, which code outside the class can read (and, if mutable, change).</summary>
    public bool IsPublic { get; init; } = true;

    /// <summary>The class a class field belongs to; null for a record's or variant's.</summary>
    public ClassSymbol? Owner { get; init; }

    /// <summary>A class field's value when an instance is made, bound in a scope of the primary constructor's parameters.</summary>
    public BoundExpression? Initializer { get; internal set; }
}

public enum VariableKind
{
    Local,
    Parameter,
    LoopVariable,

    /// <summary>Bound by a pattern in a match arm.</summary>
    PatternVariable,

    /// <summary>The receiver of an instance method.</summary>
    This,

    /// <summary>
    /// <c>base</c> in a class's instance method: the instance as its base class, so a call through it runs the base
    /// class's version of a method.
    /// </summary>
    Base,

    /// <summary>A class's field, named bare inside the class (see <see cref="VariableSymbol.Field"/>).</summary>
    Field,

    /// <summary>A class's primary constructor parameter, usable throughout the class, as in C# 12.</summary>
    ClassParameter,

    /// <summary>Introduced by the lowerer; never visible to Piper code.</summary>
    Temporary,
}

/// <summary>
/// A local, parameter or loop variable. Variables are compared by identity, so two variables with
/// the same name (shadowing) are distinct symbols; the emitter gives each a unique C# name.
/// </summary>
/// <summary>A parameter's or field's default value: a constant (or None), and its source text for display.</summary>
public sealed record ParameterDefault(BoundExpression Value, string Text);

public sealed class VariableSymbol(string name, TypeSymbol type, bool isMutable, VariableKind kind) : Symbol(name)
{
    /// <summary>For a parameter: the value a call that leaves it out passes.</summary>
    public ParameterDefault? Default { get; internal set; }

    public TypeSymbol Type { get; } = type;

    public bool IsMutable { get; } = isMutable;

    public VariableKind Kind { get; } = kind;

    /// <summary>For a <see cref="VariableKind.Field"/>: the class field it names.</summary>
    public FieldSymbol? Field { get; init; }
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

    /// <summary>Its `requires` conditions, checked when it's called.</summary>
    public ImmutableArray<BoundContract> Preconditions { get; internal set; } = [];

    public TypeSymbol ReturnType { get; } = returnType;

    /// <summary>`async fn`: <see cref="ReturnType"/> is a Task (or ValueTask), and the body awaits.</summary>
    public bool IsAsync { get; init; }

    /// <summary>
    /// What the body produces: <see cref="ReturnType"/>, except in an async function, where it's the Task's result
    /// (`int` for `Task<int>`, unit for `Task`).
    /// </summary>
    public TypeSymbol BodyReturnType { get => field ?? ReturnType; init; }

    /// <summary>
    /// For an iterator (a function that `yield`s), the type of what it yields: `int` for `IEnumerable<int>`. Its body
    /// produces nothing itself (<see cref="BodyReturnType"/> is unit). Null for other functions.
    /// </summary>
    public TypeSymbol? IteratorElementType { get; init; }

    /// <summary>A local function, declared in a block: called by its bare name, and able to use the variables around it.</summary>
    public bool IsLocal { get; init; }

    /// <summary>Null for builtins.</summary>
    public FunctionDeclarationSyntax? Declaration { get; } = declaration;
}

/// <summary>
/// A function declared in a trait or an <c>extend</c> block. Instance methods have an implicit <see cref="This"/>
/// receiver; methods without one are static (<c>Type.name(...)</c>).
/// </summary>
public sealed class MethodSymbol(
    string name,
    ImmutableArray<TypeParameterSymbol> typeParameters,
    ImmutableArray<VariableSymbol> parameters,
    TypeSymbol returnType,
    FunctionDeclarationSyntax declaration,
    TypeSymbol containingType,
    VariableSymbol? thisVariable) : FunctionSymbol(name, typeParameters, parameters, returnType, declaration)
{
    /// <summary>The record or enum definition, builtin type or trait the method belongs to.</summary>
    public TypeSymbol ContainingType { get; } = containingType;

    public VariableSymbol? This { get; } = thisVariable;

    public bool IsStatic => This is null;

    /// <summary>A user-defined operator (`operator +(other: T) => T`), named by its symbol.</summary>
    public bool IsOperator => Declaration?.Operator is not null;

    public TraitSymbol? Trait => ContainingType as TraitSymbol;

    /// <summary>A required trait method, or a class's `abstract fn`: declared without a body.</summary>
    public bool IsAbstract => !Declaration!.HasBody;

    /// <summary>A class's `virtual fn`, which derived classes may override.</summary>
    public bool IsVirtual => Declaration?.Modifier?.Text == "virtual";

    /// <summary>A class's `override fn`, replacing a base class's virtual or abstract method.</summary>
    public bool IsOverride => Declaration?.Modifier?.Text == "override";

    /// <summary>
    /// For a class's override: the base method it overrides, a Piper method or a .NET one (<c>object.ToString</c>,
    /// <c>BackgroundService.ExecuteAsync</c>).
    /// </summary>
    public object? Overridden { get; internal set; }

    /// <summary>
    /// For a trait method that overrides one it inherits: that method, and the parent trait declaring it as seen from
    /// this trait (`Equatable&lt;T&gt;`). It's emitted as an explicit implementation (`bool Equatable&lt;T&gt;.equals(…)`).
    /// </summary>
    public (MethodSymbol Method, TypeSymbol Parent)? Overrides { get; internal set; }
}

public static class BuiltinFunctions
{
    /// <summary><c>Print(value)</c> accepts a single argument of any type, so its parameter is typed as Error (accepts anything).</summary>
    public static readonly FunctionSymbol Print = new(
        "Print",
        [],
        [new VariableSymbol("value", TypeSymbol.Error, isMutable: false, VariableKind.Parameter)],
        TypeSymbol.Unit,
        declaration: null);

    /// <summary><c>Assert(condition)</c> or <c>Assert(condition, message)</c>: fails a test (throws) when the condition is false.</summary>
    public static readonly FunctionSymbol Assert = new(
        "Assert",
        [],
        [
            new VariableSymbol("condition", TypeSymbol.Bool, isMutable: false, VariableKind.Parameter),
            new VariableSymbol("message", TypeSymbol.String, isMutable: false, VariableKind.Parameter),
        ],
        TypeSymbol.Unit,
        declaration: null);

    /// <summary><c>AssertEqual(expected, actual)</c>: fails a test unless the values are equal; they must have a common type.</summary>
    public static readonly FunctionSymbol AssertEqual = new(
        "AssertEqual",
        [],
        [
            new VariableSymbol("expected", TypeSymbol.Error, isMutable: false, VariableKind.Parameter),
            new VariableSymbol("actual", TypeSymbol.Error, isMutable: false, VariableKind.Parameter),
        ],
        TypeSymbol.Unit,
        declaration: null);

    public static readonly ImmutableArray<FunctionSymbol> All = [Print, Assert, AssertEqual];

    /// <summary>
    /// The check of a `requires` condition, put at the start of a function's body (it isn't in scope, so it can't be
    /// written): <c>Require(condition, text, values, parameter)</c>.
    /// </summary>
    public static readonly FunctionSymbol Require = new(
        "Require",
        [],
        [
            new VariableSymbol("condition", TypeSymbol.Bool, isMutable: false, VariableKind.Parameter),
            new VariableSymbol("text", TypeSymbol.String, isMutable: false, VariableKind.Parameter),
            new VariableSymbol("values", TypeSymbol.String, isMutable: false, VariableKind.Parameter),
            new VariableSymbol("parameter", TypeSymbol.String, isMutable: false, VariableKind.Parameter),
        ],
        TypeSymbol.Unit,
        declaration: null);
}
