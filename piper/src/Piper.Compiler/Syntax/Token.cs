using System.Collections.Immutable;
using Piper.Text;

namespace Piper.Syntax;

/// <summary>
/// A lexical token. <see cref="Value"/> holds the decoded value of literals (int, long, double, char, string).
/// Interpolated strings carry their <see cref="Parts"/>; the parser re-lexes each hole as an expression.
/// </summary>
public sealed record Token(TokenKind Kind, TextSpan Span, string Text, object? Value = null)
{
    public ImmutableArray<InterpolationPart> Parts { get; init; } = [];

    /// <summary>True for tokens the parser synthesized during error recovery.</summary>
    public bool IsMissing { get; init; }

    /// <summary>
    /// True for a token in code a plugin wrote (a template's own syntax, or a fresh name), which has the plugin
    /// construct's span so errors point at it, but isn't in the source: the editor doesn't hover, colour or hint it.
    /// </summary>
    public bool IsGenerated { get; init; }
}

/// <summary>
/// The value of <c>2147483648</c> or <c>9223372036854775808L</c>: one past Int's (Long's) maximum, valid only after a
/// unary minus, where the parser folds it into <see cref="Negated"/> (<c>int.MinValue</c>/<c>long.MinValue</c>), as in C#.
/// </summary>
public sealed record NegatableLiteral(object Negated, string TypeName);

public abstract record InterpolationPart(TextSpan Span);

/// <summary>Literal text of an interpolated string, with escapes already decoded.</summary>
public sealed record InterpolationText(TextSpan Span, string Value) : InterpolationPart(Span);

/// <summary>The source span of the expression between <c>{</c> and <c>}</c>.</summary>
public sealed record InterpolationHole(TextSpan Span) : InterpolationPart(Span);
