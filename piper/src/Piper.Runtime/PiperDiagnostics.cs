using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Piper.Runtime;

/// <summary>
/// What the runtime reports about failures it carries on through (a snapshot that wasn't kept), with nothing but
/// .NET's own diagnostics, so Piper.Runtime needs no logging library:
/// <list type="bullet">
/// <item>a warning, through <see cref="Log"/>: to the program's <c>ILogger</c> in a server or host (which set it as
/// they're built), and to standard error otherwise;</item>
/// <item>the exception, on the span it happened in (<see cref="Activity.Current"/>), for a trace to show;</item>
/// <item>a count, on the <c>Piper.Events</c> meter, which the telemetry plugin collects with Piper's others.</item>
/// </list>
/// </summary>
public static class PiperDiagnostics
{
    /// <summary>The meter's name, and the category of what's logged: <c>Piper.Events</c>.</summary>
    public const string Name = "Piper.Events";

    private static readonly Meter Meter = new(Name);

    private static readonly Counter<long> SnapshotFailures = Meter.CreateCounter<long>(
        "piper.actor.snapshot.failures", description: "Snapshots of persisted actors' state that their store failed to keep.");

    /// <summary>
    /// Where reports go: the message, and the exception for a warning (none for information). Standard error unless a
    /// server or host has set it to its logger (or a program or test sets its own).
    /// </summary>
    public static Action<string, Exception?> Log { get; set; } = WriteToStandardError;

    private static void WriteToStandardError(string message, Exception? exception) =>
        Console.Error.WriteLine(exception is null ? $"info: {Name}: {message}" : $"warn: {Name}: {message} {exception.GetType().Name}: {exception.Message}");

    /// <summary>
    /// A snapshot of <paramref name="stream"/> wasn't kept. It's counted every time, and logged when
    /// <paramref name="log"/> (the first failure since the actor's snapshots were last kept), so a store that's down
    /// doesn't log once for every message.
    /// </summary>
    public static void SnapshotFailed(string stream, int version, Exception exception, bool log)
    {
        var slash = stream.IndexOf('/');
        SnapshotFailures.Add(1, new KeyValuePair<string, object?>("actor", slash < 0 ? stream : stream[..slash]));
        Activity.Current?.AddException(exception);
        if (log)
            Log($"A snapshot of {stream} (after {version} events) wasn't kept; its events are, so nothing is lost, but it will " +
                "replay more of them as it starts. It tries again after its next message.", exception);
    }

    /// <summary>The actor's snapshots are kept again, after failing.</summary>
    public static void SnapshotsRecovered(string stream, int version) =>
        Log($"Snapshots of {stream} are kept again (after {version} events).", null);
}
