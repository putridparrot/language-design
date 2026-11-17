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
                await host.Remote().ShutdownAsync(true);
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
                } catch (e: Proto.DeadLetterException) {
                    Print("no such actor");
                }
                // Within a trace an ask is sent with its headers, and still fails at once for an actor that isn't there.
                try {
                    await Back.Connect(client, b.Address, "nobody").Where();
                } catch (e: Proto.DeadLetterException) {
                    Print("no such actor");
                }
                await System.Threading.Tasks.Task.Delay(200);
                await client.Remote().ShutdownAsync(true);
                await a.Remote().ShutdownAsync(true);
                await b.Remote().ShutdownAsync(true);
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
    /// still gets the error); one with `stop` is stopped by Proto.Actor, so a later ask fails at once with a dead letter.
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
                try { await fuse.Check(true); } catch (e: Proto.DeadLetterException) { Print("stopped"); }
                await client.Remote().ShutdownAsync(true);
                await host.Remote().ShutdownAsync(true);
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
    /// (as a pod restarting does), it replays them as Proto.Actor starts it and carries on where it was. A handler that
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
                await client.Remote().ShutdownAsync(true);
                await first.Remote().ShutdownAsync(true);
                await second.Remote().ShutdownAsync(true);
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Empty(result.Diagnostics);
        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        Assert.Equal("3\n7\nnegative\n7\n", run.Output.Replace("\r\n", "\n"));
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
