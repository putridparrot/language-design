using System.Collections.Immutable;
using Piper.Text;

namespace Piper.Syntax;

/// <summary>
/// A markup literal as the lexer reads it (<c>&lt;p class="x"&gt;Hello, {name}&lt;/p&gt;</c>): its elements, attributes,
/// text and the spans of its holes, whose expressions the parser lexes and parses separately, as it does an
/// interpolated string's. The token's <see cref="Token.Value"/> is the root <see cref="MarkupElement"/>.
/// </summary>
public abstract record MarkupNode(TextSpan Span);

/// <summary>Text between tags, its whitespace already collapsed as JSX does (see <see cref="Lexer"/>).</summary>
public sealed record MarkupText(TextSpan Span, string Text) : MarkupNode(Span);

/// <summary><c>{expression}</c>, or <c>{for x in xs { … }}</c>: <see cref="Expression"/> is between the braces.</summary>
public sealed record MarkupHole(TextSpan Span, TextSpan Expression) : MarkupNode(Span);

/// <summary>
/// <c>&lt;name attributes&gt;children&lt;/name&gt;</c>, <c>&lt;name /&gt;</c>, or a fragment, <c>&lt;&gt;children&lt;/&gt;</c>,
/// whose <see cref="Name"/> is empty. A name starting with a capital, or with a dot in it, is a component: a call to a
/// Piper function.
/// </summary>
public sealed record MarkupElement(
    TextSpan Span, string Name, TextSpan NameSpan, ImmutableArray<MarkupAttribute> Attributes, ImmutableArray<MarkupNode> Children)
    : MarkupNode(Span)
{
    public bool IsFragment => Name.Length == 0;

    public bool IsComponent => Name.Length > 0 && (char.IsUpper(Name[0]) || Name.Contains('.'));
}

/// <summary>
/// <c>name="text"</c> (<see cref="Text"/>), <c>name={expression}</c> (<see cref="Hole"/>), or <c>name</c> alone (neither:
/// a boolean attribute, present).
/// </summary>
public sealed record MarkupAttribute(TextSpan Span, string Name, TextSpan NameSpan, string? Text, MarkupHole? Hole);
