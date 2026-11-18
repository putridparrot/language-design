using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Piper.Plugins;
using Piper.Text;

namespace Piper.Tests;

/// <summary>
/// The messaging clauses a `host` (and a `server`) has: `bus from "Messaging";` (the transport the configuration chooses,
/// and a bus, both registered), `consume Consumer(…);` and `relay store;`, each run for the host's life and stopped
/// with it; and what's reported when they're misused.
/// </summary>
public class MessagingHostTests
{
    private static readonly Lazy<(ImmutableArray<IPiperPlugin> Plugins, ProgramReferences References)> Plugins = new(() =>
    {
        var (plugins, errors) = PluginLoader.Load(PluginsDirectory(),
            ["Piper.Plugins.Config/bin/plugin/Piper.Plugins.Config.dll", "Piper.Plugins.Hosting/bin/plugin/Piper.Plugins.Hosting.dll",
             "Piper.Plugins.Messaging/bin/plugin/Piper.Plugins.Messaging.dll"]);
        Assert.Empty(errors);
        var (references, referenceErrors) = ProgramReferences.Resolve(
            Path.GetTempPath(), ImmutableDictionary<string, string>.Empty, ["Microsoft.AspNetCore.App"], [.. plugins.SelectMany(PluginLoader.References)]);
        Assert.Empty(referenceErrors);
        return (plugins, references);
    });

    private static string PluginsDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "plugins"));

    [Fact]
    public void A_host_consumes_and_relays_on_its_bus_for_its_life()
    {
        var result = Compilation.Compile([new SourceText(""""
            using System;
            using System.Collections.Concurrent;
            using System.Linq;
            using System.Threading.Tasks;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;
            using Piper.Messaging;
            using Piper.Runtime;

            messages Pings {
                command Ping(n: int) queue "pings";
            }

            class Log { public let lines = ConcurrentQueue<string>(); }

            consumer Echo(log: Log) {
                on Ping(ping) {
                    log.lines.Enqueue($"ping {ping.n}");
                    Handled.Done
                }
            }

            host Pinger(log: Log, outbox: MemoryOutbox) {
                logging off;
                bus from "Messaging";            // nothing configured: the in-memory transport
                consume Echo(log);
                relay outbox;
            }

            fn Main() {}

            async test "a host consumes and relays on its bus, and stops them with it" {
                let log = Log();
                let outbox = MemoryOutbox();
                using let host = Pinger(log, outbox);
                await host.StartAsync();
                let bus = host.Services.GetRequiredService<Bus>();
                Assert(host.Services.GetRequiredService<IMessageTransport>() is _: MemoryTransport);
                await bus.Send(Ping(1));                     // straight to the transport
                await bus.In(outbox).Send(Ping(2));           // through the outbox, sent by the relay
                for attempt in 1..500 {
                    if log.lines.Count >= 2 { break; }
                    await Task.Delay(10);
                }
                await host.StopAsync();
                AssertEqual(["ping 1", "ping 2"], [...log.lines.Order()]);
                AssertEqual(0, outbox.Waiting.Count);
            }
            """", "main.piper")],
            new CompilationOptions(LineDirectives: true, IncludeTests: true) { Plugins = Plugins.Value.Plugins, References = Plugins.Value.References });
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var run = Runner.RunTests(result.Files, references: result.References);
        Assert.Empty(run.Errors);
        Assert.True(run.Results.All(r => r.Passed), string.Join("\n", run.Results.Select(r => $"{r.Name}: {r.Failure}")));
        Assert.Single(run.Results);
    }

    [Theory]
    [InlineData("host H { consume Echo(); }", "consume", "'consume' needs a bus to consume from")]
    [InlineData("host H { relay Piper.Runtime.MemoryOutbox(); }", "relay", "'relay' needs a bus to send on")]
    [InlineData("host H { bus from \"Messaging\"; bus from \"Other\"; }", "bus from \"Other\"", "'bus' is already given for H")]
    public void Mistakes_with_a_hosts_bus_are_reported(string host, string at, string message)
    {
        var text = "fn Main() {}\n" + host;
        var result = Compilation.Compile([new SourceText(text, "main.piper")],
            new CompilationOptions { Plugins = Plugins.Value.Plugins, References = Plugins.Value.References });
        var error = Assert.Single(result.Diagnostics, d => d.Message.Contains(message));
        Assert.Equal(text.LastIndexOf(at, StringComparison.Ordinal), error.Span.Start);
    }
}
