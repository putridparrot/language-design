namespace Piper.Runtime;

/// <summary>
/// Runs code deterministically, as FoundationDB and TigerBeetle test themselves: on one thread, with a simulated clock,
/// every choice of what runs next (which actor's message, which timer, which continuation), and every fault (a message
/// lost, a node crashing), made from one seed. So a run is the same every time it's given the same seed, a race found in
/// one run of thousands (<see cref="Explore"/>) is repeated exactly by running its seed again (<see cref="Run"/>), and
/// simulated time races ahead, so a test of minutes of timeouts and heartbeats takes milliseconds.
/// <code>
/// PiperSimulation.Explore(500, async sim => {
///     let cluster = await SimulatedCluster.StartAsync(sim, 3, …);  // nodes on a simulated network (Piper.Actors.Remote)
///     …                                                          // drive it, inject faults, check what's true
/// });
/// </code>
/// A failing run is shrunk, as a property test's values are: its choices are a list (<see cref="PiperChoices"/>), in
/// which 0 is always the calmest choice (the oldest ready work runs next, no message is lost, the shortest latency, no
/// fault), and the list is made shorter and lower while the run still fails the same way. What's reported is the run
/// fewest choices away from the calmest that still fails, with its timeline: the race, without the noise.
/// Whatever the body starts takes its time, randomness and background work from <see cref="PiperRuntime.Current"/>,
/// which is the simulation: Piper's actors, clusters and failure policies do, and so does a program's own code (the
/// compiler routes its clock, delays and random numbers there). Code that starts a thread or does real I/O isn't
/// simulated, and makes runs differ; the timeline says when work arrived from outside.
/// </summary>
public sealed class PiperSimulation : PiperRuntime
{
    /// <summary>Simulated time starts here, the same in every run.</summary>
    public static readonly DateTimeOffset Epoch = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Choices one run may make: one at every step with a choice to make, and one for each fault and latency.</summary>
    private const int MaxChoices = 50_000_000;

    private readonly PiperChoices _choices;
    private readonly Random _random;
    private readonly Clock _clock;
    private readonly List<(SendOrPostCallback Callback, object? State)> _ready = [];
    private readonly PriorityQueue<Timer, (long Due, long Order)> _timers = new();
    private readonly Queue<string> _timeline = new();
    private readonly List<Func<Task>> _atEnd = [];
    private readonly Lock _gate = new();
    private Thread? _thread;
    private long _now;          // ticks since the epoch
    private long _timerOrder;
    private long _steps;
    private bool _outside;

    private PiperSimulation(int seed, SimulationOptions options, PiperChoices choices)
    {
        Seed = seed;
        Options = options;
        _choices = choices;
        _random = new ChoiceRandom(choices);
        _clock = new Clock(this);
    }

    /// <summary>The simulation the running code is in (a <c>simulate</c> test's): an InvalidOperationException outside one.</summary>
    public static PiperSimulation Running => PiperRuntime.Current as PiperSimulation
        ?? throw new InvalidOperationException("This isn't running in a simulation: a test marked `simulate` (or PiperSimulation.Run) runs in one.");

    /// <summary>The seed this run's choices come from: running it again repeats the run.</summary>
    public int Seed { get; }

    public SimulationOptions Options { get; }

    public override TimeProvider Time => _clock;

    /// <summary>Random numbers from the run's choices: a seed's, or a shrunk run's. 0 (as a choice) is the lowest.</summary>
    public override Random Random => _random;

    public override bool IsSimulated => true;

    public override TaskCreationOptions Continuations => TaskCreationOptions.None;

    /// <summary>Simulated time since the run started.</summary>
    public TimeSpan Elapsed => TimeSpan.FromTicks(_now);

    /// <summary>What happened, in order, each line stamped with its simulated time: what <see cref="Log"/> was told, and the
    /// simulation's own notes (a fault injected, a node crashing).</summary>
    public IReadOnlyList<string> Timeline => [.. _timeline];

    /// <summary>How many things the simulation has run: continuations, timers and started work.</summary>
    public long Steps => _steps;

    /// <summary>Adds a line to the timeline, stamped with the simulated time.</summary>
    public void Log(string line)
    {
        if (_timeline.Count >= Options.TimelineLimit)
            _timeline.Dequeue();
        _timeline.Enqueue($"{Elapsed.TotalSeconds,10:0.000}s  {line}");
    }

    /// <summary>
    /// True with probability <paramref name="probability"/>: one of the run's choices, false when the choice is 0, so
    /// shrinking a failing run takes away the faults it doesn't need.
    /// </summary>
    public bool Chance(double probability) => probability > 0 && (probability >= 1 || _random.NextDouble() >= 1 - probability);

    /// <summary>A time between <paramref name="least"/> and <paramref name="most"/>, chosen by the run (the least for choice 0).</summary>
    public TimeSpan Between(TimeSpan least, TimeSpan most) =>
        most <= least ? least : least + TimeSpan.FromTicks((long)(_random.NextDouble() * (most - least).Ticks));

    /// <summary>Waits <paramref name="time"/> of simulated time (which passes at once, unless something's to be done).</summary>
    public override Task Delay(TimeSpan delay, CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested)
            return Task.FromCanceled(cancellation);
        // Finished by the simulation's timer, or the cancellation, on its own thread: whatever follows runs as it comes
        // (no thread pool), so Task.WhenAny of it stays in the simulation.
        var done = new TaskCompletionSource();
        CancellationTokenRegistration registration = default;
        ITimer? timer = null;
        if (delay != Timeout.InfiniteTimeSpan)
            timer = _clock.CreateTimer(_ => { registration.Dispose(); done.TrySetResult(); }, null, delay < TimeSpan.Zero ? TimeSpan.Zero : delay, Timeout.InfiniteTimeSpan);
        if (cancellation.CanBeCanceled)
            registration = cancellation.Register(() => { timer?.Dispose(); done.TrySetCanceled(cancellation); });
        return done.Task;
    }

    /// <summary>Runs <paramref name="cleanUp"/> once the body has finished (stopping the nodes it started), so nothing it
    /// started is left waiting on a simulation that's gone.</summary>
    public void AtEnd(Func<Task> cleanUp) => _atEnd.Add(cleanUp);

    public override Task Start(Func<Task> work)
    {
        var done = new TaskCompletionSource();
        Enqueue(_ => _ = Relay(work, done), null);
        return done.Task;
    }

    private static async Task Relay(Func<Task> work, TaskCompletionSource done)
    {
        try
        {
            await work();
            done.TrySetResult();
        }
        catch (OperationCanceledException cancelled)
        {
            done.TrySetCanceled(cancelled.CancellationToken);
        }
        catch (Exception error)
        {
            done.TrySetException(error);
        }
    }

    /// <summary>
    /// Runs <paramref name="body"/> in a simulation seeded with <paramref name="seed"/>, to the end. If it fails, the run
    /// is shrunk (see the class's summary) and a <see cref="SimulationFailedException"/> thrown: the seed, which repeats
    /// it (shrinking included), why it failed, and the shrunk run's timeline. Blocks the calling thread, which the
    /// simulation runs on.
    /// </summary>
    public static void Run(int seed, Func<PiperSimulation, Task> body, SimulationOptions? options = null) =>
        Run<bool>(seed, async sim => { await body(sim); return true; }, options);

    /// <summary>The same, giving back what <paramref name="body"/> answers.</summary>
    public static T Run<T>(int seed, Func<PiperSimulation, Task<T>> body, SimulationOptions? options = null)
    {
        options ??= SimulationOptions.Default;
        var run = Attempt(seed, new PiperChoices([], new Random(seed), 100) { Limit = MaxChoices }, body, options);
        if (run.Failure is null)
            return run.Result!;
        var (shrunk, shrinks) = Shrink(run, choices => Attempt(seed, new PiperChoices(choices, null, 100) { Limit = MaxChoices }, body, options),
            options.ShrinkAttempts);
        throw new SimulationFailedException(seed, shrunk.Failure!, shrunk.Timeline) { Shrinks = shrinks, Departures = Departures(shrunk.Choices) };
    }

    /// <summary>
    /// Runs <paramref name="body"/> once, in a simulation whose choices come from <paramref name="choices"/>, without
    /// shrinking: for a simulated property test, whose values and run share their choices and are shrunk together.
    /// </summary>
    public static void Run(PiperChoices choices, Func<PiperSimulation, Task> body, SimulationOptions? options = null)
    {
        choices.Limit = MaxChoices;
        var run = Attempt<bool>(0, choices, async sim => { await body(sim); return true; }, options ?? SimulationOptions.Default);
        if (run.Failure is not null)
            throw new SimulationFailedException(null, run.Failure, run.Timeline);
    }

    /// <summary>
    /// Runs <paramref name="body"/> in <paramref name="runs"/> simulations, each with its own seed (from
    /// <paramref name="seed"/> on, or a random one), stopping at the first that fails, shrunk: a
    /// <see cref="SimulationFailedException"/> naming its seed, which <see cref="Run"/> repeats.
    /// </summary>
    public static void Explore(int runs, Func<PiperSimulation, Task> body, int? seed = null, SimulationOptions? options = null)
    {
        var first = seed ?? Random.Shared.Next();
        for (var i = 0; i < runs; i++)
            Run(unchecked(first + i), body, options);
    }

    /// <summary>
    /// For a simulated property test: the case's choices themselves, as its last value, which the simulation goes on
    /// drawing from (<see cref="Run(PiperChoices, Func{PiperSimulation, Task}, SimulationOptions?)"/>), so the values
    /// and the run shrink together.
    /// </summary>
    public static IPiperGen Choices { get; } = new ChoicesGen();

    private sealed class ChoicesGen : PiperGen<PiperChoices>
    {
        public override PiperChoices Generate(PiperChoices choices) => choices;
    }

    /// <summary>One run: what it answered, or why it failed; its timeline; and the choices it made.</summary>
    private sealed record Outcome<T>(T? Result, Exception? Failure, IReadOnlyList<string> Timeline, List<ulong> Choices);

    private static Outcome<T> Attempt<T>(int seed, PiperChoices choices, Func<PiperSimulation, Task<T>> body, SimulationOptions options)
    {
        var simulation = new PiperSimulation(seed, options, choices);
        var failure = simulation.Drive(body);
        return new Outcome<T>(failure is null ? (T)simulation._result! : default, failure, simulation.Timeline, [.. choices.Drawn]);
    }

    /// <summary>
    /// Makes a failing run's choices simpler while it still fails the same way (the same type of exception, with the
    /// same message but for its numbers), in at most <paramref name="budget"/> runs: calming its tail (every choice
    /// after a point made 0), then calming runs of choices, longest first, then single ones. Each run kept is fewer
    /// choices away from the calmest run, so shrinking ends, and it's the same every time for a seed.
    /// </summary>
    private static (Outcome<T> Best, int Shrinks) Shrink<T>(Outcome<T> best, Func<List<ulong>, Outcome<T>> run, int budget)
    {
        var failure = best.Failure!;
        int attempts = 0, shrinks = 0;

        bool Try(List<ulong> candidate)
        {
            if (attempts >= budget || !Simpler(candidate, best.Choices))
                return false;
            attempts++;
            var outcome = run(candidate);
            if (outcome.Failure is not { } failed || !Same(failed, failure) || !Simpler(outcome.Choices, best.Choices))
                return false;
            best = outcome;
            shrinks++;
            return true;
        }

        var improved = true;
        while (improved && attempts < budget)
        {
            improved = false;
            // The calmest tail that still fails: every choice after the first so many at 0.
            foreach (var fraction in (double[])[0, 1 / 16.0, 1 / 8.0, 1 / 4.0, 1 / 2.0, 3 / 4.0])
            {
                if (Try([.. best.Choices.Take((int)(best.Choices.Count * fraction))]))
                {
                    improved = true;
                    break;
                }
            }
            for (var size = Math.Max(1, best.Choices.Count / 2); size >= 1 && attempts < budget; size /= 2)
            {
                for (var start = 0; start + size <= best.Choices.Count && attempts < budget; start += size)
                {
                    if (best.Choices.Skip(start).Take(size).Any(c => c != 0) &&
                        Try([.. best.Choices.Take(start), .. Enumerable.Repeat(0UL, size), .. best.Choices.Skip(start + size)]))
                        improved = true;
                }
            }
            for (var i = 0; i < best.Choices.Count && attempts < budget; i++)
            {
                if (best.Choices[i] != 0 &&
                    (Try(new List<ulong>(best.Choices) { [i] = 0 }) || Try(new List<ulong>(best.Choices) { [i] = best.Choices[i] / 2 })))
                    improved = true;
            }
        }
        return (best, shrinks);
    }

    /// <summary>The same failure: the same type, and the same message once its numbers are set aside.</summary>
    private static bool Same(Exception failed, Exception failure) =>
        failed.GetType() == failure.GetType() &&
        System.Text.RegularExpressions.Regex.Replace(failed.Message, @"-?\d+", "#") ==
        System.Text.RegularExpressions.Regex.Replace(failure.Message, @"-?\d+", "#");

    /// <summary>How many of a run's choices weren't the calmest (0).</summary>
    private static int Departures(List<ulong> choices) => choices.Count(c => c != 0);

    /// <summary>Fewer choices that aren't 0, or as many and lower at the first that differs (trailing 0s aside).</summary>
    private static bool Simpler(List<ulong> candidate, List<ulong> best)
    {
        int a = Departures(candidate), b = Departures(best);
        if (a != b)
            return a < b;
        var count = Math.Max(candidate.Count, best.Count);
        for (var i = 0; i < count; i++)
        {
            ulong x = i < candidate.Count ? candidate[i] : 0, y = i < best.Count ? best[i] : 0;
            if (x != y)
                return x < y;
        }
        return false;
    }

    /// <summary>Runs the body to its end, and then what it left running: null, or why it failed.</summary>
    private Exception? Drive<T>(Func<PiperSimulation, Task<T>> body)
    {
        var context = SynchronizationContext.Current;
        _thread = Thread.CurrentThread;
        try
        {
            using (Use(this))
            {
                SynchronizationContext.SetSynchronizationContext(new Context(this));
                var started = Start(async () => { _result = await body(this); });
                var outcome = RunUntil(started);
                // Whatever the body started (nodes, heartbeats) is stopped, still in the simulation, before it's left.
                if (_atEnd.Count > 0)
                {
                    var cleanUp = Start(async () =>
                    {
                        for (var i = _atEnd.Count - 1; i >= 0; i--)
                            await _atEnd[i]();
                    });
                    RunUntil(cleanUp);
                }
                return outcome;
            }
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    private object? _result;

    /// <summary>Runs until <paramref name="task"/> finishes: null, or why the run failed.</summary>
    private Exception? RunUntil(Task task)
    {
        var limit = Options.MaxTime.Ticks;
        while (!task.IsCompleted)
        {
            (SendOrPostCallback Callback, object? State)? next;
            try
            {
                next = Next();
            }
            catch (PiperDiscardException)
            {
                return new InvalidOperationException($"The run made more than {MaxChoices:N0} choices: something may be looping without waiting.");
            }
            if (next is { } step)
            {
                if (++_steps > Options.MaxSteps)
                    return new InvalidOperationException($"The run took more than {Options.MaxSteps:N0} steps: something may be looping without waiting.");
                try
                {
                    // Each step under a context of its own, so a continuation it completes is posted, not run inside it.
                    SynchronizationContext.SetSynchronizationContext(new Context(this));
                    step.Callback(step.State);
                }
                catch (Exception error)
                {
                    return new InvalidOperationException("Something run by the simulation threw, outside any task.", error);
                }
                continue;
            }
            if (!Advance(limit))
            {
                if (_now >= limit)
                    return new TimeoutException($"The run hadn't finished after {Options.MaxTime} of simulated time.");
                return new InvalidOperationException(
                    "The run is stuck: nothing is left to do, and no timer is set, but it hasn't finished. It's waiting on something " +
                    "that won't happen (an ask with no timeout to a message that was lost), or on something outside the simulation.");
            }
        }
        return task.IsFaulted ? task.Exception!.InnerExceptions is [var only] ? only : task.Exception
            : task.IsCanceled ? new OperationCanceledException("The run was cancelled.") : null;
    }

    /// <summary>
    /// The next thing to run, chosen by the run among those ready (and timers due now), or null if none is: the one ready
    /// longest for choice 0, so a calm run runs things in the order they became ready.
    /// </summary>
    private (SendOrPostCallback Callback, object? State)? Next()
    {
        lock (_gate)
        {
            while (_timers.TryPeek(out var timer, out var at) && at.Due <= _now)
            {
                _timers.Dequeue();
                if (timer.Due(at.Order))
                    _ready.Add((static t => ((Timer)t!).Fire(), timer));
            }
            if (_ready.Count == 0)
                return null;
            var index = _ready.Count == 1 ? 0 : (int)_choices.Draw((ulong)_ready.Count);
            var step = _ready[index];
            _ready.RemoveAt(index);
            return step;
        }
    }

    /// <summary>Moves the clock to the next timer that's set, unless it's past <paramref name="limit"/>: whether it moved.</summary>
    private bool Advance(long limit)
    {
        lock (_gate)
        {
            while (_timers.TryPeek(out var timer, out var at))
            {
                if (!timer.Due(at.Order))
                {
                    _timers.Dequeue();
                    continue;
                }
                if (at.Due > limit)
                {
                    _now = limit;
                    return false;
                }
                _now = Math.Max(_now, at.Due);
                return true;
            }
            return false;
        }
    }

    private void Enqueue(SendOrPostCallback callback, object? state)
    {
        lock (_gate)
        {
            if (_thread is not null && Thread.CurrentThread != _thread && !_outside)
            {
                _outside = true;
                Log("note: work arrived from outside the simulation (a real thread or I/O), so this run may not repeat exactly");
            }
            _ready.Add((callback, state));
        }
    }

    private void Schedule(Timer timer, long due)
    {
        lock (_gate)
        {
            var order = ++_timerOrder;
            timer.Order = order;
            _timers.Enqueue(timer, (due, order));
        }
    }

    /// <summary>Posts continuations to the simulation; a new one for each step (see <see cref="RunUntil"/>).</summary>
    private sealed class Context(PiperSimulation simulation) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => simulation.Enqueue(callback, state);

        public override void Send(SendOrPostCallback callback, object? state) => callback(state);

        public override SynchronizationContext CreateCopy() => this;
    }

    private sealed class Clock(PiperSimulation simulation) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Epoch + simulation.Elapsed;

        // From the epoch, not zero, as real timestamps are never near zero (code may take 0 to mean "never").
        public override long GetTimestamp() => Epoch.UtcTicks + simulation._now;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(simulation, callback, state, ExecutionContext.Capture());
            timer.Change(dueTime, period);
            return timer;
        }
    }

    /// <summary>A timer on the simulated clock: it fires when the simulation chooses to run it, once its time comes.</summary>
    private sealed class Timer(PiperSimulation simulation, TimerCallback callback, object? state, ExecutionContext? context) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;
        private bool _disposed;

        /// <summary>Which scheduling of it is current: an earlier one, superseded by a <see cref="Change"/>, doesn't fire.</summary>
        public long Order { get; set; }

        public bool Due(long order) => !_disposed && order == Order;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (_disposed)
                return false;
            _period = period;
            Order = 0;
            if (dueTime != Timeout.InfiniteTimeSpan)
                simulation.Schedule(this, simulation._now + Math.Max(0, dueTime.Ticks));
            return true;
        }

        public void Fire()
        {
            if (_period > TimeSpan.Zero && _period != Timeout.InfiniteTimeSpan)
                simulation.Schedule(this, simulation._now + _period.Ticks);
            if (context is null)
                callback(state);
            else
                ExecutionContext.Run(context, static s => { var (c, v) = ((TimerCallback, object?))s!; c(v); }, (callback, state));
        }

        public void Dispose() => _disposed = true;

        public ValueTask DisposeAsync()
        {
            _disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// How far a simulation may go before it's taken to have failed, how much of its timeline it keeps (the latest lines),
/// and how many runs shrinking a failure may take.
/// </summary>
public sealed record SimulationOptions(TimeSpan MaxTime, long MaxSteps, int TimelineLimit = 10_000, int ShrinkAttempts = 500)
{
    /// <summary>Seven days of simulated time (waiting days costs nothing when nothing else is happening), ten million
    /// steps, and 500 runs to shrink a failure.</summary>
    public static readonly SimulationOptions Default = new(TimeSpan.FromDays(7), 10_000_000);
}

/// <summary>
/// A simulated run that failed: its seed, which repeats it (shrinking included), why, how far it was shrunk, and what
/// happened up to the failure in the shrunk run.
/// </summary>
public sealed class SimulationFailedException(int? seed, Exception error, IReadOnlyList<string> timeline) : Exception(error.Message, error)
{
    /// <summary>The seed the run started from (0 for a run whose choices a property test made: its seed repeats it).</summary>
    public int Seed => seed ?? 0;

    /// <summary>Whether the run came from a seed of its own, which <see cref="PiperSimulation.Run(int, Func{PiperSimulation, Task}, SimulationOptions?)"/> repeats.</summary>
    public bool HasSeed => seed is not null;

    public IReadOnlyList<string> Timeline => timeline;

    /// <summary>How many times the failing run was made simpler.</summary>
    public int Shrinks { get; init; }

    /// <summary>How many of the shrunk run's choices weren't the calmest: the scheduling and faults the failure needs.</summary>
    public int Departures { get; init; }

    public override string Message
    {
        get
        {
            var shown = timeline.Count > 60 ? ["...", .. timeline.Skip(timeline.Count - 60)] : timeline;
            var shrunk = Shrinks == 0 ? "" : $" (shrunk {Shrinks} {(Shrinks == 1 ? "time" : "times")}, to {Departures} {(Departures == 1 ? "choice" : "choices")} from the calmest run)";
            return (HasSeed ? $"The simulation with seed {Seed} failed{shrunk}: " : $"The simulation failed{shrunk}: ") +
                InnerException!.Message + "\n" +
                (HasSeed ? $"Repeat it with PiperSimulation.Run({Seed}, ...).\n" : "") +
                (shown.Count == 0 ? "" : "Its timeline (simulated time):\n" + string.Join("\n", shown));
        }
    }
}

/// <summary>
/// A <see cref="Random"/> whose numbers are a run's choices (<see cref="PiperChoices"/>): a seed's when it's explored,
/// a shrunk list's when it's shrunk. Choice 0 is each method's lowest answer.
/// </summary>
internal sealed class ChoiceRandom(PiperChoices choices) : Random
{
    private const ulong DoubleBits = 1UL << 53;

    public override int Next() => (int)choices.Draw(int.MaxValue);

    public override int Next(int maxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxValue);
        return maxValue <= 1 ? 0 : (int)choices.Draw((ulong)maxValue);
    }

    public override int Next(int minValue, int maxValue)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minValue, maxValue);
        return maxValue - minValue <= 1 ? minValue : (int)(minValue + (long)choices.Draw((ulong)((long)maxValue - minValue)));
    }

    public override long NextInt64() => (long)choices.Draw(long.MaxValue);

    public override long NextInt64(long maxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxValue);
        return maxValue <= 1 ? 0 : (long)choices.Draw((ulong)maxValue);
    }

    public override long NextInt64(long minValue, long maxValue)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minValue, maxValue);
        return maxValue - minValue <= 1 ? minValue : unchecked(minValue + (long)choices.Draw(unchecked((ulong)(maxValue - minValue))));
    }

    public override double NextDouble() => choices.Draw(DoubleBits) / (double)DoubleBits;

    public override float NextSingle() => choices.Draw(1UL << 24) / (float)(1 << 24);

    public override void NextBytes(byte[] buffer) => NextBytes(buffer.AsSpan());

    public override void NextBytes(Span<byte> buffer)
    {
        for (var i = 0; i < buffer.Length; i++)
            buffer[i] = (byte)choices.Draw(256);
    }

    protected override double Sample() => NextDouble();
}
