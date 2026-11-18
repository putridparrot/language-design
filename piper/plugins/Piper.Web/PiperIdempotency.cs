using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Piper.Runtime;

namespace Piper.Web;

/// <summary>The metadata <c>idempotent</c> gives an endpoint, which <see cref="PiperIdempotency.Use"/> looks for.</summary>
public sealed class IdempotentEndpoint;

/// <summary>
/// How long a request holds its key while it runs (<see cref="Lease"/>, after which one whose process died no longer
/// blocks its repeats), and how long its response is kept to answer them (<see cref="Keep"/>). Registered as a
/// singleton to change them: <c>singleton IdempotencyOptions = IdempotencyOptions() with { Keep = … };</c>.
/// </summary>
public sealed record IdempotencyOptions
{
    public TimeSpan Lease { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan Keep { get; init; } = TimeSpan.FromHours(24);
}

/// <summary>
/// Idempotent endpoints (<c>post "/issues" idempotent => …</c>): a request with an <c>Idempotency-Key</c> header runs
/// once, and a repeat of it (the client retrying after a timeout) is answered with the first one's response, marked
/// <c>Idempotent-Replayed: true</c>, rather than running again. The same key while the first is still running is 409
/// Conflict; for a different request (another body or route), 422. Keys are per signed-in user, so one user's can't
/// replay another's response. A request without the header runs as usual. Responses of 500 and over aren't kept, so
/// a repeat of a request that failed runs again.
/// Keys and responses are kept by the <see cref="IIdempotencyStore"/> registered (Piper.Data's
/// <c>DatabaseIdempotency</c>, shared by a service's pods), or else in memory.
/// </summary>
public static class PiperIdempotency
{
    public const string Header = "Idempotency-Key";

    public const string ReplayedHeader = "Idempotent-Replayed";

    /// <summary>The longest key taken: a UUID is 36 characters.</summary>
    public const int LongestKey = 255;

    /// <summary>Marks an endpoint, or every endpoint of a group, idempotent.</summary>
    public static TBuilder Idempotent<TBuilder>(TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new IdempotentEndpoint());

    /// <summary>The middleware, which a <c>server</c> adds after its policies' and before its endpoints.</summary>
    public static void Use(IApplicationBuilder app)
    {
        var inMemory = new Lazy<IIdempotencyStore>(() => new MemoryIdempotencyStore());
        app.Use(async (context, next) =>
        {
            if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method) ||
                context.GetEndpoint()?.Metadata.GetMetadata<IdempotentEndpoint>() is null ||
                !context.Request.Headers.TryGetValue(Header, out var given))
            {
                await next(context);
                return;
            }
            var store = context.RequestServices.GetService<IIdempotencyStore>() ?? inMemory.Value;
            var options = context.RequestServices.GetService<IdempotencyOptions>() ?? new IdempotencyOptions();
            await HandleAsync(context, next, given.ToString(), store, options);
        });
    }

    private static async Task HandleAsync(HttpContext context, RequestDelegate next, string key, IIdempotencyStore store, IdempotencyOptions options)
    {
        if (key.Length is 0 or > LongestKey)
        {
            await Results.Problem(statusCode: 400, title: $"The {Header} header must be 1 to {LongestKey} characters.").ExecuteAsync(context);
            return;
        }
        var user = context.User.Identity is { IsAuthenticated: true, Name: { } name } ? name : "";
        var scoped = $"{user}\n{key}";
        var claim = await store.ClaimAsync(scoped, await FingerprintAsync(context.Request), options.Lease);
        switch (claim.State)
        {
            case IdempotencyState.Mismatch:
                await Results.Problem(statusCode: 422, title: $"This {Header} was used for a different request.",
                    detail: "A key is for one request and its retries: the same method, route and body. Use a new key for a new request.").ExecuteAsync(context);
                return;
            case IdempotencyState.Busy:
                await Results.Problem(statusCode: 409, title: $"A request with this {Header} is still running.",
                    detail: "Try again once it has finished, to be answered with its response.").ExecuteAsync(context);
                return;
            case IdempotencyState.Done:
                await ReplayAsync(context.Response, claim.Response!);
                return;
        }

        // Claimed: run it, with the response kept as it's written, to keep it for the repeats.
        var original = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next(context);
        }
        catch
        {
            context.Response.Body = original;
            await store.ReleaseAsync(scoped);
            throw;
        }
        context.Response.Body = original;
        var response = context.Response;
        var body = buffer.ToArray();
        if (response.StatusCode < 500)
            await store.CompleteAsync(scoped, new IdempotentResponse(response.StatusCode, response.ContentType, response.Headers.Location.ToString() is { Length: > 0 } at ? at : null, body), options.Keep);
        else
            await store.ReleaseAsync(scoped);
        await original.WriteAsync(body, context.RequestAborted);
    }

    /// <summary>What makes a request the same request: its method, path, query and body, hashed.</summary>
    private static async Task<string> FingerprintAsync(HttpRequest request)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"{request.Method} {request.Path}{request.QueryString}\n"));
        // Read here and read again by the endpoint, so kept to be read twice.
        request.EnableBuffering();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk)) > 0)
            hash.AppendData(chunk, 0, read);
        request.Body.Position = 0;
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task ReplayAsync(HttpResponse response, IdempotentResponse kept)
    {
        response.StatusCode = kept.Status;
        if (kept.ContentType is { } type)
            response.ContentType = type;
        if (kept.Location is { } location)
            response.Headers.Location = location;
        response.Headers[ReplayedHeader] = "true";
        response.ContentLength = kept.Body.Length;
        await response.Body.WriteAsync(kept.Body);
    }
}
