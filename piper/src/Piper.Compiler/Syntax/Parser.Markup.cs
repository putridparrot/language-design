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
        if (FormFor(element) is { } @for)
            return Form(element, @for);

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

    /// <summary>A form's <c>for={Record}</c> attribute, which names the record the form is read into.</summary>
    private static MarkupAttribute? FormFor(MarkupElement element) =>
        element.Name.Equals("form", StringComparison.OrdinalIgnoreCase)
            ? element.Attributes.FirstOrDefault(a => a.Name.Equals("for", StringComparison.OrdinalIgnoreCase))
            : null;

    /// <summary>
    /// <c>&lt;form for={NewLot}&gt;</c>: the form without its <c>for</c>, which isn't HTML's, and the names of the inputs
    /// written in it, which the binder checks against the record's fields. Those in components or holes aren't seen.
    /// </summary>
    private ExpressionSyntax Form(MarkupElement element, MarkupAttribute @for)
    {
        var markup = Markup(element with { Attributes = element.Attributes.Remove(@for) });
        if (@for.Hole is not { } hole)
        {
            _diagnostics.ReportInvalidMarkup(@for.Span, "A form's 'for' names the record it's read into: <form for={NewLot}>.");
            return markup;
        }
        var parser = HoleParser(Lexer.Tokenize(_source, _diagnostics, hole.Expression));
        var record = parser.ParseType();
        if (parser.Current.Kind != TokenKind.EndOfFile)
        {
            _diagnostics.ReportInvalidMarkup(parser.Current.Span, "A form's 'for' is a record type: <form for={NewLot}>.");
            return markup;
        }
        var fields = ImmutableArray.CreateBuilder<(string, TextSpan)>();
        void Collect(MarkupElement node)
        {
            if (node.Name.ToLowerInvariant() is "input" or "select" or "textarea" &&
                !node.Attributes.Any(a => a.Name.Equals("type", StringComparison.OrdinalIgnoreCase) &&
                                          a.Text?.ToLowerInvariant() is "submit" or "button" or "reset" or "image") &&
                node.Attributes.FirstOrDefault(a => a.Name.Equals("name", StringComparison.OrdinalIgnoreCase)) is { Text: { } name } attribute)
                fields.Add((name, attribute.Span));
            foreach (var child in node.Children.OfType<MarkupElement>())
                Collect(child);
        }
        Collect(element);
        return new MarkupFormSyntax(element.Span, record, fields.ToImmutable(), markup);
    }

    /// <summary><c>&lt;Countdown until={ends} /&gt;</c> is <c>Countdown(until: ends)</c>; its children, if any, are a <c>children</c> argument.</summary>
    private ExpressionSyntax Component(MarkupElement element, SyntaxTemplate template)
    {
        // The call is built from the tag's own tokens, where they're written, so the editor colours, hovers and goes to
        // the component, and an error in an attribute is reported at it.
        var callee = HoleParser(Lexer.Tokenize(_source, _diagnostics, element.NameSpan)).ParseStandaloneExpression();
        var arguments = ImmutableArray.CreateBuilder<ExpressionSyntax>();
        foreach (var attribute in element.Attributes)
        {
            if (!IsParameterName(attribute.Name))
            {
                _diagnostics.ReportInvalidMarkup(attribute.NameSpan, $"'{attribute.Name}' isn't a parameter name, which a component's attributes are: <{element.Name} {Camel(attribute.Name)}=…>.");
                continue;
            }
            var value = attribute.Hole is { } hole ? HoleExpression(hole, allowFormat: false)
                : attribute.Text is { } text ? Text(attribute.Span, text)
                : template.Expression("true");
            if (value is null)
                continue;
            arguments.Add(new NamedArgumentSyntax(attribute.Span, new Token(TokenKind.Identifier, attribute.NameSpan, attribute.Name), value));
        }
        if (!element.Children.IsEmpty)
        {
            var fragment = element with { Name = "", Attributes = [] };
            var children = new Token(TokenKind.Identifier, element.NameSpan, "children") { IsGenerated = true };
            arguments.Add(new NamedArgumentSyntax(element.Span, children, Markup(fragment)));
        }
        var call = new CallExpressionSyntax(element.Span, callee, arguments.ToImmutable());
        return element.CloseNameSpan is { } close ? new MarkupComponentSyntax(element.Span, call, close) : call;
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
