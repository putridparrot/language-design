using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Piper.Plugins;
using Piper.Text;

namespace Piper.Tests;

/// <summary>
/// A host's (and a server's) `workflows Fulfil(shop) from store;`: an engine over the store, registered, running those
/// workflows for the host's life, resuming the unfinished as it starts, so a workflow one host was running when it
/// stopped is finished by the next.
/// </summary>
public class WorkflowHostTests
{
    private static readonly Lazy<(ImmutableArray<IPiperPlugin> Plugins, ProgramReferences References)> Plugins = new(() =>
    {
        var (plugins, errors) = PluginLoader.Load(PluginsDirectory(),
            ["Piper.Plugins.Config/bin/plugin/Piper.Plugins.Config.dll", "Piper.Plugins.Hosting/bin/plugin/Piper.Plugins.Hosting.dll",
             "Piper.Plugins.Workflows/bin/plugin/Piper.Plugins.Workflows.dll"]);
        Assert.Empty(errors);
        var (references, referenceErrors) = ProgramReferences.Resolve(
            Path.GetTempPath(), ImmutableDictionary<string, string>.Empty, ["Microsoft.AspNetCore.App"], [.. plugins.SelectMany(PluginLoader.References)]);
        Assert.Empty(referenceErrors);
        return (plugins, references);
    });

    private static string PluginsDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "plugins"));

    [Fact]
    public void A_hosts_workflows_resume_when_the_next_host_starts()
    {
        var result = Compilation.Compile([new SourceText(""""
            using System;
            using System.Threading.Tasks;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;
            using Piper.Runtime;
            using Piper.Workflows;

            record Order(id: string);

            class Shop {
                public mut charged = 0;
                public mut shipped = 0;
                fn Charge(id: string) => string { charged += 1; $"pay-{id}" }
                fn Ship(id: string) => bool { shipped += 1; true }
            }

            workflow Fulfil(shop: Shop) {
                start (order: Order) => string {
                    activity payment = shop.Charge(order.id);
                    timer TimeSpan.FromSeconds(2);
                    activity shipped = shop.Ship(order.id);
                    $"{payment}, shipped {shipped}"
                }
            }

            host Orders(store: IEventStore, shop: Shop) {
                logging off;
                workflows Fulfil(shop) from store;
            }

            fn Main() {}

            async test "a workflow a host was running when it stopped is finished by the next" {
                let store = MemoryEventStore();
                let shop = Shop();
                {
                    using let first = Orders(store, shop);
                    await first.StartAsync();
                    let engine = first.Services.GetRequiredService<WorkflowEngine>();
                    await Fulfil.Start(engine, "o-1", Order("o-1"));
                    for attempt in 1..500 {                      // until it has charged, and is waiting on its timer
                        if shop.charged == 1 { break; }
                        await Task.Delay(10);
                    }
                    await first.StopAsync();                 // mid-timer
                }
                AssertEqual(1, shop.charged);
                AssertEqual(0, shop.shipped);

                using let second = Orders(store, shop);
                await second.StartAsync();                  // resumes it
                let outcome = await Fulfil.Outcome(second.Services.GetRequiredService<WorkflowEngine>(), "o-1");
                await second.StopAsync();
                AssertEqual("pay-o-1, shipped True", outcome);
                AssertEqual(1, shop.charged);
                AssertEqual(1, shop.shipped);
            }
            """", "main.piper")],
            new CompilationOptions(LineDirectives: true, IncludeTests: true) { Plugins = Plugins.Value.Plugins, References = Plugins.Value.References });
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var run = Runner.RunTests(result.Files, references: result.References);
        Assert.Empty(run.Errors);
        Assert.True(run.Results.All(r => r.Passed), string.Join("\n", run.Results.Select(r => $"{r.Name}: {r.Failure}")));
        Assert.Single(run.Results);
    }

    [Fact]
    public void A_workflows_clause_given_twice_is_reported()
    {
        var text = "fn Main() {}\nhost H(store: Piper.Runtime.IEventStore) { workflows A() from store; workflows B() from store; }";
        var result = Compilation.Compile([new SourceText(text, "main.piper")],
            new CompilationOptions { Plugins = Plugins.Value.Plugins, References = Plugins.Value.References });
        var error = Assert.Single(result.Diagnostics, d => d.Message.Contains("'workflows' is already given for H"));
        Assert.Equal(text.LastIndexOf("workflows", StringComparison.Ordinal), error.Span.Start);
    }
}
