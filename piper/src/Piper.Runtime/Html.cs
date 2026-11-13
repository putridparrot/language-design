using System.Globalization;
using System.Net;
using System.Text;

namespace Piper.Runtime;

/// <summary>
/// HTML, as a Piper markup literal makes it (<c>&lt;p&gt;{name}&lt;/p&gt;</c>): text that is markup, as opposed to a
/// string, which is text to show. Putting a string into markup escapes it; only an <c>Html</c> goes in as markup, so
/// markup can't be injected by accident. <see cref="Raw"/> is the one way to make one from a string.
/// </summary>
public sealed class Html : IEquatable<Html>
{
    private readonly string _markup;

    private Html(string markup) => _markup = markup;

    /// <summary>No markup: what a hole with nothing to show (None, an `if` that's false) renders.</summary>
    public static Html Empty { get; } = new("");

    /// <summary>Markup from a string, unescaped: for HTML from somewhere trusted (a sanitizer, a Markdown renderer).</summary>
    public static Html Raw(string markup) => new(markup);

    /// <summary>The markup.</summary>
    public override string ToString() => _markup;

    public bool Equals(Html? other) => other is not null && other._markup == _markup;

    public override bool Equals(object? obj) => obj is Html other && Equals(other);

    public override int GetHashCode() => _markup.GetHashCode();

    public static bool operator ==(Html? a, Html? b) => a is null ? b is null : a.Equals(b);

    public static bool operator !=(Html? a, Html? b) => !(a == b);

    internal static Html Of(string markup) => markup.Length == 0 ? Empty : new(markup);
}

/// <summary>
/// What markup literals compile to. <c>&lt;td&gt;{lot.title}&lt;/td&gt;</c> is
/// <c>PiperHtml.Concat(PiperHtml.Trusted("&lt;td&gt;"), PiperHtml.Hole(lot.title), PiperHtml.Trusted("&lt;/td&gt;"))</c>:
/// the literal's own markup is trusted, and each hole is escaped by its type. A hole's overloads are what can be put
/// in markup: anything else (a Task, a bool, a function) is a compile error rather than nonsense on the page.
/// </summary>
public static class PiperHtml
{
    /// <summary>The literal's own markup, written in the source.</summary>
    public static Html Trusted(string markup) => Html.Of(markup);

    private static readonly AsyncLocal<Func<Html>?> TokenSource = new();

    /// <summary>
    /// Where a <c>&lt;form method="post"&gt;</c> gets its anti-forgery token: set by the web plugin's runtime while a
    /// handler answers a request, from that request, so it belongs to the request (two apps in a process, or two
    /// requests at once, each have their own). Without one (outside a web handler), forms get none.
    /// </summary>
    public static Func<Html>? FormTokenSource
    {
        get => TokenSource.Value;
        set => TokenSource.Value = value;
    }

    /// <summary>What every <c>&lt;form method="post"&gt;</c> in markup starts with: its anti-forgery token's hidden field.</summary>
    public static Html FormToken() => FormTokenSource?.Invoke() ?? Html.Empty;

    public static Html Concat(params Html[] parts)
    {
        var markup = new StringBuilder();
        foreach (var part in parts)
            markup.Append(part.ToString());
        return Html.Of(markup.ToString());
    }

    /// <summary>Markup, as it is (a nested literal, a component's result); nothing for None.</summary>
    public static Html Hole(Html? markup) => markup ?? Html.Empty;

    /// <summary>Text, escaped; nothing for None.</summary>
    public static Html Hole(string? text) => text is null ? Html.Empty : Html.Of(WebUtility.HtmlEncode(text));

    /// <summary>A number, date, id (a newtype of a number) or other formattable value, as text, in the current culture.</summary>
    public static Html Hole<T>(T value) where T : IFormattable => Hole(value.ToString(null, CultureInfo.CurrentCulture));

    /// <summary><c>{price:C}</c>: formatted, as in an interpolated string.</summary>
    public static Html Hole<T>(T value, string format) where T : IFormattable => Hole(value.ToString(format, CultureInfo.CurrentCulture));

    /// <summary>Several pieces of markup, one after another.</summary>
    public static Html Hole(IEnumerable<Html> parts) => Concat([.. parts]);

    /// <summary><c>{for item in items { … }}</c>: the markup for each item, one after another.</summary>
    public static Html Each<T>(IEnumerable<T> items, Func<T, Html> render)
    {
        var markup = new StringBuilder();
        foreach (var item in items)
            markup.Append(render(item).ToString());
        return Html.Of(markup.ToString());
    }

    /// <summary><c>name={value}</c>: the attribute, its value escaped; nothing for None.</summary>
    public static Html Attribute(string name, string? value) =>
        value is null ? Html.Empty : Html.Of($" {name}=\"{WebUtility.HtmlEncode(value)}\"");

    /// <summary><c>disabled={flag}</c>: the attribute alone when true, as HTML's boolean attributes are; nothing when false.</summary>
    public static Html Attribute(string name, bool present) => present ? Html.Of(" " + name) : Html.Empty;

    /// <summary>A number, id or other formattable value, in the invariant culture, as attributes are read by code.</summary>
    public static Html Attribute<T>(string name, T value) where T : IFormattable =>
        Attribute(name, value.ToString(null, CultureInfo.InvariantCulture));

    /// <summary>Markup as an attribute's value: its text, escaped; nothing for None.</summary>
    public static Html Attribute(string name, Html? value) => value is null ? Html.Empty : Attribute(name, value.ToString());
}
