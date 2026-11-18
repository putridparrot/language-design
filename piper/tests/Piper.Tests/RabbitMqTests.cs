using System.Collections.Immutable;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Piper.Plugins;
using Piper.Text;
using Xunit.Abstractions;

namespace Piper.Tests;

/// <summary>
/// A RabbitMQ broker for the tests, in a container (rabbitmq:4-alpine) on a free port, removed when they're done. With no
/// Docker (or no image), <see cref="Uri"/> is null and the tests say they didn't run; <c>PIPER_RABBITMQ</c> names a
/// broker to use instead.
/// </summary>
public sealed class RabbitMqBroker : IDisposable
{
    private readonly string? _container;

    public RabbitMqBroker()
    {
        if (Environment.GetEnvironmentVariable("PIPER_RABBITMQ") is { Length: > 0 } given)
        {
            Uri = given;
            return;
        }
        try
        {
            var (started, id) = Docker("run -d --rm -p 127.0.0.1::5672 rabbitmq:4-alpine");
            if (!started)
                return;
            _container = id.Trim();
            var (_, mapping) = Docker($"port {_container} 5672");
            var port = int.Parse(mapping.Trim().Split('\n')[0].Split(':')[^1]);
            // The port answers before RabbitMQ is ready; it's ready when it logs that it has started.
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < deadline && !Docker($"logs {_container}").Output.Contains("Server startup complete"))
                Thread.Sleep(500);
            using var socket = new TcpClient();
            socket.Connect("127.0.0.1", port);
            Uri = $"amqp://guest:guest@127.0.0.1:{port}/";
        }
        catch (Exception)
        {
            Uri = null;
        }
    }

    /// <summary>The broker's URI, or null if there isn't one.</summary>
    public string? Uri { get; }

    private static (bool Ok, string Output) Docker(string arguments)
    {
        var start = new ProcessStartInfo("docker", arguments) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode == 0, output);
    }

    public void Dispose()
    {
        if (_container is not null)
            Docker($"rm -f {_container}");
    }
}

[CollectionDefinition("rabbitmq")]
public sealed class RabbitMqCollection : ICollectionFixture<RabbitMqBroker>;

/// <summary>
/// The rabbitmq plugin's transport (plugins/Piper.Messaging.RabbitMq) against a real RabbitMQ: commands to one consumer
/// and events to every one, rejects to dead letters, delays and retries waiting as asked, and two processes' consumers
/// sharing a command's queue, each message handled once.
/// </summary>
[Collection("rabbitmq")]
public class RabbitMqTests(RabbitMqBroker broker, ITestOutputHelper output)
{
    private static readonly Lazy<(ImmutableArray<IPiperPlugin> Plugins, ProgramReferences References)> Plugins = new(() =>
    {
        var (plugins, errors) = PluginLoader.Load(PluginsDirectory(),
            ["Piper.Plugins.Messaging/bin/plugin/Piper.Plugins.Messaging.dll", "Piper.Plugins.Messaging.RabbitMq/bin/plugin/Piper.Plugins.Messaging.RabbitMq.dll"]);
        Assert.Empty(errors);
        var packages = plugins.SelectMany(p => p.Packages).ToDictionary(p => p.Key, p => p.Value);
        var (references, referenceErrors) = ProgramReferences.Resolve(
            Path.Combine(Path.GetTempPath(), "piper-rabbitmq-tests"), packages, [], [.. plugins.SelectMany(PluginLoader.References)]);
        Assert.Empty(referenceErrors);
        return (plugins, references);
    });

    private static string PluginsDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "plugins"));

    /// <summary>Runs the program against the broker, with its names made unique to the test; or null with no broker.</summary>
    private string? Run(string program)
    {
        if (broker.Uri is not { } uri)
        {
            output.WriteLine("Not run: no RabbitMQ (no Docker, or no rabbitmq:4-alpine image; or set PIPER_RABBITMQ).");
            return null;
        }
        var unique = Guid.NewGuid().ToString("N")[..8];
        var result = Compilation.Compile([new SourceText((Prelude + program).Replace("$uri", uri).Replace("$unique", unique), "main.piper")],
            new CompilationOptions { Plugins = Plugins.Value.Plugins, References = Plugins.Value.References });
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors) + "\n" + run.Output);
        return run.Output.Replace("\r\n", "\n");
    }

    private const string Prelude = """
        using System;
        using System.Collections.Concurrent;
        using System.Linq;
        using System.Threading.Tasks;
        using Piper.Messaging;
        using Piper.Messaging.RabbitMq;

        messages Shop {
            command PlaceOrder(orderId: int, quantity: int) queue "orders-$unique";
            event OrderPlaced(orderId: int, total: decimal) topic "order-events-$unique" key orderId;
            command Ping(n: int) queue "pings-$unique";
        }

        class Log { public let lines = ConcurrentQueue<string>(); }

        fn Broker() => RabbitMqTransport = RabbitMqTransport("$uri");

        /// Waits (up to twenty seconds) until `log` has `count` lines.
        async fn Until(log: Log, count: int) {
            let deadline = DateTime.UtcNow.AddSeconds(20);
            while log.lines.Count < count && DateTime.UtcNow < deadline {
                await Task.Delay(20);
            }
        }

        """;

    [Fact]
    public void Commands_go_to_one_consumer_events_to_each_and_rejects_to_dead_letters()
    {
        var result = Run("""
            consumer Orders(bus: Bus, log: Log) {
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
                on OrderPlaced(e) {
                    log.lines.Enqueue($"emailed about {e.orderId}");
                    Handled.Done
                }
            }

            async fn Main() {
                let transport = Broker();
                let bus = Bus(transport);
                let log = Log();
                await using let orders = await Orders(bus, log).Start(bus);
                await using let billing = await Billing(log).Start(bus);
                await using let emails = await Emails(log).Start(bus);
                await bus.Send(PlaceOrder(1, 4));
                await bus.Send(PlaceOrder(2, 0));
                await Until(log, 3);
                await Task.Delay(300);
                Print(string.Join("\n", log.lines.Order()));
                let dead = await transport.TakeDeadLettersAsync(Subscription("orders-$unique", DestinationKind.Queue, "Orders"));
                Print(string.Join(", ", dead.Select(d => $"{d.Envelope.Type} {d.Envelope.Body}: {d.Reason}")));
                await transport.DisposeAsync();
            }
            """);
        if (result is not null)
            Assert.Equal("billed 1 for 10.0\nemailed about 1\nplaced 1\nPlaceOrder {\"orderId\":2,\"quantity\":0}: no quantity\n", result);
    }

    [Fact]
    public void Delays_and_retries_wait_as_asked()
    {
        var result = Run("""
            class Tries { public mut n = 0; }

            consumer Patient(log: Log, tries: Tries, started: DateTime) {
                on Ping(ping) {
                    tries.n += 1;
                    if ping.n == 1 && tries.n == 1 {
                        return Handled.RetryLater(TimeSpan.FromMilliseconds(400));
                    }
                    let waited = (DateTime.UtcNow - started).TotalMilliseconds;
                    log.lines.Enqueue($"ping {ping.n} after {(if waited >= 380 { "the wait" } else { $"only {waited}ms" })}");
                    Handled.Done
                }
            }

            async fn Main() {
                let transport = Broker();
                let bus = Bus(transport);
                let log = Log();
                await using let patient = await Patient(log, Tries(), DateTime.UtcNow).Start(bus);
                await bus.Send(Ping(1));                                   // retried once, 400 ms later
                await bus.Send(Ping(2), Some(TimeSpan.FromMilliseconds(500)));   // sent with a delay
                await Until(log, 2);
                Print(string.Join("\n", log.lines.Order()));
                await transport.DisposeAsync();
            }
            """);
        if (result is not null)
            Assert.Equal("ping 1 after the wait\nping 2 after the wait\n", result);
    }

    [Fact]
    public void Two_processes_consumers_share_a_commands_queue_each_message_once()
    {
        var result = Run("""
            consumer Worker(name: string, log: Log) {
                on Ping(ping) {
                    log.lines.Enqueue($"{ping.n}");
                    Handled.Done
                }
            }

            async fn Main() {
                // Two connections, as two processes would have.
                let first = Broker();
                let second = Broker();
                let log = Log();
                await using let a = await Worker("a", log).Start(Bus(first));
                await using let b = await Worker("b", log).Start(Bus(second));
                let bus = Bus(first);
                for n in 1..40 {
                    await bus.Send(Ping(n));
                }
                await Until(log, 40);
                await Task.Delay(300);
                let handled = log.lines.Select(line => int.Parse(line)).ToList();
                Print(handled.Count);
                Print(handled.Distinct().Count());
                await first.DisposeAsync();
                await second.DisposeAsync();
            }
            """);
        if (result is not null)
            Assert.Equal("40\n40\n", result);
    }
}
