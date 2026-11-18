namespace Piper.Tests;

/// <summary>
/// <c>test "name" simulate { … }</c>: each run in a deterministic simulation, over many seeds, with the program's own
/// clock, delays, background work and random numbers routed to it by the compiler; a failure names its seed, which
/// <c>--seed</c> repeats, with the end of its timeline.
/// </summary>
public class SimulateTestTests
{
    private static TestRunResult Run(string program, int? seed = 1)
    {
        var result = Compilation.Compile(program, "sim.piper", new CompilationOptions(LineDirectives: true, IncludeTests: true));
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        return Runner.RunTests(result.Files, references: result.References, seed: seed);
    }

    /// <summary>
    /// The program's own <c>DateTime.UtcNow</c>, <c>Task.Delay</c>, <c>Guid.NewGuid</c> and <c>Random()</c> are the
    /// simulation's: hours pass at once, the clock starts where every run starts, and a seed's choices repeat.
    /// </summary>
    [Fact]
    public void A_simulated_tests_clock_delays_and_randomness_are_simulated()
    {
        var run = Run("""
            using System;
            using System.Threading.Tasks;

            async test "hours pass at once" simulate runs 20 {
                let start = DateTime.UtcNow;
                await Task.Delay(TimeSpan.FromHours(2));
                AssertEqual(TimeSpan.FromHours(2), DateTime.UtcNow - start);
                AssertEqual(2030, start.Year);
            }

            async test "background work is simulated too" simulate {
                let answer = await Task.Run(async () => {
                    await Task.Delay(TimeSpan.FromMinutes(5));
                    42
                });
                AssertEqual(42, answer);
                AssertEqual(TimeSpan.FromMinutes(5), PiperSimulation.Running.Elapsed);
            }

            fn Choices() => string = $"{Guid.NewGuid()} {Random().Next(1000)} {Random.Shared.Next(1000)}";

            test "a seed's choices repeat" {
                let first = PiperSimulation.Run(5, (s: PiperSimulation) => Task.FromResult(Choices()));
                let again = PiperSimulation.Run(5, (s: PiperSimulation) => Task.FromResult(Choices()));
                let other = PiperSimulation.Run(6, (s: PiperSimulation) => Task.FromResult(Choices()));
                AssertEqual(first, again);
                Assert(first != other);
            }
            """);
        Assert.Empty(run.Errors);
        Assert.All(run.Results, r => Assert.True(r.Failure is null, $"{r.Name}: {r.Failure}"));
        Assert.Equal(3, run.Results.Length);
    }

    /// <summary>
    /// Task.WhenAny of a delay stays in the simulation: .NET's Task.Delay(t, TimeProvider) finishes its task on the thread
    /// pool for anything but an await, which would make runs differ; a simulation's delays (what the program's
    /// Task.Delay is routed to) are its own.
    /// </summary>
    [Fact]
    public void Racing_a_delay_stays_in_the_simulation_and_repeats()
    {
        var run = Run("""
            using System;
            using System.Linq;
            using System.Threading.Tasks;

            async fn Race(sim: PiperSimulation) => Task<string> {
                mut wins = "";
                for i in 0..<50 {
                    let signal = TaskCompletionSource<bool>();
                    let fire = Task.Run(async () => {
                        await Task.Delay(sim.Random.Next(10));
                        signal.TrySetResult(true);
                    });
                    let first = await Task.WhenAny(signal.Task, Task.Delay(5));
                    wins += if signal.Task.IsCompleted { "s" } else { "d" };
                    await fire;
                }
                $"{wins} {sim.Timeline.Any(line => line.Contains("outside the simulation"))}"
            }

            test "racing a delay repeats, inside the simulation" {
                let first = PiperSimulation.Run(3, Race);
                AssertEqual(first, PiperSimulation.Run(3, Race));
                Assert(first.EndsWith(" False"), first);
                Assert(first.Contains("s") && first.Contains("d"), first);
            }
            """);
        Assert.Empty(run.Errors);
        Assert.All(run.Results, r => Assert.True(r.Failure is null, $"{r.Name}: {r.Failure}"));
    }

    private const string LostUpdate = """
        using System.Threading.Tasks;

        class Counter {
            public mut value = 0;
        }

        /// Reads, lets others run, then writes what it read plus one: two at once can lose an update.
        async fn Bump(counter: Counter) {
            let seen = counter.value;
            await Task.Yield();
            counter.value = seen + 1;
        }

        async test "no update is lost" simulate {
            let counter = Counter();
            PiperSimulation.Running.Log("bumping twice");
            await Task.WhenAll(Task.Run(() => Bump(counter)), Task.Run(() => Bump(counter)));
            AssertEqual(2, counter.value);
        }
        """;

    /// <summary>
    /// A race only some orders of events show is found by trying seeds; the failure names the seed, which --seed
    /// repeats (failing at once, the same way), with the timeline.
    /// </summary>
    [Fact]
    public void A_race_is_found_and_its_seed_repeats_it()
    {
        var failure = Assert.Single(Run(LostUpdate).Results).Failure;
        Assert.NotNull(failure);
        // It fails in the calmest run too (both read before either writes): shrinking says so.
        var match = System.Text.RegularExpressions.Regex.Match(failure,
            @"^Simulation failed \(seed (\d+); piper test --seed \1 repeats it(, shrunk \d+ times? to 0 choices from the calmest run)?\): Expected 2 but got 1");
        Assert.True(match.Success, failure);
        Assert.Contains("bumping twice", failure);
        Assert.Contains("sim.piper:line", failure);

        var seed = int.Parse(match.Groups[1].Value);
        var again = Assert.Single(Run(LostUpdate, seed).Results).Failure;
        Assert.Equal(failure, again);
    }

    /// <summary>
    /// A failing run is shrunk to the fewest choices away from the calmest run that still fail it: here, of a seed's run
    /// losing several of twenty messages (each latency and every order of events chosen at random), to one lost message,
    /// which its timeline shows.
    /// </summary>
    [Fact]
    public void A_failing_run_is_shrunk_to_what_causes_it()
    {
        var result = Compilation.Compile("""
            using System;
            using System.Linq;
            using System.Threading.Tasks;
            using Piper.Actors.Remote;
            using Piper.Runtime;

            actor Counter() {
                mut count = 0;
                on Add() { count += 1; }
                on Total() => int { count }
            }

            async fn Twenty(sim: PiperSimulation) {
                let network = SimulatedNetwork(sim, NetworkFaults.Lossy(0.2));
                let a = await PiperNode.StartAsync(network.Transport());
                let b = await PiperNode.StartAsync(network.Transport());
                Counter.Spawn(b, "counter");
                let counter = Counter.Connect(a, b.Address, "counter").WithTimeout(TimeSpan.FromSeconds(5));
                for i in 0..<20 {
                    counter.Add();
                }
                network.Faults = NetworkFaults.None;
                await sim.Delay(TimeSpan.FromSeconds(1));
                AssertEqual(20, await counter.Total());
            }

            fn Main() {
                try {
                    PiperSimulation.Run(3, Twenty);
                } catch (e: SimulationFailedException) {
                    Print(e.Departures);
                    Print(e.Shrinks > 0);
                    Print(e.Timeline.Count(line => line.Contains("LOST")));
                    Print(e.InnerException?.Message ?? "");
                }
            }
            """, "shrink.piper", new CompilationOptions(LineDirectives: true) with { Plugins = RemoteActors.Value.Plugins, References = RemoteActors.Value.References });
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        Assert.Equal("1\nTrue\n1\nExpected 20 but got 19\n", run.Output.Replace("\r\n", "\n"));
    }

    private static readonly Lazy<LoadedProject> RemoteActors = new(() =>
    {
        var (project, errors) = LoadedProject.Load(Path.GetFullPath(Path.Combine(TestsDirectory(), "..", "..", "samples", "remote-actors")));
        Assert.True(errors.IsEmpty, string.Join("\n", errors.Select(e => e.Format())));
        return project!;
    });

    private static string TestsDirectory([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "") => Path.GetDirectoryName(thisFile)!;

    /// <summary>
    /// What a simulated test reaches that its simulation can't run (blocking its one thread, the real clock where it isn't
    /// routed, real threads) is a warning, PIP0512, at the call, naming the test and the functions that lead there. The
    /// same code reached only by tests that aren't simulated isn't.
    /// </summary>
    [Fact]
    public void What_a_simulated_test_reaches_that_cant_be_simulated_is_a_warning()
    {
        var result = Compilation.Compile("""
            using System;
            using System.Diagnostics;
            using System.Threading;
            using System.Threading.Tasks;

            fn Pause() {
                Thread.Sleep(10);
            }

            fn Measure() => long {
                let watch = Stopwatch.StartNew();
                Pause();
                watch.ElapsedMilliseconds
            }

            fn Answer() => int = Task.FromResult(42).Result;

            class Service {
                fn Work() => long = Measure();
            }

            test "real time" {
                Pause();
            }

            test "simulated" simulate {
                Service().Work();
                Answer();
                let fine = Stopwatch.GetTimestamp();
            }
            """, "warn.piper", new CompilationOptions(IncludeTests: true));
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var warnings = result.Diagnostics.Where(d => d.Code == "PIP0512").Select(d => d.Message).ToList();
        Assert.Equal(3, warnings.Count);
        Assert.Contains(warnings, w => w.StartsWith("The simulated test 'simulated' reaches this (through Work → Measure → Pause), which its simulation can't run: Thread.Sleep"));
        Assert.Contains(warnings, w => w.StartsWith("The simulated test 'simulated' reaches this (through Work → Measure), which its simulation can't run: a Stopwatch"));
        Assert.Contains(warnings, w => w.StartsWith("The simulated test 'simulated' reaches this (through Answer), which its simulation can't run: this waits by blocking"));
    }

    /// <summary>A simulated property test: each case's run draws on the choices its values came from, so they shrink together.</summary>
    [Fact]
    public void A_simulated_property_test_shrinks_its_values_and_run_together()
    {
        var run = Run("""
            using System;
            using System.Threading.Tasks;

            async test "delays add up" for (minutes: int in 0..60) simulate {
                await Task.Delay(TimeSpan.FromMinutes(minutes));
                AssertEqual(minutes, (int)PiperSimulation.Running.Elapsed.TotalMinutes);
            }

            async test "short waits" for (minutes: int in 0..60) simulate {
                await Task.Delay(TimeSpan.FromMinutes(minutes));
                Assert(PiperSimulation.Running.Elapsed < TimeSpan.FromMinutes(30), "waited too long");
            }
            """);
        Assert.Empty(run.Errors);
        Assert.Null(run.Results[0].Failure);
        var failure = run.Results[1].Failure;
        Assert.NotNull(failure);
        Assert.Contains("minutes = 30", failure);
        Assert.Contains("simulation = (the simulated run, shrunk with the values)", failure);
        Assert.Contains("waited too long", failure);
    }
}
