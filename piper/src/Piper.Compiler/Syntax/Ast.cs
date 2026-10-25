using System.Collections.Immutable;
using Piper.Text;

namespace Piper.Syntax;

/// <summary>Base of the immutable syntax tree. Every node knows the source span it covers.</summary>
public abstract record SyntaxNode(TextSpan Span);

/// <summary>One source file: an optional file-scoped namespace, using directives, then declarations.</summary>
public sealed record CompilationUnitSyntax(
    TextSpan Span,
    SourceText Source,
    NamespaceDeclarationSyntax? Namespace,
    ImmutableArray<UsingDirectiveSyntax> Usings,
    ImmutableArray<DeclarationSyntax> Declarations) : SyntaxNode(Span);

/// <summary><c>A.B.C</c>.</summary>
public sealed record QualifiedNameSyntax(TextSpan Span, ImmutableArray<Token> Parts) : SyntaxNode(Span)
{
    public bool IsMissing => Parts.Any(p => p.IsMissing);

    public string Text => string.Join(".", Parts.Select(p => p.Text));
}

/// <summary><c>namespace Geometry.Shapes;</c> (file-scoped, as in C#).</summary>
public sealed record NamespaceDeclarationSyntax(TextSpan Span, QualifiedNameSyntax Name) : SyntaxNode(Span);

/// <summary><c>using System.Text;</c>: imports a Piper or .NET namespace's members into this file.</summary>
public sealed record UsingDirectiveSyntax(TextSpan Span, QualifiedNameSyntax Name) : SyntaxNode(Span);

// Declarations

/// <summary><see cref="Private"/> is the <c>private</c> modifier: the declaration is only visible in its own file.</summary>
public abstract record DeclarationSyntax(TextSpan Span, Token Name) : SyntaxNode(Span)
{
    public Token? Private { get; init; }

    public ImmutableArray<AttributeSyntax> Attributes { get; init; } = [];

    /// <summary>The text of the `///` comment lines directly above it, if any.</summary>
    public string? Documentation { get; init; }
}

/// <summary>
/// <c>test "adds up" { … }</c> (or <c>async test</c>): a test, run by <c>piper test</c> and left out of other builds.
/// <see cref="DeclarationSyntax.Name"/> is the name's string literal; <see cref="Function"/> is the body as a
/// parameterless function (named after the test's position, so it can't be called).
/// </summary>
public sealed record TestDeclarationSyntax(TextSpan Span, Token Name, string TestName, FunctionDeclarationSyntax Function)
    : DeclarationSyntax(Span, Name);

/// <summary>
/// <c>[Obsolete("Use area")]</c>: a .NET attribute, with constant <see cref="Arguments"/> for its constructor followed
/// by any <c>Name = value</c> assignments to its properties, as in C#.
/// </summary>
public sealed record AttributeSyntax(TextSpan Span, QualifiedNameSyntax Name, ImmutableArray<ExpressionSyntax> Arguments) : SyntaxNode(Span);

/// <summary>
/// A function or method. At most one of <see cref="BlockBody"/> and <see cref="ExpressionBody"/> is set;
/// neither is for a required trait method (<c>fn area() => double;</c>).
/// <see cref="Static"/> is the <c>static</c> modifier; methods without it are instance methods with an implicit <c>this</c>.
/// </summary>
public sealed record FunctionDeclarationSyntax(
    TextSpan Span,
    Token Name,
    ImmutableArray<TypeParameterSyntax> TypeParameters,
    Token? Static,
    ImmutableArray<ParameterSyntax> Parameters,
    TypeSyntax? ReturnType,
    BlockExpressionSyntax? BlockBody,
    ExpressionSyntax? ExpressionBody) : DeclarationSyntax(Span, Name)
{
    public bool HasBody => BlockBody is not null || ExpressionBody is not null;

    /// <summary>The `async` modifier: the function returns a Task, and its body can `await`.</summary>
    public Token? Async { get; init; }

    /// <summary>
    /// The `operator` keyword of `operator +(other: T) => T`, a user-defined operator; <see cref="DeclarationSyntax.Name"/>
    /// is then the operator's token.
    /// </summary>
    public Token? Operator { get; init; }

    /// <summary>The conditions of its `requires` clauses: preconditions on the arguments, checked when it's called.</summary>
    public ImmutableArray<ExpressionSyntax> Requires { get; init; } = [];

    /// <summary>The body contains `yield`, so (as in C#) the function is an iterator.</summary>
    public bool IsIterator { get; init; }

    /// <summary>A class method's `virtual`, `override` or `abstract`.</summary>
    public Token? Modifier { get; init; }

    /// <summary>A class method's `public`, which it is anyway.</summary>
    public Token? Public { get; init; }
}

/// <summary>
/// <c>abstract class Shape(name: string): Base(name), Named { fields and methods }</c>: a mutable reference type with
/// identity, whose fields are private unless <c>public</c> and whose methods are public unless <c>private</c>.
/// <see cref="Parameters"/> are its primary constructor's, usable in field initializers, the base's arguments and its
/// methods, as in C# 12.
/// </summary>
public sealed record ClassDeclarationSyntax(
    TextSpan Span,
    Token Name,
    ImmutableArray<TypeParameterSyntax> TypeParameters,
    ImmutableArray<ParameterSyntax> Parameters,
    ImmutableArray<BaseTypeSyntax> Bases,
    ImmutableArray<ClassFieldSyntax> Fields,
    ImmutableArray<FunctionDeclarationSyntax> Methods) : DeclarationSyntax(Span, Name)
{
    public Token? Abstract { get; init; }

    public Token? Sealed { get; init; }
}

/// <summary>
/// An entry in a class's base list: its base class, with the base constructor's <see cref="Arguments"/>
/// (<c>Exception("No item {id}")</c>), or a trait or interface it implements (no arguments).
/// </summary>
public sealed record BaseTypeSyntax(TextSpan Span, TypeSyntax Type, ImmutableArray<ExpressionSyntax>? Arguments) : SyntaxNode(Span);

/// <summary><c>mut balance: decimal = opening;</c> in a class: a field, private unless <see cref="Public"/>.</summary>
public sealed record ClassFieldSyntax(TextSpan Span, Token Keyword, Token Name, TypeSyntax? Type, ExpressionSyntax Initializer) : SyntaxNode(Span)
{
    public Token? Public { get; init; }

    /// <summary>The text of the `///` comment lines directly above it, if any.</summary>
    public string? Documentation { get; init; }

    public bool IsMutable => Keyword.Kind == TokenKind.MutKeyword;
}

public sealed record RecordDeclarationSyntax(
    TextSpan Span,
    Token Name,
    ImmutableArray<TypeParameterSyntax> TypeParameters,
    ImmutableArray<ParameterSyntax> Fields) : DeclarationSyntax(Span, Name)
{
    /// <summary>The conditions of its `requires` clauses: invariants its fields must meet, checked on construction.</summary>
    public ImmutableArray<ExpressionSyntax> Requires { get; init; } = [];
}

public sealed record EnumDeclarationSyntax(
    TextSpan Span,
    Token Name,
    ImmutableArray<TypeParameterSyntax> TypeParameters,
    ImmutableArray<VariantSyntax> Variants) : DeclarationSyntax(Span, Name);

/// <summary><c>trait Greeter&lt;T&gt;: Named { methods }</c>; <see cref="Parents"/> are the traits it inherits.</summary>
public sealed record TraitDeclarationSyntax(
    TextSpan Span,
    Token Name,
    ImmutableArray<TypeParameterSyntax> TypeParameters,
    ImmutableArray<TypeSyntax> Parents,
    ImmutableArray<FunctionDeclarationSyntax> Methods)
    : DeclarationSyntax(Span, Name);

/// <summary>
/// <c>extend Target: Trait1, Trait2 { methods }</c>. <see cref="DeclarationSyntax.Name"/> is the target type's name token.
/// </summary>
public sealed record ExtendDeclarationSyntax(
    TextSpan Span,
    Token Name,
    TypeSyntax Target,
    ImmutableArray<TypeSyntax> Traits,
    ImmutableArray<FunctionDeclarationSyntax> Methods) : DeclarationSyntax(Span, Name);

/// <summary><c>T</c> or <c>T: Trait1 + Trait2</c>.</summary>
public sealed record TypeParameterSyntax(TextSpan Span, Token Name, ImmutableArray<TypeSyntax> Constraints) : SyntaxNode(Span);

/// <summary>A variant of an enum; <see cref="Fields"/> is empty for a fieldless variant such as <c>Empty</c>.</summary>
public sealed record VariantSyntax(TextSpan Span, Token Name, ImmutableArray<ParameterSyntax> Fields) : SyntaxNode(Span)
{
    /// <summary>The text of the `///` comment lines directly above it, if any.</summary>
    public string? Documentation { get; init; }
}

/// <summary>A function's parameter, or a record's or variant's field, with an optional <see cref="Default"/> value.</summary>
public sealed record ParameterSyntax(TextSpan Span, Token Name, TypeSyntax Type) : SyntaxNode(Span)
{
    public ImmutableArray<AttributeSyntax> Attributes { get; init; } = [];

    /// <summary>`= 443`: the value used when a call leaves the argument out.</summary>
    public ExpressionSyntax? Default { get; init; }

    /// <summary>The text of the `///` comment lines directly above it, if any.</summary>
    public string? Documentation { get; init; }
}

// Types

public abstract record TypeSyntax(TextSpan Span) : SyntaxNode(Span);

/// <summary>
/// <c>Int</c>, a generic instantiation such as <c>Pair&lt;Int, String&gt;</c>, or a qualified name such as
/// <c>System.Text.StringBuilder</c> (<see cref="Qualifier"/> holds the namespace parts).
/// </summary>
public sealed record NamedTypeSyntax(TextSpan Span, ImmutableArray<Token> Qualifier, Token Name, ImmutableArray<TypeSyntax> TypeArguments)
    : TypeSyntax(Span);

/// <summary><c>T?</c>, shorthand for <c>Option&lt;T&gt;</c>.</summary>
public sealed record OptionalTypeSyntax(TextSpan Span, TypeSyntax Inner) : TypeSyntax(Span);

/// <summary><c>Int[]</c>, or <c>Int[,]</c> with <see cref="Rank"/> 2: a .NET array.</summary>
public sealed record ArrayTypeSyntax(TextSpan Span, TypeSyntax Element, int Rank) : TypeSyntax(Span);

/// <summary><c>(Int, String)</c> or <c>(x: Int, y: Int)</c>: a tuple, i.e. a .NET <c>ValueTuple</c> as in C#.</summary>
public sealed record TupleTypeSyntax(TextSpan Span, ImmutableArray<TupleElementTypeSyntax> Elements) : TypeSyntax(Span);

/// <summary>One element of a tuple type; <see cref="Name"/> is optional.</summary>
public sealed record TupleElementTypeSyntax(TextSpan Span, Token? Name, TypeSyntax Type) : SyntaxNode(Span);

/// <summary><c>(int, string) => bool</c>: a function type, i.e. the .NET delegate <c>Func</c> (or <c>Action</c> when it returns unit).</summary>
public sealed record FunctionTypeSyntax(TextSpan Span, ImmutableArray<TypeSyntax> Parameters, TypeSyntax ReturnType) : TypeSyntax(Span);

// Statements

public abstract record StatementSyntax(TextSpan Span) : SyntaxNode(Span);

public sealed record VariableDeclarationSyntax(
    TextSpan Span,
    Token Keyword,
    Token Name,
    TypeSyntax? Type,
    ExpressionSyntax Initializer) : StatementSyntax(Span)
{
    /// <summary><c>using let r = …;</c>: the value is disposed at the end of the enclosing block, and can't be reassigned.</summary>
    public Token? Using { get; init; }

    /// <summary>`await using let r = …;`: disposed with DisposeAsync (an IAsyncDisposable).</summary>
    public Token? Await { get; init; }

    public bool IsMutable => Keyword.Kind == TokenKind.MutKeyword && Using is null;
}

/// <summary>
/// <c>using (let r = open()) { … }</c> or <c>using (expression) { … }</c>: the resource is disposed after the body.
/// <see cref="Name"/> is null in the second form.
/// </summary>
public sealed record UsingStatementSyntax(
    TextSpan Span,
    Token Keyword,
    Token? Name,
    TypeSyntax? Type,
    ExpressionSyntax Resource,
    BlockExpressionSyntax Body) : StatementSyntax(Span)
{
    /// <summary>`await using (…) { … }`: disposed with DisposeAsync.</summary>
    public Token? Await { get; init; }
}

/// <summary><c>let (a, (b, _)) = value;</c>: declares a variable per name, as C#'s <c>var (a, (b, _)) = value;</c>.</summary>
public sealed record DeconstructionDeclarationSyntax(
    TextSpan Span,
    Token Keyword,
    ParenthesizedDesignationSyntax Designation,
    ExpressionSyntax Initializer) : StatementSyntax(Span)
{
    public bool IsMutable => Keyword.Kind == TokenKind.MutKeyword;
}

/// <summary>
/// <c>let Some(user) = Find(id) else { return; };</c>: binds the pattern's variables when the value matches it, and
/// otherwise runs <see cref="Else"/>, which must leave (return, break, continue or throw). Without an else
/// (<c>let Point(x, y) = p;</c>) the pattern must match every value.
/// </summary>
public sealed record LetElseStatementSyntax(TextSpan Span, Token Keyword, PatternSyntax Pattern, ExpressionSyntax Initializer, BlockExpressionSyntax? Else)
    : StatementSyntax(Span);

/// <summary>What a deconstruction declares: a name (<c>_</c> discards), or a parenthesized list of designations.</summary>
public abstract record DesignationSyntax(TextSpan Span) : SyntaxNode(Span);

public sealed record SingleDesignationSyntax(TextSpan Span, Token Name) : DesignationSyntax(Span)
{
    public bool IsDiscard => Name.Text == "_";
}

public sealed record ParenthesizedDesignationSyntax(TextSpan Span, ImmutableArray<DesignationSyntax> Elements) : DesignationSyntax(Span);

public sealed record ExpressionStatementSyntax(TextSpan Span, ExpressionSyntax Expression) : StatementSyntax(Span);

/// <summary>
/// `fn Helper(x: int) => int { … }` inside a block: a function usable throughout that block (even before it, so local
/// functions can call each other), which captures the variables around it, as C#'s local functions do.
/// </summary>
public sealed record LocalFunctionStatementSyntax(TextSpan Span, FunctionDeclarationSyntax Function) : StatementSyntax(Span);

public sealed record WhileStatementSyntax(TextSpan Span, ExpressionSyntax Condition, BlockExpressionSyntax Body)
    : StatementSyntax(Span);

/// <summary><c>for x in xs</c>, or with <see cref="Deconstruction"/> <c>for (key, value) in dictionary</c> (then <see cref="Variable"/> is missing).</summary>
public sealed record ForStatementSyntax(TextSpan Span, Token Variable, ExpressionSyntax Range, BlockExpressionSyntax Body)
    : StatementSyntax(Span)
{
    public ParenthesizedDesignationSyntax? Deconstruction { get; init; }

    /// <summary>`await for x in stream`: iterates an IAsyncEnumerable (C#'s `await foreach`).</summary>
    public Token? Await { get; init; }
}

public sealed record ReturnStatementSyntax(TextSpan Span, Token Keyword, ExpressionSyntax? Value) : StatementSyntax(Span);

/// <summary><c>try { … } catch (e: FormatException) { … } catch { … } finally { … }</c>.</summary>
public sealed record TryStatementSyntax(
    TextSpan Span,
    BlockExpressionSyntax Body,
    ImmutableArray<CatchClauseSyntax> Catches,
    BlockExpressionSyntax? Finally) : StatementSyntax(Span);

/// <summary>
/// <c>catch (e: FormatException)</c>, <c>catch (e)</c> (any exception, bound to <c>e</c>) or a bare <c>catch</c>.
/// </summary>
public sealed record CatchClauseSyntax(TextSpan Span, Token Keyword, Token? Name, TypeSyntax? Type, BlockExpressionSyntax Body)
    : SyntaxNode(Span);

/// <summary><c>defer close(f);</c> or <c>defer { … }</c>: runs <see cref="Body"/> when the enclosing block ends, however it ends.</summary>
public sealed record DeferStatementSyntax(TextSpan Span, Token Keyword, ExpressionSyntax Body) : StatementSyntax(Span);

public sealed record BreakStatementSyntax(TextSpan Span, Token Keyword) : StatementSyntax(Span);

/// <summary>`yield return value;` or (with no <see cref="Value"/>) `yield break;`, in an iterator.</summary>
public sealed record YieldStatementSyntax(TextSpan Span, Token Keyword, ExpressionSyntax? Value) : StatementSyntax(Span);

public sealed record ContinueStatementSyntax(TextSpan Span, Token Keyword) : StatementSyntax(Span);

// Expressions

public abstract record ExpressionSyntax(TextSpan Span) : SyntaxNode(Span);

public sealed record LiteralExpressionSyntax(TextSpan Span, Token Literal, object? Value) : ExpressionSyntax(Span);

public sealed record InterpolatedStringExpressionSyntax(TextSpan Span, ImmutableArray<InterpolatedStringPartSyntax> Parts)
    : ExpressionSyntax(Span);

public abstract record InterpolatedStringPartSyntax(TextSpan Span) : SyntaxNode(Span);

public sealed record InterpolatedTextSyntax(TextSpan Span, string Text) : InterpolatedStringPartSyntax(Span);

/// <summary><c>{expression}</c>, or as in C# <c>{price,10:C}</c> with an <see cref="Alignment"/> and a <see cref="Format"/>.</summary>
public sealed record InterpolationSyntax(TextSpan Span, ExpressionSyntax Expression) : InterpolatedStringPartSyntax(Span)
{
    public int? Alignment { get; init; }

    public string? Format { get; init; }
}

/// <summary><c>x</c>, or <c>identity&lt;Int&gt;</c> with explicit <see cref="TypeArguments"/>.</summary>
public sealed record NameExpressionSyntax(TextSpan Span, Token Identifier) : ExpressionSyntax(Span)
{
    public ImmutableArray<TypeSyntax> TypeArguments { get; init; } = [];
}

public sealed record UnaryExpressionSyntax(TextSpan Span, Token Operator, ExpressionSyntax Operand) : ExpressionSyntax(Span);

/// <summary><c>(Int)x</c>: an explicit conversion, as in C#.</summary>
public sealed record CastExpressionSyntax(TextSpan Span, TypeSyntax Type, ExpressionSyntax Operand) : ExpressionSyntax(Span);

public sealed record BinaryExpressionSyntax(TextSpan Span, ExpressionSyntax Left, Token Operator, ExpressionSyntax Right)
    : ExpressionSyntax(Span);

/// <summary><c>x |> f(a)</c>: calls <see cref="Right"/> with <see cref="Left"/> as its first argument, <c>f(x, a)</c>.</summary>
public sealed record PipeExpressionSyntax(TextSpan Span, ExpressionSyntax Left, Token Operator, ExpressionSyntax Right) : ExpressionSyntax(Span)
{
    /// <summary><c>x ?> f</c>: f is only called when x is Some (or Ok); see <see cref="Operator"/>, the <c>?</c>.</summary>
    public bool IsOptional { get; init; }
}

/// <summary><c>1..10</c> includes its end, as in Swift; <c>1..&lt;10</c> stops before it.</summary>
public sealed record RangeExpressionSyntax(TextSpan Span, ExpressionSyntax Start, Token Operator, ExpressionSyntax End)
    : ExpressionSyntax(Span)
{
    public bool IsInclusive => Operator.Kind == TokenKind.DotDot;
}

public sealed record AssignmentExpressionSyntax(TextSpan Span, ExpressionSyntax Target, Token Operator, ExpressionSyntax Value)
    : ExpressionSyntax(Span);

public sealed record CallExpressionSyntax(TextSpan Span, ExpressionSyntax Callee, ImmutableArray<ExpressionSyntax> Arguments)
    : ExpressionSyntax(Span);

/// <summary><c>target.member</c>, or <c>Enumerable.Empty&lt;Int&gt;</c> with explicit <see cref="TypeArguments"/>.</summary>
public sealed record MemberAccessExpressionSyntax(TextSpan Span, ExpressionSyntax Target, Token Member) : ExpressionSyntax(Span)
{
    public ImmutableArray<TypeSyntax> TypeArguments { get; init; } = [];
}

public sealed record ParenthesizedExpressionSyntax(TextSpan Span, ExpressionSyntax Expression) : ExpressionSyntax(Span);

/// <summary><see cref="Else"/> is either a <see cref="BlockExpressionSyntax"/> or another <see cref="IfExpressionSyntax"/>.</summary>
public sealed record IfExpressionSyntax(TextSpan Span, ExpressionSyntax Condition, BlockExpressionSyntax Then, ExpressionSyntax? Else)
    : ExpressionSyntax(Span)
{
    /// <summary>The <c>?</c> of <c>c ? a : b</c>, which is written as this if.</summary>
    public Token? Question { get; init; }
}

/// <summary>A block is an expression: its value is the optional trailing expression (no semicolon), as in Rust.</summary>
public sealed record BlockExpressionSyntax(TextSpan Span, ImmutableArray<StatementSyntax> Statements, ExpressionSyntax? Trailing)
    : ExpressionSyntax(Span);

public sealed record MatchExpressionSyntax(TextSpan Span, Token Keyword, ExpressionSyntax Scrutinee, ImmutableArray<MatchArmSyntax> Arms)
    : ExpressionSyntax(Span);

public sealed record MatchArmSyntax(TextSpan Span, PatternSyntax Pattern, ExpressionSyntax? Guard, ExpressionSyntax Body)
    : SyntaxNode(Span);

/// <summary>
/// <c>x =&gt; x * 2</c>, <c>(a, b) =&gt; a + b</c>, <c>(x: Int) =&gt; { … }</c>, <c>() =&gt; 42</c>. Parameter types come
/// from the delegate type the context expects, or from annotations.
/// </summary>
public sealed record LambdaExpressionSyntax(TextSpan Span, ImmutableArray<LambdaParameterSyntax> Parameters, ExpressionSyntax Body)
    : ExpressionSyntax(Span)
{
    /// <summary>`async x => …`: the lambda produces a Task, and its body can `await`.</summary>
    public Token? Async { get; init; }
}

/// <summary>`await task`: waits for an awaitable value (a Task, ValueTask, or anything with C#'s `GetAwaiter` pattern).</summary>
public sealed record AwaitExpressionSyntax(TextSpan Span, Token Keyword, ExpressionSyntax Operand) : ExpressionSyntax(Span);

public sealed record LambdaParameterSyntax(TextSpan Span, Token Name, TypeSyntax? Type) : SyntaxNode(Span);

/// <summary><c>target[index]</c>: an array element, a string's character or a .NET indexer.</summary>
public sealed record IndexExpressionSyntax(TextSpan Span, ExpressionSyntax Target, ImmutableArray<ExpressionSyntax> Arguments)
    : ExpressionSyntax(Span);

/// <summary>
/// <c>(1, "a")</c> or <c>(x: 1, y: 2)</c>: a tuple. As the target of an assignment, <c>(a, b) = (b, a)</c>, it
/// deconstructs into existing variables.
/// </summary>
public sealed record TupleExpressionSyntax(TextSpan Span, ImmutableArray<TupleElementSyntax> Elements) : ExpressionSyntax(Span);

/// <summary>One element of a tuple expression; <see cref="Name"/> is optional.</summary>
public sealed record TupleElementSyntax(TextSpan Span, Token? Name, ExpressionSyntax Expression) : SyntaxNode(Span);

/// <summary>
/// `{ ...p, y: 5, height }`: a copy of the record <see cref="Source"/> with some fields changed (C#'s `p with { … }`).
/// A field written alone, like `height`, takes the value of the variable of that name, as in TypeScript.
/// </summary>
public sealed record RecordSpreadExpressionSyntax(TextSpan Span, ExpressionSyntax Source, ImmutableArray<FieldUpdateSyntax> Updates)
    : ExpressionSyntax(Span);

/// <summary>`y: 5`, or `height` alone (<see cref="Value"/> null) for `height: height`.</summary>
public sealed record FieldUpdateSyntax(TextSpan Span, Token Name, ExpressionSyntax? Value) : SyntaxNode(Span);

/// <summary><c>[1, 2, 3]</c>: a <c>List&lt;T&gt;</c> (or another collection the context expects).</summary>
public sealed record CollectionExpressionSyntax(TextSpan Span, ImmutableArray<ExpressionSyntax> Elements) : ExpressionSyntax(Span);

/// <summary><c>...items</c> as an element of a collection literal: every item of a sequence, in order. Only allowed there.</summary>
public sealed record SpreadElementSyntax(TextSpan Span, ExpressionSyntax Source) : ExpressionSyntax(Span);

/// <summary><c>throw ArgumentException("…")</c>; has type Never, so it also works as an expression (<c>x ?? throw …</c>).</summary>
public sealed record ThrowExpressionSyntax(TextSpan Span, Token Keyword, ExpressionSyntax Exception) : ExpressionSyntax(Span);

/// <summary>
/// An argument for a .NET <c>out</c> parameter: <c>out let n</c> or <c>out mut n</c> (declaring n, as C#'s <c>out var n</c>),
/// <c>out n</c> (an existing mutable variable) or <c>out _</c>. Only valid in an argument list.
/// </summary>
public sealed record OutArgumentSyntax(TextSpan Span, Token Keyword, Token? Declaration, Token Name) : ExpressionSyntax(Span)
{
    public bool IsDiscard => Declaration is null && Name.Text == "_";
}

/// <summary><c>retries: 5</c> in an argument list: an argument for the parameter of that name, as in C#.</summary>
public sealed record NamedArgumentSyntax(TextSpan Span, Token Name, ExpressionSyntax Value) : ExpressionSyntax(Span);

/// <summary><c>ref x</c> or <c>ref items[i]</c>: passes a variable or array element to a .NET <c>ref</c> parameter. <c>ref</c> is contextual.</summary>
public sealed record RefArgumentSyntax(TextSpan Span, Token Keyword, ExpressionSyntax Target) : ExpressionSyntax(Span);

/// <summary><c>null</c>: the None of whatever optional type the context expects.</summary>
public sealed record NullLiteralExpressionSyntax(TextSpan Span, Token Keyword) : ExpressionSyntax(Span);

/// <summary><c>value is Pattern</c>; in an if/while condition the pattern's variables are in scope in the body.</summary>
public sealed record IsExpressionSyntax(TextSpan Span, ExpressionSyntax Expression, PatternSyntax Pattern) : ExpressionSyntax(Span);

/// <summary>
/// <c>target?.rest</c>. <see cref="WhenNotNull"/> is the rest of the member/call chain, rooted at an
/// <see cref="ImplicitReceiverSyntax"/> that stands for the unwrapped target (as in C#, <c>a?.b.c</c> skips the whole chain).
/// </summary>
public sealed record ConditionalAccessExpressionSyntax(TextSpan Span, ExpressionSyntax Target, ExpressionSyntax WhenNotNull)
    : ExpressionSyntax(Span);

/// <summary>The unwrapped value inside the <see cref="ConditionalAccessExpressionSyntax.WhenNotNull"/> chain.</summary>
public sealed record ImplicitReceiverSyntax(TextSpan Span) : ExpressionSyntax(Span);

/// <summary><c>operand?</c>: unwraps an Option or Result, returning None/Err from the function if there is no value.</summary>
public sealed record TryExpressionSyntax(TextSpan Span, ExpressionSyntax Operand) : ExpressionSyntax(Span);

/// <summary>Placeholder produced by error recovery; binds to the error type without further diagnostics.</summary>
public sealed record ErrorExpressionSyntax(TextSpan Span) : ExpressionSyntax(Span);

// Patterns

public abstract record PatternSyntax(TextSpan Span) : SyntaxNode(Span);

public sealed record WildcardPatternSyntax(TextSpan Span) : PatternSyntax(Span);

/// <summary><c>c: Circle</c> or <c>_: Circle</c>: a type test, binding the value as that type unless the name is <c>_</c>.</summary>
public sealed record TypePatternSyntax(TextSpan Span, Token Name, TypeSyntax Type) : PatternSyntax(Span);

/// <summary>A bare name: a fieldless variant of the matched enum if one has that name, otherwise a new binding.</summary>
public sealed record IdentifierPatternSyntax(TextSpan Span, Token Identifier) : PatternSyntax(Span);

/// <summary>
/// <c>Circle(r)</c>, <c>Shape.Circle(r)</c>, <c>Shape.Empty</c> or a record deconstruction <c>Point(x, 0)</c>.
/// <see cref="Qualifier"/> is the enum name when written.
/// </summary>
public sealed record ConstructorPatternSyntax(
    TextSpan Span,
    Token? Qualifier,
    Token Name,
    ImmutableArray<PatternSyntax> Arguments,
    bool HasArgumentList) : PatternSyntax(Span);

/// <summary><see cref="Value"/> already includes a leading minus sign, if any.</summary>
public sealed record LiteralPatternSyntax(TextSpan Span, Token Literal, object? Value) : PatternSyntax(Span);

/// <summary><c>1..10</c> includes its end, as in Swift; <c>1..&lt;10</c> stops before it.</summary>
public sealed record RangePatternSyntax(TextSpan Span, LiteralPatternSyntax Start, Token Operator, LiteralPatternSyntax End)
    : PatternSyntax(Span)
{
    public bool IsInclusive => Operator.Kind == TokenKind.DotDot;
}

public sealed record OrPatternSyntax(TextSpan Span, ImmutableArray<PatternSyntax> Alternatives) : PatternSyntax(Span);

/// <summary><c>(0, _)</c>: matches a tuple element by element (C#'s positional pattern).</summary>
public sealed record TuplePatternSyntax(TextSpan Span, ImmutableArray<PatternSyntax> Elements) : PatternSyntax(Span);

/// <summary><c>[]</c>, <c>[first, ...rest]</c>, <c>[_, ..., last]</c>: matches an array, string or list by its elements (C#'s list pattern).</summary>
public sealed record ListPatternSyntax(TextSpan Span, ImmutableArray<PatternSyntax> Elements) : PatternSyntax(Span);

/// <summary><c>...rest</c> or <c>...</c> in a list pattern: any number of elements, bound to <see cref="Name"/> when there is one.</summary>
public sealed record SlicePatternSyntax(TextSpan Span, Token? Name) : PatternSyntax(Span);

/// <summary>
/// <c>Point { x: 0 }</c>, <c>Shape.Rect { w: 1.0 }</c> or <c>{ Length: &gt; 5 }</c>: tests fields or properties by name
/// (C#'s property pattern). <see cref="Name"/> is null for the last form, which matches the value's own type.
/// </summary>
public sealed record PropertyPatternSyntax(TextSpan Span, Token? Qualifier, Token? Name, ImmutableArray<PropertySubpatternSyntax> Properties)
    : PatternSyntax(Span);

/// <summary><c>x: 0</c> in a property pattern.</summary>
public sealed record PropertySubpatternSyntax(TextSpan Span, Token Name, PatternSyntax Pattern) : SyntaxNode(Span);

/// <summary><c>&lt; 0</c>, <c>&gt;= 'a'</c>: compares the value with a constant (C#'s relational pattern).</summary>
public sealed record RelationalPatternSyntax(TextSpan Span, Token Operator, LiteralPatternSyntax Value) : PatternSyntax(Span);

/// <summary><c>null</c> in a pattern: the same as <c>None</c>.</summary>
public sealed record NullPatternSyntax(TextSpan Span) : PatternSyntax(Span);

/// <summary>Placeholder produced by error recovery; binds like a wildcard so no follow-on errors are reported.</summary>
public sealed record ErrorPatternSyntax(TextSpan Span) : PatternSyntax(Span);
