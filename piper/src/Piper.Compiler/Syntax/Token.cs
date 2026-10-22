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
}

public abstract record InterpolationPart(TextSpan Span);

/// <summary>Literal text of an interpolated string, with escapes already decoded.</summary>
public sealed record InterpolationText(TextSpan Span, string Value) : InterpolationPart(Span);

/// <summary>The source span of the expression between <c>{</c> and <c>}</c>.</summary>
public sealed record InterpolationHole(TextSpan Span) : InterpolationPart(Span);
