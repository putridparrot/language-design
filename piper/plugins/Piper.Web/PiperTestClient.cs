using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Piper.Web;

/// <summary>
/// A web app served in memory for tests, and an <see cref="HttpClient"/> calling it: no port, no sockets.
/// <c>using let site = await TestClient.Start(() =&gt; BuildApp());</c> builds the app with the function the program
/// already has (whatever it returns is <see cref="TestClient{T}.App"/>), serving it from memory instead of the
/// addresses it asks for, and starts it. The client keeps cookies, as a browser does, and <see cref="PostForm"/> sends
/// the anti-forgery token a form needs. Disposing it stops the app.
/// </summary>
public class TestClient : HttpClient
{
    private readonly IHost _host;
    private readonly CookieContainer _cookies;

    private protected TestClient(IHost host, InMemoryServer server, CookieContainer cookies)
        : base(new CookieHandler(cookies, server))
    {
        _host = host;
        _cookies = cookies;
        BaseAddress = InMemoryServer.Address;
    }

    /// <summary>The app's services, to reach what it registered.</summary>
    public IServiceProvider Services => _host.Services;

    /// <summary>
    /// Builds the app with <paramref name="build"/>, served in memory, and starts it. The function must build one web
    /// app (with <c>WebApplication.CreateBuilder</c> or a host builder); what it returns is the client's <c>App</c>.
    /// </summary>
    public static async Task<TestClient<T>> Start<T>(Func<T> build)
    {
        var server = new InMemoryServer();
        var (value, host) = HostCapture.Build(build, server);
        await host.StartAsync();
        return new TestClient<T>(value, host, server, new CookieContainer());
    }

    /// <summary>
    /// Posts <paramref name="fields"/> as a form, with the anti-forgery token the app would have put in it. With
    /// <paramref name="htmx"/>, as htmx does (an <c>HX-Request</c> header), so a group with a layout answers with
    /// the fragment alone.
    /// </summary>
    public async Task<HttpResponseMessage> PostForm(string url, IEnumerable<KeyValuePair<string, string>> fields, bool htmx = false)
    {
        var all = fields.ToList();
        if (Services.GetService<IAntiforgery>() is { } antiforgery)
        {
            // The token is made for a request, and its cookie half set on the response: kept, as a browser would.
            var context = new DefaultHttpContext { RequestServices = Services };
            context.Request.Scheme = InMemoryServer.Address.Scheme;
            context.Request.Host = new HostString(InMemoryServer.Address.Host);
            var tokens = antiforgery.GetAndStoreTokens(context);
            foreach (var cookie in context.Response.Headers.SetCookie)
                _cookies.SetCookies(InMemoryServer.Address, cookie!);
            all.Add(new(tokens.FormFieldName, tokens.RequestToken!));
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(all) };
        if (htmx)
            request.Headers.Add("HX-Request", "true");
        return await SendAsync(request);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _host.StopAsync().GetAwaiter().GetResult();
            _host.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>Sends the cookies kept for the app, and keeps the ones it sets.</summary>
    private sealed class CookieHandler(CookieContainer cookies, InMemoryServer server) : DelegatingHandler(server.Handler)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (cookies.GetCookieHeader(uri) is { Length: > 0 } header)
                request.Headers.Add("Cookie", header);
            var response = await base.SendAsync(request, cancellationToken);
            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                foreach (var setCookie in setCookies)
                    cookies.SetCookies(uri, setCookie);
            }
            return response;
        }
    }
}

/// <summary>A <see cref="TestClient"/> with what the app's build function returned.</summary>
public sealed class TestClient<T> : TestClient
{
    internal TestClient(T app, IHost host, InMemoryServer server, CookieContainer cookies) : base(host, server, cookies) => App = app;

    /// <summary>What the build function returned: the app, or the app and what the test drives directly.</summary>
    public T App { get; }
}

/// <summary>
/// Catches the host a build function makes, through the hosting library's "HostBuilding" event (as ASP.NET Core's
/// WebApplicationFactory does), to give it the in-memory server before it's built. Only hosts built in the same async
/// flow as the call are touched, so tests running together don't take each other's.
/// </summary>
internal static class HostCapture
{
    private static readonly AsyncLocal<Capture?> Current = new();

    static HostCapture() => DiagnosticListener.AllListeners.Subscribe(new Observer<DiagnosticListener>(listener =>
    {
        if (listener.Name == "Microsoft.Extensions.Hosting")
            listener.Subscribe(new Observer<KeyValuePair<string, object?>>(OnEvent));
    }));

    private sealed class Capture(InMemoryServer server)
    {
        public InMemoryServer Server { get; } = server;

        public IHost? Host { get; set; }
    }

    public static (T Value, IHost Host) Build<T>(Func<T> build, InMemoryServer server)
    {
        var outer = Current.Value;
        var capture = new Capture(server);
        Current.Value = capture;
        try
        {
            var value = build();
            return (value, capture.Host ?? throw new InvalidOperationException(
                "The function given to TestClient.Start built no web app: it should build one with WebApplication.CreateBuilder (or a host builder) and return it."));
        }
        finally
        {
            Current.Value = outer;
        }
    }

    private static void OnEvent(KeyValuePair<string, object?> e)
    {
        if (Current.Value is not { } capture || capture.Host is not null)
            return;
        switch (e.Key, e.Value)
        {
            case ("HostBuilding", HostApplicationBuilder builder):
                UseServer(builder.Services, capture.Server);
                break;
            case ("HostBuilding", IHostBuilder builder):
                builder.ConfigureServices(services => UseServer(services, capture.Server));
                break;
            case ("HostBuilt", IHost host):
                capture.Host = host;
                break;
        }
    }

    private static void UseServer(IServiceCollection services, InMemoryServer server)
    {
        services.RemoveAll<IServer>();
        services.AddSingleton<IServer>(server);
    }

    private sealed class Observer<T>(Action<T> next) : IObserver<T>
    {
        public void OnNext(T value) => next(value);

        public void OnError(Exception error) { }

        public void OnCompleted() { }
    }
}

/// <summary>
/// An ASP.NET Core server with no sockets: each request a client sends through <see cref="Handler"/> is given to the
/// app directly, and its response streams back as it's written, so server-sent events work as over HTTP.
/// </summary>
internal sealed class InMemoryServer : IServer
{
    public static readonly Uri Address = new("http://localhost/");

    private Func<IFeatureCollection, CancellationToken, Task<HttpResponseMessage>>? _send;

    public InMemoryServer()
    {
        var addresses = new ServerAddressesFeature();
        addresses.Addresses.Add(Address.ToString().TrimEnd('/'));
        Features.Set<IServerAddressesFeature>(addresses);
        Handler = new SendHandler(this);
    }

    public IFeatureCollection Features { get; } = new FeatureCollection();

    public HttpMessageHandler Handler { get; }

    public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken) where TContext : notnull
    {
        _send = (features, aborted) => Exchange.Run(application, features, aborted);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _send = null;
        return Task.CompletedTask;
    }

    public void Dispose() { }

    private sealed class BodyDetection(bool canHaveBody) : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody { get; } = canHaveBody;
    }

    private sealed class ServerAddressesFeature : IServerAddressesFeature
    {
        public ICollection<string> Addresses { get; } = [];

        public bool PreferHostingUrls { get; set; }
    }

    private sealed class SendHandler(InMemoryServer server) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var send = server._send ?? throw new InvalidOperationException("The app isn't running.");
            var uri = request.RequestUri!.IsAbsoluteUri ? request.RequestUri : new Uri(Address, request.RequestUri);

            var requestFeature = new HttpRequestFeature
            {
                Method = request.Method.Method,
                Scheme = uri.Scheme,
                Protocol = "HTTP/1.1",
                PathBase = "",
                Path = Uri.UnescapeDataString(uri.AbsolutePath),
                RawTarget = uri.PathAndQuery,
                QueryString = uri.Query,
                Body = request.Content is null ? Stream.Null : await request.Content.ReadAsStreamAsync(cancellationToken),
            };
            requestFeature.Headers.Host = uri.Authority;
            foreach (var header in request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>()))
                requestFeature.Headers.Append(header.Key, header.Value.ToArray());

            var features = new FeatureCollection();
            features.Set<IHttpRequestFeature>(requestFeature);
            // Without it, ASP.NET Core takes the request to have no body, and doesn't read one.
            features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetection(request.Content is not null));
            features.Set<IHttpConnectionFeature>(new HttpConnectionFeature
            {
                ConnectionId = Guid.NewGuid().ToString("N"),
                LocalIpAddress = IPAddress.Loopback,
                RemoteIpAddress = IPAddress.Loopback,
                LocalPort = 80,
                RemotePort = 50000,
            });
            return await send(features, cancellationToken);
        }
    }
}

/// <summary>One request through the app: its response features, and the response the client gets once it starts.</summary>
internal sealed class Exchange : IHttpResponseFeature, IHttpResponseBodyFeature, IHttpRequestLifetimeFeature
{
    private readonly Pipe _pipe = new();
    private readonly TaskCompletionSource<HttpResponseMessage> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _aborted;
    private readonly List<(Func<object, Task> Callback, object State)> _onStarting = [];
    private readonly List<(Func<object, Task> Callback, object State)> _onCompleted = [];
    private readonly ResponseStream _stream;

    private Exchange(CancellationToken aborted)
    {
        _aborted = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        _stream = new ResponseStream(this);
        Writer = PipeWriter.Create(_stream);
    }

    public static Task<HttpResponseMessage> Run<TContext>(IHttpApplication<TContext> application, IFeatureCollection features, CancellationToken aborted)
        where TContext : notnull
    {
        var exchange = new Exchange(aborted);
        features.Set<IHttpResponseFeature>(exchange);
        features.Set<IHttpResponseBodyFeature>(exchange);
        features.Set<IHttpRequestLifetimeFeature>(exchange);
        _ = Task.Run(() => exchange.Process(application, features));
        return exchange._response.Task;
    }

    private async Task Process<TContext>(IHttpApplication<TContext> application, IFeatureCollection features) where TContext : notnull
    {
        var context = application.CreateContext(features);
        Exception? failure = null;
        try
        {
            await application.ProcessRequestAsync(context);
            await CompleteAsync();
        }
        catch (BadHttpRequestException bad) when (!HasStarted)
        {
            // As Kestrel answers it: a status the app means to give (a 400 for a missing anti-forgery token).
            failure = bad;
            StatusCode = bad.StatusCode;
            Headers.Clear();
            await CompleteAsync();
        }
        catch (Exception ex)
        {
            failure = ex;
            // As ASP.NET Core's TestServer does: an exception before the response started is the caller's, so a test
            // shows what went wrong rather than a 500.
            if (!_response.TrySetException(ex))
                await _pipe.Writer.CompleteAsync(ex);
        }
        finally
        {
            foreach (var (callback, state) in Enumerable.Reverse(_onCompleted))
                await callback(state);
            application.DisposeContext(context, failure);
        }
    }

    // IHttpResponseFeature

    public int StatusCode { get; set; } = 200;

    public string? ReasonPhrase { get; set; }

    public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();

    public Stream Body
    {
        get => _stream;
        set => throw new NotSupportedException("The in-memory server's response body can't be replaced.");
    }

    public bool HasStarted { get; private set; }

    public void OnStarting(Func<object, Task> callback, object state) => _onStarting.Add((callback, state));

    public void OnCompleted(Func<object, Task> callback, object state) => _onCompleted.Add((callback, state));

    // IHttpResponseBodyFeature

    public Stream Stream => _stream;

    public PipeWriter Writer { get; }

    public void DisableBuffering() { }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (HasStarted)
            return;
        // OnStarting callbacks run last added first, and may still set headers.
        foreach (var (callback, state) in Enumerable.Reverse(_onStarting))
            await callback(state);
        HasStarted = true;

        var reader = new ReaderStream(_pipe.Reader.AsStream(), _aborted);
        var response = new HttpResponseMessage((HttpStatusCode)StatusCode) { Content = new StreamContent(reader), Version = HttpVersion.Version11 };
        if (ReasonPhrase is { } reason)
            response.ReasonPhrase = reason;
        foreach (var (name, values) in Headers)
        {
            if (!response.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values))
                response.Content.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values);
        }
        _response.TrySetResult(response);
    }

    public async Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default)
    {
        await using var file = File.OpenRead(path);
        file.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[count ?? file.Length - offset];
        await file.ReadExactlyAsync(buffer, cancellationToken);
        await _stream.WriteAsync(buffer, cancellationToken);
    }

    public async Task CompleteAsync()
    {
        await StartAsync();
        await Writer.FlushAsync();
        await _pipe.Writer.CompleteAsync();
    }

    // IHttpRequestLifetimeFeature

    public CancellationToken RequestAborted
    {
        get => _aborted.Token;
        set => throw new NotSupportedException();
    }

    public void Abort() => _aborted.Cancel();

    /// <summary>The body as the app writes it: the first write starts the response.</summary>
    private sealed class ResponseStream(Exchange exchange) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await exchange.StartAsync(cancellationToken);
            await exchange._pipe.Writer.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await exchange.StartAsync(cancellationToken);
            await exchange._pipe.Writer.FlushAsync(cancellationToken);
        }

        public override void Flush() => FlushAsync().GetAwaiter().GetResult();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>The body as the client reads it: closing it aborts the request, as closing a connection would.</summary>
    private sealed class ReaderStream(Stream inner, CancellationTokenSource aborted) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                aborted.Cancel();
                inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
