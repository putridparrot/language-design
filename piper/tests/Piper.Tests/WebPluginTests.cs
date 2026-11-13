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
