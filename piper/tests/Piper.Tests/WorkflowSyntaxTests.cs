using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Piper.Plugins;
using Piper.Text;

namespace Piper.Tests;

/// <summary>
/// The workflows plugin's syntax: `workflow` with its `start` block, `activity` and `timer` steps, and what's reported
/// (PIP0513) where a workflow's code could do differently when it's replayed.
/// </summary>
public class WorkflowSyntaxTests
{
    private static readonly Lazy<(ImmutableArray<IPiperPlugin> Plugins, ProgramReferences References)> Plugins = new(() =>
    {
        var (plugins, errors) = PluginLoader.Load(PluginsDirectory(), ["Piper.Plugins.Workflows/bin/plugin/Piper.Plugins.Workflows.dll"]);
        Assert.Empty(errors);
        var (references, referenceErrors) = ProgramReferences.Resolve(
            Path.GetTempPath(), ImmutableDictionary<string, string>.Empty, [], [.. plugins.SelectMany(PluginLoader.References)]);
        Assert.Empty(referenceErrors);
        return (plugins, references);
    });

    private static string PluginsDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "plugins"));

    private static CompilationResult Compile(string program) =>
        Compilation.Compile([new SourceText(program, "main.piper")],
            new CompilationOptions { Plugins = Plugins.Value.Plugins, References = Plugins.Value.References });

    private const string Shop = """
        using System;
        using System.IO;
        using System.Linq;
        using System.Threading.Tasks;
        using Piper.Runtime;
        using Piper.Workflows;

        record Order(id: string, total: decimal);
        record Receipt(id: string, payment: string, shipped: bool);

        /// What the workflow's activities do, counting each time they really run.
        class Shop {
            public mut charged = 0;
            public mut shipped = 0;
            async fn Charge(order: Order) => Task<string> {
                charged += 1;
                $"pay-{order.id}"
            }
            fn Ship(id: string) => bool {
                shipped += 1;
                true
            }
        }

        """;

    [Fact]
    public void A_declared_workflow_survives_a_restart_running_each_activity_once()
    {
        var result = Compile(Shop + """
            workflow Fulfil(shop: Shop) {
                start (order: Order, express: bool) => Receipt {
                    activity payment = shop.Charge(order);
                    let when = await flow.Now();
                    timer TimeSpan.FromDays(if express { 1 } else { 2 });
                    activity shipped = shop.Ship(order.id);
                    Receipt(order.id, payment, shipped)
                }
            }

            async fn Restarted(sim: PiperSimulation) => Task<string> {
                let store = MemoryEventStore();
                let shop = Shop();
                let first = WorkflowEngine(store);
                Fulfil(shop).Register(first);
                await Fulfil.Start(first, "o-1", Order("o-1", 10m), false);
                await sim.Delay(TimeSpan.FromDays(1));
                await first.DisposeAsync();

                let second = WorkflowEngine(store);
                Fulfil(shop).Register(second);
                await second.ResumeAsync();
                let receipt = await Fulfil.Outcome(second, "o-1");
                $"{receipt} | charged {shop.charged}, shipped {shop.shipped} | day {(int)sim.Elapsed.TotalDays}"
            }

            fn Main() {
                Print(PiperSimulation.Run(1, Restarted));
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors) + "\n" + run.Output);
        Assert.Equal("Receipt { id = o-1, payment = pay-o-1, shipped = True } | charged 1, shipped 1 | day 2\n", run.Output.Replace("\r\n", "\n"));
    }

    /// <summary>
    /// A saga: a workflow waiting on a signal from outside (a payment) for up to a day, compensating if it doesn't come.
    /// The signal sent while no engine was running waits in the instance's inbox, and is taken as it resumes.
    /// </summary>
    [Fact]
    public void A_workflow_waits_for_a_signal_and_compensates_if_it_doesnt_come()
    {
        var result = Compile(Shop + """
            record Payment(amount: decimal);

            class Stock {
                public mut held = 0;
                fn Reserve(id: string) { held += 1; }
                fn Release(id: string) { held -= 1; }
            }

            workflow Checkout(shop: Shop, stock: Stock) {
                start (order: Order) => string {
                    activity stock.Reserve(order.id);
                    match await flow.Signal<Payment>("paid", TimeSpan.FromDays(1)) {
                        Some(payment) => {
                            activity shipped = shop.Ship(order.id);
                            $"shipped, paid {payment.amount}"
                        },
                        None => {
                            activity stock.Release(order.id);
                            "cancelled: not paid in time"
                        },
                    }
                }
            }

            async fn Saga(sim: PiperSimulation) => Task<string> {
                let store = MemoryEventStore();
                let shop = Shop();
                let stock = Stock();
                let first = WorkflowEngine(store);
                Checkout(shop, stock).Register(first);
                await Checkout.Start(first, "paid", Order("paid", 10m));
                await Checkout.Start(first, "unpaid", Order("unpaid", 20m));
                await sim.Delay(TimeSpan.FromHours(2));
                await first.DisposeAsync();                                  // nothing running now

                let second = WorkflowEngine(store);
                Checkout(shop, stock).Register(second);
                await Checkout.Signal(second, "paid", "paid", Payment(10m));   // kept in its inbox
                await second.ResumeAsync();
                let paid = await Checkout.Outcome(second, "paid");
                let unpaid = await Checkout.Outcome(second, "unpaid");
                $"{paid} | {unpaid} | held {stock.held}, shipped {shop.shipped} | after {(int)sim.Elapsed.TotalHours}h\n" +
                    string.Join("\n", await second.StepsAsync("Checkout", "unpaid"))
            }

            fn Main() {
                Print(PiperSimulation.Run(1, Saga));
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors) + "\n" + run.Output);
        Assert.Equal(
            "shipped, paid 10 | cancelled: not paid in time | held 1, shipped 1 | after 24h\n" +
            "started Checkout with {\"order\":{\"id\":\"unpaid\",\"total\":20}}\n" +
            "1: stock.Reserve gave true\n" +
            "2: waiting for paid until 2030-01-02 00:00:00\n" +
            "2: paid didn't come in time\n" +
            "3: stock.Release gave true\n" +
            "completed with \"cancelled: not paid in time\"\n", run.Output.Replace("\r\n", "\n"));
    }

    /// <summary>A signal sent at any moment, with the workflow restarted at any moment: it's taken once, and the order ships.</summary>
    [Fact]
    public void A_signal_is_taken_once_however_the_workflow_is_restarted()
    {
        var result = Compile(Shop + """
            record Payment(amount: decimal);

            workflow Pay(shop: Shop) {
                start (order: Order) => string {
                    let payment = await flow.Signal<Payment>("paid");
                    activity shipped = shop.Ship(order.id);
                    $"paid {payment.amount}, shipped {shipped}"
                }
            }

            async fn Restarts(sim: PiperSimulation) {
                let store = MemoryEventStore();
                let shop = Shop();
                mut engine = WorkflowEngine(store);
                Pay(shop).Register(engine);
                await Pay.Start(engine, "o", Order("o", 5m));
                let signalAt = sim.Random.Next(3);
                for round in 0..<3 {
                    if round == signalAt {
                        await Pay.Signal(engine, "o", "paid", Payment(5m));
                    }
                    await sim.Delay(TimeSpan.FromMinutes(sim.Random.Next(120)));
                    await engine.DisposeAsync();
                    engine = WorkflowEngine(store);
                    Pay(shop).Register(engine);
                    await engine.ResumeAsync();
                }
                let outcome = await Pay.Outcome(engine, "o");
                let steps = await engine.StepsAsync("Pay", "o");
                if outcome != "paid 5, shipped True" { throw InvalidOperationException(outcome); }
                if steps.Count(s => s.Contains("paid came")) != 1 { throw InvalidOperationException(string.Join(" / ", steps)); }
                await engine.DisposeAsync();
            }

            fn Main() {
                PiperSimulation.Explore(20, Restarts, 1);
                Print("held");
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors) + "\n" + run.Output);
        Assert.Equal("held\n", run.Output.Replace("\r\n", "\n"));
    }

    /// <summary>
    /// A workflow's shape is recorded (Fulfil.shape.json): its input, its result and its steps in order. A change
    /// instances already running couldn't replay (a step inserted before others, or moved; a new input they weren't
    /// started with) is an error; a step added after the others, or an optional input, isn't.
    /// </summary>
    [Fact]
    public void A_change_running_instances_couldnt_replay_is_an_error()
    {
        var folder = Directory.CreateTempSubdirectory("piper-workflows").FullName;
        try
        {
            CompilationResult CompileIn(string text, Piper.Plugins.WireShapeMode mode)
            {
                var path = Path.Combine(folder, "orders.piper");
                File.WriteAllText(path, text);
                return Compilation.Compile([new SourceText(text, path)],
                    new CompilationOptions { Plugins = Plugins.Value.Plugins, References = Plugins.Value.References, WireShapes = mode });
            }
            const string Before = """
                using System;
                using System.Threading.Tasks;
                record Order(id: string);
                class Shop {
                    fn Charge(id: string) => string = $"pay-{id}";
                    fn Ship(id: string) => bool = true;
                }
                workflow Fulfil(shop: Shop) {
                    start (order: Order) => string {
                        activity payment = shop.Charge(order.id);
                        timer TimeSpan.FromDays(1);
                        activity shipped = shop.Ship(order.id);
                        payment
                    }
                }
                fn Main() {}
                """;
            Assert.True(CompileIn(Before, Piper.Plugins.WireShapeMode.Record).Success);
            var shape = File.ReadAllText(Path.Combine(folder, "Fulfil.shape.json"));
            Assert.Contains("\"1: activity shop.Charge\"", shape);
            Assert.Contains("\"2: timer\"", shape);
            Assert.Contains("\"3: activity shop.Ship\"", shape);

            // After the others, or optional: instances replay what they have, then go on.
            Assert.True(CompileIn(Before.Replace("        payment\n", "        activity shop.Ship(order.id);\n        payment\n"), Piper.Plugins.WireShapeMode.Check).Success);
            Assert.True(CompileIn(Before.Replace("(order: Order)", "(order: Order, note: string?)"), Piper.Plugins.WireShapeMode.Check).Success);

            // Before them: step 1 is now another.
            var inserted = CompileIn(Before.Replace("        activity payment = shop.Charge", "        activity checked = shop.Ship(order.id);\n        activity payment = shop.Charge"),
                Piper.Plugins.WireShapeMode.Check);
            Assert.Contains(inserted.FormattedDiagnostics, d => d.Contains(
                "A breaking change to Fulfil: step 1: activity shop.Charge is gone or has moved, but instances already running recorded it there, and would replay differently"));
            // A new input that instances weren't started with.
            Assert.Contains(CompileIn(Before.Replace("(order: Order)", "(order: Order, express: bool)"), Piper.Plugins.WireShapeMode.Check).FormattedDiagnostics,
                d => d.Contains("start Fulfil now needs 'express' (bool), which instances already running don't send"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void What_could_differ_when_a_workflow_is_replayed_is_an_error_where_its_written()
    {
        var program = Shop + """
            /// Called from the workflow's code: it's checked too.
            fn Stamp() => string = File.ReadAllText("stamp.txt");

            /// Called from an activity: it isn't, as an activity runs once and is recorded.
            fn Fresh() => DateTime = DateTime.UtcNow;

            workflow Careless(shop: Shop) {
                start (order: Order) => string {
                    let now = DateTime.UtcNow;
                    await Task.Delay(1000);
                    let id = Guid.NewGuid();
                    let roll = Random.Shared.Next(6);
                    let stamp = Stamp();
                    activity fresh = Fresh();
                    activity payment = shop.Charge(order);
                    let recorded = await flow.Now();
                    $"{now} {id} {roll} {stamp} {fresh} {payment} {recorded}"
                }
            }

            fn Main() {}
            """;
        var errors = Compile(program).Diagnostics.Where(d => d.Code == "PIP0513").ToList();
        string At(string text) => program.Substring(errors.Single(e => program.Substring(e.Span.Start).StartsWith(text)).Span.Start, text.Length);
        Assert.Equal(5, errors.Count);
        Assert.Equal("DateTime.UtcNow", At("DateTime.UtcNow;"[..^1]));
        Assert.Contains(errors, e => e.Message.Contains("Careless must do the same every time it's replayed, but it reads the clock") && e.Message.Contains("flow.Now()"));
        Assert.Contains(errors, e => e.Message.Contains("this wait isn't durable") && e.Message.Contains("timer"));
        Assert.Contains(errors, e => e.Message.Contains("a new id differs") && e.Message.Contains("flow.NewGuid()"));
        Assert.Contains(errors, e => e.Message.Contains("random numbers differ"));
        Assert.Contains(errors, e => e.Message.Contains("(this is reached through Stamp)") && e.Message.Contains("it does I/O"));
    }

    [Theory]
    [InlineData("async fn Main() { activity x = Charge(); }", "Charge()", "An activity is a step of a workflow")]
    [InlineData("async fn Main() { timer TimeSpan.FromDays(1); }", "TimeSpan.FromDays(1)", "A timer is a step of a workflow")]
    [InlineData("workflow W { start () => int { activity x = 1 + 2; x } }\nfn Main() {}", "1 + 2", "An activity is a call")]
    [InlineData("workflow W { }\nfn Main() {}", "}", "Expected the workflow's code")]
    public void Mistakes_in_a_workflow_are_reported(string program, string at, string message)
    {
        var text = "using System;\nfn Charge() => int = 1;\n" + program;
        var error = Assert.Single(Compile(text).Diagnostics, d => d.Message.Contains(message));
        Assert.Equal(text.IndexOf(at, text.IndexOf(program, StringComparison.Ordinal), StringComparison.Ordinal), error.Span.Start);
    }
}
