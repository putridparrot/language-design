using System.Runtime.CompilerServices;
using Piper.Text;

namespace Piper.Tests;

/// <summary>
/// The remote actors plugin (plugins/Piper.Plugins.Actors.Remote): what it checks, and actors talking over the network.
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
        using Piper.Actors.Remote;

        async fn StartNode() => Task<ActorSystem> {
            let system = await PiperNode.StartAsync("127.0.0.1", 0);
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
                await client.StopAsync();
                await host.StopAsync();
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Empty(result.Diagnostics);
        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        Assert.Equal("15\n0\nfault: no\n", run.Output.Replace("\r\n", "\n"));
    }

    /// <summary>
    /// A cluster in one process (Piper.Actors.Remote's PiperCluster), with faults injected: a node cut off, two split
    /// from each other, messages dropped one way, a slow node, a stopped one, one added; and healed again. Each ask
    /// goes from one node to another's counter, in memory or over HTTP/2 on loopback ports; one that can't get through
    /// times out.
    /// </summary>
    [Theory]
    [InlineData("StartAsync")]
    [InlineData("StartOverNetworkAsync")]
    public void A_cluster_in_one_process_takes_injected_faults(string start)
    {
        var result = Compile($$"""
            using System;
            using System.Collections.Generic;
            using System.Linq;
            using System.Threading.Tasks;
            using Piper.Actors.Remote;

            actor Counter(node: int) {
                mut count = 0;
                on Add() => string { count += 1; $"node {node}: {count}" }
            }

            /// Asks node `to`'s counter from node `from`, within a second: long enough for a busy machine (a CI runner's) to
            /// answer one that gets through.
            async fn Ask(cluster: PiperCluster, from: int, to: int) => Task<string> {
                try {
                    return await Counter.Connect(cluster[from], cluster.Addresses[to], "counter").WithTimeout(TimeSpan.FromSeconds(1)).Add();
                } catch (_: TimeoutException) {
                    return $"node {to}: timed out";
                }
            }

            async fn Line(cluster: PiperCluster, asks: List<(int, int)>) => Task<string> {
                mut answers: List<string> = [];
                for (from, to) in asks {
                    answers.Add($"{from}->{await Ask(cluster, from, to)}");
                }
                string.Join(", ", answers)
            }

            async fn Main() {
                mut started = 0;
                // The faults are on plain asks, not the cluster's membership: a node isolated longer than the cluster's
                // one second (several timeouts, on a slow machine) would be found down, failing asks to it after it's
                // healed. So the cluster here finds no node down for a minute.
                let cluster = await PiperCluster.{{start}}(3, node => {
                    Counter.Spawn(node, "counter", started);
                    started += 1;
                }, ClusterOptions(TimeSpan.FromMilliseconds(100), TimeSpan.FromMinutes(1)));
                Print(await Line(cluster, [(0, 1), (1, 2), (2, 0)]));
                cluster.Isolate(1);
                Print(await Line(cluster, [(0, 1), (2, 1), (1, 0), (1, 1), (0, 2)]));
                cluster.Heal();
                cluster.Partition(0, 2);
                Print(await Line(cluster, [(0, 2), (2, 0), (1, 2)]));
                cluster.Heal();
                cluster.Drop(0, 1);
                Print(await Line(cluster, [(0, 1), (1, 0)]));
                cluster.Heal();
                cluster.Slow(2, TimeSpan.FromSeconds(2));       // longer than an ask waits
                Print(await Line(cluster, [(0, 2)]));
                cluster.Heal();
                await cluster.StopAsync(2);
                let added = await cluster.AddNodeAsync();
                Print(await Line(cluster, [(0, 2), (0, 3)]));
                Print(cluster.Nodes.Count);
                await cluster.StopAsync();
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        Assert.Equal(
            "0->node 1: 1, 1->node 2: 1, 2->node 0: 1\n" +
            "0->node 1: timed out, 2->node 1: timed out, 1->node 0: timed out, 1->node 1: 2, 0->node 2: 2\n" +
            "0->node 2: timed out, 2->node 0: timed out, 1->node 2: 3\n" +
            "0->node 1: timed out, 1->node 0: 2\n" +
            "0->node 2: timed out\n" +
            "0->node 2: timed out, 0->node 3: 1\n" +
            "4\n",
            run.Output.Replace("\r\n", "\n"));
    }

    /// <summary>A remote actor's own functions, shared by its handlers, as a local actor's are.</summary>
    [Fact]
    public void A_remote_actor_can_have_functions_its_handlers_share()
    {
        var result = Compile(Node + """
            actor Ledger(limit: int) {
                mut balance = 0;
                fn Fits(amount: int) => bool = balance + amount <= limit;
                fn Move(amount: int) { balance += amount; }
                on Deposit(amount: int) => bool { if Fits(amount) { Move(amount); true } else { false } }
                on Balance() => int { balance }
            }
            async fn Main() {
                let host = await StartNode();
                let ledger = Ledger.Spawn(host, "ledger", 100);
                Print(await ledger.Deposit(70));
                Print(await ledger.Deposit(50));
                Print(await ledger.Balance());
                await ledger.Stop();
                await host.StopAsync();
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Empty(result.Diagnostics);
        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        Assert.Equal("True\nFalse\n70\n", run.Output.Replace("\r\n", "\n"));
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
                } catch (e: DeadLetterException) {
                    Print("no such actor");
                }
                await client.StopAsync();
                await host.StopAsync();
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

    /// <summary>
    /// One request is one trace across nodes: the caller's trace context goes in each message's headers, and each
    /// handler runs in a span continuing it (kind Server for an ask, Consumer for a tell), so the client's request,
    /// Front on node A and Back on node B share a trace, each the parent of the next. Each handler is timed too.
    /// </summary>
    [Fact]
    public void A_request_through_actors_on_several_nodes_is_one_trace()
    {
        var spans = new System.Collections.Concurrent.ConcurrentQueue<System.Diagnostics.Activity>();
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name is "Piper.Actors" or "TraceTest",
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        var timed = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var meters = new System.Diagnostics.Metrics.MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == "Piper.Actors")
                    l.EnableMeasurementEvents(instrument);
            },
        };
        meters.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            timed.Enqueue($"{instrument.Name} {tags.ToArray().FirstOrDefault(t => t.Key == "actor.name").Value}"));
        meters.Start();

        var result = Compile(Node + """
            fn Trace() => string = match System.Diagnostics.Activity.Current {
                Some(span) => $"{span.TraceId} {span.ParentSpanId} {span.SpanId}",
                None => "none",
            };
            actor Back {
                on Where() => string { Trace() }
                on Note(text: string) { }
            }
            actor Front(back: Back) {
                on Where() => string { back.Note("seen"); $"{Trace()}|{await back.Where()}" }
            }
            async fn Main() {
                let a = await StartNode();
                let b = await StartNode();
                let client = await StartNode();
                Back.Spawn(b, "back");
                Front.Spawn(a, "front", Back.Connect(a, b.Address, "back"));
                let front = Front.Connect(client, a.Address, "front");
                using let root = System.Diagnostics.ActivitySource("TraceTest").StartActivity("request");
                Print($"{Trace()}|{await front.Where()}");
                // Within a trace an ask is sent with its headers, and still fails at once for an actor that isn't there.
                try {
                    await Back.Connect(client, b.Address, "nobody").Where();
                } catch (e: DeadLetterException) {
                    Print("no such actor");
                }
                // Within a trace an ask is sent with its headers, and still fails at once for an actor that isn't there.
                try {
                    await Back.Connect(client, b.Address, "nobody").Where();
                } catch (e: DeadLetterException) {
                    Print("no such actor");
                }
                await System.Threading.Tasks.Task.Delay(200);
                await client.StopAsync();
                await a.StopAsync();
                await b.StopAsync();
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors));

        // "trace parent span" for the client, Front and Back, in that order.
        var lines = run.Output.Replace("\r\n", "\n").Trim().Split('\n');
        Assert.Equal("no such actor", lines[1]);
        var hops = lines[0].Split('|').Select(hop => hop.Split(' ')).ToArray();
        Assert.Equal(3, hops.Length);
        Assert.All(hops, hop => Assert.Equal(hops[0][0], hop[0]));
        Assert.Equal(hops[0][2], hops[1][1]);
        Assert.Equal(hops[1][2], hops[2][1]);

        var ours = spans.Where(s => s.TraceId.ToHexString() == hops[0][0] && s.Source.Name == "Piper.Actors").ToList();
        Assert.Contains(ours, s => s.DisplayName == "Front.Where" && s.Kind == System.Diagnostics.ActivityKind.Server && (string?)s.GetTagItem("actor.name") == "Front");
        Assert.Contains(ours, s => s.DisplayName == "Back.Where" && s.Kind == System.Diagnostics.ActivityKind.Server);
        Assert.Contains(ours, s => s.DisplayName == "Back.Note" && s.Kind == System.Diagnostics.ActivityKind.Consumer);
        Assert.Contains("piper.actor.handler.duration Front", timed);
        Assert.Contains("piper.actor.handler.duration Back", timed);
    }

    /// <summary>
    /// Failure policies across nodes: a remote actor with `rollback` changes nothing when a handler throws (its caller
    /// still gets the error); one with `stop` is stopped, so a later ask fails at once with a dead letter.
    /// </summary>
    [Fact]
    public void A_remote_actors_failure_policy_applies_across_nodes()
    {
        var result = Compile(Node + """
            actor Shelf {
                mut count = 0;
                on failure rollback;
                on Take(n: int) => int {
                    count -= n;
                    if count < 0 { throw System.InvalidOperationException("not enough"); }
                    count
                }
                on Put(n: int) => int { count += n; count }
            }
            actor Fuse {
                on failure stop;
                on Check(ok: bool) => string { if !ok { throw System.InvalidOperationException("blown"); } "fine" }
            }
            async fn Main() {
                let host = await StartNode();
                let client = await StartNode();
                Shelf.Spawn(host, "shelf");
                Fuse.Spawn(host, "fuse");
                let shelf = Shelf.Connect(client, host.Address, "shelf");
                Print(await shelf.Put(3));
                try { await shelf.Take(5); } catch (e: System.InvalidOperationException) { Print(e.Message); }
                Print(await shelf.Take(1));

                let fuse = Fuse.Connect(client, host.Address, "fuse");
                Print(await fuse.Check(true));
                try { await fuse.Check(false); } catch (e: System.InvalidOperationException) { Print(e.Message); }
                await Task.Delay(100);
                try { await fuse.Check(true); } catch (e: DeadLetterException) { Print("stopped"); }
                await client.StopAsync();
                await host.StopAsync();
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Empty(result.Diagnostics);
        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        Assert.Equal("3\nnot enough\n2\nfine\nblown\nstopped\n", run.Output.Replace("\r\n", "\n"));
    }

    /// <summary>
    /// A persisted remote actor keeps its state through its events: stopped on one node and spawned again on another
    /// (as a pod restarting does), it replays them as it starts and carries on where it was. A handler that
    /// throws saves none of its events.
    /// </summary>
    [Fact]
    public void A_persisted_remote_actor_comes_back_with_its_state()
    {
        var result = Compile(Node + """
            actor Tally(id: string) persisted by id {
                mut total = 0;
                event Added(amount: int) { total += amount; }
                on Add(amount: int) => int {
                    emit Added(amount);
                    if amount < 0 { throw System.InvalidOperationException("negative"); }
                    total
                }
                on Total() => int { total }
            }

            async fn Main() {
                let store = Piper.Runtime.MemoryEventStore();
                let first = await StartNode();
                let second = await StartNode();
                let client = await StartNode();

                let here = Tally.Spawn(first, "tally", store, "visits");
                let tally = Tally.Connect(client, first.Address, "tally");
                Print(await tally.Add(3));
                Print(await tally.Add(4));
                try { await tally.Add(-100); } catch (e: System.InvalidOperationException) { Print(e.Message); }
                await here.Stop();

                Tally.Spawn(second, "tally", store, "visits");
                Print(await Tally.Connect(client, second.Address, "tally").Total());
                await client.StopAsync();
                await first.StopAsync();
                await second.StopAsync();
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Empty(result.Diagnostics);
        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        Assert.Equal("3\n7\nnegative\n7\n", run.Output.Replace("\r\n", "\n"));
    }

    /// <summary>
    /// The proto-actor plugin's transport: the same actors, with Proto.Actor's remoting carrying their messages. Asks,
    /// faults and dead letters behave as on the built-in transport, and the plugin registers <c>proto</c> beside
    /// <c>http</c> and <c>memory</c>, so configuration can choose it.
    /// </summary>
    [Fact]
    public void Actors_can_talk_over_proto_actors_remoting_instead()
    {
        var folder = Directory.CreateTempSubdirectory("piper-proto-actor").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, PiperProject.FileName), """{ "plugins": ["remote-actors", "proto-actor"] }""");
            var (project, errors) = LoadedProject.Load(folder);
            Assert.True(errors.IsEmpty, string.Join("\n", errors.Select(e => e.Format())));
            var result = Compilation.Compile([new SourceText("""
                using System.Threading.Tasks;
                using Piper.Actors.Remote;
                using Piper.Actors.Proto;
                using Piper.Runtime;

                actor Counter(start: int) {
                    mut count = start;
                    on Increment(by: int) { count += by; }
                    on Get() => int { count }
                    on Fail() => Result<int, string> { throw System.ArgumentException("no") }
                }
                async fn Main() {
                    let host = await PiperNode.StartAsync(ProtoActorTransport("127.0.0.1", 0));
                    let client = await PiperNode.StartAsync(ProtoActorTransport("127.0.0.1", 0));
                    Counter.Spawn(host, "counter", 10);
                    let counter = Counter.Connect(client, host.Address, "counter");
                    counter.Increment(5);
                    Print(await counter.Get());
                    try { await counter.Fail(); } catch (e: System.InvalidOperationException) { Print($"fault: {e.Message}"); }
                    try { await Counter.Connect(client, host.Address, "nobody").Get(); } catch (e: DeadLetterException) { Print("no such actor"); }
                    Print(string.Join(", ", PiperProviders.Names<IActorTransport>()));
                    await client.StopAsync();
                    await host.StopAsync();
                }
                """, "main.piper")], new CompilationOptions { Plugins = project!.Plugins, References = project.References });
            Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
            var run = Runner.Run(result.Files, references: result.References);
            Assert.True(run.Success, string.Join("\n", run.Errors));
            Assert.Equal("15\nfault: no\nno such actor\nhttp, memory, proto\n", run.Output.Replace("\r\n", "\n"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// Replies of every kind cross the network as their declared types: Option, Result (both ways), enums, records with
    /// immutable collections, and tuples.
    /// </summary>
    [Fact]
    public void Answers_of_every_kind_cross_the_network()
    {
        var result = Compile(Node + """
            record Line(sku: string, quantity: int);
            enum Refusal { TooMany(asked: int), Unknown }
            actor Shop {
                on Find(sku: string) => string? { if sku == "tea" { Some("Earl Grey") } else { None } }
                on Reserve(quantity: int) => Result<Line, Refusal> { if quantity > 3 { Err(Refusal.TooMany(quantity)) } else { Ok(Line("tea", quantity)) } }
                on Lines() => System.Collections.Immutable.ImmutableList<Line> { System.Collections.Immutable.ImmutableList.Create(Line("tea", 1), Line("cake", 2)) }
                on Pair() => (string, int) { ("tea", 2) }
                on Status(open: bool) => Refusal { if open { Refusal.Unknown } else { Refusal.TooMany(0) } }
            }
            async fn Main() {
                let host = await StartNode();
                let client = await StartNode();
                Shop.Spawn(host, "shop");
                let shop = Shop.Connect(client, host.Address, "shop");
                Print(await shop.Find("tea"));
                Print(await shop.Find("cheese"));
                Print(await shop.Reserve(2));
                Print(await shop.Reserve(9));
                Print(string.Join(", ", await shop.Lines()));
                Print(await shop.Pair());
                Print(await shop.Status(true));
                Print(await shop.Status(false));
                await client.StopAsync();
                await host.StopAsync();
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        Assert.Equal(
            "Some(Earl Grey)\nNone\nOk(Line { sku = tea, quantity = 2 })\nErr(TooMany { asked = 9 })\n" +
            "Line { sku = tea, quantity = 1 }, Line { sku = cake, quantity = 2 }\n(tea, 2)\nUnknown\nTooMany { asked = 0 }\n",
            run.Output.Replace("\r\n", "\n"));
    }

    private const string Tallies = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Threading.Tasks;
        using Piper.Actors.Remote;
        using Piper.Runtime;

        actor Tally(id: string) persisted by id {
            mut total = 0;
            event Added(amount: int) { total += amount; }
            on Add(amount: int) => int { emit Added(amount); total }
        }

        /// The nodes running `key`'s tally.
        fn Where(cluster: PiperCluster, key: string) => List<int> =
            [...Enumerable.Range(0, cluster.Nodes.Count).Where(i => cluster[i].Cluster.Hosted("Tally").Contains(key))];

        /// A cluster of three, sharing one store of events, each hosting tallies.
        async fn Start(store: IEventStore) => Task<PiperCluster> =
            await PiperCluster.StartAsync(3, node => {
                Tally.HostInCluster(node.Cluster, (n, name, key) => Tally.Spawn(n, name, store, key));
            });

        """;

    /// <summary>
    /// Actors in a cluster: asked from any node, each runs once, on the owner every node agrees on, and the actors are
    /// shared out. A node leaving hands its actors over; one crashing is found down and its actors start elsewhere; one
    /// joining takes its share. Persisted, they carry on from their events wherever they start. Simulated, over twenty
    /// seeds (twenty orders of messages, pings and timeouts), so it doesn't depend on how busy the machine is, and a
    /// failure names the seed that repeats it.
    /// </summary>
    [Fact]
    public void Actors_in_a_cluster_run_once_and_move_as_nodes_come_and_go()
    {
        var result = Compile(Tallies + """
            /// The nodes of a simulated cluster running `key`'s tally.
            fn Running(cluster: SimulatedCluster, key: string) => List<int> =
                [...Enumerable.Range(0, cluster.Count).Where(i => cluster.IsUp(i) && cluster[i].Cluster.Hosted("Tally").Contains(key))];

            async fn AddToAll(cluster: SimulatedCluster, from: int, keys: List<string>) => Task<List<int>> =
                [...await Task.WhenAll(keys.Select(key => Tally.InCluster(cluster[from], key).Add(1)))];

            fn Check(what: string, holds: bool) {
                if !holds { throw InvalidOperationException($"Not so: {what}"); }
            }

            async fn ComeAndGo(sim: PiperSimulation) {
                let store = MemoryEventStore();
                let cluster = await SimulatedCluster.StartAsync(sim, 3, node => {
                    Tally.HostInCluster(node.Cluster, (n, name, key) => Tally.Spawn(n, name, store, key));
                }, NetworkFaults.None);
                Check("the nodes agree", await cluster.SettleAsync());
                let keys = [...Enumerable.Range(0, 30).Select(i => $"key-{i}")];
                for from in 0..<3 {
                    await AddToAll(cluster, from, keys);
                }
                Check("each runs once", keys.All(key => Running(cluster, key).Count == 1));
                Check("every node picks the same owner",
                    keys.All(key => Enumerable.Range(0, 3).Select(i => cluster[i].Cluster.OwnerOf($"Tally/{key}")).Distinct().Count() == 1));
                Check("each runs on its owner", keys.All(key => cluster.Addresses[Running(cluster, key)[0]] == cluster[0].Cluster.OwnerOf($"Tally/{key}")));
                Check("they're shared out", Enumerable.Range(0, 3).All(i => cluster[i].Cluster.Hosted("Tally").Count > 0));

                // Node 1 leaves: its tallies stop, and start again on the others, from their events.
                await cluster.StopAsync(1);
                Check("the others see it gone", await cluster.SettleAsync());
                Check("they carry on from their events", (await AddToAll(cluster, 0, keys)).All(total => total == 4));
                Check("none is left on node 1", keys.All(key => Running(cluster, key).Count == 1 && Running(cluster, key)[0] != 1));

                // Node 2 crashes: once it's found down, its tallies start again on node 0.
                await cluster.CrashAsync(2);
                Check("node 0 finds it down", await cluster.WaitForMembersAsync(0, 1));
                Check("they carry on on node 0", (await AddToAll(cluster, 0, keys)).All(total => total == 5));

                // A node joins and takes its share.
                let joined = await cluster.AddNodeAsync();
                Check("it joins", await cluster.SettleAsync());
                Check("they carry on there too", (await AddToAll(cluster, joined, keys)).All(total => total == 6));
                Check("it takes its share", cluster[joined].Cluster.Hosted("Tally").Count > 0);
            }

            fn Main() {
                PiperSimulation.Explore(20, ComeAndGo, 1);
                Print("held");
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        Assert.Equal("held\n", run.Output.Replace("\r\n", "\n"));
    }

    /// <summary>
    /// A split network: both sides think they own an actor. The majority's copy starts from the events and carries on;
    /// the cut-off node's copy is behind, so the store refuses its events and it stops. Healed, the actor is back on its
    /// owner, from everything that was saved.
    /// </summary>
    [Fact]
    public void A_split_cluster_cant_write_an_actors_events_twice()
    {
        var result = Compile(Tallies + """
            async fn Main() {
                let cluster = await Start(MemoryEventStore());
                let key = [...Enumerable.Range(0, 200).Select(i => $"key-{i}")].First(k => cluster[0].Cluster.OwnerOf($"Tally/{k}") == cluster.Addresses[2]);
                Print(await Tally.InCluster(cluster[0], key).Add(1));

                cluster.Isolate(2);
                await cluster.WaitForMembersAsync(0, 2);
                await cluster.WaitForMembersAsync(2, 1);
                Print(await Tally.InCluster(cluster[0], key).Add(10));
                try {
                    await Tally.InCluster(cluster[2], key).Add(100);
                } catch (e: InvalidOperationException) {
                    Print(if e.Message.StartsWith("The events of") { "refused" } else { e.Message });
                }

                cluster.Heal();
                await cluster.WaitForMembersAsync(0, 3);
                await cluster.WaitForMembersAsync(2, 3);
                Print(await Tally.InCluster(cluster[2], key).Add(1));
                Print(Where(cluster, key).SequenceEqual([2]));
                await cluster.StopAsync();
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        Assert.Equal("1\n11\nrefused\n12\nTrue\n", run.Output.Replace("\r\n", "\n"));
    }

    private const string Stock = """
        record Level(sku: string, onHand: int);
        enum Refusal { TooMany(asked: int), Unknown }
        actor Product(sku: string) {
            mut onHand = 0;
            on Receive(quantity: int) { onHand += quantity; }
            on Reserve(quantity: int) => Result<Level, Refusal> { if quantity > onHand { Err(Refusal.TooMany(quantity)) } else { onHand -= quantity; Ok(Level(sku, onHand)) } }
            on Count() => Level { Level(sku, onHand) }
        }
        fn Main() {}
        """;

    /// <summary>
    /// During a rolling update old and new nodes send each other messages, so an actor's messages are recorded in
    /// Name.shape.json beside it, and a change that breaks the nodes not yet updated is an error, as for an api.
    /// </summary>
    [Theory]
    [InlineData("on Receive(quantity: int) { onHand += quantity; }", "", "nothing answers Product.Receive any more")]
    [InlineData("on Receive(quantity: int)", "on Receive(quantity: int, from: string)", "Product.Receive now needs 'from' (string)")]
    [InlineData("on Count() => Level { Level(sku, onHand) }", "on Count() => int { onHand }", "Product.Count answers int, but callers built before read Level")]
    [InlineData("on Receive(quantity: int) { onHand += quantity; }", "on Receive(quantity: int) => int { onHand += quantity; onHand }",
        "Product.Receive answers int, but callers built before read nothing (a tell)")]
    [InlineData("record Level(sku: string, onHand: int);", "record Level(sku: string, count: int);", "Level's field 'onHand' is gone")]
    [InlineData("enum Refusal { TooMany(asked: int), Unknown }", "enum Refusal { TooMany(asked: int), Unknown, Frozen }", "Refusal has a new variant, Frozen")]
    public void A_breaking_change_to_an_actors_messages_is_an_error(string before, string after, string expected)
    {
        var folder = Directory.CreateTempSubdirectory("piper-actor-shape").FullName;
        try
        {
            CompilationResult CompileIn(string program, Plugins.WireShapeMode mode)
            {
                var path = Path.Combine(folder, "stock.piper");
                File.WriteAllText(path, program);
                return Compilation.Compile([new SourceText(program, path)],
                    new CompilationOptions { Plugins = Project.Value.Plugins, References = Project.Value.References, WireShapes = mode });
            }
            var shape = Path.Combine(folder, "Product.shape.json");
            var first = CompileIn(Stock, Plugins.WireShapeMode.Record);
            Assert.True(first.Success, string.Join("\n", first.FormattedDiagnostics));
            var recorded = File.ReadAllText(shape);
            Assert.Contains("\"Product.Reserve\"", recorded);
            Assert.Contains("\"output\": \"Result<Level, Refusal>\"", recorded);
            Assert.Contains("\"output\": \"nothing (a tell)\"", recorded);

            // A new handler, or a new optional value, is compatible.
            var grown = Stock.Replace("on Count()", "on Audit(note: string?) { }\n    on Count()");
            var compatible = CompileIn(grown, Plugins.WireShapeMode.Record);
            Assert.True(compatible.Success, string.Join("\n", compatible.FormattedDiagnostics));
            Assert.Contains("\"Product.Audit\"", File.ReadAllText(shape));
            File.WriteAllText(shape, recorded);

            var changed = Stock.Replace(before, after);
            Assert.NotEqual(Stock, changed);
            var result = CompileIn(changed, Plugins.WireShapeMode.Record);
            Assert.Contains(result.FormattedDiagnostics, d => d.Contains($"A breaking change to Product: {expected}"));
            Assert.Equal(recorded, File.ReadAllText(shape));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
