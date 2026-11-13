using System.Collections.Immutable;
using System.Text;
using Piper.Plugins;
using Piper.Text;

namespace Piper.Syntax;

/// <summary>
/// Markup literals, turned into ordinary Piper calls to Piper.Runtime's <c>PiperHtml</c>, which the binder checks as any
/// other code:
/// <list type="bullet">
/// <item>An element is its tags and attributes, as trusted markup, around its children, joined by <c>Concat</c>.</item>
/// <item>A hole is <c>PiperHtml.Hole(expression)</c>, whose overloads escape it by its type (<c>{price:C}</c> passes the
/// format too). <c>{for x in xs { … }}</c> is <c>PiperHtml.Each(xs, x =&gt; …)</c>, and an <c>if</c> without an
/// <c>else</c> renders nothing when false.</item>
/// <item>An attribute's hole is <c>PiperHtml.Attribute("name", expression)</c>.</item>
/// <item>A component, <c>&lt;Card title="x"&gt;…&lt;/Card&gt;</c>, is a call, <c>Card(title: "x", children: …)</c>, with
/// its children as a fragment.</item>
/// </list>
/// </summary>
public sealed partial class Parser
{
    private const string Runtime = "Piper.Runtime.PiperHtml";

    private ExpressionSyntax ParseMarkup()
    {
        var token = NextToken();
        return Markup((MarkupElement)token.Value!);
    }

    private ExpressionSyntax Markup(MarkupElement element)
    {
        var template = new SyntaxTemplate(element.Span);
        if (element.IsComponent)
            return Component(element, template);

        // Trusted markup, and expressions, in order; adjacent markup joined.
        var parts = new List<object>();
        void Add(string markup)
        {
            if (parts.Count > 0 && parts[^1] is string previous)
                parts[^1] = previous + markup;
            else
                parts.Add(markup);
        }

        if (!element.IsFragment)
        {
            Add("<" + element.Name);
            foreach (var attribute in element.Attributes)
            {
                if (attribute.Hole is { } hole)
                {
                    if (HoleExpression(hole, allowFormat: false) is { } value)
                        parts.Add(template.Expression($"{Runtime}.Attribute($name, $value)", ("name", Text(attribute.NameSpan, attribute.Name)), ("value", value)));
                }
                else if (attribute.Text is { } text)
                    Add($" {attribute.Name}=\"{text.Replace("\"", "&quot;")}\"");
                else
                    Add(" " + attribute.Name);
            }
            Add(">");
            // A form that posts carries its anti-forgery token, which C# leaves to be remembered.
            if (element.Name.Equals("form", StringComparison.OrdinalIgnoreCase) &&
                element.Attributes.Any(a => a.Name.Equals("method", StringComparison.OrdinalIgnoreCase) && a.Text?.Equals("post", StringComparison.OrdinalIgnoreCase) == true))
                parts.Add(template.Expression($"{Runtime}.FormToken()"));
        }
        foreach (var child in element.Children)
        {
            switch (child)
            {
                case MarkupText text:
                    Add(text.Text);
                    break;
                case MarkupElement nested:
                    parts.Add(Markup(nested));
                    break;
                case MarkupHole hole when Child(hole, template) is { } expression:
                    parts.Add(expression);
                    break;
            }
        }
        // A void element (<br>) has no closing tag; any other written <div /> closes, as HTML has no self-closing tags.
        if (!element.IsFragment && !IsVoidElement(element.Name))
            Add($"</{element.Name}>");

        var arguments = parts.Select(p => p as ExpressionSyntax ?? template.Expression($"{Runtime}.Trusted($markup)", ("markup", Text(element.Span, (string)p)))).ToList();
        return arguments.Count == 0
            ? template.Expression($"{Runtime}.Trusted(\"\")")
            : template.Expression($"{Runtime}.Concat($parts)", ("parts", arguments));
    }

    /// <summary><c>&lt;Countdown until={ends} /&gt;</c> is <c>Countdown(until: ends)</c>; its children, if any, are a <c>children</c> argument.</summary>
    private ExpressionSyntax Component(MarkupElement element, SyntaxTemplate template)
    {
        var arguments = new List<string>();
        var holes = new List<(string, object)>();
        foreach (var attribute in element.Attributes)
        {
            if (!IsParameterName(attribute.Name))
            {
                _diagnostics.ReportInvalidMarkup(attribute.NameSpan, $"'{attribute.Name}' isn't a parameter name, which a component's attributes are: <{element.Name} {Camel(attribute.Name)}=…>.");
                continue;
            }
            object? value = attribute.Hole is { } hole ? HoleExpression(hole, allowFormat: false)
                : attribute.Text is { } text ? Text(attribute.Span, text)
                : template.Expression("true");
            if (value is null)
                continue;
            var name = $"a{holes.Count}";
            arguments.Add($"{attribute.Name}: ${name}");
            holes.Add((name, value));
        }
        if (!element.Children.IsEmpty)
        {
            var fragment = element with { Name = "", Attributes = [] };
            arguments.Add("children: $children");
            holes.Add(("children", Markup(fragment)));
        }
        return template.Expression($"{element.Name}({string.Join(", ", arguments)})", [.. holes]);
    }

    /// <summary>A child hole: <c>{expression}</c> through <c>Hole</c>, or <c>{for x in xs { … }}</c> through <c>Each</c>.</summary>
    private ExpressionSyntax? Child(MarkupHole hole, SyntaxTemplate template)
    {
        var tokens = Lexer.Tokenize(_source, _diagnostics, hole.Expression);
        if (tokens is [{ Kind: TokenKind.EndOfFile }])
            return null;   // {} or {/* a comment */}
        if (tokens[0].Kind == TokenKind.ForKeyword)
            return Each(hole, tokens, template);

        var (span, _, format) = SplitHole(hole.Expression);
        if (HoleExpression(hole, allowFormat: true) is not { } expression)
            return null;
        return format is null
            ? template.Expression($"{Runtime}.Hole($value)", ("value", expression))
            : template.Expression($"{Runtime}.Hole($value, $format)", ("value", expression), ("format", Text(TextSpan.FromBounds(span.End, hole.Expression.End), format)));
    }

    /// <summary><c>{for lot in lots { &lt;li&gt;…&lt;/li&gt; }}</c>: the body's markup for each item, as a lambda given to <c>Each</c>.</summary>
    private ExpressionSyntax? Each(MarkupHole hole, ImmutableArray<Token> tokens, SyntaxTemplate template)
    {
        var parser = HoleParser(tokens);
        var loop = parser.ParseFor();
        if (parser.Current.Kind != TokenKind.EndOfFile)
            _diagnostics.ReportInvalidMarkup(parser.Current.Span, "A 'for' in markup is the whole of its '{…}': {for x in xs { <li>{x}</li> }}.");
        if (loop.Deconstruction is { } deconstruction)
        {
            var designation = _source.ToString(deconstruction.Span);
            return template.Expression($"{Runtime}.Each($items, (__markupItem) => {{ let {designation} = __markupItem; $body }})",
                ("items", loop.Range), ("body", loop.Body));
        }
        return template.Expression($"{Runtime}.Each($items, ($item) => $body)", ("items", loop.Range), ("item", loop.Variable), ("body", loop.Body));
    }

    /// <summary>
    /// A hole's expression, parsed as an interpolation's is (with the enclosing function's async context, so it can
    /// await); an <c>if</c> without an <c>else</c> gets one rendering nothing. Null if the hole is empty.
    /// </summary>
    private ExpressionSyntax? HoleExpression(MarkupHole hole, bool allowFormat)
    {
        var (span, alignment, format) = SplitHole(hole.Expression);
        if (alignment is not null || (format is not null && !allowFormat))
            _diagnostics.ReportInvalidMarkup(hole.Span, allowFormat
                ? "A hole in markup can have a format ({price:C}), but not an alignment."
                : "An attribute's value can't have a format; format it in the expression: {price.ToString(\"C\")}.");
        var tokens = Lexer.Tokenize(_source, _diagnostics, span);
        if (tokens is [{ Kind: TokenKind.EndOfFile }])
        {
            _diagnostics.ReportInvalidMarkup(hole.Span, "This hole is empty: put an expression in it.");
            return null;
        }
        return OrNothing(HoleParser(tokens).ParseStandaloneExpression());
    }

    /// <summary>A parser for a hole, sharing this one's async context.</summary>
    private Parser HoleParser(ImmutableArray<Token> tokens) =>
        new(_source, _diagnostics, tokens) { _inAsync = _inAsync, _asyncContext = _asyncContext };

    /// <summary><c>if c { … }</c> in markup, as <c>if c { … } else { Html.Empty }</c>, along a chain of <c>else if</c>s.</summary>
    private static ExpressionSyntax OrNothing(ExpressionSyntax expression) => expression switch
    {
        IfExpressionSyntax { Else: null, Question: null } branch =>
            branch with { Else = new SyntaxTemplate(branch.Span).Expression("Piper.Runtime.Html.Empty") },
        IfExpressionSyntax { Else: IfExpressionSyntax next } branch => branch with { Else = OrNothing(next) },
        _ => expression,
    };

    private static LiteralExpressionSyntax Text(TextSpan span, string value) =>
        new(span, new Token(TokenKind.StringLiteral, span, value, value) { IsGenerated = true }, value);

    private static bool IsParameterName(string name) =>
        name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') && name.All(c => char.IsLetterOrDigit(c) || c == '_');

    private static bool IsVoidElement(string name) =>
        name.ToLowerInvariant() is "area" or "base" or "br" or "col" or "embed" or "hr" or "img" or "input" or "link" or "meta" or "source" or "track" or "wbr";

    /// <summary><c>data-id</c> as a parameter name would be: <c>dataId</c>.</summary>
    private static string Camel(string name)
    {
        var result = new StringBuilder();
        bool upper = false;
        foreach (var c in name)
        {
            if (c is '-' or ':' or '.' or '@')
                upper = result.Length > 0;
            else
            {
                result.Append(upper ? char.ToUpperInvariant(c) : c);
                upper = false;
            }
        }
        return result.ToString();
    }
}
