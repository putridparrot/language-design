using System.Collections.Immutable;
using Piper.Text;

namespace Piper.Syntax;

/// <summary>Base of the immutable syntax tree. Every node knows the source span it covers.</summary>
public abstract record SyntaxNode(TextSpan Span);

public sealed record CompilationUnitSyntax(TextSpan Span, ImmutableArray<DeclarationSyntax> Declarations)
    : SyntaxNode(Span);

// Declarations

public abstract record DeclarationSyntax(TextSpan Span, Token Name) : SyntaxNode(Span);

/// <summary>
/// A function or method. At most one of <see cref="BlockBody"/> and <see cref="ExpressionBody"/> is set;
/// neither is for a required trait method (<c>fn area() -> Double;</c>).
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
}

public sealed record RecordDeclarationSyntax(
    TextSpan Span,
    Token Name,
    ImmutableArray<TypeParameterSyntax> TypeParameters,
    ImmutableArray<ParameterSyntax> Fields) : DeclarationSyntax(Span, Name);

public sealed record EnumDeclarationSyntax(
    TextSpan Span,
    Token Name,
    ImmutableArray<TypeParameterSyntax> TypeParameters,
    ImmutableArray<VariantSyntax> Variants) : DeclarationSyntax(Span, Name);

public sealed record TraitDeclarationSyntax(TextSpan Span, Token Name, ImmutableArray<FunctionDeclarationSyntax> Methods)
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
public sealed record VariantSyntax(TextSpan Span, Token Name, ImmutableArray<ParameterSyntax> Fields) : SyntaxNode(Span);

public sealed record ParameterSyntax(TextSpan Span, Token Name, TypeSyntax Type) : SyntaxNode(Span);

// Types

public abstract record TypeSyntax(TextSpan Span) : SyntaxNode(Span);

/// <summary><c>Int</c> or a generic instantiation such as <c>Pair&lt;Int, String&gt;</c>.</summary>
public sealed record NamedTypeSyntax(TextSpan Span, Token Name, ImmutableArray<TypeSyntax> TypeArguments) : TypeSyntax(Span);

/// <summary><c>T?</c>, shorthand for <c>Option&lt;T&gt;</c>.</summary>
public sealed record OptionalTypeSyntax(TextSpan Span, TypeSyntax Inner) : TypeSyntax(Span);

// Statements

public abstract record StatementSyntax(TextSpan Span) : SyntaxNode(Span);

public sealed record VariableDeclarationSyntax(
    TextSpan Span,
    Token Keyword,
    Token Name,
    TypeSyntax? Type,
    ExpressionSyntax Initializer) : StatementSyntax(Span)
{
    public bool IsMutable => Keyword.Kind == TokenKind.VarKeyword;
}

public sealed record ExpressionStatementSyntax(TextSpan Span, ExpressionSyntax Expression) : StatementSyntax(Span);

public sealed record WhileStatementSyntax(TextSpan Span, ExpressionSyntax Condition, BlockExpressionSyntax Body)
    : StatementSyntax(Span);

public sealed record ForStatementSyntax(TextSpan Span, Token Variable, ExpressionSyntax Range, BlockExpressionSyntax Body)
    : StatementSyntax(Span);

public sealed record ReturnStatementSyntax(TextSpan Span, Token Keyword, ExpressionSyntax? Value) : StatementSyntax(Span);

public sealed record BreakStatementSyntax(TextSpan Span, Token Keyword) : StatementSyntax(Span);

public sealed record ContinueStatementSyntax(TextSpan Span, Token Keyword) : StatementSyntax(Span);

// Expressions

public abstract record ExpressionSyntax(TextSpan Span) : SyntaxNode(Span);

public sealed record LiteralExpressionSyntax(TextSpan Span, Token Literal, object? Value) : ExpressionSyntax(Span);

public sealed record InterpolatedStringExpressionSyntax(TextSpan Span, ImmutableArray<InterpolatedStringPartSyntax> Parts)
    : ExpressionSyntax(Span);

public abstract record InterpolatedStringPartSyntax(TextSpan Span) : SyntaxNode(Span);

public sealed record InterpolatedTextSyntax(TextSpan Span, string Text) : InterpolatedStringPartSyntax(Span);

public sealed record InterpolationSyntax(TextSpan Span, ExpressionSyntax Expression) : InterpolatedStringPartSyntax(Span);

public sealed record NameExpressionSyntax(TextSpan Span, Token Identifier) : ExpressionSyntax(Span);

public sealed record UnaryExpressionSyntax(TextSpan Span, Token Operator, ExpressionSyntax Operand) : ExpressionSyntax(Span);

public sealed record BinaryExpressionSyntax(TextSpan Span, ExpressionSyntax Left, Token Operator, ExpressionSyntax Right)
    : ExpressionSyntax(Span);

public sealed record RangeExpressionSyntax(TextSpan Span, ExpressionSyntax Start, Token Operator, ExpressionSyntax End)
    : ExpressionSyntax(Span)
{
    public bool IsInclusive => Operator.Kind == TokenKind.DotDotEquals;
}

public sealed record AssignmentExpressionSyntax(TextSpan Span, ExpressionSyntax Target, Token Operator, ExpressionSyntax Value)
    : ExpressionSyntax(Span);

public sealed record CallExpressionSyntax(TextSpan Span, ExpressionSyntax Callee, ImmutableArray<ExpressionSyntax> Arguments)
    : ExpressionSyntax(Span);

public sealed record MemberAccessExpressionSyntax(TextSpan Span, ExpressionSyntax Target, Token Member) : ExpressionSyntax(Span);

public sealed record ParenthesizedExpressionSyntax(TextSpan Span, ExpressionSyntax Expression) : ExpressionSyntax(Span);

/// <summary><see cref="Else"/> is either a <see cref="BlockExpressionSyntax"/> or another <see cref="IfExpressionSyntax"/>.</summary>
public sealed record IfExpressionSyntax(TextSpan Span, ExpressionSyntax Condition, BlockExpressionSyntax Then, ExpressionSyntax? Else)
    : ExpressionSyntax(Span);

/// <summary>A block is an expression: its value is the optional trailing expression (no semicolon), as in Rust.</summary>
public sealed record BlockExpressionSyntax(TextSpan Span, ImmutableArray<StatementSyntax> Statements, ExpressionSyntax? Trailing)
    : ExpressionSyntax(Span);

public sealed record MatchExpressionSyntax(TextSpan Span, Token Keyword, ExpressionSyntax Scrutinee, ImmutableArray<MatchArmSyntax> Arms)
    : ExpressionSyntax(Span);

public sealed record MatchArmSyntax(TextSpan Span, PatternSyntax Pattern, ExpressionSyntax? Guard, ExpressionSyntax Body)
    : SyntaxNode(Span);

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

public sealed record RangePatternSyntax(TextSpan Span, LiteralPatternSyntax Start, Token Operator, LiteralPatternSyntax End)
    : PatternSyntax(Span)
{
    public bool IsInclusive => Operator.Kind == TokenKind.DotDotEquals;
}

public sealed record OrPatternSyntax(TextSpan Span, ImmutableArray<PatternSyntax> Alternatives) : PatternSyntax(Span);

/// <summary><c>null</c> in a pattern: the same as <c>None</c>.</summary>
public sealed record NullPatternSyntax(TextSpan Span) : PatternSyntax(Span);

/// <summary>Placeholder produced by error recovery; binds like a wildcard so no follow-on errors are reported.</summary>
public sealed record ErrorPatternSyntax(TextSpan Span) : PatternSyntax(Span);
