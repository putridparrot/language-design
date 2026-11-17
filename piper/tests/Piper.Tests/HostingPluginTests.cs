using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Piper.Plugins;
using Piper.Text;

namespace Piper.Tests;

/// <summary>
/// The hosting plugin (plugins/Piper.Plugins.Hosting): `host` declarations, a program without a web server over .NET's
/// generic host. The plugins are loaded from their build output; the host comes from ASP.NET Core's shared framework,
/// so these tests run offline without restoring the plugin's package.
/// </summary>
public class HostingPluginTests
{
    private static readonly Lazy<(ImmutableArray<IPiperPlugin> Plugins, ProgramReferences References)> Hosting = new(() =>
    {
        var (plugins, errors) = PluginLoader.Load(PluginsDirectory(),
            ["Piper.Plugins.Config/bin/plugin/Piper.Plugins.Config.dll", "Piper.Plugins.Hosting/bin/plugin/Piper.Plugins.Hosting.dll"]);
        Assert.Empty(errors);
        var (references, referenceErrors) = ProgramReferences.Resolve(
            Path.GetTempPath(), ImmutableDictionary<string, string>.Empty, ["Microsoft.AspNetCore.App"], [.. plugins.SelectMany(PluginLoader.References)]);
        Assert.Empty(referenceErrors);
        return (plugins, references);
    });

    private static string PluginsDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "plugins"));

    private static CompilationResult Compile(string text) =>
        Compilation.Compile([new SourceText(text, "main.piper")],
            new CompilationOptions { Plugins = Hosting.Value.Plugins, References = Hosting.Value.References });

    /// <summary>
    /// A host reads its config, registers its services, runs a worker for its life (its token cancelled as it stops,
    /// and waited for) and one each period, and runs its stopping hook before its workers stop.
    /// </summary>
    [Fact]
    public void A_host_runs_its_workers_and_stops_them()
    {
        var result = Compilation.Compile([new SourceText(""""
            using System;
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;

            config Settings from "PiperHostTest" {
                name: string = "grid";
            }

            class Log { public mut lines: List<string> = []; }
            class Ticks { public mut count = 0; }

            host Worker(log: Log) {
                config settings: Settings;
                logging off;
                singleton Log = log;
                singleton Ticks;
                worker async (log: Log, settings: Settings, stopping: CancellationToken) => {
                    log.lines.Add($"{settings.name} working");
                    try {
                        await Task.Delay(Timeout.Infinite, stopping);
                    } catch (_: OperationCanceledException) {
                        log.lines.Add("finished");
                    }
                };
                worker every 10 milliseconds (ticks: Ticks) => {
                    ticks.count += 1;
                };
                stopping (log: Log) => log.lines.Add("stopping");
                shutdown 5 seconds;
            }

            fn Main() {}

            async test "a host runs its workers, and stops them" {
                let log = Log();
                using let host = Worker(log);
                await host.StartAsync();
                let ticks = host.Services.GetRequiredService<Ticks>();
                for attempt in 1..200 {
                    if ticks.count >= 3 { break; }
                    await Task.Delay(10);
                }
                Assert(ticks.count >= 3, $"ticked {ticks.count} times");
                await host.StopAsync();
                AssertEqual(["grid working", "stopping", "finished"], log.lines);
            }
            """", "main.piper")],
            new CompilationOptions(LineDirectives: true, IncludeTests: true) { Plugins = Hosting.Value.Plugins, References = Hosting.Value.References });
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Empty(result.Diagnostics);
        var run = Runner.RunTests(result.Files, references: result.References);
        Assert.Empty(run.Errors);
        Assert.True(run.Results.All(r => r.Passed), string.Join("\n", run.Results.Select(r => $"{r.Name}: {r.Failure}")));
        Assert.Single(run.Results);
    }

    [Theory]
    [InlineData("host H { worker () => {}; urls \"x\"; }", "urls", "'urls' isn't a host clause")]
    [InlineData("host H { shutdown 1 second; shutdown 2 seconds; }", "shutdown 2", "'shutdown' is already given for H")]
    [InlineData("host H { worker (store) => {}; }", "store", "Give 'store' a type")]
    [InlineData("host H(port) { }", "port", "Give 'port' a type")]
    [InlineData("host H { worker every 2 fortnights () => {}; }", "fortnights", "Expected the unit")]
    public void Mistakes_in_a_host_are_reported(string host, string at, string message)
    {
        var text = "fn Main() {}\n" + host;
        var error = Assert.Single(Compile(text).Diagnostics, d => d.Message.Contains(message));
        Assert.Equal("PIP0503", error.Code);
        Assert.Equal(text.LastIndexOf(at, StringComparison.Ordinal), error.Span.Start);
    }
}
