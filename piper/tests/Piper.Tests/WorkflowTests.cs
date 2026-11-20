using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Piper.Plugins;
using Piper.Text;

namespace Piper.Tests;

/// <summary>
/// Durable workflows (Piper.Workflows' WorkflowEngine), in simulations, so days of durable timers pass at once and a
/// restart can come at any moment: a workflow survives a restart, its activities run once and their results replay, a
/// failed activity can be compensated for, and code that changes under a running instance is caught.
/// </summary>
public class WorkflowTests
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

    private static string Run(string program)
    {
        var result = Compilation.Compile([new SourceText(Orders + program, "main.piper")],
            new CompilationOptions { Plugins = Plugins.Value.Plugins, References = Plugins.Value.References });
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors) + "\n" + run.Output);
        return run.Output.Replace("\r\n", "\n");
    }

    private const string Orders = """
        using System;
        using System.Linq;
        using System.Threading.Tasks;
        using Piper.Runtime;
        using Piper.Workflows;

        record Order(id: string, total: decimal);
        record Receipt(id: string, payment: string, shipped: bool);

        /// How many times each activity really ran (not replayed).
        class Counts {
            public mut charged = 0;
            public mut shipped = 0;
        }

        /// An order: charged, then two days later shipped.
        fn Fulfil(engine: WorkflowEngine, counts: Counts) {
            engine.Register<Order, Receipt>("Fulfil", async (flow, order) => {
                let payment = await flow.Activity("Charge", async () => {
                    counts.charged += 1;
                    $"pay-{order.id}"
                });
                await flow.Delay(TimeSpan.FromDays(2));
                let shipped = await flow.Activity("Ship", async () => {
                    counts.shipped += 1;
                    true
                });
                Receipt(order.id, payment, shipped)
            });
        }

        """;

    [Fact]
    public void A_workflow_survives_a_restart_mid_wait_running_each_activity_once()
    {
        var output = Run("""
            async fn Restarted(sim: PiperSimulation) => Task<string> {
                let store = MemoryEventStore();
                let counts = Counts();
                let first = WorkflowEngine(store);
                Fulfil(first, counts);
                await first.StartAsync("Fulfil", "o-1", Order("o-1", 10m));
                await first.StartAsync("Fulfil", "o-1", Order("o-1", 10m));     // again: still one workflow
                await sim.Delay(TimeSpan.FromDays(1));
                await first.DisposeAsync();                                     // the process stops, a day into the wait

                let second = WorkflowEngine(store);                             // and another starts
                Fulfil(second, counts);
                let resumed = await second.ResumeAsync();
                let receipt = await second.ResultAsync<Receipt>("Fulfil", "o-1");
                let steps = await second.StepsAsync("Fulfil", "o-1");
                $"{resumed} | {receipt} | charged {counts.charged}, shipped {counts.shipped} | day {(int)sim.Elapsed.TotalDays} | {await second.StatusAsync("Fulfil", "o-1")}\n{string.Join("\n", steps)}"
            }

            fn Main() {
                Print(PiperSimulation.Run(1, Restarted));
            }
            """);
        Assert.Equal(
            "1 | Receipt { id = o-1, payment = pay-o-1, shipped = True } | charged 1, shipped 1 | day 2 | Completed\n" +
            "started Fulfil with {\"id\":\"o-1\",\"total\":10}\n" +
            "1: Charge gave \"pay-o-1\"\n" +
            "2: waiting until 2030-01-03 00:00:00\n" +
            "2: waited\n" +
            "3: Ship gave true\n" +
            "completed with {\"id\":\"o-1\",\"payment\":\"pay-o-1\",\"shipped\":true}\n", output);
    }

    [Fact]
    public void A_failed_activity_is_retried_then_thrown_in_the_workflow_which_can_compensate()
    {
        var output = Run("""
            class Tries { public mut n = 0; }

            async fn Decline(tries: Tries) => Task<string> {
                tries.n += 1;
                throw InvalidOperationException("declined")
            }

            async fn Pay(sim: PiperSimulation) => Task<string> {
                let engine = WorkflowEngine(MemoryEventStore());
                let tries = Tries();
                engine.Register<int, string>("Pay", async (flow, amount) => {
                    mut outcome = "";
                    try {
                        let payment = await flow.Activity("Charge", () => Decline(tries), ActivityOptions() { Attempts: 3 });
                        outcome = $"paid {payment}";
                    } catch (e: ActivityFailedException) {
                        await flow.Activity("Apologise", async () => {});
                        outcome = $"not paid: {e.Error}";
                    }
                    outcome
                });
                await engine.StartAsync("Pay", "p-1", 25);
                let result = await engine.ResultAsync<string>("Pay", "p-1");
                $"{result} | tried {tries.n} times | {string.Join(" / ", await engine.StepsAsync("Pay", "p-1"))}"
            }

            fn Main() {
                Print(PiperSimulation.Run(1, Pay));
            }
            """);
        Assert.Equal(
            "not paid: InvalidOperationException: declined | tried 3 times | started Pay with 25 / " +
            "1: Charge failed: InvalidOperationException: declined / 2: Apologise gave true / completed with \"not paid: InvalidOperationException: declined\"\n",
            output);
    }

    /// <summary>
    /// An activity giving a Result is retried on a transient Err, as on a throw; any other Err is its result at once,
    /// recorded like any value (it's the workflow's to handle, not a failure).
    /// </summary>
    [Fact]
    public void An_activity_is_retried_on_a_transient_err_but_not_on_another()
    {
        var output = Run("""
            enum PayError { [Transient] Busy, Declined }

            class Tries { public mut n = 0; }

            async fn Charge(tries: Tries, busyFor: int) => Task<Result<string, PayError>> {
                tries.n += 1;
                if tries.n <= busyFor { Err(PayError.Busy) } else { Err(PayError.Declined) }
            }

            async fn Pay(sim: PiperSimulation) => Task<string> {
                let engine = WorkflowEngine(MemoryEventStore());
                let tries = Tries();
                engine.Register<int, string>("Pay", async (flow, busyFor) => {
                    tries.n = 0;
                    let charged = await flow.Activity("Charge", () => Charge(tries, busyFor), ActivityOptions() { Attempts: 3 });
                    $"{charged} after {tries.n}"
                });
                await engine.StartAsync("Pay", "busy-once", 1);
                let once = await engine.ResultAsync<string>("Pay", "busy-once");
                await engine.StartAsync("Pay", "always-busy", 5);
                let always = await engine.ResultAsync<string>("Pay", "always-busy");
                $"{once} | {always}"
            }

            fn Main() {
                Print(PiperSimulation.Run(1, Pay));
            }
            """);
        Assert.Equal("Err(Declined) after 2 | Err(Busy) after 3\n", output);
    }

    [Fact]
    public void Code_changed_under_a_running_workflow_fails_it_saying_where()
    {
        var output = Run("""
            async fn Changed(sim: PiperSimulation) => Task<string> {
                let store = MemoryEventStore();
                let before = WorkflowEngine(store);
                Fulfil(before, Counts());
                await before.StartAsync("Fulfil", "o-2", Order("o-2", 5m));
                await sim.Delay(TimeSpan.FromHours(1));
                await before.DisposeAsync();

                // The new version checks stock before charging: a step the running instance never had.
                let after = WorkflowEngine(store);
                after.Register<Order, Receipt>("Fulfil", async (flow, order) => {
                    let inStock = await flow.Activity("CheckStock", async () => true);
                    let payment = await flow.Activity("Charge", async () => "pay");
                    Receipt(order.id, payment, inStock)
                });
                await after.ResumeAsync();
                mut said = "";
                try {
                    await after.ResultAsync<Receipt>("Fulfil", "o-2");
                } catch (e: WorkflowFailedException) {
                    said = e.Error;
                }
                said
            }

            fn Main() {
                Print(PiperSimulation.Run(1, Changed));
            }
            """);
        Assert.StartsWith("Replaying Fulfil 'o-2', step 1 asked for the activity CheckStock, but its history has the activity Charge.", output);
    }

    [Fact]
    public void Restarted_at_any_moment_a_workflow_ends_the_same_with_each_step_recorded_once()
    {
        var output = Run("""
            async fn AnyMoment(sim: PiperSimulation) {
                let store = MemoryEventStore();
                let counts = Counts();
                mut engine = WorkflowEngine(store);
                Fulfil(engine, counts);
                await engine.StartAsync("Fulfil", "o-3", Order("o-3", 7m));
                // Restarted twice, each time at a moment the run chooses (within the first three days).
                for restart in 0..<2 {
                    await sim.Delay(TimeSpan.FromMinutes(sim.Random.Next(3 * 24 * 60)));
                    await engine.DisposeAsync();
                    engine = WorkflowEngine(store);
                    Fulfil(engine, counts);
                    await engine.ResumeAsync();
                }
                let receipt = await engine.ResultAsync<Receipt>("Fulfil", "o-3");
                let steps = await engine.StepsAsync("Fulfil", "o-3");
                if receipt != Receipt("o-3", "pay-o-3", true) { throw InvalidOperationException($"ended as {receipt}"); }
                if steps.Count(s => s.Contains("Charge gave")) != 1 || steps.Count(s => s.Contains("Ship gave")) != 1 {
                    throw InvalidOperationException($"steps: {string.Join(" / ", steps)}");
                }
                if counts.charged < 1 || counts.shipped < 1 { throw InvalidOperationException("an activity never ran"); }
                await engine.DisposeAsync();
            }

            fn Main() {
                PiperSimulation.Explore(30, AnyMoment, 1);
                Print("held");
            }
            """);
        Assert.Equal("held\n", output);
    }
}
