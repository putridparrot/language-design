using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Piper.Runtime;

namespace Piper.Web;

/// <summary>
/// htmx (htmx.org, Zero-Clause BSD), served by the app itself: every <c>endpoints</c> group maps
/// <c>/_piper/htmx.min.js</c> (once per app), and <c>&lt;Htmx.Script /&gt;</c> puts it in a page, with what sends the
/// anti-forgery token as a header on each request htmx makes (an <c>hx-post</c> on a button isn't in a form, so has no
/// field to send it in), which <see cref="Form{T}"/> accepts as it does the form field.
/// </summary>
public static class Htmx
{
    /// <summary>Where the app serves htmx.</summary>
    public const string ScriptPath = "/_piper/htmx.min.js";

    /// <summary>Where the app serves htmx's server-sent events extension, which live pages use (see <see cref="Live"/>).</summary>
    public const string SsePath = "/_piper/htmx-sse.js";

    private static readonly Lazy<byte[]> Code = new(() => Resource("Piper.Web.htmx.min.js"));

    private static readonly Lazy<byte[]> SseCode = new(() => Resource("Piper.Web.htmx-sse.js"));

    private static byte[] Resource(string name)
    {
        using var stream = typeof(Htmx).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Piper.Web is missing {name}.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static readonly ConditionalWeakTable<IEndpointRouteBuilder, object> Mapped = [];

    /// <summary>Maps <see cref="ScriptPath"/>, <see cref="SsePath"/> and <see cref="Live.Path"/>, once per app (every endpoints group calls it).</summary>
    public static void MapScript(IEndpointRouteBuilder app)
    {
        lock (Mapped)
        {
            if (Mapped.TryGetValue(app, out _))
                return;
            Mapped.Add(app, new object());
        }
        // Piper.Web's own routes, not the app's API: left out of OpenAPI.
        app.MapGet(ScriptPath, (HttpContext http) => Script(http, Code.Value)).ExcludeFromDescription();
        app.MapGet(SsePath, (HttpContext http) => Script(http, SseCode.Value)).ExcludeFromDescription();
        Live.Map(app);
    }

    private static IResult Script(HttpContext http, byte[] code)
    {
        http.Response.Headers.CacheControl = "public, max-age=86400";
        return Results.Bytes(code, "text/javascript");
    }

    /// <summary>
    /// <c>&lt;Htmx.Script /&gt;</c>, in a page's <c>&lt;head&gt;</c>: htmx, and the anti-forgery token (made for the
    /// request the page answers) that it sends as a header with each request it makes.
    /// </summary>
    public static Html Script()
    {
        var meta = "";
        if (PiperResponses.Answering is { } http && http.RequestServices.GetService<IAntiforgery>() is { } antiforgery)
        {
            var tokens = antiforgery.GetAndStoreTokens(http);
            if (tokens.HeaderName is { } header)
                meta = $"<meta name=\"piper-antiforgery\" data-header=\"{WebUtility.HtmlEncode(header)}\" content=\"{WebUtility.HtmlEncode(tokens.RequestToken)}\">";
        }
        return Html.Raw(
            $"<script src=\"{ScriptPath}\"></script><script src=\"{SsePath}\"></script>{meta}" +
            "<script>document.addEventListener(\"htmx:configRequest\", e => { const t = document.querySelector('meta[name=\"piper-antiforgery\"]'); " +
            "if (t) e.detail.headers[t.dataset.header] = t.content; });</script>");
    }

    /// <summary>
    /// A request htmx made to swap part of a page, which is answered with just its fragment: not one that boosts a link
    /// (<c>hx-boost</c>) or restores history, which want the whole page.
    /// </summary>
    public static bool WantsFragment(HttpContext http) =>
        http.Request.Headers["HX-Request"] == "true" && http.Request.Headers["HX-Boosted"] != "true" &&
        http.Request.Headers["HX-History-Restore-Request"] != "true";
}

/// <summary>
/// What htmx sent with a request, and what to tell it in the response: a handler takes <c>(htmx: HtmxRequest)</c>.
/// </summary>
public sealed class HtmxRequest
{
    private readonly HttpContext _http;

    private HtmxRequest(HttpContext http) => _http = http;

    /// <summary>How ASP.NET Core binds a handler's <c>HtmxRequest</c> parameter.</summary>
    public static ValueTask<HtmxRequest?> BindAsync(HttpContext http) => ValueTask.FromResult<HtmxRequest?>(new HtmxRequest(http));

    /// <summary>Whether htmx made the request (rather than a browser loading a page).</summary>
    public bool IsHtmx => _http.Request.Headers["HX-Request"] == "true";

    /// <summary>Whether it's an <c>hx-boost</c>ed link or form, which wants the whole page.</summary>
    public bool Boosted => _http.Request.Headers["HX-Boosted"] == "true";

    /// <summary>The id of the element the response will go into, if it has one.</summary>
    public string? Target => Header("HX-Target");

    /// <summary>The id of the element that made the request, if it has one.</summary>
    public string? TriggerId => Header("HX-Trigger");

    /// <summary>The name of the element that made the request, if it has one.</summary>
    public string? TriggerName => Header("HX-Trigger-Name");

    /// <summary>The page's URL when the request was made.</summary>
    public string? CurrentUrl => Header("HX-Current-URL");

    /// <summary>Go to <paramref name="url"/>, as a full page load.</summary>
    public void Redirect(string url) => _http.Response.Headers["HX-Redirect"] = url;

    /// <summary>Put <paramref name="url"/> in the address bar and history, as if the page had gone there.</summary>
    public void PushUrl(string url) => _http.Response.Headers["HX-Push-Url"] = url;

    /// <summary>Put <paramref name="url"/> in the address bar in place of the current one.</summary>
    public void ReplaceUrl(string url) => _http.Response.Headers["HX-Replace-Url"] = url;

    /// <summary>Raise an event in the browser (several calls raise several), for other elements to react to (<c>hx-trigger="saved from:body"</c>).</summary>
    public void TriggerEvent(string name) => _http.Response.Headers.Append("HX-Trigger", name);

    /// <summary>Put the response into the element matching <paramref name="selector"/> instead.</summary>
    public void Retarget(string selector) => _http.Response.Headers["HX-Retarget"] = selector;

    /// <summary>Swap the response in another way: <c>"outerHTML"</c>, <c>"beforeend"</c>, ….</summary>
    public void Reswap(string swap) => _http.Response.Headers["HX-Reswap"] = swap;

    /// <summary>Reload the whole page.</summary>
    public void Refresh() => _http.Response.Headers["HX-Refresh"] = "true";

    private string? Header(string name) => _http.Request.Headers.TryGetValue(name, out var value) ? value.ToString() : null;
}
