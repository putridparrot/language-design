using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Piper.Runtime;

namespace Piper.Web;

/// <summary>
/// <c>[Status(409)]</c> on an enum variant: the HTTP status a handler's <c>Err</c> holding it answers. On a whole enum
/// (or a record), the status of all its errors, which a variant's own overrides. Without one, an error is a 400.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class StatusAttribute(int code) : Attribute
{
    public int Code { get; } = code;
}

/// <summary>
/// Answers a handler's Option or Result as HTTP: <c>Some(x)</c> and <c>Ok(x)</c> are 200 with <c>x</c>, <c>None</c> is
/// 404, and <c>Err(e)</c> is ProblemDetails with the status <see cref="StatusAttribute"/> gives. Markup (<see cref="Html"/>),
/// alone or inside either, is a page: <c>text/html</c>. The web plugin wraps
/// each endpoint it maps in <see cref="Answer"/>; a handler returning anything else is left alone, at no cost.
/// </summary>
public static class PiperResponses
{
    /// <summary>The ProblemDetails members an error's field can't also be an extension of.</summary>
    private static readonly HashSet<string> ProblemMembers = new(["type", "title", "status", "detail", "instance"], StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<Type, Func<object, HttpContext, object?>?> Readers = new();

    public static RouteHandlerBuilder Answer(RouteHandlerBuilder endpoint) =>
        Describe(endpoint.AddEndpointFilterFactory((context, next) => Filter(context, next, null)));

    /// <summary>
    /// An endpoint of a group with a <c>layout</c>: its markup is put in the layout, <c>Page(body)</c>, unless htmx asked
    /// for just the fragment (see <see cref="Htmx.WantsFragment"/>). So a handler is written once, for both.
    /// </summary>
    public static RouteHandlerBuilder Answer(RouteHandlerBuilder endpoint, Func<Html, Html> layout) =>
        Describe(endpoint.AddEndpointFilterFactory((context, next) => Filter(context, next, layout)));

    /// <summary>
    /// Says what the endpoint answers, for OpenAPI and other readers of its metadata, from its handler's return type, as
    /// <see cref="Filter"/> answers it: an Option is 200 with its value or 404; a Result is 200 with its value or a
    /// ProblemDetails with each status its error's <see cref="StatusAttribute"/>s give (400 without one); markup is
    /// <c>text/html</c>. ASP.NET Core would describe the handler's Option or Result itself as the 200's JSON. C# writes
    /// these by hand (<c>.Produces&lt;Lot&gt;(200).Produces(404)</c>), where they can drift from what the handler does.
    /// </summary>
    private static RouteHandlerBuilder Describe(RouteHandlerBuilder endpoint)
    {
        ((IEndpointConventionBuilder)endpoint).Finally(builder =>
        {
            if (builder.Metadata.OfType<MethodInfo>().FirstOrDefault() is not { } handler)
                return;
            var returned = Awaited(handler.ReturnType);
            if (Reader(returned) is null)
                return;
            // ASP.NET Core's own guess: the Option or Result as JSON.
            foreach (var guessed in builder.Metadata.OfType<IProducesResponseTypeMetadata>().Where(m => m.Type == returned).ToList())
                builder.Metadata.Remove(guessed);
            foreach (var (status, type, contentType) in Responses(returned).DistinctBy(r => (r.Status, r.ContentType)))
                builder.Metadata.Add(new ProducesResponseTypeMetadata(status, type, contentType is null ? [] : [contentType]));
        });
        return endpoint;
    }

    /// <summary>What a handler's value of <paramref name="type"/> is answered with: statuses, the body's type, and its content type.</summary>
    private static IEnumerable<(int Status, Type? Type, string? ContentType)> Responses(Type type)
    {
        if (type == typeof(Html))
        {
            yield return (StatusCodes.Status200OK, typeof(string), "text/html");
            yield break;
        }
        if (type == typeof(string))
        {
            yield return (StatusCodes.Status200OK, typeof(string), "text/plain");
            yield break;
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Option<>))
        {
            foreach (var response in Responses(type.GetGenericArguments()[0]))
                yield return response;
            yield return (StatusCodes.Status404NotFound, typeof(void), null);
            yield break;
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Result<,>))
        {
            foreach (var response in Responses(type.GetGenericArguments()[0]))
                yield return response;
            foreach (var status in ErrorStatuses(type.GetGenericArguments()[1]).Order())
                yield return (status, typeof(Microsoft.AspNetCore.Mvc.ProblemDetails), "application/problem+json");
            yield break;
        }
        if (typeof(IResult).IsAssignableFrom(type))
            yield break;
        yield return (StatusCodes.Status200OK, type, "application/json");
    }

    /// <summary>
    /// The statuses an error of <paramref name="error"/> can be answered with: each variant's <see cref="StatusAttribute"/>
    /// (a Piper enum's variants are records nested in it, deriving from it), or the type's own, or 400.
    /// </summary>
    private static IEnumerable<int> ErrorStatuses(Type error)
    {
        int Own(Type type, int fallback) => type.GetCustomAttribute<StatusAttribute>(inherit: false)?.Code ?? fallback;
        int whole = error.GetCustomAttribute<StatusAttribute>(inherit: true)?.Code ?? StatusCodes.Status400BadRequest;
        var variants = error.IsAbstract
            ? error.GetNestedTypes().Where(t => t.IsSubclassOf(error) && !t.IsAbstract).ToList()
            : [];
        return variants.Count == 0 ? [whole] : variants.Select(v => Own(v, whole)).Distinct();
    }

    private static readonly AsyncLocal<HttpContext?> Request = new();

    /// <summary>The request a handler answering with markup is answering (for <see cref="Htmx.Script"/>), or null.</summary>
    internal static HttpContext? Answering => Request.Value;

    private const string LayoutKey = "Piper.Web.Layout";

    /// <summary>
    /// A <c>&lt;form method="post"&gt;</c>'s hidden anti-forgery field, made (and its cookie set) for the request being
    /// answered; none without ASP.NET Core's antiforgery.
    /// </summary>
    private static Html FormToken(HttpContext http)
    {
        if (http.RequestServices.GetService<Microsoft.AspNetCore.Antiforgery.IAntiforgery>() is not { } antiforgery)
            return Html.Empty;
        var tokens = antiforgery.GetAndStoreTokens(http);
        return Html.Raw($"<input type=\"hidden\" name=\"{System.Net.WebUtility.HtmlEncode(tokens.FormFieldName)}\" value=\"{System.Net.WebUtility.HtmlEncode(tokens.RequestToken)}\">");
    }

    /// <summary>
    /// A filter for a handler whose result (once awaited) is an Option, a Result or markup; other handlers get none. While
    /// it runs, the request is known to the forms its markup makes.
    /// </summary>
    private static EndpointFilterDelegate Filter(EndpointFilterFactoryContext context, EndpointFilterDelegate next, Func<Html, Html>? layout) =>
        Reader(Awaited(context.MethodInfo.ReturnType)) is { } read
            ? async invocation =>
            {
                var http = invocation.HttpContext;
                if (layout is not null)
                    http.Items[LayoutKey] = layout;
                PiperHtml.FormTokenSource = () => FormToken(http);
                Request.Value = http;
                try
                {
                    // A request ASP.NET Core couldn't bind (a missing body, a parameter that isn't a number) never
                    // reached the handler: its answer (a 400) is ASP.NET Core's result, which goes as it is.
                    return await next(invocation) switch
                    {
                        IResult answered => answered,
                        { } value => read(value, http),
                        null => null,
                    };
                }
                finally
                {
                    PiperHtml.FormTokenSource = null;
                    Request.Value = null;
                }
            }
            : next;

    private static Type Awaited(Type type) =>
        type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Task<>) || type.GetGenericTypeDefinition() == typeof(ValueTask<>))
            ? type.GetGenericArguments()[0]
            : type;

    /// <summary>How to answer a value of <paramref name="type"/>, if it's an Option, a Result or markup.</summary>
    private static Func<object, HttpContext, object?>? Reader(Type type) =>
        Readers.GetOrAdd(type, t =>
        {
            if (t == typeof(Html))
                return (value, http) => Page((Html)value, http);
            if (!t.IsGenericType)
                return null;
            var definition = t.GetGenericTypeDefinition();
            var method = definition == typeof(Option<>) ? nameof(FromOption) : definition == typeof(Result<,>) ? nameof(FromResult) : null;
            return method is null
                ? null
                : typeof(PiperResponses).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(t.GetGenericArguments())
                    .CreateDelegate<Func<object, HttpContext, object?>>();
        });

    /// <summary>
    /// Markup (a markup literal's <see cref="Html"/>) as a page: <c>text/html</c>, not JSON. In a group with a layout,
    /// put in it, unless htmx asked for the fragment alone; the answer then varies by <c>HX-Request</c>, for caches.
    /// </summary>
    private static IResult Page(Html markup, HttpContext http)
    {
        if (http.Items[LayoutKey] is Func<Html, Html> layout)
        {
            http.Response.Headers.Append("Vary", "HX-Request");
            if (!Htmx.WantsFragment(http))
                markup = layout(markup);
        }
        return TypedResults.Content(markup.ToString(), "text/html; charset=utf-8");
    }

    private static object? FromOption<T>(object value, HttpContext http) =>
        (Option<T>)value is { IsSome: true } some ? Ok(some.Value, http) : TypedResults.NotFound();

    private static object? FromResult<T, E>(object value, HttpContext http)
    {
        var result = (Result<T, E>)value;
        return result.IsOk ? Ok(result.Value, http) : Problem(result.Error, http);
    }

    /// <summary>
    /// A successful value: as JSON of its static type, so a Piper enum is written as one (its runtime type is a
    /// variant's record); a string as text, as ASP.NET Core writes a handler's string; and an Option inside answered
    /// as one (<c>Ok(None)</c> is 404).
    /// </summary>
    private static object? Ok<T>(T value, HttpContext http) => value switch
    {
        null => null,
        Html markup => Page(markup, http),
        string or IResult => value,
        _ when Reader(typeof(T)) is { } read => read(value, http),
        _ => TypedResults.Json(value),
    };

    /// <summary>
    /// An error as ProblemDetails. A string is its <c>detail</c>. Otherwise it's written as JSON, with the app's JSON
    /// options: an object's properties (a variant's <c>kind</c> and fields, or a record's fields) become extension
    /// members, a fieldless variant is <c>kind</c>, and any other value is <c>error</c>. A field named as one of
    /// ProblemDetails' own members fills it if it's a string (<c>detail</c>, <c>title</c>), or else is left out.
    /// </summary>
    private static IResult Problem<E>(E error, HttpContext http)
    {
        var status = error?.GetType().GetCustomAttribute<StatusAttribute>(inherit: true)?.Code ?? StatusCodes.Status400BadRequest;
        if (error is string message)
            return TypedResults.Problem(detail: message, statusCode: status);

        var options = http.RequestServices.GetService<IOptions<JsonOptions>>()?.Value.SerializerOptions ?? JsonSerializerOptions.Web;
        var json = JsonSerializer.SerializeToElement(error, options);
        var members = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var extensions = new Dictionary<string, object?>();
        if (json.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in json.EnumerateObject())
            {
                if (!ProblemMembers.Contains(property.Name))
                    extensions[property.Name] = property.Value;
                else if (property.Value.ValueKind == JsonValueKind.String)
                    members[property.Name] = property.Value.GetString()!;
            }
        }
        else
        {
            var isEnum = new PiperEnumJsonConverterFactory().CanConvert(typeof(E));
            extensions[isEnum ? PiperEnumJsonConverterFactory.Kind : "error"] = json;
        }
        return TypedResults.Problem(
            detail: members.GetValueOrDefault("detail"),
            instance: members.GetValueOrDefault("instance"),
            statusCode: status,
            title: members.GetValueOrDefault("title"),
            type: members.GetValueOrDefault("type"),
            extensions: extensions);
    }
}
