using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Piper.Runtime;

namespace Piper.Web;

/// <summary>
/// Live pages: the server pushes fragments to the pages watching a topic, which htmx swaps in, so a price, a list or
/// a countdown changes on every open page when it changes on the server, with no JavaScript written and no state per
/// page. A page subscribes with htmx's SSE extension (which <c>&lt;Htmx.Script /&gt;</c> loads):
/// <code>
/// &lt;section hx-ext="sse" sse-connect={Live.Url("lot-7")}&gt;
///     &lt;span sse-swap="price"&gt;{lot.current:C}&lt;/span&gt;
/// &lt;/section&gt;
/// </code>
/// and anything that changes it publishes the fragment, by topic and event: <c>Live.Publish("lot-7", "price", …)</c>.
/// <para>
/// The topics are the process's: an app serving several pods publishes on each (through the actor holding the state,
/// say), as each pod's pages are connected to it. A page that can't keep up has its oldest updates dropped rather than
/// holding the publisher up.
/// </para>
/// </summary>
public static class Live
{
    /// <summary>Where pages connect: <c>/_piper/live?topic=…</c>, mapped by every endpoints group (once per app).</summary>
    public const string Path = "/_piper/live";

    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<ChannelWriter<string>, byte>> Topics = new();

    /// <summary>A page's <c>sse-connect</c> URL, for the topics it watches.</summary>
    public static string Url(params string[] topics) =>
        Path + "?" + string.Join("&", topics.Select(t => "topic=" + Uri.EscapeDataString(t)));

    /// <summary>
    /// Sends <paramref name="fragment"/> to every page watching <paramref name="topic"/>, as the event
    /// <paramref name="name"/>, which elements with <c>sse-swap="name"</c> swap in. How many pages it went to.
    /// </summary>
    public static int Publish(string topic, string name, Html fragment)
    {
        if (!Topics.TryGetValue(topic, out var pages) || pages.IsEmpty)
            return 0;
        var message = Event(name, fragment.ToString());
        int sent = 0;
        foreach (var page in pages.Keys)
        {
            if (page.TryWrite(message))
                sent++;
        }
        return sent;
    }

    /// <summary>How many pages are watching <paramref name="topic"/>.</summary>
    public static int Watching(string topic) => Topics.TryGetValue(topic, out var pages) ? pages.Count : 0;

    /// <summary>A server-sent event: its name, then each line of its data.</summary>
    private static string Event(string name, string data)
    {
        var text = new StringBuilder();
        text.Append("event: ").Append(name.Replace("\n", "").Replace("\r", "")).Append('\n');
        foreach (var line in data.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            text.Append("data: ").Append(line).Append('\n');
        return text.Append('\n').ToString();
    }

    internal static void Map(IEndpointRouteBuilder app) =>
        app.MapGet(Path, (Delegate)Stream).ExcludeFromDescription();

    /// <summary>A page's connection: its topics' events as they're published, and a comment now and then to keep it open.</summary>
    private static async Task Stream(HttpContext http, string[] topic)
    {
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        var page = Channel.CreateBounded<string>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
        foreach (var name in topic)
            Topics.GetOrAdd(name, _ => new()).TryAdd(page.Writer, 0);
        try
        {
            await Write(http, ": connected\n\n");
            var stopped = http.RequestAborted;
            while (!stopped.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stopped);
                wait.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    var message = await page.Reader.ReadAsync(wait.Token);
                    await Write(http, message);
                }
                catch (OperationCanceledException) when (!stopped.IsCancellationRequested)
                {
                    await Write(http, ": still here\n\n");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            foreach (var name in topic)
            {
                if (Topics.TryGetValue(name, out var pages))
                    pages.TryRemove(page.Writer, out _);
            }
        }
    }

    private static async Task Write(HttpContext http, string text)
    {
        await http.Response.WriteAsync(text, http.RequestAborted);
        await http.Response.Body.FlushAsync(http.RequestAborted);
    }
}
