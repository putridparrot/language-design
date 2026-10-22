using System.Collections.Immutable;
using Piper.Text;

namespace Piper.Syntax;

/// <summary>Base of the immutable syntax tree. Every node knows the source span it covers.</summary>
public abstract record SyntaxNode(TextSpan Span);

public sealed record CompilationUnitSyntax(TextSpan Span, ImmutableArray<DeclarationSyntax> Declarations)
    : SyntaxNode(Span);

// Declarations

public abstract record DeclarationSyntax(TextSpan Span, Token Name) : SyntaxNode(Span);

/// <summary>Exactly one of <see cref="BlockBody"/> and <see cref="ExpressionBody"/> is set.</summary>
public sealed record FunctionDeclarationSyntax(
    TextSpan Span,
    Token Name,
    ImmutableArray<ParameterSyntax> Parameters,
    TypeSyntax? ReturnType,
    BlockExpressionSyntax? BlockBody,
    ExpressionSyntax? ExpressionBody) : DeclarationSyntax(Span, Name);

public sealed record RecordDeclarationSyntax(TextSpan Span, Token Name, ImmutableArray<ParameterSyntax> Fields)
    : DeclarationSyntax(Span, Name);

public sealed record EnumDeclarationSyntax(TextSpan Span, Token Name, ImmutableArray<VariantSyntax> Variants)
    : DeclarationSyntax(Span, Name);

/// <summary>A variant of an enum; <see cref="Fields"/> is empty for a fieldless variant such as <c>Empty</c>.</summary>
public sealed record VariantSyntax(TextSpan Span, Token Name, ImmutableArray<ParameterSyntax> Fields) : SyntaxNode(Span);

public sealed record ParameterSyntax(TextSpan Span, Token Name, TypeSyntax Type) : SyntaxNode(Span);

// Types

public abstract record TypeSyntax(TextSpan Span) : SyntaxNode(Span);

public sealed record NamedTypeSyntax(TextSpan Span, Token Name) : TypeSyntax(Span);

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

/// <summary>Placeholder produced by error recovery; binds like a wildcard so no follow-on errors are reported.</summary>
public sealed record ErrorPatternSyntax(TextSpan Span) : PatternSyntax(Span);
