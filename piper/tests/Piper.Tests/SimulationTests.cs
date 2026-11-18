using System.Runtime.CompilerServices;
using Piper.Text;

namespace Piper.Tests;

/// <summary>
/// Deterministic simulation (Piper.Runtime's PiperSimulation, Piper.Actors.Remote's SimulatedCluster): actors, clusters
/// and their network on one thread with a simulated clock, every choice from one seed. A seed's run is the same every
/// time; simulated minutes pass in milliseconds; exploring many seeds finds a race, and its seed repeats it.
/// </summary>
public class SimulationTests
{
    private static readonly Lazy<LoadedProject> Project = new(() =>
    {
        var (project, errors) = LoadedProject.Load(SampleDirectory());
        Assert.True(errors.IsEmpty, string.Join("\n", errors.Select(e => e.Format())));
        return project!;
    });

    private static string SampleDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "samples", "remote-actors"));

    private static string Run(string program)
    {
        var result = Compilation.Compile([new SourceText(program, "main.piper")],
            new CompilationOptions { Plugins = Project.Value.Plugins, References = Project.Value.References });
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors) + "\n" + run.Output);
        return run.Output.Replace("\r\n", "\n");
    }

    private const string Tallies = """
        using System;
        using System.Collections.Generic;
        using System.Diagnostics;
        using System.Linq;
        using System.Threading.Tasks;
        using Piper.Actors.Remote;
        using Piper.Runtime;

        actor Tally(id: string) persisted by id {
            mut total = 0;
            event Added(amount: int) { total += amount; }
            on Add(amount: int) => int { emit Added(amount); total }
            on Total() => int { total }
        }

        /// Three nodes in a simulation, sharing one store of events, each hosting tallies.
        async fn Start(sim: PiperSimulation, store: IEventStore, faults: NetworkFaults) => Task<SimulatedCluster> =
            await SimulatedCluster.StartAsync(sim, 3, node => {
                Tally.HostInCluster(node.Cluster, (n, name, key) => Tally.Spawn(n, name, store, key));
            }, faults);

        """;

    /// <summary>
    /// A seed's run is the same every time, to the last message in its timeline; another seed's differs. A crashed node
    /// is found down after five seconds of simulated time, which passes in a moment.
    /// </summary>
    [Fact]
    public void A_seeds_run_is_the_same_every_time_and_simulated_time_races_ahead()
    {
        var output = Run(Tallies + """
            async fn Scenario(sim: PiperSimulation) => Task<string> {
                let store = MemoryEventStore();
                let cluster = await Start(sim, store, NetworkFaults.None);
                let keys = [...Enumerable.Range(0, 8).Select(i => $"key-{i}")];
                await Task.WhenAll(keys.Select(key => Tally.InCluster(cluster[0], key).Add(1)));
                await cluster.CrashAsync(1);
                // Every node must notice: until one does, it sends to the crashed node, and those asks time out.
                let found = await cluster.SettleAsync();
                let totals = await Task.WhenAll(keys.Select(key => Tally.InCluster(cluster[2], key).Add(1)));
                sim.Log($"totals {string.Join(",", totals)}, found down: {found}, after {sim.Elapsed.TotalSeconds >= 5}");
                string.Join("\n", sim.Timeline)
            }

            fn Main() {
                let clock = Stopwatch.StartNew();
                let first = PiperSimulation.Run(7, Scenario);
                let again = PiperSimulation.Run(7, Scenario);
                let other = PiperSimulation.Run(8, Scenario);
                Print(first == again);
                Print(first != other);
                Print(first.Contains("totals 2,2,2,2,2,2,2,2, found down: True, after True"));
                Print(first.Contains("sim:2: crashed"));
                // Nothing left the simulation for a real thread, which would make runs differ.
                Print(!first.Contains("outside the simulation"));
                Print(clock.Elapsed.TotalSeconds < 20);
            }
            """);
        Assert.Equal("True\nTrue\nTrue\nTrue\nTrue\nTrue\n", output);
    }

    /// <summary>
    /// Asks to an actor whose node crashes fail once the crash is noticed (a NodeDownException, a TimeoutException),
    /// not at their timeout: here a minute, while noticing takes about five seconds. Asked again, it's on its new owner.
    /// </summary>
    [Fact]
    public void Asks_to_a_crashed_node_fail_when_its_noticed_not_at_their_timeout()
    {
        var output = Run(Tallies + """
            async fn Crash(sim: PiperSimulation) => Task<string> {
                let cluster = await Start(sim, MemoryEventStore(), NetworkFaults.None);
                let keys = [...Enumerable.Range(0, 12).Select(i => $"key-{i}")];
                await Task.WhenAll(keys.Select(key => Tally.InCluster(cluster[0], key).Add(1)));
                let doomed = [...keys.Where(key => cluster[0].Cluster.OwnerOf($"Tally/{key}") == cluster.Addresses[1])];
                await cluster.CrashAsync(1);
                // All at once, while node 0 still takes node 1 to be up: each waits on the crashed node.
                let waited = await Task.WhenAll(doomed.Select(key => AskUntilDown(sim, cluster, key)));
                let again = await Tally.InCluster(cluster[0], doomed[0]).Add(1);
                $"{doomed.Count > 0} {waited.All(s => s > 0)} {waited.All(s => s > 3 && s < 10)} {again}"
            }

            /// How long the ask waited before failing because its node was down, in seconds; 0 if it didn't.
            async fn AskUntilDown(sim: PiperSimulation, cluster: SimulatedCluster, key: string) => Task<double> {
                let asked = sim.Elapsed;
                mut waited = 0.0;
                try {
                    await Tally.InCluster(cluster[0], key).WithTimeout(TimeSpan.FromMinutes(1)).Add(1);
                } catch (e: NodeDownException) {
                    waited = (sim.Elapsed - asked).TotalSeconds;
                }
                waited
            }

            fn Main() {
                Print(PiperSimulation.Run(3, Crash));
            }
            """);
        Assert.Equal("True True True 2\n", output);
    }

    /// <summary>
    /// Faults the run chooses (StartChaos: crashes, restarts, a node cut off, the network healing) find that a count an
    /// actor keeps only in memory is lost when its node goes; and the failure shrinks to the one fault that does it.
    /// </summary>
    [Fact]
    public void The_runs_own_faults_find_a_lost_count_and_shrink_to_the_one_that_loses_it()
    {
        var output = Run("""
            using System;
            using System.Linq;
            using System.Threading.Tasks;
            using Piper.Actors.Remote;
            using Piper.Runtime;

            /// Not persisted: its count is in memory only.
            actor Visits(id: string) {
                mut total = 0;
                on Add() => int { total += 1; total }
                on Total() => int { total }
            }

            async fn Forgetful(sim: PiperSimulation) {
                let cluster = await SimulatedCluster.StartAsync(sim, 3, node => {
                    Visits.HostInCluster(node.Cluster, (n, name, key) => Visits.Spawn(n, name, key));
                }, NetworkFaults.None);
                cluster.StartChaos();
                mut answered = 0;
                for i in 0..<40 {
                    await sim.Delay(TimeSpan.FromMilliseconds(500));
                    let from = Enumerable.Range(0, 3).First(n => cluster.IsUp(n));
                    try {
                        await Visits.InCluster(cluster[from], "home").Add();
                        answered += 1;
                    } catch (e: Exception) {
                    }
                }
                Assert(await cluster.CalmAsync(), "the cluster settled");
                let total = await Visits.InCluster(cluster[0], "home").Total();
                Assert(total >= answered, "no answered visit is forgotten");
            }

            fn Main() {
                try {
                    PiperSimulation.Explore(50, Forgetful, 1);
                    Print("held");
                } catch (e: SimulationFailedException) {
                    Print(e.Message.Contains("no answered visit is forgotten"));
                    Print(e.Timeline.Count(line => line.Contains(": crashed") || line.Contains("cut off")));
                }
            }
            """);
        Assert.Equal("True\n1\n", output);
    }

    /// <summary>
    /// A check-then-act race (two spenders each asking the balance, then withdrawing) is found by exploring seeds, each
    /// trying another order of the actors' messages; the failing seed repeats it exactly, and a seed that passes passes.
    /// </summary>
    [Fact]
    public void Exploring_seeds_finds_a_race_and_its_seed_repeats_it()
    {
        var output = Run("""
            using System;
            using System.Threading.Tasks;
            using Piper.Actors.Remote;
            using Piper.Runtime;

            actor Account(opening: int) {
                mut balance = opening;
                on Balance() => int { balance }
                on Withdraw(amount: int) => int { balance -= amount; balance }
            }

            async fn Spend(account: Account, amount: int) {
                if await account.Balance() >= amount {
                    await account.Withdraw(amount);
                }
            }

            async fn TwoSpenders(sim: PiperSimulation) {
                let node = await PiperNode.StartAsync(SimulatedNetwork(sim).Transport());
                let account = Account.Spawn(node, "account", 10);
                await Task.WhenAll(Spend(account, 10), Spend(account, 10));
                let left = await account.Balance();
                if left < 0 {
                    throw InvalidOperationException($"overdrawn: {left}");
                }
            }

            fn Main() {
                try {
                    PiperSimulation.Explore(200, TwoSpenders, 1);
                    Print("not found");
                } catch (e: SimulationFailedException) {
                    Print(e.Message.Contains("overdrawn: -10"));
                    try {
                        PiperSimulation.Run(e.Seed, TwoSpenders);
                        Print("didn't repeat");
                    } catch (again: SimulationFailedException) {
                        Print($"repeated: {again.Message == e.Message}");
                    }
                    Print(e.Message.Contains($"PiperSimulation.Run({e.Seed}"));
                }
            }
            """);
        Assert.Equal("True\nrepeated: True\nTrue\n", output);
    }

    /// <summary>
    /// Persisted actors in a cluster, over many seeds, on a network losing messages, with a node crashing, the network
    /// splitting and healing, and the node restarting: however it runs, once the cluster settles, each tally's total is
    /// what its saved events say, and at least every add that was answered.
    /// </summary>
    [Fact]
    public void Persisted_actors_keep_their_counts_through_faults_in_every_run()
    {
        var output = Run(Tallies + """
            async fn TryAdd(tally: Tally) => Task<bool> {
                mut answered = false;
                try {
                    await tally.Add(1);
                    answered = true;
                } catch (e: Exception) {
                }
                answered
            }

            async fn Faults(sim: PiperSimulation, unanswered: List<int>) {
                let store = MemoryEventStore();
                let cluster = await Start(sim, store, NetworkFaults.Lossy(0.05));
                let keys = ["a", "b", "c", "d"];
                let answered = Dictionary<string, int>();
                for key in keys { answered[key] = 0; }
                for round in 0..<6 {
                    match round {
                        1 => await cluster.CrashAsync(1),
                        2 => cluster.Partition(0),
                        3 => cluster.Heal(),
                        4 => await cluster.RestartAsync(1),
                        _ => {},
                    }
                    for key in keys {
                        let from = sim.Random.Next(3);
                        if cluster.IsUp(from) && await TryAdd(Tally.InCluster(cluster[from], key)) {
                            answered[key] += 1;
                        }
                    }
                    await sim.Delay(TimeSpan.FromSeconds(2));
                }
                cluster.Network.Faults = NetworkFaults.None;
                if !(await cluster.SettleAsync()) {
                    throw InvalidOperationException("the cluster didn't settle");
                }
                for key in keys {
                    let total = await Tally.InCluster(cluster[0], key).Total();
                    let saved = (await store.LoadAsync($"Tally/{key}")).Count;
                    sim.Log($"{key}: total {total}, saved {saved}, answered {answered[key]}");
                    unanswered.Add(total - answered[key]);
                    if total != saved || total < answered[key] {
                        throw InvalidOperationException($"{key}: total {total}, saved {saved}, answered {answered[key]}");
                    }
                }
            }

            fn Main() {
                let unanswered = List<int>();
                PiperSimulation.Explore(25, sim => Faults(sim, unanswered), 100);
                Print("held");
                // The faults did something: some adds were made but their answers lost.
                Print(unanswered.Sum() > 0);
            }
            """);
        Assert.Equal("held\nTrue\n", output);
    }
}
