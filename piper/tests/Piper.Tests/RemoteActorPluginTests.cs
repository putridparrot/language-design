using System.Runtime.CompilerServices;
using Piper.Text;

namespace Piper.Tests;

/// <summary>
/// The remote actors plugin (plugins/Piper.Plugins.Actors.Remote): what it checks, and actors talking over gRPC.
/// samples/remote-actors runs too. The plugin and its packages are loaded through that sample's piper.json, as the
/// CLI would load them.
/// </summary>
public class RemoteActorPluginTests
{
    private static readonly Lazy<LoadedProject> Project = new(() =>
    {
        var (project, errors) = LoadedProject.Load(SampleDirectory());
        Assert.True(errors.IsEmpty, string.Join("\n", errors.Select(e => e.Format())));
        return project!;
    });

    private static string SampleDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "samples", "remote-actors"));

    private static CompilationResult Compile(string text) =>
        Compilation.Compile([new SourceText(text, "main.piper")],
            new CompilationOptions { Plugins = Project.Value.Plugins, References = Project.Value.References });

    private const string Node = """
        using System.Threading.Tasks;
        using Proto;
        using Proto.Remote;
        using Proto.Remote.GrpcNet;

        async fn StartNode() => Task<ActorSystem> {
            let system = ActorSystem().WithRemote(RemoteConfig.BindToLocalhost(0));
            await system.Remote().StartAsync();
            system
        }

        """;

    [Fact]
    public void Actors_on_two_nodes_talk_over_the_network()
    {
        var result = Compile(Node + """
            actor Counter(start: int) {
                mut count = start;
                on Increment(by: int) { count += by; }
                on Reset() { count = 0; }
                on Get() => int { count }
                on Fail() => int { throw System.ArgumentException("no") }
            }
            async fn Main() {
                let host = await StartNode();
                let client = await StartNode();
                Counter.Spawn(host, "counter", 10);
                let counter = Counter.Connect(client, host.Address, "counter");
                counter.Increment(5);
                Print(await counter.Get());
                counter.Reset();
                Print(await counter.Get());
                try {
                    await counter.Fail();
                } catch (e: System.InvalidOperationException) {
                    Print($"fault: {e.Message}");
                }
                await counter.Stop();
                await client.Remote().ShutdownAsync(true);
                await host.Remote().ShutdownAsync(true);
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Empty(result.Diagnostics);
        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        Assert.Equal("15\n0\nfault: no\n", run.Output.Replace("\r\n", "\n"));
    }

    [Fact]
    public void A_slow_ask_times_out_and_a_missing_actor_fails_at_once()
    {
        var result = Compile(Node + """
            actor Slow { on Get() => int { await Task.Delay(5000); 1 } }
            async fn Main() {
                let host = await StartNode();
                let client = await StartNode();
                Slow.Spawn(host, "slow");
                let slow = Slow.Connect(client, host.Address, "slow").WithTimeout(System.TimeSpan.FromMilliseconds(300));
                try {
                    await slow.Get();
                } catch (e: System.TimeoutException) {
                    Print("timed out");
                }
                try {
                    await Slow.Connect(client, host.Address, "nobody").Get();
                } catch (e: Proto.DeadLetterException) {
                    Print("no such actor");
                }
                await client.Remote().ShutdownAsync(true);
                await host.Remote().ShutdownAsync(true);
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        Assert.Equal("timed out\nno such actor\n", run.Output.Replace("\r\n", "\n"));
    }

    [Fact]
    public void Spawns_own_parameter_names_are_reserved()
    {
        var text = "actor A(name: string) { on Get() => string { name } }\nfn Main() {}";
        var error = Assert.Single(Compile(text).Diagnostics);
        Assert.Equal("PIP0503", error.Code);
        Assert.Contains("'Spawn' takes 'name' itself", error.Message);
    }

    [Fact]
    public void Messages_that_cant_be_sent_as_data_are_warned_about()
    {
        var text = """
            using System.Collections.Generic;
            trait Shape { fn Area() => double; }
            record Point(x: int, y: int);
            actor Other { on Ping() { } }
            actor Mailbox {
                on Lines(items: List<string>) { }
                on Draw(shape: Shape) { }
                on Forward(to: Other) { }
                on Move(to: Point, at: System.DateTime) { }
            }
            fn Main() {}
            """;
        var result = Compile(text);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.All(result.Diagnostics, d => Assert.Equal("PIP0505", d.Code));
        Assert.Equal(
            [text.IndexOf("items:", StringComparison.Ordinal), text.IndexOf("shape:", StringComparison.Ordinal), text.IndexOf("to: Other", StringComparison.Ordinal)],
            result.Diagnostics.Select(d => d.Span.Start));
    }
}
