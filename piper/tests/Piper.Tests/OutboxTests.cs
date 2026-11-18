using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Piper.Plugins;
using Piper.Text;

namespace Piper.Tests;

/// <summary>
/// The outbox: a `database`'s `outbox;` (the data plugin) and the messaging plugin's `bus.In(db.Outbox)` and
/// `bus.Relay(…)`, against SQLite files. A message is saved with the change it tells of, or not at all, and sent once
/// it's saved; relays sharing the table send each message once.
/// </summary>
[Collection("data plugin")]
public class OutboxTests
{
    private static readonly Lazy<(ImmutableArray<IPiperPlugin> Plugins, ProgramReferences References)> Plugins = new(() =>
    {
        var (plugins, errors) = PluginLoader.Load(PluginsDirectory(),
            ["Piper.Plugins.Data/bin/plugin/Piper.Plugins.Data.dll", "Piper.Plugins.Messaging/bin/plugin/Piper.Plugins.Messaging.dll"]);
        Assert.Empty(errors);
        var packages = plugins.SelectMany(p => p.Packages).ToDictionary(p => p.Key, p => p.Value);
        packages["Microsoft.EntityFrameworkCore.Sqlite"] = "10.0.14";
        var (references, referenceErrors) = ProgramReferences.Resolve(
            Path.Combine(Path.GetTempPath(), "piper-outbox-tests"), packages,
            [.. plugins.SelectMany(p => p.Frameworks).Distinct()], [.. plugins.SelectMany(PluginLoader.References)]);
        Assert.Empty(referenceErrors);
        return (plugins, references);
    });

    private static string PluginsDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "plugins"));

    private static string Run(string program)
    {
        var path = Path.Combine(Path.GetTempPath(), $"piper-outbox-{Guid.NewGuid():N}.db");
        try
        {
            var result = Compilation.Compile([new SourceText(Shop.Replace("$path", path.Replace("\\", "/")) + program, "main.piper")],
                new CompilationOptions { Plugins = Plugins.Value.Plugins, References = Plugins.Value.References });
            Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
            var run = Runner.Run(result.Files, references: result.References);
            Assert.True(run.Success, string.Join("\n", run.Errors) + "\n" + run.Output);
            return run.Output.Replace("\r\n", "\n");
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private const string Shop = """
        using System;
        using System.Collections.Concurrent;
        using System.Linq;
        using System.Threading.Tasks;
        using Microsoft.EntityFrameworkCore;
        using Piper.Data;
        using Piper.Messaging;
        using Piper.Runtime;

        record Order(id: int, total: decimal);

        database Shop {
            table Orders: Order key id;
            outbox;
        }

        messages Sales {
            event OrderPlaced(orderId: int, total: decimal) topic "orders";
            command Ship(orderId: int) queue "shipping";
        }

        fn Options() => DbContextOptions<Shop> =
            DbContextOptionsBuilder<Shop>().UseSqlite("Data Source=$path;Pooling=False").Options;

        fn Open() => Shop = Shop(Options());

        /// Places an order: the row and its messages, saved together.
        async fn Place(bus: Bus, id: int, total: decimal) {
            using let shop = Open();
            shop.Orders.Add(Order(id, total));
            await bus.In(shop.Outbox).Publish(OrderPlaced(id, total));
            await bus.In(shop.Outbox).Send(Ship(id));
            await shop.SaveChangesAsync();
        }

        async fn Waiting() => Task<int> {
            using let shop = Open();
            await DatabaseOutbox.WaitingAsync(shop)
        }

        """;

    /// <summary>
    /// A message added to the outbox is saved with its change, and sent only once it's saved, by the relay; a change that
    /// fails to save takes its messages with it.
    /// </summary>
    [Fact]
    public void A_change_and_its_messages_are_saved_together_and_sent_once_saved()
    {
        var output = Run("""
            async fn Main() {
                {
                    using let shop = Open();
                    shop.Database.EnsureCreated();
                }
                let transport = MemoryTransport();
                let bus = Bus(transport);

                await Place(bus, 1, 25m);
                Print(transport.Sent.Count);                 // nothing sent yet: saved, waiting
                Print(await Waiting());

                let relay = bus.Relay(DatabaseOutbox.Store(() => Open()), OutboxOptions() { Background: false });
                Print(await relay.FlushAsync());
                Print(string.Join(", ", transport.Sent.Select(e => $"{e.Type} to {e.Destination}")));
                Print(await Waiting());

                // The same order again: its row is refused, so neither is it placed nor are its messages sent.
                try {
                    await Place(bus, 1, 99m);
                } catch (e: DbUpdateException) {
                    Print("refused");
                }
                Print(await Waiting());
                Print(await relay.FlushAsync());
                await relay.DisposeAsync();
            }
            """);
        Assert.Equal("0\n2\n2\nOrderPlaced to orders, Ship to shipping\n0\nrefused\n0\n0\n", output);
    }

    /// <summary>Two relays sharing one outbox (as two pods would) send each message once between them.</summary>
    [Fact]
    public void Relays_sharing_an_outbox_send_each_message_once()
    {
        var output = Run("""
            async fn Main() {
                {
                    using let shop = Open();
                    shop.Database.EnsureCreated();
                }
                let transport = MemoryTransport();
                let bus = Bus(transport);
                for id in 1..50 {
                    await Place(bus, id, 10m);
                }
                let options = OutboxOptions() { Batch: 7, Background: false };
                let first = bus.Relay(DatabaseOutbox.Store(() => Open()), options);
                let second = bus.Relay(DatabaseOutbox.Store(() => Open()), options);
                await Task.WhenAll(first.FlushAsync(), second.FlushAsync());
                let ids = transport.Sent.Select(e => e.Id).ToList();
                Print(ids.Count);
                Print(ids.Distinct().Count());
                Print(first.Relayed + second.Relayed);
                Print(await Waiting());
                await first.DisposeAsync();
                await second.DisposeAsync();
            }
            """);
        Assert.Equal("100\n100\n100\n0\n", output);
    }

    /// <summary>A relay running in the background sends what's saved to the consumers, without being asked to.</summary>
    [Fact]
    public void A_relay_in_the_background_delivers_to_consumers()
    {
        var output = Run("""
            class Log { public let lines = ConcurrentQueue<string>(); }

            consumer Warehouse(log: Log) {
                on Ship(command) {
                    log.lines.Enqueue($"shipping {command.orderId}");
                    Handled.Done
                }
            }

            async fn Main() {
                {
                    using let shop = Open();
                    shop.Database.EnsureCreated();
                }
                let transport = MemoryTransport();
                let bus = Bus(transport);
                let log = Log();
                await using let warehouse = await Warehouse(log).Start(bus);
                await using let relay = bus.Relay(DatabaseOutbox.Store(() => Open()), OutboxOptions() { Poll: TimeSpan.FromMilliseconds(20) });

                await Place(bus, 7, 12m);
                let deadline = DateTime.UtcNow.AddSeconds(10);
                while log.lines.IsEmpty && DateTime.UtcNow < deadline {
                    await Task.Delay(20);
                }
                await transport.Idle();
                Print(string.Join(", ", log.lines));
            }
            """);
        Assert.Equal("shipping 7\n", output);
    }
}
