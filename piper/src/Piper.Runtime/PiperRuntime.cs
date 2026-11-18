namespace Piper.Runtime;

/// <summary>
/// Where a program's time, randomness and background work come from: the real ones (<see cref="Real"/>), or a
/// simulation's (<see cref="PiperSimulation"/>), in which every choice they make comes from one seed, so a run can be
/// repeated exactly. Piper's runtime libraries (actors, clusters, failure policies) take it from <see cref="Current"/>,
/// which flows with the code (an <see cref="AsyncLocal{T}"/>), so whatever a simulation's body starts is simulated too.
/// </summary>
public abstract class PiperRuntime
{
    private static readonly AsyncLocal<PiperRuntime?> Ambient = new();

    /// <summary>The real clock, <see cref="System.Random.Shared"/>, and the thread pool.</summary>
    public static readonly PiperRuntime Real = new RealRuntime();

    /// <summary>The runtime the running code is in: a simulation's inside one, otherwise <see cref="Real"/>.</summary>
    public static PiperRuntime Current => Ambient.Value ?? Real;

    /// <summary>Makes <paramref name="runtime"/> current for the code that follows on this flow, until it's disposed.</summary>
    internal static IDisposable Use(PiperRuntime runtime)
    {
        var before = Ambient.Value;
        Ambient.Value = runtime;
        return new Restore(before);
    }

    /// <summary>The clock: time now, timestamps, delays (<c>Task.Delay(t, Time)</c>), timeouts and timers.</summary>
    public abstract TimeProvider Time { get; }

    /// <summary>Random numbers: seeded by a simulation, so its runs repeat.</summary>
    public abstract Random Random { get; }

    /// <summary>Whether this is a simulation.</summary>
    public virtual bool IsSimulated => false;

    /// <summary>
    /// How a <see cref="TaskCompletionSource{T}"/> that something waits on should run what follows it: asynchronously
    /// (on the thread pool, so an answer doesn't run its asker inside the answerer); in a simulation, as it comes,
    /// since the simulation posts every continuation to itself anyway, and the thread pool is outside it.
    /// </summary>
    public virtual TaskCreationOptions Continuations => TaskCreationOptions.RunContinuationsAsynchronously;

    /// <summary>
    /// Starts <paramref name="work"/> in the background (an actor's mailbox, a heartbeat): on the thread pool, or as one
    /// more thing a simulation may choose to run next.
    /// </summary>
    public abstract Task Start(Func<Task> work);

    /// <summary>
    /// A task done after <paramref name="delay"/> by this runtime's clock (cancelled with <paramref name="cancellation"/>):
    /// <c>Task.Delay</c>, or a simulation's timer. In a simulation, use this rather than <c>Task.Delay(t, Time)</c>, whose
    /// task finishes on the thread pool for anything but an <c>await</c> (<c>Task.WhenAny</c> of it), leaving the
    /// simulation's thread, so a run wouldn't repeat.
    /// </summary>
    public virtual Task Delay(TimeSpan delay, CancellationToken cancellation) => Task.Delay(delay, Time, cancellation);

    /// <summary>The same, not cancelled.</summary>
    public Task Delay(TimeSpan delay) => Delay(delay, CancellationToken.None);

    /// <summary>The time now, by this runtime's clock.</summary>
    public DateTime UtcNow => Time.GetUtcNow().UtcDateTime;

    private sealed class RealRuntime : PiperRuntime
    {
        public override TimeProvider Time => TimeProvider.System;
        public override Random Random => Random.Shared;
        public override Task Start(Func<Task> work) => Task.Run(work);
    }

    private sealed class Restore(PiperRuntime? before) : IDisposable
    {
        public void Dispose() => Ambient.Value = before;
    }
}
