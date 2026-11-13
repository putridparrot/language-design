using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

[assembly: HostingStartup(typeof(Piper.Watch.BrowserReload))]

namespace Piper.Watch;

/// <summary>
/// Loaded by ASP.NET Core when <c>ASPNETCORE_HOSTINGSTARTUPASSEMBLIES</c> names it, as <c>piper run --watch</c> does: adds
/// <see cref="Inject"/> in front of the program's own middleware.
/// </summary>
public sealed class BrowserReload : IHostingStartup
{
    /// <summary>Where the watcher's event stream is: it sends "reload" when it has restarted the program.</summary>
    public const string ReloadVariable = "PIPER_WATCH_RELOAD";

    public void Configure(IWebHostBuilder builder) =>
        builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, Filter>());

    private sealed class Filter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(Inject);
            next(app);
        };
    }

    /// <summary>
    /// Puts the reload script in an HTML page a browser navigated to. Only requests accepting HTML are touched (a
    /// browser's navigations), so JSON, files and event streams the program serves pass straight through, unbuffered.
    /// </summary>
    private static async Task Inject(HttpContext context, RequestDelegate next)
    {
        var events = Environment.GetEnvironmentVariable(ReloadVariable);
        if (events is null || !HttpMethods.IsGet(context.Request.Method) ||
            !context.Request.Headers.Accept.Any(a => a?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true))
        {
            await next(context);
            return;
        }

        // The page is read and changed here, so it mustn't come compressed.
        context.Request.Headers.AcceptEncoding = "identity";
        var original = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next(context);
        }
        finally
        {
            context.Response.Body = original;
        }

        buffer.Position = 0;
        if (context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) != true)
        {
            await buffer.CopyToAsync(original);
            return;
        }
        var page = Encoding.UTF8.GetString(buffer.ToArray());
        var end = page.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        page = end < 0 ? page + Script(events) : page.Insert(end, Script(events));
        var bytes = Encoding.UTF8.GetBytes(page);
        context.Response.ContentLength = bytes.Length;
        await original.WriteAsync(bytes);
    }

    /// <summary>
    /// Listens to the watcher; on "reload", asks for the page until the restarted program answers (any answer), then
    /// reloads it.
    /// </summary>
    private static string Script(string events) => $$"""
        <script>
        (() => {
          const events = new EventSource({{System.Text.Json.JsonSerializer.Serialize(events)}});
          events.onmessage = message => {
            if (message.data !== "reload") return;
            events.close();
            const reload = () => fetch(location.href, { method: "HEAD", cache: "no-store" })
              .then(() => location.reload(), () => setTimeout(reload, 250));
            setTimeout(reload, 250);
          };
        })();
        </script>
        """;
}
