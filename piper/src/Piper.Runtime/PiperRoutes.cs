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
}
