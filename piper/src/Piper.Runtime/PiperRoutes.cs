using System.Globalization;

namespace Piper.Runtime;

/// <summary>
/// What the web plugin's route functions build paths with: <c>get Lot "/lots/{id:int}" (id: LotId) =&gt; …</c> gives
/// <c>SiteRoutes.Lot(id: LotId) =&gt; string</c>, whose body is <c>PiperRoutes.Path("/lots/", PiperRoutes.Segment(id))</c>.
/// Each value is escaped for a path segment, and numbers and ids are written in the invariant culture, as ASP.NET Core
/// reads route values.
/// </summary>
public static class PiperRoutes
{
    public static string Path(params string[] parts) => string.Concat(parts);

    /// <summary>A route value, escaped (<c>"a b/c"</c> is <c>a%20b%2Fc</c>); nothing for None.</summary>
    public static string Segment(string? value) => value is null ? "" : Uri.EscapeDataString(value);

    /// <summary>A number, date, <c>Guid</c> or newtype of one, in the invariant culture.</summary>
    public static string Segment<T>(T value) where T : IFormattable => Uri.EscapeDataString(value.ToString(null, CultureInfo.InvariantCulture));

    /// <summary>Anything else by its text: a newtype of a string (a <c>Slug</c>) is its value.</summary>
    public static string Segment(object value) => Uri.EscapeDataString(value.ToString() ?? "");

    /// <summary><c>{*path}</c>: each part escaped, the <c>/</c>s between them kept.</summary>
    public static string CatchAll(string? value) => value is null ? "" : string.Join("/", value.Split('/').Select(Uri.EscapeDataString));

    /// <summary>
    /// A path with one more query value: <c>/search?q=clock</c>, then <c>/search?q=clock&amp;page=2</c>. The value is a
    /// <see cref="Query(string, string?)"/> pair, or null (None) for one left out.
    /// </summary>
    public static string WithQuery(string path, string? value) =>
        value is null ? path : path + (path.Contains('?') ? "&" : "?") + value;

    /// <summary><c>name=value</c>, both escaped; null (left out) for None.</summary>
    public static string? Query(string name, string? value) =>
        value is null ? null : $"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}";

    /// <summary>A number, date, <c>Guid</c> or newtype of one, in the invariant culture, as ASP.NET Core reads it.</summary>
    public static string? Query<T>(string name, T value) where T : IFormattable =>
        Query(name, value.ToString(null, CultureInfo.InvariantCulture));

    /// <summary>An optional number, date or id: left out for None.</summary>
    public static string? Query<T>(string name, T? value) where T : struct, IFormattable =>
        value is { } present ? Query(name, present) : null;

    /// <summary>Anything else by its text (a <c>bool</c> as <c>true</c> or <c>false</c>); left out for None.</summary>
    public static string? Query(string name, object? value) => value switch
    {
        null => null,
        bool flag => Query(name, flag ? "true" : "false"),
        _ => Query(name, value.ToString()),
    };
}
