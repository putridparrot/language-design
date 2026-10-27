using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Piper.Plugins;
using Piper.Text;

namespace Piper.Tests;

/// <summary>
/// The services plugin (plugins/Piper.Plugins.Services): what `services` becomes and checks, without the web. The plugin
/// is loaded from its build output, as a program's piper.json would load it, and the program is bound against just the
/// package it asks for (restored from nuget.org the first time, then from the local cache). samples/services runs it.
/// </summary>
public class ServicesPluginTests
{
    private static readonly Lazy<(ImmutableArray<IPiperPlugin> Plugins, ProgramReferences References)> Services = new(() =>
    {
        var (plugins, errors) = PluginLoader.Load(PluginsDirectory(), ["Piper.Plugins.Services/bin/plugin/Piper.Plugins.Services.dll"]);
        Assert.Empty(errors);
        var (references, referenceErrors) = ProgramReferences.Resolve(
            Directory.CreateTempSubdirectory("piper-services-").FullName,
            plugins.SelectMany(p => p.Packages).ToImmutableDictionary(), [.. plugins.SelectMany(p => p.Frameworks)], []);
        Assert.Empty(referenceErrors);
        return (plugins, references);
    });

    private static string PluginsDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "plugins"));

    private static CompilationResult Compile(string text) =>
        Compilation.Compile([new SourceText(text, "main.piper")],
            new CompilationOptions { Plugins = Services.Value.Plugins, References = Services.Value.References });

    private const string Types = """
        trait Clock { fn Now() => string; }
        record FixedClock(time: string);
        extend FixedClock: Clock { fn Now() => string = this.time; }
        class Report(clock: Clock) { fn Text() => string = $"at {clock.Now()}"; }
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
    public void It_needs_the_dependency_injection_package_and_no_web_framework()
    {
        var plugin = Assert.Single(Services.Value.Plugins);
        Assert.Empty(plugin.Frameworks);
        Assert.Contains("Microsoft.Extensions.DependencyInjection", plugin.Packages.Keys);
    }

    [Fact]
    public void Services_become_a_function_a_console_program_builds_a_provider_from()
    {
        var result = Compile(Types + """

            services Registrations {
                singleton Clock = FixedClock("noon");
                scoped FixedClock = provider => FixedClock("later");
                transient Report;
            }
            fn Main() {
                let provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(
                    Registrations(Microsoft.Extensions.DependencyInjection.ServiceCollection()));
                let report = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Report>(provider);
                Print(report.Text());
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Empty(result.Diagnostics);
        Assert.Contains("ServiceCollectionServiceExtensions.AddTransient<Report>(services)", result.CSharp);

        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        Assert.Equal("at noon", run.Output.Trim());
    }

    [Fact]
    public void Dependency_injection_takes_traits_as_type_arguments_without_the_plugin_s_syntax()
    {
        var result = Compile(Types + """

            fn Main() {
                let services = Microsoft.Extensions.DependencyInjection.ServiceCollection();
                Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<Clock>(services, FixedClock("noon"));
                Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddTransient<Report>(services);
                let provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
                Print(Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Report>(provider).Text());
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        Assert.Equal("at noon", run.Output.Trim());
    }

    [Fact]
    public void A_service_registered_twice_is_reported()
    {
        AssertPluginError(Types + """

            services Registrations {
                singleton Clock = FixedClock("noon");
                scoped Clock = FixedClock("later");
            }
            fn Main() {}
            """, "Clock = FixedClock(\"later\")", "'Clock' is already registered (as singleton)");
    }

    [Fact]
    public void An_unknown_lifetime_is_reported()
    {
        AssertPluginError(Types + """

            services Registrations {
                forever Clock = FixedClock("noon");
            }
            fn Main() {}
            """, "forever", "Expected 'singleton', 'scoped' or 'transient'.");
    }
}
