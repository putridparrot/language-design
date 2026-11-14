using System.Text.Json.Nodes;
using Piper.Binding;
using Piper.Diagnostics;
using Piper.Syntax;
using Piper.Text;

namespace Piper.LanguageServer;

/// <summary>
/// Completion inside markup, where Piper's names and keywords don't belong: tag names (HTML's elements, and components,
/// the functions returning Html), attributes (a component's parameters, HTML's attributes), and some attributes' values
/// (an input's <c>name</c> inside <c>&lt;form for={NewLot}&gt;</c> is one of the record's fields). In a hole,
/// <c>{…}</c>, it's code again, and the usual completion applies, unless the hole holds markup of its own.
/// </summary>
public static class MarkupCompletion
{
    /// <summary>Where in markup a position is: what's being written there, and the elements it's inside.</summary>
    private abstract record Context(IReadOnlyList<Open> Elements);

    /// <summary>
    /// An element the position is inside: its name, the record its <c>for</c> names, if it's a form, and where its
    /// start tag ends (just after its <c>&gt;</c>).
    /// </summary>
    private sealed record Open(string Name, string? For, int TagEnd = -1);

    private sealed record InText(IReadOnlyList<Open> Elements) : Context(Elements);

    private sealed record InTagName(IReadOnlyList<Open> Elements) : Context(Elements);

    private sealed record InClosingTag(IReadOnlyList<Open> Elements) : Context(Elements);

    private sealed record InAttributes(string Tag, IReadOnlyList<string> Written, IReadOnlyList<Open> Elements) : Context(Elements);

    private sealed record InAttributeValue(string Tag, string Attribute, IReadOnlyList<Open> Elements) : Context(Elements);

    // LSP CompletionItemKind and InsertTextFormat values.
    private const int FunctionKind = 3, FieldKind = 5, PropertyKind = 10, ValueKind = 12, KeywordKind = 14, SnippetFormat = 2;

    /// <summary>
    /// The completion items at <paramref name="position"/> if it's in markup (an empty list in text, where nothing is
    /// offered), or null if it's in code, including a hole's.
    /// </summary>
    public static JsonArray? Items(AnalysisResult analysis, SourceText source, int position)
    {
        var context = Find(source, Lexer.Tokenize(source, new DiagnosticBag()), position, []);
        if (context is null)
            return null;
        var items = context switch
        {
            InTagName => TagItems(analysis, source),
            InClosingTag { Elements: [.., var open] } => [Item(open.Name, KeywordKind, "closes the element")],
            InAttributes attributes => AttributeItems(analysis, attributes),
            InAttributeValue value => ValueItems(analysis, value),
            _ => [],
        };
        return [.. items];
    }

    /// <summary>
    /// What to put after the cursor when <c>&gt;</c> has just been typed at <paramref name="position"/>: the closing
    /// tag, as a snippet (<c>$0&lt;/div&gt;</c>), if that <c>&gt;</c> ended a start tag in markup; null otherwise, as
    /// after a void element (<c>&lt;br&gt;</c>), a self-closing tag, <c>&lt;script&gt;</c>, or a tag already closed.
    /// </summary>
    public static string? AutoClose(SourceText source, int position)
    {
        if (position < 2 || position > source.Length || source[position - 1] != '>')
            return null;
        if (Find(source, Lexer.Tokenize(source, new DiagnosticBag()), position, []) is not InText { Elements: [.., var open] } ||
            open.TagEnd != position)
            return null;
        var closing = $"</{open.Name}>";
        // Already closed, as when the start tag is being written again in front of its end tag.
        var rest = source.ToString(TextSpan.FromBounds(position, source.Length)).TrimStart();
        return rest.StartsWith(closing, StringComparison.Ordinal) ? null : "$0" + closing;
    }

    /// <summary>
    /// The element or attribute name in markup at <paramref name="position"/>: an element's (in its start or end tag),
    /// or an attribute's, with the element it's on. Null elsewhere: in text, a value, a hole's code, or outside markup.
    /// </summary>
    internal static (string? Element, string? Attribute, TextSpan Span)? NameAt(SourceText source, int position)
    {
        if (position >= source.Length)
            return null;
        // The whole name around the position, as the scan stops where it is.
        int start = position, end = position;
        while (start > 0 && (IsNameChar(source[start - 1]) || source[start - 1] == '@'))
            start--;
        while (end < source.Length && (IsNameChar(source[end]) || source[end] == '@'))
            end++;
        if (start == end)
            return null;
        var name = source.ToString(TextSpan.FromBounds(start, end));
        var span = TextSpan.FromBounds(start, end);
        // Scanned from the name's start, so it's the name being read there, not one before it.
        return Find(source, Lexer.Tokenize(source, new DiagnosticBag()), start, []) switch
        {
            InTagName when start > 0 && source[start - 1] == '<' => (name, null, span),
            InClosingTag when start > 1 && source[start - 1] == '/' && source[start - 2] == '<' => (name, null, span),
            InAttributes attributes => (attributes.Tag, name, span),
            _ => null,
        };
    }

    /// <summary>The markup context in the markup literal among <paramref name="tokens"/> that holds the position, if any.</summary>
    private static Context? Find(SourceText source, IEnumerable<Token> tokens, int position, IReadOnlyList<Open> outer)
    {
        var markup = tokens.FirstOrDefault(t => t.Kind == TokenKind.MarkupLiteral && t.Span.Start < position && position <= t.Span.End);
        return markup is null ? null : Scan(source, markup.Span.Start, position, outer);
    }

    /// <summary>
    /// Reads the markup from <paramref name="start"/> up to <paramref name="position"/>, as the lexer would but tolerant
    /// of what's half written, to tell what's being written there.
    /// </summary>
    private static Context? Scan(SourceText source, int start, int position, IReadOnlyList<Open> outer)
    {
        var elements = new List<Open>(outer);
        int i = start;
        char At(int index) => index < source.Length ? source[index] : '\0';

        while (i < position)
        {
            char c = At(i);
            if (c == '{')
            {
                // A hole: code, unless the position is in markup inside it.
                int close = MatchingBrace(source, i);
                if (close < 0 || close >= position)
                {
                    var hole = TextSpan.FromBounds(i + 1, close < 0 ? source.Length : close);
                    return Find(source, Lexer.Tokenize(source, new DiagnosticBag(), hole), position, elements);
                }
                i = close + 1;
            }
            else if (c == '<' && At(i + 1) == '/')
            {
                // A closing tag: </name>, or </> for a fragment.
                int nameStart = i + 2, end = nameStart;
                while (end < position && IsNameChar(At(end)))
                    end++;
                if (end >= position)
                    return new InClosingTag(elements);
                var name = source.ToString(TextSpan.FromBounds(nameStart, end));
                int last = elements.FindLastIndex(e => e.Name == name);
                if (last >= 0)
                    elements.RemoveRange(last, elements.Count - last);
                i = IndexOf(source, '>', end, position) is var gt and >= 0 ? gt + 1 : position;
            }
            else if (c == '<' && At(i + 1) == '>')
            {
                elements.Add(new Open("", null, i + 2));
                i += 2;
            }
            else if (c == '<' && At(i + 1) == '!')
            {
                i = IndexOf(source, '>', i, position) is var gt and >= 0 ? gt + 1 : position;
            }
            else if (c == '<')
            {
                int nameStart = i + 1, end = nameStart;
                while (end < position && IsNameChar(At(end)))
                    end++;
                if (end >= position)
                    return new InTagName(elements);
                var tag = source.ToString(TextSpan.FromBounds(nameStart, end));
                var (context, after, @for, selfClosing) = ScanTag(source, tag, end, position, elements);
                if (context is not null)
                    return context;
                if (!selfClosing && !IsVoid(tag) && tag.ToLowerInvariant() is not ("script" or "style"))
                    elements.Add(new Open(tag, @for, after));
                i = after;
            }
            else
            {
                i++;
            }
        }
        return new InText(elements);
    }

    /// <summary>
    /// A start tag's attributes, from just after its name: the context if the position is among them, otherwise where
    /// the tag ends, the record a form's <c>for={…}</c> names, and whether it closed itself (<c>/&gt;</c>).
    /// </summary>
    private static (Context? Context, int After, string? For, bool SelfClosing) ScanTag(
        SourceText source, string tag, int i, int position, List<Open> elements)
    {
        var written = new List<string>();
        string? @for = null;
        while (i < position)
        {
            char c = source[i];
            if (c == '>')
                return (null, i + 1, @for, false);
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '>')
                return (null, i + 2, @for, true);
            if (IsNameChar(c) || c == '@')
            {
                int nameStart = i;
                while (i < position && (IsNameChar(source[i]) || source[i] == '@'))
                    i++;
                if (i >= position)
                    return (new InAttributes(tag, written, elements), i, @for, false);
                var name = source.ToString(TextSpan.FromBounds(nameStart, i));
                written.Add(name);
                while (i < position && char.IsWhiteSpace(source[i]))
                    i++;
                if (i < position && source[i] == '=')
                {
                    i++;
                    while (i < position && char.IsWhiteSpace(source[i]))
                        i++;
                    if (i < position && source[i] == '"')
                    {
                        int close = IndexOf(source, '"', i + 1, position);
                        if (close < 0)
                            return (new InAttributeValue(tag, name, elements), position, @for, false);
                        i = close + 1;
                    }
                    else if (i < position && source[i] == '{')
                    {
                        int close = MatchingBrace(source, i);
                        if (close < 0 || close >= position)
                        {
                            var hole = TextSpan.FromBounds(i + 1, close < 0 ? source.Length : close);
                            return (Find(source, Lexer.Tokenize(source, new DiagnosticBag(), hole), position, elements), position, @for, false);
                        }
                        if (name == "for")
                            @for = source.ToString(TextSpan.FromBounds(i + 1, close)).Trim();
                        i = close + 1;
                    }
                }
                continue;
            }
            i++;
        }
        return (new InAttributes(tag, written, elements), position, @for, false);
    }

    /// <summary>The <c>}</c> closing the <c>{</c> at <paramref name="open"/>, past nested braces and strings; -1 if none.</summary>
    private static int MatchingBrace(SourceText source, int open)
    {
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            char c = source[i];
            if (c == '"')
            {
                for (i++; i < source.Length && source[i] != '"' && source[i] != '\n'; i++)
                {
                    if (source[i] == '\\')
                        i++;
                }
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}' && --depth == 0)
            {
                return i;
            }
        }
        return -1;
    }

    private static int IndexOf(SourceText source, char c, int from, int before)
    {
        for (int i = from; i < before && i < source.Length; i++)
        {
            if (source[i] == c)
                return i;
        }
        return -1;
    }

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or ':';

    private static bool IsVoid(string tag) =>
        tag.ToLowerInvariant() is "area" or "base" or "br" or "col" or "embed" or "hr" or "img" or "input" or "link" or "meta" or "source" or "track" or "wbr";

    // Tags

    private static readonly string[] Elements =
    [
        "a", "abbr", "article", "aside", "b", "blockquote", "body", "br", "button", "caption", "code", "dd", "details",
        "dialog", "div", "dl", "dt", "em", "fieldset", "figcaption", "figure", "footer", "form", "h1", "h2", "h3", "h4",
        "h5", "h6", "head", "header", "hr", "html", "i", "img", "input", "label", "legend", "li", "link", "main", "mark",
        "meta", "nav", "ol", "optgroup", "option", "output", "p", "pre", "progress", "script", "section", "select",
        "small", "span", "strong", "style", "sub", "summary", "sup", "table", "tbody", "td", "template", "textarea",
        "tfoot", "th", "thead", "time", "title", "tr", "u", "ul", "video",
    ];

    private static IEnumerable<JsonObject> TagItems(AnalysisResult analysis, SourceText source)
    {
        foreach (var element in Elements)
            yield return Item(element, KeywordKind, "HTML element");
        foreach (var component in Components(analysis, source))
            yield return Item(component.Name, FunctionKind, SymbolDisplay.Describe(component), component.Documentation);
    }

    /// <summary>The functions markup can use as tags: those returning Html.</summary>
    private static IEnumerable<FunctionSymbol> Components(AnalysisResult analysis, SourceText source) =>
        analysis.Program.Functions.Select(f => f.Symbol)
            .Where(f => (!f.IsPrivate || f.DeclaringFile == source) && IsHtml(f.ReturnType));

    private static bool IsHtml(TypeSymbol type) => ClrTypes.ToClr(type)?.FullName == "Piper.Runtime.Html";

    // Attributes

    private static readonly string[] GlobalAttributes =
    [
        "id", "class", "style", "title", "hidden", "lang", "tabindex", "role", "aria-label", "aria-describedby",
        "aria-hidden", "data-", "hx-get", "hx-post", "hx-put", "hx-patch", "hx-delete", "hx-target", "hx-swap",
        "hx-trigger", "hx-select", "hx-push-url", "hx-confirm", "hx-indicator", "hx-vals", "hx-ext", "sse-connect", "sse-swap",
    ];

    private static readonly Dictionary<string, string[]> ElementAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = ["href", "target", "rel", "download"],
        ["button"] = ["type", "name", "value", "disabled", "form"],
        ["form"] = ["for", "action", "method", "enctype", "novalidate", "autocomplete"],
        ["img"] = ["src", "alt", "width", "height", "loading"],
        ["input"] = ["name", "type", "value", "placeholder", "required", "disabled", "readonly", "checked", "min", "max", "step", "pattern", "autocomplete", "autofocus", "list"],
        ["label"] = ["for"],
        ["link"] = ["rel", "href", "type"],
        ["meta"] = ["name", "content", "charset"],
        ["option"] = ["value", "selected", "disabled"],
        ["script"] = ["src", "type", "defer", "async"],
        ["select"] = ["name", "required", "disabled", "multiple"],
        ["td"] = ["colspan", "rowspan"],
        ["th"] = ["colspan", "rowspan", "scope"],
        ["textarea"] = ["name", "rows", "cols", "placeholder", "required", "disabled", "readonly"],
        ["time"] = ["datetime"],
    };

    private static readonly HashSet<string> BooleanAttributes =
        ["hidden", "required", "disabled", "readonly", "checked", "selected", "multiple", "autofocus", "novalidate", "defer", "async", "download"];

    private static IEnumerable<JsonObject> AttributeItems(AnalysisResult analysis, InAttributes context)
    {
        bool component = char.IsUpper(context.Tag[0]) || context.Tag.Contains('.');
        if (component)
        {
            // A component's attributes are its parameters (bar `children`, given by what's inside it).
            var name = context.Tag.Split('.')[^1];
            var function = analysis.Program.Functions.Select(f => f.Symbol).FirstOrDefault(f => f.Name == name && IsHtml(f.ReturnType));
            foreach (var parameter in function?.Parameters ?? [])
            {
                if (parameter.Name == "children" || context.Written.Contains(parameter.Name))
                    continue;
                var snippet = parameter.Type == TypeSymbol.Bool ? parameter.Name
                    : parameter.Type == TypeSymbol.String ? $"{parameter.Name}=\"$1\""
                    : $"{parameter.Name}={{$1}}";
                yield return Snippet(Item(parameter.Name, PropertyKind, $"{parameter.Name}: {parameter.Type}", parameter.Documentation), snippet);
            }
            yield break;
        }
        var names = (ElementAttributes.TryGetValue(context.Tag, out var own) ? own : []).Concat(GlobalAttributes);
        foreach (var attribute in names.Where(a => !context.Written.Contains(a)))
        {
            var snippet = BooleanAttributes.Contains(attribute) ? attribute
                : attribute == "for" && context.Tag.Equals("form", StringComparison.OrdinalIgnoreCase) ? "for={$1}"
                : attribute == "data-" ? "data-$1=\"$2\""
                : $"{attribute}=\"$1\"";
            var detail = attribute == "for" && context.Tag.Equals("form", StringComparison.OrdinalIgnoreCase)
                ? "the record the form is read into: its inputs' names are checked against its fields"
                : attribute.StartsWith("hx-") || attribute.StartsWith("sse-") ? "htmx" : "HTML attribute";
            yield return Snippet(Item(attribute, PropertyKind, detail), snippet);
        }
    }

    // Values

    private static IEnumerable<JsonObject> ValueItems(AnalysisResult analysis, InAttributeValue context)
    {
        var tag = context.Tag.ToLowerInvariant();
        if (context.Attribute == "name" && tag is "input" or "select" or "textarea" &&
            context.Elements.LastOrDefault(e => e.For is not null) is { For: { } recordName } &&
            analysis.Program.Types.OfType<RecordSymbol>().FirstOrDefault(r => r.Name == recordName) is { } record)
        {
            foreach (var field in record.Fields)
                yield return Item(field.Name, FieldKind, $"{field.Name}: {field.Type} (in {record.Name})", field.Documentation);
        }
        else if (context.Attribute == "type" && tag == "input")
        {
            foreach (var type in new[] { "text", "number", "email", "password", "search", "checkbox", "radio", "date", "datetime-local", "time", "hidden", "file", "url", "tel", "range", "color", "submit" })
                yield return Item(type, ValueKind, "input type");
        }
        else if (context.Attribute == "type" && tag == "button")
        {
            foreach (var type in new[] { "submit", "button", "reset" })
                yield return Item(type, ValueKind, "button type");
        }
        else if (context.Attribute == "method" && tag == "form")
        {
            yield return Item("post", ValueKind, "form method");
            yield return Item("get", ValueKind, "form method");
        }
        else if (context.Attribute == "hx-swap")
        {
            foreach (var swap in new[] { "innerHTML", "outerHTML", "beforebegin", "afterbegin", "beforeend", "afterend", "delete", "none" })
                yield return Item(swap, ValueKind, "htmx swap");
        }
    }

    private static JsonObject Item(string label, int kind, string? detail, string? documentation = null)
    {
        var item = new JsonObject { ["label"] = label, ["kind"] = kind };
        if (detail is not null)
            item["detail"] = detail;
        if (documentation is not null)
            item["documentation"] = new JsonObject { ["kind"] = "markdown", ["value"] = documentation };
        return item;
    }

    private static JsonObject Snippet(JsonObject item, string snippet)
    {
        if (snippet != item["label"]!.GetValue<string>())
        {
            item["insertText"] = snippet;
            item["insertTextFormat"] = SnippetFormat;
        }
        return item;
    }
}
