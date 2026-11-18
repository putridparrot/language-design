using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Piper.Plugins;
using Piper.Text;

namespace Piper.Tests;

/// <summary>
/// The messaging plugin (plugins/Piper.Plugins.Messaging): `messages` and `consumer` declarations over Piper.Messaging's
/// bus, run on the in-memory transport (the local provider), and what's checked as they compile.
/// </summary>
public class MessagingPluginTests
{
    private static readonly Lazy<(ImmutableArray<IPiperPlugin> Plugins, ProgramReferences References)> Plugins = new(() =>
    {
        var (plugins, errors) = PluginLoader.Load(PluginsDirectory(), ["Piper.Plugins.Messaging/bin/plugin/Piper.Plugins.Messaging.dll"]);
        Assert.Empty(errors);
        var (references, referenceErrors) = ProgramReferences.Resolve(
            Path.GetTempPath(), ImmutableDictionary<string, string>.Empty, [], [.. plugins.SelectMany(PluginLoader.References)]);
        Assert.Empty(referenceErrors);
        return (plugins, references);
    });

    private static string PluginsDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "plugins"));

    private static CompilationResult Compile(string text) =>
        Compilation.Compile([new SourceText(text, "main.piper")], new CompilationOptions { Plugins = Plugins.Value.Plugins, References = Plugins.Value.References });

    private const string Orders = """
        using System;
        using System.Collections.Concurrent;
        using System.Linq;
        using System.Threading.Tasks;
        using Piper.Messaging;
        using Piper.Runtime;

        newtype OrderId = int;

        messages Orders {
            command PlaceOrder(orderId: OrderId, quantity: int) queue "orders";
            event OrderPlaced(orderId: OrderId, total: decimal) topic "order-events" key orderId;
            command Ping(n: int);
        }

        class Log { public let lines = ConcurrentQueue<string>(); }
        class Counter { public mut n = 0; }

        """;

    /// <summary>
    /// Messaging in a simulation: the in-memory transport, the outbox's relay and a handler's RetryLater run on the
    /// simulation's clock and scheduling, so an hour's retry passes at once, nothing leaves for a real thread, and a
    /// seed's run is the same every time.
    /// </summary>
    [Fact]
    public void Messaging_runs_in_a_simulation()
    {
        var result = Compile(Orders + """
            consumer Patient(log: Log, tries: Counter) {
                on Ping(ping) {
                    tries.n += 1;
                    if tries.n == 1 {
                        return Handled.RetryLater(TimeSpan.FromHours(1));
                    }
                    log.lines.Enqueue($"pinged {ping.n} after {PiperSimulation.Running.Elapsed.TotalHours:0}h");
                    Handled.Done
                }
            }

            async fn Scenario(sim: PiperSimulation) => Task<string> {
                let transport = MemoryTransport();
                let bus = Bus(transport);
                let log = Log();
                await using let patient = await Patient(log, Counter()).Start(bus);
                let outbox = MemoryOutbox();
                await using let relay = bus.Relay(outbox);
                await bus.In(outbox).Send(Ping(7));
                while log.lines.IsEmpty {
                    await sim.Delay(TimeSpan.FromSeconds(1));
                }
                await transport.Idle();
                $"{string.Join(", ", log.lines)} | {sim.Timeline.Any(line => line.Contains("outside the simulation"))}"
            }

            fn Main() {
                let first = PiperSimulation.Run(4, Scenario);
                Print(first);
                Print(first == PiperSimulation.Run(4, Scenario));
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        Assert.Equal("pinged 7 after 1h | False\nTrue\n", run.Output.Replace("\r\n", "\n"));
    }

    /// <summary>
    /// A command goes to one handler and an event to every consumer; a handler's Handled says what became of its
    /// message (rejected ones are dead-lettered); one that throws is retried, with backoff, until it's handled.
    /// </summary>
    [Fact]
    public void Commands_and_events_reach_their_consumers()
    {
        var result = Compile(Orders + """
            consumer Shop(bus: Bus, log: Log) {
                on PlaceOrder(order) {
                    if order.quantity <= 0 { return Handled.Reject("no quantity"); }
                    await bus.Publish(OrderPlaced(order.orderId, order.quantity * 2.5m));
                    log.lines.Enqueue($"placed {order.orderId}");
                    Handled.Done
                }
            }

            consumer Billing(log: Log) {
                on OrderPlaced(e) {
                    log.lines.Enqueue($"billed {e.orderId} for {e.total}");
                    Handled.Done
                }
            }

            consumer Emails(log: Log) {
                on OrderPlaced(e) => Handled {
                    log.lines.Enqueue($"emailed about {e.orderId}");
                    Handled.Done
                }
            }

            consumer Flaky(log: Log, counter: Counter) {
                on Ping(ping) {
                    counter.n += 1;
                    if counter.n < 3 { throw InvalidOperationException("not yet"); }
                    log.lines.Enqueue($"pong {ping.n} on attempt {counter.n}");
                    Handled.Done
                }
            }

            async fn Main() {
                // The local transport, as configuration chooses when it names none.
                let transport = PiperProviders.Create<IMessageTransport>(ProviderSettings.Empty("Messaging"));
                let memory = (MemoryTransport)transport;
                let bus = Bus(transport, BusOptions() { Backoff: TimeSpan.FromMilliseconds(5) });
                let log = Log();
                let running = [
                    await Shop(bus, log).Start(bus), await Billing(log).Start(bus),
                    await Emails(log).Start(bus), await Flaky(log, Counter()).Start(bus),
                ];

                await bus.Send(PlaceOrder(OrderId(7), 2));
                await bus.Send(PlaceOrder(OrderId(8), 0));
                await bus.Send(Ping(1));
                await memory.Idle();

                for line in log.lines.Order() { Print(line); }
                for letter in memory.DeadLetters { Print($"dead: {letter.Envelope.Type} ({letter.Reason})"); }
                Print(string.Join(", ", memory.Sent.Select(e => $"{e.Type} to {e.Destination}{(e.Key is Some(k) ? $" key {k}" : "")}")));
                for subscription in running { await subscription.DisposeAsync(); }
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Empty(result.Diagnostics);
        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        Assert.Equal("""
            billed 7 for 5.0
            emailed about 7
            placed 7
            pong 1 on attempt 3
            dead: PlaceOrder (no quantity)
            PlaceOrder to orders, PlaceOrder to orders, Ping to ping, OrderPlaced to order-events key 7

            """, run.Output.Replace("\r\n", "\n"));
    }

    /// <summary>What's checked as messages and consumers compile.</summary>
    [Theory]
    [InlineData("messages M { command Add(items: System.Collections.Generic.List<int>); }", "Add", "Add's 'items' holds a List<int>")]
    [InlineData("messages M { event Seen(id: int) topic \"t\" key name; }", "name", "'name' isn't a field of Seen")]
    [InlineData("messages M { command Go(id: int) topic \"t\"; }", "topic", "A command goes to a queue")]
    [InlineData("messages M { command Go(id: int) key id; }", "key", "A command has one receiver, so it has no key")]
    [InlineData("messages M { command Go(id: int); command Go(n: int); }", "Go(n", "M already has a message called 'Go'")]
    [InlineData("messages M { command Go(id: int); }\nconsumer A { on Go(g) { Handled.Done } }\nconsumer B { on Go(g) { Handled.Done } }", "Go(g) { Handled.Done } }",
        "Go is a command, which has one receiver, but A handles it too")]
    [InlineData("consumer A { on Stray(s) { Handled.Done } }", "Stray", "Stray isn't a message")]
    [InlineData("messages M { command Go(id: int); }\nconsumer A { on Go(g) => int { 1 } }", "int", "A handler answers what became of its message")]
    public void Mistakes_are_reported(string text, string at, string message)
    {
        var source = "using Piper.Messaging;\nfn Main() {}\n" + text;
        var error = Assert.Single(Compile(source).Diagnostics, d => d.Message.Contains(message));
        Assert.Equal(source.LastIndexOf(at, StringComparison.Ordinal), error.Span.Start);
    }

    /// <summary>
    /// A messages block's shape is recorded (Orders.shape.json): queues keep messages for days and other services read
    /// the events, so a change that breaks them (a new field they don't send, a message moved to another queue) is an
    /// error, and an optional new field isn't.
    /// </summary>
    [Fact]
    public void A_breaking_change_to_messages_is_an_error()
    {
        var folder = Directory.CreateTempSubdirectory("piper-messages").FullName;
        try
        {
            CompilationResult CompileIn(string text, WireShapeMode mode)
            {
                var path = Path.Combine(folder, "orders.piper");
                File.WriteAllText(path, text);
                return Compilation.Compile([new SourceText(text, path)],
                    new CompilationOptions { Plugins = Plugins.Value.Plugins, References = Plugins.Value.References, WireShapes = mode });
            }
            const string Before = "fn Main() {}\nmessages Orders {\n    command PlaceOrder(id: int, quantity: int) queue \"orders\";\n    event OrderPlaced(id: int) topic \"order-events\";\n}\n";
            Assert.True(CompileIn(Before, WireShapeMode.Record).Success);
            Assert.Contains("\"orders: PlaceOrder\"", File.ReadAllText(Path.Combine(folder, "Orders.shape.json")));

            Assert.True(CompileIn(Before.Replace("quantity: int)", "quantity: int, note: string?)"), WireShapeMode.Check).Success);
            Assert.Contains(CompileIn(Before.Replace("quantity: int)", "quantity: int, coupon: string)"), WireShapeMode.Check).FormattedDiagnostics,
                d => d.Contains("A breaking change to Orders: command PlaceOrder now needs 'coupon' (string)"));
            Assert.Contains(CompileIn(Before.Replace("queue \"orders\"", "queue \"orders-v2\""), WireShapeMode.Check).FormattedDiagnostics,
                d => d.Contains("nothing answers orders: PlaceOrder"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
