using System.Collections.Immutable;
using System.Text;
using Piper.Diagnostics;
using Piper.Text;

namespace Piper.Syntax;

/// <summary>
/// Markup literals: <c>&lt;p class="x"&gt;Hello, {name}&lt;/p&gt;</c> is one <see cref="TokenKind.MarkupLiteral"/> token,
/// whose value is the element, read here with its attributes, text and holes; the parser lexes and parses each hole.
/// <para>
/// A <c>&lt;</c> starts markup where an expression starts (after <c>=</c>, <c>=&gt;</c>, <c>(</c>, <c>[</c>, <c>{</c>,
/// <c>,</c>, <c>:</c>, <c>return</c>, <c>??</c>, <c>|&gt;</c>, <c>==</c> or <c>!=</c>, or at the start of a hole) and a letter or <c>&gt;</c>
/// follows it, so <c>a &lt; b</c>, <c>List&lt;int&gt;</c> and a relational pattern (<c>&lt; 0</c>) aren't markup.
/// </para>
/// <para>
/// Text keeps its spaces on one line; whitespace running over lines goes, and lines join with a space, as in JSX, so
/// markup can be indented freely. Text can't contain <c>&lt;</c>, <c>{</c> or <c>}</c>: write <c>&amp;lt;</c>, or a
/// hole, <c>{"{"}</c>. HTML comments are left out.
/// </para>
/// </summary>
public sealed partial class Lexer
{
    /// <summary>The kind of the token before this one, to tell where an expression (so markup) can start.</summary>
    private TokenKind? _previous;

    /// <summary>HTML's elements that have no closing tag, so <c>&lt;br&gt;</c> needs no <c>/&gt;</c>.</summary>
    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr",
    };

    private bool StartsMarkup() =>
        (char.IsAsciiLetter(Peek(1)) || Peek(1) == '>' || StartsDoctype()) &&
        _previous is null or TokenKind.Equals or TokenKind.FatArrow or TokenKind.OpenParen or TokenKind.OpenBracket or
            TokenKind.OpenBrace or TokenKind.Comma or TokenKind.Colon or TokenKind.Semicolon or TokenKind.ReturnKeyword or
            TokenKind.QuestionQuestion or TokenKind.PipeGreater or TokenKind.EqualsEquals or TokenKind.BangEquals;

    private Token ScanMarkup()
    {
        int start = _position;
        var element = StartsDoctype() ? ScanDocument() : ScanElement();
        var span = TextSpan.FromBounds(start, _position);
        return new Token(TokenKind.MarkupLiteral, span, _source.ToString(span), element);
    }

    private bool StartsDoctype() =>
        Peek(1) == '!' && _position + 9 <= _end && _source.ToString(new TextSpan(_position, 9)).Equals("<!doctype", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>&lt;!doctype html&gt;&lt;html&gt;…&lt;/html&gt;</c>: a whole page, as a fragment of its doctype and its element.</summary>
    private MarkupElement ScanDocument()
    {
        int start = _position;
        while (!IsAtEnd && Current != '>')
            _position++;
        if (!IsAtEnd)
            _position++;
        var doctype = new MarkupText(TextSpan.FromBounds(start, _position), _source.ToString(TextSpan.FromBounds(start, _position)));
        SkipMarkupSpace();
        if (Current != '<' || !char.IsAsciiLetter(Peek(1)))
        {
            _diagnostics.ReportInvalidMarkup(doctype.Span, "A doctype is followed by the page's element: <!doctype html><html>…</html>.");
            return new MarkupElement(doctype.Span, "", doctype.Span, [], [doctype]);
        }
        var page = ScanElement();
        return new MarkupElement(TextSpan.FromBounds(start, _position), "", doctype.Span, [], [doctype, page]);
    }

    /// <summary>
    /// <c>&lt;script&gt;</c> and <c>&lt;style&gt;</c> hold code, not markup, as HTML has it: their contents are read as
    /// written, braces and all, up to their closing tag, with no holes.
    /// </summary>
    private ImmutableArray<MarkupNode> ScanRawText(string name, TextSpan nameSpan)
    {
        int start = _position;
        var close = "</" + name;
        while (!IsAtEnd && !(Current == '<' && Peek(1) == '/' && _position + close.Length <= _end &&
                             _source.ToString(new TextSpan(_position, close.Length)).Equals(close, StringComparison.OrdinalIgnoreCase)))
            _position++;
        var content = _source.ToString(TextSpan.FromBounds(start, _position));
        if (IsAtEnd)
        {
            _closeNameSpan = null;
            _diagnostics.ReportInvalidMarkup(nameSpan, $"<{name}> isn't closed: expected </{name}>.");
            return content.Length > 0 ? [new MarkupText(TextSpan.FromBounds(start, _position), content)] : [];
        }
        _position += close.Length;
        SkipMarkupSpace();
        if (Current == '>')
            _position++;
        return content.Length > 0 ? [new MarkupText(TextSpan.FromBounds(start, _position), content)] : [];
    }

    private MarkupElement ScanElement()
    {
        int start = _position;
        _position++;   // <
        if (Current == '>')
        {
            _position++;
            var fragment = ScanChildren(start, "", new TextSpan(start, 2));
            return new MarkupElement(TextSpan.FromBounds(start, _position), "", new TextSpan(start, 2), [], fragment);
        }

        int nameStart = _position;
        while (IsMarkupNameChar(Current))
            _position++;
        var nameSpan = TextSpan.FromBounds(nameStart, _position);
        var name = _source.ToString(nameSpan);

        var attributes = ImmutableArray.CreateBuilder<MarkupAttribute>();
        while (true)
        {
            SkipMarkupSpace();
            if (IsAtEnd)
            {
                _diagnostics.ReportInvalidMarkup(nameSpan, $"The tag <{name}> isn't finished: expected '>' or '/>'.");
                return new MarkupElement(TextSpan.FromBounds(start, _position), name, nameSpan, attributes.ToImmutable(), []);
            }
            if (Current == '/' && Peek(1) == '>')
            {
                _position += 2;
                return new MarkupElement(TextSpan.FromBounds(start, _position), name, nameSpan, attributes.ToImmutable(), []);
            }
            if (Current == '>')
            {
                _position++;
                break;
            }
            if (IsAttributeStart(Current))
                attributes.Add(ScanAttribute());
            else
            {
                _diagnostics.ReportInvalidMarkup(new TextSpan(_position, 1), $"'{Current}' can't be in a tag: expected an attribute, '>' or '/>'.");
                _position++;
            }
        }

        if (VoidElements.Contains(name))
            return new MarkupElement(TextSpan.FromBounds(start, _position), name, nameSpan, attributes.ToImmutable(), []);
        _closeNameSpan = null;
        var children = name.ToLowerInvariant() is "script" or "style" ? ScanRawText(name, nameSpan) : ScanChildren(start, name, nameSpan);
        var closeNameSpan = _closeNameSpan;
        return new MarkupElement(TextSpan.FromBounds(start, _position), name, nameSpan, attributes.ToImmutable(), children) { CloseNameSpan = closeNameSpan };
    }

    /// <summary>An element's contents, up to and including its closing tag (<c>&lt;/name&gt;</c>, or <c>&lt;/&gt;</c> for a fragment).</summary>
    private ImmutableArray<MarkupNode> ScanChildren(int elementStart, string name, TextSpan nameSpan)
    {
        _openTags.Add(name);
        try
        {
            return ScanChildrenOf(name, nameSpan);
        }
        finally
        {
            _openTags.RemoveAt(_openTags.Count - 1);
        }
    }

    /// <summary>Where the closing tag just read names its element (<c>Card</c> in <c>&lt;/Card&gt;</c>); null if it didn't.</summary>
    private TextSpan? _closeNameSpan;

    /// <summary>The elements being read, outermost first, so a closing tag of an enclosing one ends an unclosed child.</summary>
    private readonly List<string> _openTags = [];

    private ImmutableArray<MarkupNode> ScanChildrenOf(string name, TextSpan nameSpan)
    {
        var children = ImmutableArray.CreateBuilder<MarkupNode>();
        var text = new StringBuilder();
        int textStart = _position;
        // A stray '}' in text is reported once the element closes: if it never does, the code after it was read as
        // its text, and the one error to give is that it isn't closed.
        var strays = new List<int>();
        void ReportStrays()
        {
            foreach (var at in strays)
                _diagnostics.ReportInvalidMarkup(new TextSpan(at, 1), "A '}' in markup's text is written {\"}\"}.");
        }

        void Flush()
        {
            if (text.Length > 0 && Collapse(text.ToString()) is { Length: > 0 } collapsed)
                children.Add(new MarkupText(TextSpan.FromBounds(textStart, _position), collapsed));
            text.Clear();
        }

        while (true)
        {
            if (IsAtEnd)
            {
                Flush();
                _closeNameSpan = null;
                _diagnostics.ReportInvalidMarkup(nameSpan, $"<{name}> isn't closed: expected </{name}>.");
                return children.ToImmutable();
            }
            if (Current == '<' && Peek(1) == '/')
            {
                Flush();
                int closeStart = _position;
                _position += 2;
                int closeNameStart = _position;
                while (IsMarkupNameChar(Current))
                    _position++;
                var closeName = _source.ToString(TextSpan.FromBounds(closeNameStart, _position));
                // </div> while reading a <p> inside a <div>: the <p> wasn't closed, and the </div> is the div's.
                if (closeName != name && _openTags.Take(_openTags.Count - 1).Contains(closeName))
                {
                    _closeNameSpan = null;
                    _diagnostics.ReportInvalidMarkup(nameSpan, $"<{name}> isn't closed: expected </{name}> before </{closeName}>.");
                    _position = closeStart;
                    ReportStrays();
                    return children.ToImmutable();
                }
                SkipMarkupSpace();
                if (Current == '>')
                    _position++;
                else
                    _diagnostics.ReportInvalidMarkup(TextSpan.FromBounds(closeStart, _position), $"The closing tag </{closeName}> isn't finished: expected '>'.");
                _closeNameSpan = closeName == name ? TextSpan.FromBounds(closeNameStart, closeNameStart + closeName.Length) : null;
                if (closeName != name)
                    _diagnostics.ReportInvalidMarkup(TextSpan.FromBounds(closeStart, _position), $"</{closeName}> doesn't close <{name}>: expected </{name}>.");
                ReportStrays();
                return children.ToImmutable();
            }
            if (Current == '<' && Peek(1) == '!' && Peek(2) == '-' && Peek(3) == '-')
            {
                Flush();
                int commentStart = _position;
                while (!IsAtEnd && !(Current == '-' && Peek(1) == '-' && Peek(2) == '>'))
                    _position++;
                if (IsAtEnd)
                    _diagnostics.ReportInvalidMarkup(new TextSpan(commentStart, 4), "This comment isn't closed: expected '-->'.");
                else
                    _position += 3;
                textStart = _position;
                continue;
            }
            if (Current == '<' && (char.IsAsciiLetter(Peek(1)) || Peek(1) == '>'))
            {
                Flush();
                children.Add(ScanElement());
                textStart = _position;
                continue;
            }
            if (Current == '{')
            {
                Flush();
                children.Add(ScanHole());
                textStart = _position;
                continue;
            }
            if (Current == '<')
                _diagnostics.ReportInvalidMarkup(new TextSpan(_position, 1), "A '<' in markup's text is written &lt;.");
            else if (Current == '}')
                strays.Add(_position);
            text.Append(Current);
            _position++;
        }
    }

    private MarkupAttribute ScanAttribute()
    {
        int start = _position;
        while (IsAttributeChar(Current))
            _position++;
        var nameSpan = TextSpan.FromBounds(start, _position);
        var name = _source.ToString(nameSpan);

        int afterName = _position;
        SkipMarkupSpace();
        if (Current != '=')
        {
            _position = afterName;
            return new MarkupAttribute(nameSpan, name, nameSpan, null, null);
        }
        _position++;
        SkipMarkupSpace();
        if (Current is '"' or '\'')
        {
            char quote = Current;
            int valueStart = ++_position;
            while (!IsAtEnd && Current != quote)
                _position++;
            var value = _source.ToString(TextSpan.FromBounds(valueStart, _position));
            if (IsAtEnd)
                _diagnostics.ReportInvalidMarkup(nameSpan, $"The value of '{name}' isn't closed: expected {quote}.");
            else
                _position++;
            return new MarkupAttribute(TextSpan.FromBounds(start, _position), name, nameSpan, value, null);
        }
        if (Current == '{')
        {
            var hole = ScanHole();
            return new MarkupAttribute(TextSpan.FromBounds(start, _position), name, nameSpan, null, hole);
        }
        _diagnostics.ReportInvalidMarkup(nameSpan, $"Expected a value for '{name}': \"text\" or {{expression}}.");
        return new MarkupAttribute(TextSpan.FromBounds(start, _position), name, nameSpan, null, null);
    }

    /// <summary>
    /// <c>{…}</c>: where it ends is found by lexing its contents (with a lexer of its own, whose errors the parser's lex
    /// reports), so braces in strings, characters and nested markup don't end it early.
    /// </summary>
    private MarkupHole ScanHole()
    {
        int open = _position;
        var inside = new Lexer(_source, new DiagnosticBag(), TextSpan.FromBounds(open + 1, _end));
        int depth = 0;
        while (true)
        {
            var token = inside.NextToken();
            if (token.Kind == TokenKind.EndOfFile)
            {
                _diagnostics.ReportInvalidMarkup(new TextSpan(open, 1), "This '{' isn't closed: expected '}'.");
                _position = _end;
                return new MarkupHole(TextSpan.FromBounds(open, _end), TextSpan.FromBounds(open + 1, _end));
            }
            if (token.Kind == TokenKind.OpenBrace)
                depth++;
            else if (token.Kind == TokenKind.CloseBrace && depth-- == 0)
            {
                _position = token.Span.End;
                return new MarkupHole(TextSpan.FromBounds(open, _position), TextSpan.FromBounds(open + 1, token.Span.Start));
            }
        }
    }

    /// <summary>
    /// JSX's rule for text: on one line it's kept as written; across lines, each line's indentation and trailing spaces
    /// go, blank lines go, and the rest join with single spaces.
    /// </summary>
    private static string Collapse(string text)
    {
        if (!text.Contains('\n'))
            return text;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Replace('\t', ' ');
            if (i > 0)
                line = line.TrimStart();
            if (i < lines.Length - 1)
                line = line.TrimEnd();
            if (line.Length > 0)
                kept.Add(line);
        }
        return string.Join(" ", kept);
    }

    private void SkipMarkupSpace()
    {
        while (!IsAtEnd && char.IsWhiteSpace(Current))
            _position++;
    }

    private static bool IsMarkupNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or ':' or '_';

    private static bool IsAttributeStart(char c) => char.IsAsciiLetter(c) || c is '_' or ':' or '@';

    private static bool IsAttributeChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or ':' or '@' or '_' or '.';
}
