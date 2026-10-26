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
            Path.GetTempPath(), ImmutableDictionary<string, string>.Empty, [.. plugins.SelectMany(p => p.Frameworks)], []);
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
                put "/users/{id:int}" (id: int, name: string) => "{id} {name}";
                delete "/users/{id:int}" (id: int) => id;
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Contains("MapGet(app, \"/users/{id:int}\", (int id) =>", result.CSharp);
        Assert.Contains("MapPut(app, \"/users/{id:int}\", (int id, string name) =>", result.CSharp);
        Assert.Contains("MapDelete(app, \"/users/{id:int}\", (int id) =>", result.CSharp);
    }

    [Fact]
    public void The_web_plugin_no_longer_adds_services()
    {
        var web = Web.Value.Plugins.Single(p => p.Name == "web");
        Assert.Equal(["endpoints"], web.Keywords.Select(k => k.Text));
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
}
