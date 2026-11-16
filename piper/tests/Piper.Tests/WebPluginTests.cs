using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Piper.Plugins;
using Piper.Text;

namespace Piper.Tests;

/// <summary>
/// The web plugin (plugins/Piper.Plugins.Web): what `endpoints` checks, alongside the services plugin's `services`, as a
/// web program uses them. samples/webapp runs what they become. The plugins are loaded from their build output, as a
/// program's piper.json would load them. ASP.NET Core includes dependency injection, so the services plugin's package
/// isn't restored here: these tests run offline. (ServicesPluginTests covers `services` without the web.)
/// </summary>
public class WebPluginTests
{
    private static readonly Lazy<(ImmutableArray<IPiperPlugin> Plugins, ProgramReferences References)> Web = new(() =>
    {
        var (plugins, errors) = PluginLoader.Load(PluginsDirectory(),
            ["Piper.Plugins.Services/bin/plugin/Piper.Plugins.Services.dll", "Piper.Plugins.Web/bin/plugin/Piper.Plugins.Web.dll"]);
        Assert.Empty(errors);
        var (references, referenceErrors) = ProgramReferences.Resolve(
            Path.GetTempPath(), ImmutableDictionary<string, string>.Empty, [.. plugins.SelectMany(p => p.Frameworks)], [.. plugins.SelectMany(PluginLoader.References)]);
        Assert.Empty(referenceErrors);
        return (plugins, references);
    });

    private static string PluginsDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "plugins"));

    private static CompilationResult Compile(string text) =>
        Compilation.Compile([new SourceText(text, "main.piper")],
            new CompilationOptions { Plugins = Web.Value.Plugins, References = Web.Value.References });

    /// <summary>
    /// <c>TestClient</c> serves an app from memory for <c>test</c> blocks: the app as its build function makes it, with
    /// JSON, forms (their anti-forgery token sent for them), cookies, live pages' streams, and a request ASP.NET Core
    /// can't bind (a 400, which the response filter once broke on). An exception the app throws fails the test.
    /// </summary>
    [Fact]
    public void A_test_client_serves_the_app_from_memory()
    {
        var result = Compilation.Compile([new SourceText(""""
            using System;
            using System.IO;
            using System.Net;
            using System.Net.Http;
            using System.Net.Http.Json;
            using System.Text;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.AspNetCore.Http;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Logging;
            using Piper.Web;

            record Order(id: int, note: string?) requires id > 0;
            record NewBid(bidder: string, amount: decimal) requires amount > 0m else "Bid something.";
            enum Refusal { Unknown }

            endpoints Site {
                get Hello "/hello" (name: string?) => $"hello {name ?? "nobody"}";
                post Orders "/orders" (order: Order) => if order.id == 1 { Ok(order) } else { Err(Refusal.Unknown) };
                get Visit "/visit" (http: HttpContext) => {
                    http.Response.Cookies.Append("seen", "yes");
                    "welcome"
                };
                get Back "/back" (http: HttpContext) => http.Request.Cookies["seen"] ?? "new";
                post Bid "/bids" (form: Form<NewBid>, htmx: HtmxRequest) => match form.Result {
                    Ok(bid) => {
                        let _ = Live.Publish("bids", "bid", <b>{bid.amount:F2}</b>);
                        <p>{bid.bidder}{if htmx.IsHtmx { <i>htmx</i> }}</p>
                    },
                    Err(errors) => <p>{errors.For("amount")}</p>,
                };
                get Broken "/broken" () => {
                    let zero = 0;
                    $"{1 / zero}"
                };
            }

            /// The app as a program builds it, asking for a port it won't get.
            fn BuildApp() => (app: WebApplication, name: string) {
                let builder = WebApplication.CreateBuilder();
                builder.Logging.ClearProviders();
                builder.Services.AddAntiforgery();
                let urls: string[] = ["http://127.0.0.1:1"];
                builder.WebHost.UseUrls(urls);
                let app = builder.Build();
                Site(app);
                (app, "site")
            }

            fn Main() {}

            async test "json, a missing body and a refusal" {
                using let site = await TestClient.Start(() => BuildApp());
                AssertEqual("site", site.App.name);
                AssertEqual("hello Ada", await site.GetStringAsync("/hello?name=Ada"));
                let ok = await site.PostAsJsonAsync("/orders", Order(1, Some("first")));
                AssertEqual("""{"id":1,"note":"first"}""", await ok.Content.ReadAsStringAsync());
                AssertEqual(HttpStatusCode.BadRequest, (await site.PostAsync("/orders", None)).StatusCode);
                AssertEqual(HttpStatusCode.BadRequest, (await site.PostAsJsonAsync("/orders", Order(2, None))).StatusCode);
            }

            async test "cookies are kept between requests" {
                using let site = await TestClient.Start(() => BuildApp());
                AssertEqual("new", await site.GetStringAsync("/back"));
                await site.GetStringAsync("/visit");
                AssertEqual("yes", await site.GetStringAsync("/back"));
            }

            async test "forms send their token, and live pages stream" {
                using let site = await TestClient.Start(() => BuildApp());
                using let watching = await site.GetAsync(Live.Url("bids"), HttpCompletionOption.ResponseHeadersRead);
                using let events = StreamReader(await watching.Content.ReadAsStreamAsync());
                AssertEqual(": connected", await events.ReadLineAsync() ?? "");
                await events.ReadLineAsync();

                let bid = await site.PostForm("/bids", ["bidder": "ada", "amount": "12"], htmx: true);
                AssertEqual("<p>ada<i>htmx</i></p>", await bid.Content.ReadAsStringAsync());
                AssertEqual("event: bid", await events.ReadLineAsync() ?? "");
                AssertEqual("data: <b>12.00</b>", await events.ReadLineAsync() ?? "");

                let refused = await site.PostForm("/bids", ["bidder": "ada", "amount": "0"]);
                AssertEqual("<p>Bid something.</p>", await refused.Content.ReadAsStringAsync());
                let forged = await site.PostAsync("/bids", FormUrlEncodedContent(["bidder": "ada", "amount": "5"]));
                AssertEqual(HttpStatusCode.BadRequest, forged.StatusCode);
            }

            async test "an exception in the app fails the test" {
                using let site = await TestClient.Start(() => BuildApp());
                await site.GetStringAsync("/broken");
            }

            async test "a build function must build an app" {
                using let site = await TestClient.Start(() => 42);
            }
            """", "main.piper")],
            new CompilationOptions(LineDirectives: true, IncludeTests: true) { Plugins = Web.Value.Plugins, References = Web.Value.References });
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var run = Runner.RunTests(result.Files, references: result.References);
        Assert.Empty(run.Errors);
        Assert.Equal(
            [("json, a missing body and a refusal", true), ("cookies are kept between requests", true), ("forms send their token, and live pages stream", true),
             ("an exception in the app fails the test", false), ("a build function must build an app", false)],
            run.Results.Select(r => (r.Name, r.Passed)));
        Assert.True(run.Results.Take(3).All(r => r.Passed), string.Join("\n", run.Results.Select(r => $"{r.Name}: {r.Failure}")));
        Assert.StartsWith("Unhandled exception: System.DivideByZeroException", run.Results[3].Failure);
        Assert.StartsWith("Unhandled exception: System.InvalidOperationException: The function given to TestClient.Start built no web app", run.Results[4].Failure);
    }

    /// <summary>
    /// A <c>server</c> builds its app as one function: its services (a block, and inline ones from its parameters),
    /// middleware in order (problems, the app's own, the policies'), endpoints, health checks, and a hook run as it
    /// stops. TestClient serves it as it would a hand-written build function.
    /// </summary>
    [Fact]
    public void A_server_builds_the_app_with_its_middleware_in_order()
    {
        var result = Compilation.Compile([new SourceText(""""
            using System;
            using System.Collections.Generic;
            using System.Net;
            using System.Net.Http;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Http;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Primitives;
            using Piper.Web;

            trait Clock { fn Now() => string; }
            record FixedClock(time: string);
            extend FixedClock: Clock { fn Now() => string = this.time; }
            class Log { public mut lines: List<string> = []; }
            class Store { public mut up = true; }
            class Greeting { fn Text() => string = "hi"; }

            services AppServices {
                transient Greeting;
            }

            policies Security {
                authorize "admin" roles "Admin";
            }

            endpoints Api "/api" {
                get "/time" (clock: Clock) => clock.Now();
                get "/hi" (greeting: Greeting) => greeting.Text();
                get "/admin" authorize "admin" () => "secret";
                get "/broken" () => {
                    let zero = 0;
                    $"{1 / zero}"
                };
            }

            server Shop(clock: Clock, log: Log, store: Store) {
                urls "http://127.0.0.1:0";
                logging off;
                services AppServices;
                singleton Clock = clock;
                singleton Store = store;
                singleton Log = log;
                policies Security;
                configure {
                    builder.Services.AddAuthentication("Cookies").AddCookie("Cookies");
                }
                problems;
                use {
                    app.Use(async (context: HttpContext, next: RequestDelegate) => {
                        context.Response.Headers["X-Shop"] = StringValues("yes");
                        await next(context);
                    });
                }
                endpoints Api;
                health "/healthz" ready "/ready" {
                    check "store" (store: Store) => store.up;
                }
                stopping (log: Log) => log.lines.Add("stopped");
                shutdown 5 seconds;
            }

            fn Main() {}

            async test "the server's parts work together" {
                using let shop = await TestClient.Start(() => Shop(FixedClock("noon"), Log(), Store()));
                let time = await shop.GetAsync("/api/time");
                AssertEqual("noon", await time.Content.ReadAsStringAsync());
                AssertEqual("yes", string.Join(",", time.Headers.GetValues("X-Shop")));
                AssertEqual("hi", await shop.GetStringAsync("/api/hi"));
                AssertEqual(HttpStatusCode.Redirect, (await shop.GetAsync("/api/admin")).StatusCode);

                let broken = await shop.GetAsync("/api/broken");
                AssertEqual(HttpStatusCode.InternalServerError, broken.StatusCode);
                AssertEqual("application/problem+json", broken.Content.Headers.ContentType?.MediaType ?? "");

                AssertEqual("Healthy", await shop.GetStringAsync("/healthz"));
                AssertEqual("Healthy", await shop.GetStringAsync("/ready"));
            }

            async test "readiness fails while a check does, liveness doesn't, and stopping runs its hook" {
                let log = Log();
                let store = Store();
                store.up = false;
                using let shop = await TestClient.Start(() => Shop(FixedClock("noon"), log, store));
                AssertEqual(HttpStatusCode.OK, (await shop.GetAsync("/healthz")).StatusCode);
                AssertEqual(HttpStatusCode.ServiceUnavailable, (await shop.GetAsync("/ready")).StatusCode);
                shop.Dispose();
                AssertEqual(["stopped"], log.lines);
            }
            """", "main.piper")],
            new CompilationOptions(LineDirectives: true, IncludeTests: true) { Plugins = Web.Value.Plugins, References = Web.Value.References });
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var run = Runner.RunTests(result.Files, references: result.References);
        Assert.Empty(run.Errors);
        Assert.True(run.Results.All(r => r.Passed), string.Join("\n", run.Results.Select(r => $"{r.Name}: {r.Failure}")));
        Assert.Equal(2, run.Results.Length);
    }

    [Theory]
    [InlineData("server S { endpoints Api; colour red; }", "colour", "'colour' isn't a server clause")]
    [InlineData("server S { urls \"a\"; urls \"b\"; endpoints Api; }", "urls \"b\"", "'urls' is already given for S")]
    [InlineData("server S { urls \"a\"; }", "S", "S maps no endpoints")]
    [InlineData("server S { endpoints Api, Api; }", "Api;", "'Api' is already mapped by S")]
    [InlineData("server S { endpoints Api; health \"/h\" { check \"a\" () => true; check \"a\" () => true; } }", "\"a\" () => true; }", "There's already a check named \"a\"")]
    [InlineData("server S(port) { endpoints Api; }", "port", "Give 'port' a type")]
    public void Mistakes_in_a_server_are_reported(string server, string at, string message)
    {
        var text = "endpoints Api { get \"/\" () => \"hi\"; }\nfn Main() {}\n" + server;
        var errors = Compile(text).Diagnostics.Where(d => d.Code == "PIP0503").ToList();
        var error = Assert.Single(errors);
        Assert.Contains(message, error.Message);
        Assert.Equal(text.LastIndexOf(at, StringComparison.Ordinal), error.Span.Start);
    }

    private static readonly Lazy<(ImmutableArray<IPiperPlugin> Plugins, ProgramReferences References)> WebAndConfig = new(() =>
    {
        var (plugins, errors) = PluginLoader.Load(PluginsDirectory(),
            ["Piper.Plugins.Config/bin/plugin/Piper.Plugins.Config.dll", "Piper.Plugins.Web/bin/plugin/Piper.Plugins.Web.dll"]);
        Assert.Empty(errors);
        // ASP.NET Core has the configuration packages the config plugin would bring, so nothing is restored.
        var (references, referenceErrors) = ProgramReferences.Resolve(
            Path.GetTempPath(), ImmutableDictionary<string, string>.Empty, [.. plugins.SelectMany(p => p.Frameworks)], [.. plugins.SelectMany(PluginLoader.References)]);
        Assert.Empty(referenceErrors);
        return (plugins, references);
    });

    /// <summary>
    /// <c>config shop: Shop;</c> reads the config as the app is built (a missing value stops it, saying which), registers
    /// it (and as IOptions), and names it for the clauses after.
    /// </summary>
    [Fact]
    public void A_server_reads_its_config_as_it_starts()
    {
        var result = Compilation.Compile([new SourceText(""""
            using System;
            using Microsoft.Extensions.Options;
            using Piper.Web;

            config Shop from "PiperServerConfigTest" {
                name: string;
                url: string = "http://127.0.0.1:0";
            }

            endpoints Api {
                get "/name" (shop: Shop) => shop.name;
                get "/options" (options: IOptions<Shop>) => options.Value.name;
            }

            server S {
                config shop: Shop;
                urls shop.url;
                logging off;
                endpoints Api;
            }

            fn Main() {}

            async test "a config is read, registered and named" {
                Environment.SetEnvironmentVariable("PiperServerConfigTest__Name", "Piper Books");
                using let site = await TestClient.Start(() => S());
                AssertEqual("Piper Books", await site.GetStringAsync("/name"));
                AssertEqual("Piper Books", await site.GetStringAsync("/options"));
            }

            async test "a missing value stops the app as it starts" {
                Environment.SetEnvironmentVariable("PiperServerConfigTest__Name", None);
                mut message = "";
                try {
                    using let site = await TestClient.Start(() => S());
                } catch (e: Exception) {
                    message = e.Message;
                }
                Assert(message.Contains("PiperServerConfigTest:name is missing"), message);
            }
            """", "main.piper")],
            new CompilationOptions(LineDirectives: true, IncludeTests: true) { Plugins = WebAndConfig.Value.Plugins, References = WebAndConfig.Value.References });
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Empty(result.Diagnostics);
        var run = Runner.RunTests(result.Files, references: result.References);
        Assert.Empty(run.Errors);
        Assert.True(run.Results.All(r => r.Passed), string.Join("\n", run.Results.Select(r => $"{r.Name}: {r.Failure}")));
        Assert.Equal(2, run.Results.Length);
    }

    private const string ServerServices = """
        using Microsoft.Extensions.DependencyInjection;
        trait Clock { fn Now() => string; }
        record SystemClock();
        extend SystemClock: Clock { fn Now() => string = "now"; }
        class Db { public let name = "db"; }
        class Store(db: Db) { public let label = db.name; }
        record Settings(name: string);
        endpoints Api {
            get "/now" (clock: Clock) => clock.Now();
            get "/store" (store: Store, body: Settings) => store.label;
        }
        fn Main() {}

        """;

    /// <summary>
    /// What a server's handlers, checks and hooks take must be registered, and a singleton mustn't keep a scoped
    /// service: errors at the parameter (or the server), as DI would only fail at the first request.
    /// </summary>
    [Theory]
    [InlineData("server S { endpoints Api; }",
        "PIP0503", "clock: Clock", "S registers no Clock, but this handler takes it",
        "PIP0503", "store: Store", "S registers no Store, but this handler takes it")]
    [InlineData("server S { singleton Clock => SystemClock; scoped Db; singleton Store; endpoints Api; }",
        "PIP0503", "S {", "Store is a singleton, but takes Db, which is scoped", null, null, null)]
    [InlineData("server S { singleton Clock => SystemClock; singleton Store; endpoints Api; }",
        "PIP0503", "S {", "Store (registered for Store) takes a Db, which S doesn't register.", null, null, null)]
    [InlineData("server S { singleton Clock => SystemClock; scoped Db; scoped Store; endpoints Api; health \"/h\" { check \"c\" (settings: Settings) => true; } }",
        "PIP0503", "settings: Settings", "S registers no Settings, but it's given to this", null, null, null)]
    [InlineData("server S { configure { builder.Services.AddSingleton(typeof(Store)); } endpoints Api; }",
        "PIP0505", "clock: Clock", "S registers no Clock", "PIP0505", "store: Store", "S registers no Store")]
    public void What_a_server_needs_is_checked_against_what_it_registers(
        string server, string code1, string at1, string message1, string? code2, string? at2, string? message2)
    {
        var text = ServerServices + server;
        var reported = Compile(text).Diagnostics.Where(d => d.Message.EndsWith("(web)")).OrderBy(d => d.Span.Start).ToList();
        var expected = new List<(string Code, int At, string Message)> { (code1, text.LastIndexOf(at1, StringComparison.Ordinal), message1) };
        if (code2 is not null)
            expected.Add((code2, text.LastIndexOf(at2!, StringComparison.Ordinal), message2!));
        Assert.True(expected.Count == reported.Count, string.Join("\n", reported.Select(d => d.Format())));
        foreach (var ((code, at, message), diagnostic) in expected.Zip(reported))
        {
            Assert.Equal(code, diagnostic.Code);
            Assert.Equal(at, diagnostic.Span.Start);
            Assert.Contains(message, diagnostic.Message);
        }
    }

    [Fact]
    public void A_server_registering_what_it_needs_reports_nothing()
    {
        var result = Compile(ServerServices + """
            services AppServices { scoped Db; scoped Store; }
            server S {
                singleton Clock => SystemClock;
                singleton Settings = Settings("shop");
                services AppServices;
                endpoints Api;
                health "/h" { check "c" (settings: Settings, store: Store) => settings.name != store.label; }
                stopping (clock: Clock) => clock.Now();
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Endpoints_needing_a_signed_in_user_need_an_authentication_scheme()
    {
        var text = """
            policies Security { authorize "admin" roles "Admin"; }
            endpoints Api { get "/" authorize "admin" () => "hi"; }
            server S { policies Security; endpoints Api; }
            fn Main() {}
            """;
        var error = Assert.Single(Compile(text).Diagnostics);
        Assert.Equal("PIP0503", error.Code);
        Assert.Contains("Api has endpoints needing a signed-in user ('authorize'), but S adds no authentication scheme", error.Message);
        Assert.Equal(text.IndexOf("Api; }", StringComparison.Ordinal), error.Span.Start);
    }

    [Fact]
    public void A_server_mapping_guarded_endpoints_without_their_policies_is_warned_about()
    {
        var text = """
            using Microsoft.Extensions.DependencyInjection;
            policies Security { authorize "admin" roles "Admin"; }
            endpoints Api { get "/" authorize "admin" () => "hi"; }
            server S { configure { builder.Services.AddAuthentication("Cookies").AddCookie("Cookies"); } endpoints Api; }
            fn Main() {}
            """;
        var result = Compile(text);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var warning = Assert.Single(result.Diagnostics);
        Assert.Equal("PIP0505", warning.Code);
        Assert.Contains("Api uses 'authorize', which needs its policies' middleware: add 'policies Name;' to S.", warning.Message);
        Assert.Equal(text.IndexOf("Api; }", StringComparison.Ordinal), warning.Span.Start);
    }

    private const string Types = """
        trait Clock { fn Now() => string; }
        record FixedClock(time: string);
        extend FixedClock: Clock { fn Now() => string = this.time; }
        fn Main() {}
        """;

    /// <summary>The one error <paramref name="text"/> has, which must be the plugin's, at <paramref name="at"/>.</summary>
    private static void AssertPluginError(string text, string at, string message)
    {
        var error = Assert.Single(Compile(text).Diagnostics);
        Assert.Equal("PIP0503", error.Code);
        Assert.Contains(message, error.Message);
        Assert.Equal(text.IndexOf(at, StringComparison.Ordinal), error.Span.Start);
    }

    [Fact]
    public void Services_and_endpoints_become_functions()
    {
        var result = Compile(Types + """

            services Registrations {
                singleton Clock = FixedClock("noon");
                scoped FixedClock = provider => FixedClock("later");
                transient System.Text.StringBuilder;
            }
            endpoints Routes {
                get "/" () => "home";
                post "/clocks/{id:int}" async (id: int, clock: Clock) => {
                    await System.Threading.Tasks.Task.Yield();
                    clock.Now()
                }
                delete "/clocks/{*rest}" (rest: string) => rest;
            }
            fn Setup(services: Microsoft.Extensions.DependencyInjection.IServiceCollection, app: Microsoft.AspNetCore.Routing.IEndpointRouteBuilder) {
                Registrations(services);
                Routes(app);
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Empty(result.Diagnostics);
        Assert.Contains("ServiceCollectionServiceExtensions.AddTransient<global::System.Text.StringBuilder>(services)", result.CSharp);
        Assert.Contains("EndpointRouteBuilderExtensions.MapPost(app, \"/clocks/{id:int}\"", result.CSharp);
    }

    [Fact]
    public void Handlers_keep_their_parameter_names_which_ASP_NET_Core_binds_route_values_to()
    {
        // Each handler is a lambda in the same function; their parameters mustn't be renamed apart (`id_1`), or `{id}`
        // would no longer bind and every request would get 400 Bad Request.
        var result = Compile(Types + """

            endpoints Routes {
                get "/users/{id:int}" (id: int) => id;
                put "/users/{id:int}" (id: int, name: string) => $"{id} {name}";
                delete "/users/{id:int}" (id: int) => id;
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Contains("MapGet(app, \"/users/{id:int}\", (int id) =>", result.CSharp);
        Assert.Contains("MapPut(app, \"/users/{id:int}\", (int id, string name) =>", result.CSharp);
        Assert.Contains("MapDelete(app, \"/users/{id:int}\", (int id) =>", result.CSharp);
    }

    /// <summary>
    /// Piper's types at the boundary, served for real: an optional query value is None when it's missing, a request body
    /// reads and writes Options and enums as JSON, and one a record's invariant rejects is a 400, not a 500.
    /// </summary>
    [Fact]
    public void Options_enums_and_invariants_work_in_requests_and_responses()
    {
        var result = Compile(""""
            using System;
            using System.Linq;
            using System.Net.Http;
            using System.Net.Http.Json;
            using System.Text;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.Logging;

            enum Status { Open, Closed(reason: string) }
            record Order(id: int, status: Status, note: string?) requires id > 0;

            endpoints Api {
                get "/hello" (name: string?, times: int?) => $"{name ?? "nobody"} x{times ?? 1}";
                get "/items/{id:int}" (id: int, verbose: bool?) => $"{id} {verbose ?? false}";
                post "/orders" (order: Order) => order;
            }

            async fn Main() {
                let builder = WebApplication.CreateBuilder();
                builder.Logging.ClearProviders();
                let urls: string[] = ["http://127.0.0.1:0"];
                builder.WebHost.UseUrls(urls);
                let app = builder.Build();
                Api(app);
                await app.StartAsync();
                using let client = HttpClient();
                client.BaseAddress = Uri(app.Urls.First());

                async fn Show(what: string, response: HttpResponseMessage) {
                    Print($"{what} {(int)response.StatusCode} [{await response.Content.ReadAsStringAsync()}]");
                }
                for path in ["/hello", "/hello?name=Ada&times=3", "/hello?times=many", "/items/4", "/items/4?verbose=true"] {
                    await Show(path, await client.GetAsync(path));
                }
                await Show("post", await client.PostAsJsonAsync("/orders", Order(1, Status.Closed("done"), None)));
                await Show("post open", await client.PostAsync("/orders", StringContent("""{"id":2,"status":"Open"}""", Encoding.UTF8, "application/json")));
                await Show("post invalid", await client.PostAsync("/orders", StringContent("""{"id":0,"status":"Open"}""", Encoding.UTF8, "application/json")));
                await app.StopAsync();
            }
            """");
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        Assert.Equal("""
            /hello 200 [nobody x1]
            /hello?name=Ada&times=3 200 [Ada x3]
            /hello?times=many 400 []
            /items/4 200 [4 False]
            /items/4?verbose=true 200 [4 True]
            post 200 [{"id":1,"status":{"kind":"Closed","reason":"done"},"note":null}]
            post open 200 [{"id":2,"status":"Open","note":null}]
            post invalid 400 []

            """, run.Output.Replace("\r\n", "\n"));
    }

    /// <summary>
    /// A handler's Option is 200 or 404, and its Result 200 or ProblemDetails, the error's [Status] choosing the status
    /// (a variant's over its enum's, and 400 without either), awaited or not. Other handlers answer as before.
    /// </summary>
    [Fact]
    public void Options_and_results_are_answered_as_HTTP()
    {
        var result = Compile(""""
            using System;
            using System.Linq;
            using System.Net.Http;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.Logging;
            using Piper.Web;

            record Book(code: string, title: string);

            [Status(409)]
            enum LoanError {
                [Status(404)] UnknownBook(code: string),
                OutOnLoan(code: string, until: string),
                Closed,
            }

            record Invalid(field: string, detail: string);

            fn Find(code: string) => Book? = if code == "dune" { Some(Book("dune", "Dune")) } else { None };

            fn Lend(code: string) => Result<Book, LoanError> =
                match code {
                    "dune" => Ok(Book("dune", "Dune")),
                    "emma" => Err(LoanError.OutOnLoan("emma", "Friday")),
                    "shut" => Err(LoanError.Closed),
                    _ => Err(LoanError.UnknownBook(code)),
                };

            fn Check(n: int) => Result<int, string> = if n > 0 { Ok(n) } else { Err("must be positive") };
            fn Validate() => Result<int, Invalid> = Err(Invalid("name", "is required"));
            fn Maybe(n: int) => Result<int?, string> = if n > 0 { Ok(Some(n)) } else { Ok(None) };

            endpoints Api {
                get "/books/{code}" (code: string) => Find(code);
                get "/later/{code}" async (code: string) => { await Task.Yield(); Find(code) }
                post "/loans/{code}" (code: string) => Lend(code);
                get "/titles/{code}" (code: string) => Find(code)?.title;
                get "/check/{n:int}" (n: int) => Check(n);
                get "/validate" () => Validate();
                get "/maybe/{n:int}" (n: int) => Maybe(n);
                get "/plain" () => "unchanged";
            }

            async fn Main() {
                let builder = WebApplication.CreateBuilder();
                builder.Logging.ClearProviders();
                let urls: string[] = ["http://127.0.0.1:0"];
                builder.WebHost.UseUrls(urls);
                let app = builder.Build();
                Api(app);
                await app.StartAsync();
                using let client = HttpClient();
                client.BaseAddress = Uri(app.Urls.First());

                async fn Show(what: string, response: HttpResponseMessage) {
                    Print($"{what} {(int)response.StatusCode} [{await response.Content.ReadAsStringAsync()}]");
                }
                for path in ["/books/dune", "/books/nope", "/later/dune", "/later/nope", "/titles/dune", "/check/3", "/check/0",
                             "/validate", "/maybe/2", "/maybe/0", "/plain"] {
                    await Show(path, await client.GetAsync(path));
                }
                for code in ["dune", "emma", "shut", "nope"] {
                    await Show($"lend {code}", await client.PostAsync($"/loans/{code}", StringContent("")));
                }
                await app.StopAsync();
            }
            """");
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        Assert.Equal("""
            /books/dune 200 [{"code":"dune","title":"Dune"}]
            /books/nope 404 []
            /later/dune 200 [{"code":"dune","title":"Dune"}]
            /later/nope 404 []
            /titles/dune 200 [Dune]
            /check/3 200 [3]
            /check/0 400 [{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"Bad Request","status":400,"detail":"must be positive"}]
            /validate 400 [{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"Bad Request","status":400,"detail":"is required","field":"name"}]
            /maybe/2 200 [2]
            /maybe/0 404 []
            /plain 200 [unchanged]
            lend dune 200 [{"code":"dune","title":"Dune"}]
            lend emma 409 [{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.10","title":"Conflict","status":409,"kind":"OutOnLoan","code":"emma","until":"Friday"}]
            lend shut 409 [{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.10","title":"Conflict","status":409,"kind":"Closed"}]
            lend nope 404 [{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.5","title":"Not Found","status":404,"kind":"UnknownBook","code":"nope"}]

            """, run.Output.Replace("\r\n", "\n"));
    }

    /// <summary>
    /// An endpoint's OpenAPI tags are its own `tags`, or its group's, or else its declaration's name; `tags` is no
    /// policy, so it needs no `policies` block (which would be warned about for a policy word).
    /// </summary>
    [Fact]
    public void Endpoints_are_tagged_for_OpenAPI()
    {
        var result = Compile(""""
            using System;
            using System.Linq;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.AspNetCore.Http.Metadata;
            using Microsoft.AspNetCore.Mvc.ApiExplorer;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Logging;

            endpoints Library "/books" tags "Books" {
                get "/" () => "all";
                post "/{code}/loans" tags "Loans", "Admin" (code: string) => code;
            }

            endpoints Health {
                get "/health" () => "ok";
            }

            async fn Main() {
                let builder = WebApplication.CreateBuilder();
                builder.Logging.ClearProviders();
                builder.Services.AddEndpointsApiExplorer();
                let urls: string[] = ["http://127.0.0.1:0"];
                builder.WebHost.UseUrls(urls);
                let app = builder.Build();
                Library(app);
                Health(app);
                await app.StartAsync();
                let explorer = app.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>();
                for api in explorer.ApiDescriptionGroups.Items.SelectMany(g => g.Items).OrderBy(a => a.RelativePath) {
                    let tags = api.ActionDescriptor.EndpointMetadata.OfType<ITagsMetadata>().SelectMany(t => t.Tags);
                    Print($"{api.HttpMethod ?? ""} {api.RelativePath ?? ""}: {string.Join(", ", tags)}");
                }
                await app.StopAsync();
            }
            """");
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Empty(result.Diagnostics);
        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        Assert.Equal("""
            GET books/: Books
            POST books/{code}/loans: Loans, Admin
            GET health: Health

            """, run.Output.Replace("\r\n", "\n"));
    }

    /// <summary>
    /// What each endpoint answers, as OpenAPI generators read it (ASP.NET Core's API explorer), follows from its handler's
    /// type as it's answered: an Option is its value or 404, a Result its value or a ProblemDetails for each status its
    /// error's [Status]es give, markup is text/html. Other handlers are described as ASP.NET Core describes them.
    /// </summary>
    [Fact]
    public void What_an_endpoint_answers_is_described_for_OpenAPI()
    {
        var result = Compile(""""
            using System;
            using System.Linq;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Mvc.ApiExplorer;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Logging;
            using Piper.Web;

            record Book(code: string, title: string);

            [Status(409)]
            enum LoanError {
                [Status(404)] UnknownBook(code: string),
                OutOnLoan(code: string, until: string),
                Closed,
            }

            fn Find(code: string) => Book? = None;
            fn Lend(code: string) => Result<Book, LoanError> = Err(LoanError.Closed);
            fn Check(n: int) => Result<int, string> = Ok(n);

            endpoints Api "/api" {
                get "/books/{code}" (code: string) => Find(code);
                get "/later/{code}" async (code: string) => { await Task.Yield(); Find(code) }
                post "/loans/{code}" (code: string) => Lend(code);
                get "/check/{n:int}" (n: int) => Check(n);
                get "/page" () => <p>hi</p>;
                get "/plain" () => 42;
                get "/result" () => Results.Ok(1);
            }

            async fn Main() {
                let builder = WebApplication.CreateBuilder();
                builder.Logging.ClearProviders();
                builder.Services.AddEndpointsApiExplorer();
                let urls: string[] = ["http://127.0.0.1:0"];
                builder.WebHost.UseUrls(urls);
                let app = builder.Build();
                Api(app);
                await app.StartAsync();
                let explorer = app.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>();
                for group in explorer.ApiDescriptionGroups.Items {
                    for api in group.Items.OrderBy(a => a.RelativePath) {
                        let responses = api.SupportedResponseTypes
                            .OrderBy(r => r.StatusCode)
                            .Select(r => $"{r.StatusCode} {r.Type?.Name ?? "-"} {string.Join(",", r.ApiResponseFormats.Select(f => f.MediaType))}".TrimEnd());
                        Print($"{api.HttpMethod ?? ""} {api.RelativePath ?? ""}: {string.Join("; ", responses)}");
                    }
                }
                await app.StopAsync();
            }
            """");
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        Assert.Equal("""
            GET api/books/{code}: 200 Book application/json; 404 Void
            GET api/check/{n:int}: 200 Int32 application/json; 400 ProblemDetails application/problem+json
            GET api/later/{code}: 200 Book application/json; 404 Void
            POST api/loans/{code}: 200 Book application/json; 404 ProblemDetails application/problem+json; 409 ProblemDetails application/problem+json
            GET api/page: 200 String text/html
            GET api/plain: 200 Int32 application/json
            GET api/result: 200 Void

            """, run.Output.Replace("\r\n", "\n"));
    }

    /// <summary>A handler's markup is a page: text/html, alone or in an Option (None is 404) or a Result.</summary>
    [Fact]
    public void Markup_is_answered_as_a_page()
    {
        var result = Compile(""""
            using System;
            using System.Linq;
            using System.Net.Http;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.Logging;

            newtype LotId = int;

            fn Lot(id: LotId) => Html? = if id == LotId(1) { Some(<h1>Clock &amp; case</h1>) } else { None };
            fn Bid(id: LotId, amount: int) => Result<Html, string> =
                if amount > 0 { Ok(<p>Bid {amount} on lot {id}</p>) } else { Err("must be more than 0") };

            endpoints Site {
                get "/" () => <main><a href="/lots/1">{"Lot <1>"}</a></main>;
                get "/lots/{id:int}" (id: LotId) => Lot(id);
                post "/lots/{id:int}/bids" (id: LotId, amount: int) => Bid(id, amount);
            }

            async fn Main() {
                let builder = WebApplication.CreateBuilder();
                builder.Logging.ClearProviders();
                let urls: string[] = ["http://127.0.0.1:0"];
                builder.WebHost.UseUrls(urls);
                let app = builder.Build();
                Site(app);
                await app.StartAsync();
                using let client = HttpClient();
                client.BaseAddress = Uri(app.Urls.First());

                async fn Show(what: string, response: HttpResponseMessage) {
                    let media = response.Content.Headers.ContentType?.MediaType ?? "none";
                    Print($"{what} {(int)response.StatusCode} {media} [{await response.Content.ReadAsStringAsync()}]");
                }
                for path in ["/", "/lots/1", "/lots/2"] {
                    await Show(path, await client.GetAsync(path));
                }
                await Show("bid 5", await client.PostAsync("/lots/1/bids?amount=5", StringContent("")));
                await Show("bid 0", await client.PostAsync("/lots/1/bids?amount=0", StringContent("")));
                await app.StopAsync();
            }
            """");
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        Assert.Equal("""
            / 200 text/html [<main><a href="/lots/1">Lot &lt;1&gt;</a></main>]
            /lots/1 200 text/html [<h1>Clock &amp; case</h1>]
            /lots/2 404 none []
            bid 5 200 text/html [<p>Bid 5 on lot 1</p>]
            bid 0 400 application/problem+json [{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"Bad Request","status":400,"detail":"must be more than 0"}]

            """, run.Output.Replace("\r\n", "\n"));
    }

    /// <summary>
    /// Named endpoints have route functions, typed by their handlers' parameters; a posted form is read into a record,
    /// with errors by field, and its anti-forgery token (put in every post form) is checked.
    /// </summary>
    [Fact]
    public void Routes_are_typed_and_forms_are_read_into_records()
    {
        var result = Compile(""""
            using System;
            using System.Linq;
            using System.Net;
            using System.Net.Http;
            using System.Text;
            using System.Text.RegularExpressions;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Logging;
            using Piper.Web;

            newtype LotId = int;
            enum Category { Art, Clocks }

            record NewLot(title: string, startPrice: decimal, reserve: decimal?, category: Category, featured: bool)
                requires title.Trim().Length > 0 else "Give the lot a title."
                requires (reserve ?? startPrice) >= startPrice else "The reserve can't be below the starting price.";

            fn NewLotPage(form: Form<NewLot>?, errors: FormErrors<NewLot>?) => Html =
                <form for={NewLot} method="post" action={SiteRoutes.Create()}>
                    <input name="title" value={form?.Text("title")} />
                    <span class="error">{errors?.For("title")}</span>
                    <span class="error">{errors?.For("startPrice")}</span>
                    <p class="error">{errors?.Form}</p>
                </form>;

            endpoints Site {
                get Lot "/lots/{id:int}" (id: LotId) => <a href={SiteRoutes.Lot(id)}>lot {id}</a>;
                get Files "/files/{*path}" (path: string) => <p>{path}</p>;
                get Search "/search/{group}" (group: string, q: string?, page: int?, exact: bool, featured: LotId?) => <p>{q ?? ""}</p>;
                get NewLot "/lots/new" () => NewLotPage(None, None);
                post Create "/lots" (form: Form<NewLot>) => match form.Result {
                    Ok(lot) => <p>Listed {lot.title}: {lot.category.Name}, from {lot.startPrice:F2}{if lot.featured { <b> (featured)</b> }}</p>,
                    Err(errors) => NewLotPage(Some(form), Some(errors)),
                };
            }

            async fn Main() {
                Print(SiteRoutes.Lot(LotId(7)));
                Print(SiteRoutes.Files("photos/a b.jpg"));
                Print(SiteRoutes.Create());
                Print(SiteRoutes.Search("art", false));
                Print(SiteRoutes.Search("clocks", true, q: "a&b c", page: 2, featured: LotId(7)));

                let builder = WebApplication.CreateBuilder();
                builder.Logging.ClearProviders();
                builder.Services.AddAntiforgery();
                let urls: string[] = ["http://127.0.0.1:0"];
                builder.WebHost.UseUrls(urls);
                let app = builder.Build();
                Site(app);
                await app.StartAsync();
                let handler = HttpClientHandler() { CookieContainer: CookieContainer() };
                using let client = HttpClient(handler);
                client.BaseAddress = Uri(app.Urls.First());

                let page = await client.GetStringAsync("/lots/new");
                Print(page.Contains("name=\"__RequestVerificationToken\""));
                let token = Uri.EscapeDataString(Regex.Match(page, "__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);

                async fn Post(fields: string) => Task<string> {
                    let body = StringContent(fields, Encoding.UTF8, "application/x-www-form-urlencoded");
                    let response = await client.PostAsync(SiteRoutes.Create(), body);
                    let text = await response.Content.ReadAsStringAsync();
                    $"{(int)response.StatusCode} {Regex.Replace(text, "<input type=\"hidden\"[^>]*>", "")}"
                }
                Print(await Post($"__RequestVerificationToken={token}&title=Clock&startPrice=12.50&category=Clocks&featured=on"));
                Print(await Post($"__RequestVerificationToken={token}&title=&startPrice=lots&category=Art"));
                Print(await Post($"__RequestVerificationToken={token}&title=Vase&startPrice=40&reserve=10&category=Art"));
                Print(await Post($"__RequestVerificationToken={token}&title=+&startPrice=40&category=Art"));
                Print(await Post($"__RequestVerificationToken={token}&title=+&startPrice=40&reserve=10&category=Art"));
                Print((await Post("title=Clock&startPrice=1&category=Art")).Split(' ')[0]);
                Print(await client.GetStringAsync(SiteRoutes.Lot(LotId(3))));
                Print(await client.GetStringAsync(SiteRoutes.Search("clocks", true, q: "a&b c")));
                await app.StopAsync();
            }
            """");
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        Assert.Equal("""
            /lots/7
            /files/photos/a%20b.jpg
            /lots
            /search/art?exact=false
            /search/clocks?exact=true&q=a%26b%20c&page=2&featured=7
            True
            200 <p>Listed Clock: Clocks, from 12.50<b> (featured)</b></p>
            200 <form method="post" action="/lots"><input name="title" value=""><span class="error">Give the lot a title.</span><span class="error">&#39;lots&#39; isn&#39;t a number.</span><p class="error"></p></form>
            200 <form method="post" action="/lots"><input name="title" value="Vase"><span class="error"></span><span class="error"></span><p class="error">The reserve can&#39;t be below the starting price.</p></form>
            200 <form method="post" action="/lots"><input name="title" value=" "><span class="error">Give the lot a title.</span><span class="error"></span><p class="error"></p></form>
            200 <form method="post" action="/lots"><input name="title" value=" "><span class="error">Give the lot a title.</span><span class="error"></span><p class="error">The reserve can&#39;t be below the starting price.</p></form>
            400
            <a href="/lots/3">lot 3</a>
            <p>a&amp;b c</p>

            """, run.Output.Replace("\r\n", "\n"));
    }

    /// <summary>
    /// htmx: the app serves it; a group's layout wraps a page for a browser but not for htmx, which gets the fragment;
    /// HtmxRequest reads what htmx sent and sets its response headers; and an hx-post sends the anti-forgery token as a
    /// header, which Form&lt;T&gt; accepts.
    /// </summary>
    [Fact]
    public void Htmx_gets_fragments_and_browsers_get_pages()
    {
        var result = Compile(""""
            using System;
            using System.Linq;
            using System.Net;
            using System.Net.Http;
            using System.Text.RegularExpressions;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Logging;
            using Piper.Web;

            record NewBid(amount: decimal) requires amount > 0m else "Bid something.";

            fn Page(body: Html) => Html =
                <html><head><Htmx.Script /></head><body>{body}</body></html>;

            endpoints Site layout Page {
                get Lot "/lots/{id:int}" (id: int) => <section id="lot">Lot {id}</section>;
                post Bid "/lots/{id:int}/bids" (id: int, form: Form<NewBid>, htmx: HtmxRequest) => match form.Result {
                    Ok(bid) => {
                        htmx.TriggerEvent("bid-placed");
                        <p>{bid.amount:F2} on lot {id}, from {htmx.TriggerId ?? "somewhere"}</p>
                    },
                    Err(errors) => <p class="error">{errors.For("amount")}</p>,
                };
            }

            async fn Main() {
                let builder = WebApplication.CreateBuilder();
                builder.Logging.ClearProviders();
                builder.Services.AddAntiforgery();
                let urls: string[] = ["http://127.0.0.1:0"];
                builder.WebHost.UseUrls(urls);
                let app = builder.Build();
                Site(app);
                await app.StartAsync();
                let handler = HttpClientHandler() { CookieContainer: CookieContainer() };
                using let client = HttpClient(handler);
                client.BaseAddress = Uri(app.Urls.First());

                let script = await client.GetAsync(Htmx.ScriptPath);
                Print($"script {(int)script.StatusCode} {script.Content.Headers.ContentType?.MediaType ?? ""} {(await script.Content.ReadAsStringAsync()).StartsWith("var htmx=")}");

                // A browser gets the page; htmx, the fragment; a boosted link, the page.
                let page = await client.GetStringAsync(SiteRoutes.Lot(7));
                Print(Regex.Replace(page, "<head>.*</head>", "<head>…</head>"));
                Print(page.Contains("name=\"piper-antiforgery\""));
                async fn Fragment(boosted: bool) => Task<string> {
                    let request = HttpRequestMessage(HttpMethod.Get, SiteRoutes.Lot(7));
                    request.Headers.Add("HX-Request", "true");
                    if boosted { request.Headers.Add("HX-Boosted", "true"); }
                    let response = await client.SendAsync(request);
                    $"{await response.Content.ReadAsStringAsync()} (vary {response.Headers.Vary})"
                }
                Print(await Fragment(false));
                Print((await Fragment(true)).StartsWith("<html>"));

                // An hx-post on a button: no form field for the token, which comes as a header instead.
                let meta = Regex.Match(page, "data-header=\"([^\"]+)\" content=\"([^\"]+)\"");
                async fn Bid(amount: string, token: bool) => Task<string> {
                    let request = HttpRequestMessage(HttpMethod.Post, SiteRoutes.Bid(7));
                    request.Headers.Add("HX-Request", "true");
                    request.Headers.Add("HX-Trigger", "bid-button");
                    if token { request.Headers.Add(meta.Groups[1].Value, WebUtility.HtmlDecode(meta.Groups[2].Value)); }
                    request.Content = StringContent($"amount={amount}", System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");
                    let response = await client.SendAsync(request);
                    let events = if response.Headers.TryGetValues("HX-Trigger", out let values) { string.Join(",", values) } else { "none" };
                    $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()} (events {events})"
                }
                Print(await Bid("12.5", true));
                Print(await Bid("0", true));
                Print((await Bid("12.5", false)).Split(' ')[0]);
                await app.StopAsync();
            }
            """");
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        Assert.Equal("""
            script 200 text/javascript True
            <html><head>…</head><body><section id="lot">Lot 7</section></body></html>
            True
            <section id="lot">Lot 7</section> (vary HX-Request)
            True
            200 <p>12.50 on lot 7, from bid-button</p> (events bid-placed)
            200 <p class="error">Bid something.</p> (events none)
            400

            """, run.Output.Replace("\r\n", "\n"));
    }

    /// <summary>
    /// Live pages: a page watches a topic over server-sent events; publishing a fragment to it (here from a handler
    /// taking a bid) sends it to every watching page, as a named event htmx's SSE extension swaps in.
    /// </summary>
    [Fact]
    public void Published_fragments_reach_the_pages_watching_their_topic()
    {
        var result = Compile(""""
            using System;
            using System.IO;
            using System.Linq;
            using System.Net.Http;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.Extensions.Logging;
            using Piper.Web;

            fn Topic(lot: int) => string = $"lot-{lot}";
            fn Price(amount: decimal) => Html = <span class="price">{amount:F2}
                <small>current</small></span>;

            endpoints Site {
                get Lot "/lots/{id:int}" (id: int) =>
                    <section hx-ext="sse" sse-connect={Live.Url(Topic(id))}>
                        <span sse-swap="price">{Price(10m)}</span>
                    </section>;
                post Bid "/lots/{id:int}/bids" (id: int, amount: decimal) => {
                    let pages = Live.Publish(Topic(id), "price", Price(amount));
                    <p>Bid {amount:F2}, shown on {pages} pages</p>
                };
            }

            async fn Main() {
                let builder = WebApplication.CreateBuilder();
                builder.Logging.ClearProviders();
                let urls: string[] = ["http://127.0.0.1:0"];
                builder.WebHost.UseUrls(urls);
                let app = builder.Build();
                Site(app);
                await app.StartAsync();
                using let client = HttpClient();
                client.BaseAddress = Uri(app.Urls.First());

                Print(await client.GetStringAsync(SiteRoutes.Lot(7)));
                let sse = await client.GetAsync(Live.Url(Topic(7)), HttpCompletionOption.ResponseHeadersRead);
                Print(sse.Content.Headers.ContentType?.MediaType ?? "");
                using let reader = StreamReader(await sse.Content.ReadAsStreamAsync());
                Print(await reader.ReadLineAsync() ?? "");   // ": connected": the page is watching
                await reader.ReadLineAsync();

                let posted = await client.PostAsync(SiteRoutes.Bid(7, 12.5m), StringContent(""));
                Print(await posted.Content.ReadAsStringAsync());
                mut line = await reader.ReadLineAsync() ?? "";
                while line != "" {
                    Print(line);
                    line = await reader.ReadLineAsync() ?? "";
                }
                // Markup over several lines is sent as one event, a data line each.
                Live.Publish(Topic(7), "note", Html.Raw("<b>one\ntwo</b>"));
                line = await reader.ReadLineAsync() ?? "";
                while line != "" {
                    Print(line);
                    line = await reader.ReadLineAsync() ?? "";
                }
                Print(Live.Watching(Topic(7)));
                Print(Live.Publish(Topic(8), "price", Price(1m)));
                sse.Dispose();
                await app.StopAsync();
            }
            """");
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        Assert.Equal("""
            <section hx-ext="sse" sse-connect="/_piper/live?topic=lot-7"><span sse-swap="price"><span class="price">10.00<small>current</small></span></span></section>
            text/event-stream
            : connected
            <p>Bid 12.50, shown on 1 pages</p>
            event: price
            data: <span class="price">12.50<small>current</small></span>
            event: note
            data: <b>one
            data: two</b>
            1
            0

            """, run.Output.Replace("\r\n", "\n"));
    }

    /// <summary><c>&lt;form for={NewLot}&gt;</c>: its inputs' names are checked against the record's fields.</summary>
    [Fact]
    public void A_forms_inputs_are_checked_against_its_record()
    {
        const string text = """
            record NewLot(title: string, startPrice: decimal);

            fn Page() => Html =
                <form for={NewLot} method="post">
                    <label>Title <input name="title" /></label>
                    <div><input name="startprice" /></div>
                    <textarea name="notes"></textarea>
                    <input type="submit" name="go" value="List it" />
                </form>;

            fn NotARecord() => Html = <form for={int}></form>;

            fn Main() {}
            """;
        var errors = Compile(text).Diagnostics.OrderBy(d => d.Span.Start).ToList();
        Assert.Collection(errors,
            e =>
            {
                Assert.Equal("PIP0510", e.Code);
                Assert.Equal("'startprice' isn't a field of NewLot, which this form is read into; its fields are 'title', 'startPrice'.", e.Message);
                Assert.Equal(text.IndexOf("name=\"startprice\"", StringComparison.Ordinal), e.Span.Start);
            },
            e => Assert.Contains("'notes' isn't a field of NewLot", e.Message),
            e => Assert.Equal("A form is for a record, which it's read into, and 'int' isn't one.", e.Message));
    }

    /// <summary>The fields named to a form's errors and text are checked against its record, when they're constants.</summary>
    [Fact]
    public void Field_names_given_to_a_forms_errors_and_text_are_checked()
    {
        const string text = """
            using Piper.Web;

            record NewLot(title: string, startPrice: decimal);

            fn Page(form: Form<NewLot>?, errors: FormErrors<NewLot>?) => Html =
                <form for={NewLot} method="post">
                    <input name="title" value={form?.Text("title")} />
                    <span>{errors?.For("title")}</span>
                    <input name="startPrice" value={form?.Text("price")} />
                    <span>{errors?.For(field: "start")}</span>
                    <span>{errors?.For("start" + "Price")}</span>
                </form>;

            fn Main() {}
            """;
        var errors = Compile(text).Diagnostics.OrderBy(d => d.Span.Start).ToList();
        Assert.Collection(errors,
            e =>
            {
                Assert.Equal("PIP0511", e.Code);
                Assert.Equal("'price' isn't a field of NewLot; its fields are 'title', 'startPrice'.", e.Message);
                Assert.Equal(text.IndexOf("\"price\"", StringComparison.Ordinal), e.Span.Start);
            },
            e => Assert.Equal("'start' isn't a field of NewLot; its fields are 'title', 'startPrice'.", e.Message));
    }

    [Fact]
    public void The_web_plugin_no_longer_adds_services()
    {
        var web = Web.Value.Plugins.Single(p => p.Name == "web");
        Assert.DoesNotContain("services", web.Keywords.Select(k => k.Text));
    }

    [Fact]
    public void A_route_parameter_the_handler_doesnt_take_is_reported()
    {
        AssertPluginError(Types + """

            endpoints Routes {
                get "/users/{id}/posts/{postId:int}" (id: int) => id;
            }
            """, "\"/users/{id}", "The route parameter '{postId}' isn't one of the handler's parameters");
    }

    [Fact]
    public void A_route_mapped_twice_is_reported()
    {
        AssertPluginError(Types + """

            endpoints Routes {
                get "/time" (clock: Clock) => clock.Now();
                post "/time" (clock: Clock) => clock.Now();
                get "/time" () => "again";
            }
            """, "\"/time\" () =>", "'get /time' is already mapped.");
    }

    [Fact]
    public void A_handler_parameter_needs_a_type()
    {
        AssertPluginError(Types + """

            endpoints Routes {
                get "/users/{id}" (id) => id;
            }
            """, "id) =>", "Give 'id' a type");
    }

    [Fact]
    public void An_error_in_a_handler_is_reported_where_it_is()
    {
        var text = Types + """

            endpoints Routes {
                get "/time" (clock: Clock) => clock.Later();
            }
            """;
        var error = Assert.Single(Compile(text).Diagnostics);
        Assert.NotEqual("PIP0503", error.Code);
        Assert.Equal(text.IndexOf("Later", StringComparison.Ordinal), error.Span.Start);
    }

    /// <summary>
    /// Policies on a group and its endpoints, declared by a `policies` block, against a real server signing users in
    /// with a cookie: anonymous endpoints in an authorized group, roles, rate limits (and `off`), CORS and caching.
    /// </summary>
    [Fact]
    public void Policies_authorize_and_limit_endpoints()
    {
        var result = Compile(""""
            using System;
            using System.Linq;
            using System.Net;
            using System.Net.Http;
            using System.Security.Claims;
            using Microsoft.AspNetCore.Authentication;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.AspNetCore.Http;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Logging;

            policies Security {
                authorize "admin" roles "Admin";
                authorize "staff" claim "department" = "IT", "Ops";
                ratelimit "one" fixed 1 per minute;
                ratelimit "burst" sliding 20 per 10 seconds segments 5;
                ratelimit "tokens" bucket 5 refill 1 per second;
                ratelimit "uploads" concurrent 2;
                cors "partners" origins "https://partner.example";
                cache "short" expire 30 seconds;
            }

            endpoints Api "/api" authorize {
                get "/open" anonymous () => "open";
                post "/login/{role}" anonymous async (role: string, context: HttpContext) => {
                    let identity = ClaimsIdentity("Cookies");
                    identity.AddClaim(Claim(ClaimTypes.Role, role));
                    await AuthenticationHttpContextExtensions.SignInAsync(context, "Cookies", ClaimsPrincipal(identity));
                    "signed in"
                }
                get "/me" () => "member";
                get "/admin" authorize "admin" () => "admin";
                get "/limited" anonymous ratelimit "one" () => "limited";
                get "/free" anonymous ratelimit off () => "free";
                get "/report" anonymous cors "partners" cache "short" () => "report";
                get "/staff" authorize "staff" ratelimit "burst" () => "staff";
                get "/tokens" anonymous ratelimit "tokens" () => "tokens";
                get "/upload" anonymous ratelimit "uploads" () => "upload";
            }

            async fn Main() {
                let builder = WebApplication.CreateBuilder();
                builder.Logging.ClearProviders();
                let urls: string[] = ["http://127.0.0.1:0"];
                builder.WebHost.UseUrls(urls);
                Security(builder.Services);
                builder.Services.AddAuthentication("Cookies").AddCookie("Cookies");
                let app = builder.Build();
                UseSecurity(app);
                Api(app);
                await app.StartAsync();

                let handler = HttpClientHandler() { AllowAutoRedirect: false, CookieContainer: CookieContainer() };
                using let client = HttpClient(handler);
                client.BaseAddress = Uri(app.Urls.First());
                fn Show(what: string, response: HttpResponseMessage) {
                    // The cookie scheme answers "sign in first" and "not allowed" with redirects.
                    let location = match response.Headers.Location { Some(l) => $" {l.AbsolutePath}", None => "" };
                    Print($"{what} {(int)response.StatusCode}{location}");
                }
                for path in ["/api/open", "/api/me", "/api/limited", "/api/limited", "/api/free", "/api/free", "/api/report"] {
                    Show(path, await client.GetAsync(path));
                }
                Show("login User", await client.PostAsync("/api/login/User", StringContent("")));
                Show("/api/me", await client.GetAsync("/api/me"));
                Show("/api/admin", await client.GetAsync("/api/admin"));
                Show("login Admin", await client.PostAsync("/api/login/Admin", StringContent("")));
                Show("/api/admin", await client.GetAsync("/api/admin"));
                await app.StopAsync();
            }
            """");
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Empty(result.Diagnostics);
        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        Assert.Equal("""
            /api/open 200
            /api/me 302 /Account/Login
            /api/limited 200
            /api/limited 429
            /api/free 200
            /api/free 200
            /api/report 200
            login User 200
            /api/me 200
            /api/admin 302 /Account/AccessDenied
            login Admin 200
            /api/admin 200

            """, run.Output.Replace("\r\n", "\n"));
    }

    /// <summary>
    /// The one error <paramref name="text"/> has, the plugin's, at <paramref name="at"/>, ignoring the warning that
    /// endpoints with policy words but no <c>policies</c> block get.
    /// </summary>
    private static void AssertPolicyError(string text, string at, string message)
    {
        var error = Assert.Single(Compile(text).Diagnostics, d => d.Code != "PIP0505");
        Assert.Equal("PIP0503", error.Code);
        Assert.Contains(message, error.Message);
        Assert.Equal(text.IndexOf(at, StringComparison.Ordinal), error.Span.Start);
    }

    [Fact]
    public void Contradicting_or_repeated_policy_words_are_reported()
    {
        AssertPolicyError(Types + """

            endpoints Routes {
                get "/a" authorize anonymous () => "a";
            }
            """, "anonymous", "'anonymous' contradicts 'authorize'");
        AssertPolicyError(Types + """

            endpoints Routes "/x" ratelimit "a" ratelimit "b" {
                get "/a" () => "a";
            }
            """, "ratelimit \"b\"", "'ratelimit' is already given here.");
        AssertPolicyError(Types + """

            endpoints Routes {
                get "/a" ratelimit () => "a";
            }
            """, "() => \"a\"", "Expected the rate limit policy's name");
    }

    [Fact]
    public void A_policy_needs_a_unit_for_its_time()
    {
        AssertPluginError(Types + """

            policies Security {
                ratelimit "api" fixed 10 per fortnight;
            }
            endpoints Routes {
                get "/a" ratelimit "api" () => "a";
            }
            """, "fortnight", "Expected the unit");
    }

    [Fact]
    public void Endpoints_and_policies_are_checked_against_each_other()
    {
        var text = Types + """

            policies Security {
                ratelimit "api" fixed 10 per minute;
                authorize "admin" roles "Admin";
            }
            endpoints Routes {
                get "/a" ratelimit "apii" () => "a";
                get "/b" ratelimit "api" () => "b";
            }
            """;
        var result = Compile(text);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Equal(
            [
                ("PIP0505", text.IndexOf("\"admin\"", StringComparison.Ordinal), "No endpoint uses the authorization policy \"admin\". (web)"),
                ("PIP0505", text.IndexOf("\"apii\"", StringComparison.Ordinal), "No 'policies' block declares the rate limit policy \"apii\". (web)"),
            ],
            result.Diagnostics.OrderBy(d => d.Span.Start).Select(d => (d.Code, d.Span.Start, d.Message)));
    }

    [Fact]
    public void Endpoints_with_policies_but_no_policies_block_are_warned_about_once()
    {
        var text = Types + """

            endpoints Routes "/x" authorize {
                get "/a" ratelimit "api" () => "a";
            }
            """;
        var warning = Assert.Single(Compile(text).Diagnostics);
        Assert.Equal("PIP0505", warning.Code);
        Assert.Contains("'authorize' needs the app to register and use its middleware", warning.Message);
    }

    /// <summary>An error's field named `status` would be left out of its ProblemDetails, which has its own: a warning.</summary>
    [Fact]
    public void An_error_field_named_status_is_warned_about()
    {
        var text = """
            using System.Threading.Tasks;

            enum OrderError {
                Stuck(id: int, status: string),
                Missing(id: int),
            }

            fn Find(id: int) => Result<int, OrderError> = Err(OrderError.Missing(id));

            endpoints Orders {
                get "/orders/{id:int}" (id: int) => Find(id);
                get "/later/{id:int}" async (id: int) => { await Task.Yield(); Find(id) }
            }
            fn Main() { }
            """;
        var warning = Assert.Single(Compile(text).Diagnostics);
        Assert.Equal("PIP0505", warning.Code);
        Assert.Contains("its own 'status'", warning.Message);
        Assert.Equal(text.IndexOf("status: string", StringComparison.Ordinal), warning.Span.Start);
    }
}
