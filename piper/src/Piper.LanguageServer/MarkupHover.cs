using Piper.Text;

namespace Piper.LanguageServer;

/// <summary>
/// Hover for HTML's own elements and attributes in markup: what each is for, in a line, and where to read more (MDN,
/// or htmx's site for its attributes). A component's tag is its function, and hovers as one; this is for the rest.
/// </summary>
public static class MarkupHover
{
    /// <summary>The hover text for the element or attribute name at <paramref name="position"/>, if it's one HTML has.</summary>
    public static (string Markdown, TextSpan Span)? At(SourceText source, int position)
    {
        if (MarkupCompletion.NameAt(source, position) is not var (element, attribute, span) || element is null)
            return null;
        // A component (`<Card>`, `<Htmx.Script />`) is Piper's, not HTML's.
        if (char.IsUpper(element[0]) || element.Contains('.'))
            return null;
        var tag = element.ToLowerInvariant();
        if (attribute is null)
        {
            return Elements.TryGetValue(tag, out var about)
                ? ($"**`<{tag}>`** (HTML element)\n\n{about}\n\n[MDN](https://developer.mozilla.org/docs/Web/HTML/Element/{tag})", span)
                : null;
        }
        return Attribute(tag, attribute) is { } text ? (text, span) : null;
    }

    private static string? Attribute(string tag, string attribute)
    {
        var name = attribute.ToLowerInvariant();
        if (name == "for" && tag == "form")
            return "**`for`** (Piper)\n\nThe record the form is read into, as `for={NewLot}`: each `input`, `select` and " +
                   "`textarea` in it must be named after one of the record's fields, and a handler reads the form with `Form<NewLot>`. " +
                   "It isn't written out to the page.";
        if (name.StartsWith("hx-"))
        {
            return Htmx.TryGetValue(name, out var about)
                ? $"**`{name}`** (htmx)\n\n{about}\n\n[htmx](https://htmx.org/attributes/{name}/)"
                : $"**`{name}`** (htmx)\n\n[htmx](https://htmx.org/reference/#attributes)";
        }
        if (name is "sse-connect" or "sse-swap")
        {
            var about = name == "sse-connect"
                ? "The URL of a stream of server-sent events to listen to, with `hx-ext=\"sse\"`: in Piper, `{Live.Url(topic)}`."
                : "The event whose data replaces this element's content: `Live.Publish(topic, event, markup)` sends it.";
            return $"**`{name}`** (htmx's server-sent events)\n\n{about}\n\n[htmx](https://htmx.org/extensions/sse/)";
        }
        if (name.StartsWith("data-"))
            return $"**`{name}`** (HTML data attribute)\n\nData for the page's scripts, read as `element.dataset`.\n\n" +
                   "[MDN](https://developer.mozilla.org/docs/Web/HTML/Global_attributes/data-*)";
        if (name.StartsWith("aria-"))
            return $"**`{name}`** (ARIA)\n\nWhat assistive technology, such as a screen reader, is told about the element.\n\n" +
                   $"[MDN](https://developer.mozilla.org/docs/Web/Accessibility/ARIA/Attributes/{name})";
        if (ElementAttributes.TryGetValue((tag, name), out var own))
            return $"**`{name}`** (on `<{tag}>`)\n\n{own}\n\n[MDN](https://developer.mozilla.org/docs/Web/HTML/Element/{tag}#{name})";
        if (GlobalAttributes.TryGetValue(name, out var global))
            return $"**`{name}`** (HTML attribute)\n\n{global}\n\n[MDN](https://developer.mozilla.org/docs/Web/HTML/Global_attributes/{name})";
        return null;
    }

    private static readonly Dictionary<string, string> Elements = new()
    {
        ["a"] = "A link, to `href`. In Piper, link to a route with its function: `href={SiteRoutes.Lot(id)}`.",
        ["abbr"] = "An abbreviation; its `title` is what it stands for.",
        ["article"] = "A piece of content that stands on its own, such as a post or a card.",
        ["aside"] = "Content beside the main content, such as a sidebar.",
        ["b"] = "Text drawn attention to, in bold, without extra importance.",
        ["blockquote"] = "A quotation set apart from the text.",
        ["body"] = "The page's content.",
        ["br"] = "A line break. A void element: it has no content or end tag.",
        ["button"] = "A button. In a form it submits it, unless its `type` is `button`.",
        ["caption"] = "A table's title.",
        ["code"] = "A piece of code.",
        ["dd"] = "A description of the term before it, in a `<dl>`.",
        ["details"] = "A disclosure box, opened and closed by its `<summary>`.",
        ["dialog"] = "A dialog box or other interactive window.",
        ["div"] = "A block with no meaning of its own, for grouping and styling.",
        ["dl"] = "A list of terms (`<dt>`) and their descriptions (`<dd>`).",
        ["dt"] = "A term, in a `<dl>`.",
        ["em"] = "Stressed text, usually in italics.",
        ["fieldset"] = "A group of a form's controls, named by its `<legend>`.",
        ["figcaption"] = "A `<figure>`'s caption.",
        ["figure"] = "Self-contained content, such as an image or code, with an optional `<figcaption>`.",
        ["footer"] = "The footer of its section or page.",
        ["form"] = "A form, sent to `action` with `method`. In Piper, `for={Record}` checks its inputs against a record's fields.",
        ["h1"] = "A heading of the first (highest) level.",
        ["h2"] = "A heading of the second level.",
        ["h3"] = "A heading of the third level.",
        ["h4"] = "A heading of the fourth level.",
        ["h5"] = "A heading of the fifth level.",
        ["h6"] = "A heading of the sixth (lowest) level.",
        ["head"] = "What's about the page rather than in it: its title, styles, scripts and metadata.",
        ["header"] = "The introduction of its section or page, such as a title and navigation.",
        ["hr"] = "A break between paragraphs, drawn as a line. A void element.",
        ["html"] = "The page's root element.",
        ["i"] = "Text set apart, in italics: a term, a thought, a name.",
        ["img"] = "An image, from `src`, described by `alt`. A void element.",
        ["input"] = "A form control: its `type` says which (text, number, checkbox…), its `name` what it's sent as. A void element.",
        ["label"] = "A caption for a form control: the one whose `id` its `for` names, or the one inside it.",
        ["legend"] = "A `<fieldset>`'s caption.",
        ["li"] = "An item of a list (`<ul>` or `<ol>`).",
        ["link"] = "A link to another resource, such as a stylesheet. A void element.",
        ["main"] = "The page's main content: one per page.",
        ["mark"] = "Text highlighted for reference, such as a search's matches.",
        ["meta"] = "Metadata about the page: its character set, viewport, description. A void element.",
        ["nav"] = "A section of navigation links.",
        ["ol"] = "A numbered list of `<li>` items.",
        ["optgroup"] = "A group of a `<select>`'s options.",
        ["option"] = "One of a `<select>`'s choices.",
        ["output"] = "The result of a calculation or of what someone did.",
        ["p"] = "A paragraph.",
        ["pre"] = "Preformatted text, shown as written, with its spaces and line breaks.",
        ["progress"] = "How far through a task is.",
        ["script"] = "A script. Its content is JavaScript, not markup, so Piper leaves it as written.",
        ["section"] = "A section of the page, usually with a heading.",
        ["select"] = "A drop-down list of `<option>`s.",
        ["small"] = "Side comments and small print.",
        ["span"] = "Inline text with no meaning of its own, for styling.",
        ["strong"] = "Text of strong importance, usually in bold.",
        ["style"] = "A stylesheet. Its content is CSS, not markup, so Piper leaves it as written.",
        ["sub"] = "Subscript text.",
        ["summary"] = "The caption of a `<details>`, which opens and closes it.",
        ["sup"] = "Superscript text.",
        ["table"] = "A table of rows (`<tr>`) of cells (`<td>`, `<th>`).",
        ["tbody"] = "A table's body rows.",
        ["td"] = "A table's data cell.",
        ["template"] = "Markup kept for scripts to use, not shown.",
        ["textarea"] = "A form control for text over several lines.",
        ["tfoot"] = "A table's footer rows.",
        ["th"] = "A table's header cell.",
        ["thead"] = "A table's header rows.",
        ["time"] = "A date or time, given readably and, in `datetime`, for machines.",
        ["title"] = "The page's title, shown in its tab.",
        ["tr"] = "A table's row.",
        ["u"] = "Text marked out as different, underlined.",
        ["ul"] = "A bulleted list of `<li>` items.",
        ["video"] = "A video, from `src`.",
    };

    private static readonly Dictionary<string, string> GlobalAttributes = new()
    {
        ["id"] = "The element's identifier, unique in the page: what `#id` selects and a `<label for>` names.",
        ["class"] = "The element's classes, separated by spaces, for stylesheets and scripts to select.",
        ["style"] = "CSS for this element alone.",
        ["title"] = "Advice about the element, usually shown as a tooltip.",
        ["hidden"] = "The element isn't shown. A boolean attribute: in Piper, `hidden` or `hidden={flag}`.",
        ["lang"] = "The language of the element's content, such as `en`.",
        ["tabindex"] = "Whether, and in what order, the element takes focus from the keyboard.",
        ["role"] = "What the element is, for assistive technology, where its tag doesn't say.",
    };

    private static readonly Dictionary<(string Tag, string Attribute), string> ElementAttributes = new()
    {
        [("a", "href")] = "Where the link goes. In Piper, a route's function gives a checked URL: `href={SiteRoutes.Lot(id)}`.",
        [("a", "target")] = "Where to open the link: `_blank` for a new tab.",
        [("a", "rel")] = "How the linked page relates to this one, such as `noopener`.",
        [("a", "download")] = "Download what's linked to rather than go to it.",
        [("button", "type")] = "`submit` (the default in a form), `button` (does nothing by itself) or `reset`.",
        [("button", "name")] = "The name the button's `value` is sent as, when it submits the form.",
        [("button", "value")] = "What's sent as the button's `name` when it submits the form.",
        [("button", "disabled")] = "The button can't be used. A boolean attribute.",
        [("form", "action")] = "Where the form is sent. In Piper, a route's function: `action={SiteRoutes.CreateLot()}`.",
        [("form", "method")] = "How the form is sent: `get` (in the URL) or `post` (in the body; Piper adds an anti-forgery token).",
        [("form", "enctype")] = "How a posted form is encoded: `multipart/form-data` for files.",
        [("form", "novalidate")] = "Don't check the form's fields in the browser before sending it.",
        [("img", "src")] = "The image's URL.",
        [("img", "alt")] = "What the image shows, for those who can't see it: give one, or `\"\"` for a decorative image.",
        [("img", "loading")] = "`lazy` loads the image only as it nears the screen.",
        [("input", "name")] = "What the input's value is sent as. In a `<form for={Record}>`, it must be one of the record's fields.",
        [("input", "type")] = "What kind of input: `text`, `number`, `email`, `checkbox`, `date`, `hidden`…",
        [("input", "value")] = "The input's value as the page starts.",
        [("input", "placeholder")] = "A hint shown in the input while it's empty.",
        [("input", "required")] = "The form can't be sent without it. A boolean attribute.",
        [("input", "disabled")] = "The input can't be used, and isn't sent. A boolean attribute.",
        [("input", "readonly")] = "The input can't be changed, but is sent. A boolean attribute.",
        [("input", "checked")] = "A checkbox or radio button starts checked. A boolean attribute.",
        [("input", "min")] = "The least value a number or date may be.",
        [("input", "max")] = "The greatest value a number or date may be.",
        [("input", "step")] = "The steps a number or date goes in, such as `0.01` for pence.",
        [("input", "pattern")] = "A regular expression the value must match.",
        [("input", "autofocus")] = "The input has focus as the page opens. A boolean attribute.",
        [("label", "for")] = "The `id` of the form control it captions.",
        [("select", "name")] = "What the chosen option's value is sent as. In a `<form for={Record}>`, one of the record's fields.",
        [("select", "multiple")] = "Several options can be chosen. A boolean attribute.",
        [("textarea", "name")] = "What the text is sent as. In a `<form for={Record}>`, one of the record's fields.",
        [("textarea", "rows")] = "How many lines high it is.",
        [("option", "value")] = "What's sent when it's chosen; its text, if it has none.",
        [("option", "selected")] = "It's chosen as the page starts. A boolean attribute.",
        [("script", "src")] = "The script's URL, rather than its content.",
        [("script", "defer")] = "Run the script once the page is read. A boolean attribute.",
        [("td", "colspan")] = "How many columns the cell spans.",
        [("th", "colspan")] = "How many columns the cell spans.",
        [("th", "scope")] = "Which cells it heads: `col`, `row`, `colgroup` or `rowgroup`.",
        [("time", "datetime")] = "The date or time for machines, such as `2026-11-14`.",
        [("meta", "charset")] = "The page's character set: `utf-8`.",
        [("meta", "name")] = "What the metadata in `content` is: `viewport`, `description`…",
        [("meta", "content")] = "The metadata's value.",
        [("link", "rel")] = "What's linked to: `stylesheet`, `icon`…",
        [("link", "href")] = "The URL of what's linked to.",
    };

    private static readonly Dictionary<string, string> Htmx = new()
    {
        ["hx-get"] = "Sends a GET to this URL when the element is triggered (clicked, or a form submitted), and swaps in the markup that comes back. In Piper, `hx-get={SiteRoutes.Search()}` is checked like a link.",
        ["hx-post"] = "Sends a POST to this URL when triggered, with the form's values, and swaps in what comes back. Piper's `<Htmx.Script />` sends the anti-forgery token with it.",
        ["hx-put"] = "Sends a PUT to this URL when triggered, and swaps in what comes back.",
        ["hx-patch"] = "Sends a PATCH to this URL when triggered, and swaps in what comes back.",
        ["hx-delete"] = "Sends a DELETE to this URL when triggered, and swaps in what comes back.",
        ["hx-target"] = "The element the response goes into, as a CSS selector (`#results`), or `this`, `closest tr`…",
        ["hx-swap"] = "How the response goes in: `innerHTML` (the default), `outerHTML`, `beforeend`, `none`…",
        ["hx-trigger"] = "What sends the request: `click`, `change`, `keyup changed delay:300ms`, `load`, `every 5s`…",
        ["hx-select"] = "Which part of the response to swap in, as a CSS selector.",
        ["hx-push-url"] = "Puts the request's URL in the browser's history (`true`), or another URL.",
        ["hx-confirm"] = "Asks this question, and sends the request only if it's confirmed.",
        ["hx-indicator"] = "The element shown while the request is on its way, as a CSS selector.",
        ["hx-vals"] = "More values to send, as JSON.",
        ["hx-ext"] = "The htmx extensions this element and those in it use, such as `sse` for live pages.",
        ["hx-boost"] = "Makes the links and forms in it send their requests through htmx, swapping the page's body.",
    };
}
