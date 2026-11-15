using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Piper.Runtime;

/// <summary>
/// A URL, as <c>url$"/lots/{id}?q={text}"</c> makes it: each hole is encoded for the part of the URL it's in, so text
/// can't add a path segment, a query parameter or a fragment by accident. <see cref="Raw"/> is the one way to make one
/// from a string. It's formattable, so it goes in markup's attributes (<c>href={url}</c>) and in <c>$"…"</c> as itself.
/// </summary>
[InterpolationKind("url", Holds = "text, a number, a bool, a date or time, a Guid, or another formattable value (encoded for where it " +
                                   "is), an Option of one (None leaves a query parameter out), a list of them, or another Url")]
public sealed class Url : IEquatable<Url>, IFormattable
{
    private readonly string _text;

    private Url(string text) => _text = text;

    /// <summary>A URL from a string, as it is: for one from somewhere trusted, such as configuration.</summary>
    public static Url Raw(string text) => new(text);

    /// <summary>What <c>url$"…"</c> compiles to.</summary>
    public static UrlBuilder Builder() => new();

    /// <summary>The URL, as a <see cref="Uri"/> (relative or absolute, as it's written).</summary>
    public Uri ToUri() => new(_text, UriKind.RelativeOrAbsolute);

    public override string ToString() => _text;

    string IFormattable.ToString(string? format, IFormatProvider? formatProvider) => _text;

    public bool Equals(Url? other) => other is not null && other._text == _text;

    public override bool Equals(object? obj) => obj is Url other && Equals(other);

    public override int GetHashCode() => _text.GetHashCode();

    public static bool operator ==(Url? a, Url? b) => a is null ? b is null : a.Equals(b);

    public static bool operator !=(Url? a, Url? b) => !(a == b);

    internal static Url Of(string text) => new(text);
}

/// <summary>
/// Builds a <see cref="Url"/>: what <c>url$"…"</c> compiles to. The text written so far says which part of the URL a hole
/// is in. In the path, a hole is one segment (a <c>/</c> in it is encoded), a list is a segment for each item, and a
/// Url goes in as it is (<c>url$"{site}/lots"</c>). In the query or the fragment, every hole is a value, encoded, a Url
/// too (<c>?returnUrl={here}</c>); a list after <c>name=</c> repeats the parameter for each item, and None leaves the
/// parameter out. Values are written as code reads them: in the invariant culture, dates and times in ISO 8601.
/// </summary>
public sealed partial class UrlBuilder
{
    private enum Part { Path, Query, Fragment }

    private readonly StringBuilder _text = new();
    private Part _part = Part.Path;
    // A parameter left out (None) that was the query's first: the next one starts the query instead of `&`.
    private bool _queryStartRemoved;

    /// <summary>The URL as written in the literal.</summary>
    public UrlBuilder Text(string text)
    {
        if (_queryStartRemoved && text.StartsWith('&'))
            text = "?" + text[1..];
        _queryStartRemoved = false;
        Append(text);
        return this;
    }

    public UrlBuilder Hole(Url url)
    {
        if (_part == Part.Path)
        {
            Text(url.ToString());
            return this;
        }
        return Value(url.ToString());
    }

    public UrlBuilder Hole(Uri uri) => Hole(Url.Of(uri.OriginalString));

    public UrlBuilder Hole(string value) => Value(value);

    public UrlBuilder Hole(bool value) => Value(value ? "true" : "false");

    public UrlBuilder Hole(char value) => Value(value.ToString());

    public UrlBuilder Hole(DateTime value) => Value(value.ToString("O", CultureInfo.InvariantCulture));

    public UrlBuilder Hole(DateTimeOffset value) => Value(value.ToString("O", CultureInfo.InvariantCulture));

    public UrlBuilder Hole(DateOnly value) => Value(value.ToString("O", CultureInfo.InvariantCulture));

    public UrlBuilder Hole(TimeOnly value) => Value(value.ToString("O", CultureInfo.InvariantCulture));

    /// <summary>A number, Guid or other formattable value, in the invariant culture.</summary>
    public UrlBuilder Hole<T>(T value) where T : IFormattable => Value(value.ToString(null, CultureInfo.InvariantCulture));

    /// <summary><c>{price:F2}</c>: formatted, in the invariant culture.</summary>
    public UrlBuilder Hole<T>(T value, string format) where T : IFormattable => Value(value.ToString(format, CultureInfo.InvariantCulture));

    public UrlBuilder Hole(Option<string> value) => value.IsSome ? Hole(value.Value) : Missing();

    public UrlBuilder Hole(Option<bool> value) => value.IsSome ? Hole(value.Value) : Missing();

    public UrlBuilder Hole(Option<DateTime> value) => value.IsSome ? Hole(value.Value) : Missing();

    public UrlBuilder Hole(Option<DateOnly> value) => value.IsSome ? Hole(value.Value) : Missing();

    public UrlBuilder Hole(Option<DateTimeOffset> value) => value.IsSome ? Hole(value.Value) : Missing();

    public UrlBuilder Hole(Option<TimeOnly> value) => value.IsSome ? Hole(value.Value) : Missing();

    // Options of formattable values, each its own overload, as Piper reads `int?` as Option<int> itself.
    public UrlBuilder Hole(Option<int> value) => Formattable(value);
    public UrlBuilder Hole(Option<long> value) => Formattable(value);
    public UrlBuilder Hole(Option<short> value) => Formattable(value);
    public UrlBuilder Hole(Option<byte> value) => Formattable(value);
    public UrlBuilder Hole(Option<uint> value) => Formattable(value);
    public UrlBuilder Hole(Option<ulong> value) => Formattable(value);
    public UrlBuilder Hole(Option<float> value) => Formattable(value);
    public UrlBuilder Hole(Option<double> value) => Formattable(value);
    public UrlBuilder Hole(Option<decimal> value) => Formattable(value);
    public UrlBuilder Hole(Option<Guid> value) => Formattable(value);
    public UrlBuilder Hole(Option<TimeSpan> value) => Formattable(value);

    private UrlBuilder Formattable<T>(Option<T> value) where T : IFormattable => value.IsSome ? Hole(value.Value) : Missing();

    public UrlBuilder Hole(IEnumerable<string> values) => Each(values);

    public UrlBuilder Hole<T>(IEnumerable<T> values) where T : IFormattable =>
        Each(values.Select(v => v.ToString(null, CultureInfo.InvariantCulture)));

    public Url Build() => Url.Of(_text.ToString());

    private UrlBuilder Value(string value)
    {
        _queryStartRemoved = false;
        _text.Append(Uri.EscapeDataString(value));
        return this;
    }

    /// <summary>None: in the query, after <c>name=</c>, the parameter is left out; anywhere else, nothing is written.</summary>
    private UrlBuilder Missing()
    {
        if (_part == Part.Query && ParameterName().Match(_text.ToString()) is { Success: true } parameter)
        {
            _queryStartRemoved = parameter.Value[0] == '?';
            _text.Length = parameter.Index;
            if (!_queryStartRemoved)
                return this;
            // The URL may end here: `/search?q=` without q is `/search`; otherwise the next parameter starts the query.
            _part = Part.Path;
        }
        return this;
    }

    /// <summary>
    /// Several values: in the path, a segment each (<c>a/b</c>); in the query after <c>name=</c>, the parameter for each
    /// (<c>tag=a&amp;tag=b</c>), or left out if there are none; elsewhere, separated by commas.
    /// </summary>
    private UrlBuilder Each(IEnumerable<string> values)
    {
        var items = values.ToList();
        if (items.Count == 0)
            return Missing();
        string separator = "/";
        if (_part != Part.Path)
            separator = _part == Part.Query && ParameterName().Match(_text.ToString()) is { Success: true } parameter
                ? "&" + parameter.Groups[1].Value + "="
                : ",";
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0)
                _text.Append(separator);
            Value(items[i]);
        }
        return this;
    }

    private void Append(string text)
    {
        // `?` or `#` from the literal (or a Url in the path) moves on to the query or the fragment.
        foreach (char c in text)
        {
            if (c == '?' && _part == Part.Path)
                _part = Part.Query;
            else if (c == '#' && _part != Part.Fragment)
                _part = Part.Fragment;
        }
        _text.Append(text);
    }

    /// <summary>A query parameter's start at the end of the URL so far: <c>&amp;page=</c> (or <c>?page=</c>).</summary>
    [GeneratedRegex(@"[?&]([^?&=#]+)=$")]
    private static partial Regex ParameterName();
}
