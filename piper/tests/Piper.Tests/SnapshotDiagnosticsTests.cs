using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Piper.Runtime;

namespace Piper.Tests;

/// <summary>
/// A snapshot its store fails to keep doesn't fail the actor's message (its events are kept), but isn't silent: it's
/// logged (the first failure, and the recovery, not every message while the store is down) and counted every time.
/// It replaces the process's <see cref="PiperDiagnostics.Log"/>, which a hosting plugin's host (the apps' tests start
/// them) also sets, so it runs alone rather than alongside them.
/// </summary>
[Collection(nameof(SnapshotDiagnosticsTests))]
public class SnapshotDiagnosticsTests
{
    [CollectionDefinition(nameof(SnapshotDiagnosticsTests), DisableParallelization = true)]
    public sealed class RunsAlone;

    /// <summary>A store in memory whose snapshots fail while <see cref="Failing"/>.</summary>
    private sealed class FlakySnapshots : IEventStore
    {
        private readonly MemoryEventStore _events = new();

        public bool Failing { get; set; } = true;

        public Task<IReadOnlyList<string>> LoadAsync(string stream) => _events.LoadAsync(stream);

        public Task AppendAsync(string stream, int expected, IReadOnlyList<string> events) => _events.AppendAsync(stream, expected, events);

        public Task<StoredSnapshot?> LoadSnapshotAsync(string stream) => _events.LoadSnapshotAsync(stream);

        public Task SaveSnapshotAsync(string stream, StoredSnapshot snapshot) =>
            Failing ? throw new IOException("the disk is full") : ((IEventStore)_events).SaveSnapshotAsync(stream, snapshot);
    }

    [Fact]
    public async Task A_snapshot_that_isnt_kept_is_logged_once_and_counted_every_time()
    {
        var stream = $"Counter/{Guid.NewGuid():N}";
        var logged = new ConcurrentQueue<string>();
        long counted = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PiperDiagnostics.Name)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            if (tags.ToArray().Any(t => t is { Key: "actor", Value: "Counter" }))
                Interlocked.Add(ref counted, value);
        });
        listener.Start();

        var previous = PiperDiagnostics.Log;
        // Other tests' actors may report at the same time: keep this stream's alone.
        PiperDiagnostics.Log = (message, exception) =>
        {
            if (message.Contains(stream))
                logged.Enqueue(exception is null ? $"info: {message.Replace(stream, "S")}" : $"warn: {message.Replace(stream, "S")} ({exception.Message})");
        };
        try
        {
            var store = new FlakySnapshots();
            var log = new PiperEventLog<int>(store, stream);
            await log.LoadSnapshotAsync();
            await log.ReplayAsync();
            for (var i = 1; i <= 4; i++)
            {
                log.Add(i);
                await log.CommitAsync();
                if (log.SnapshotDue(1))
                    await log.SaveSnapshotAsync(new PiperSnapshot().With("n", i));
                if (i == 3)
                    store.Failing = false;
            }
            Assert.Equal(
                [
                    "warn: A snapshot of S (after 1 events) wasn't kept; its events are, so nothing is lost, but it will replay more of them as it starts. It tries again after its next message. (the disk is full)",
                    "info: Snapshots of S are kept again (after 4 events).",
                ],
                logged);
            listener.RecordObservableInstruments();
            Assert.True(Interlocked.Read(ref counted) >= 3, $"counted {counted}");
            Assert.Equal(4, (await ((IEventStore)store).LoadSnapshotAsync(stream))?.Version ?? 0);
        }
        finally
        {
            PiperDiagnostics.Log = previous;
        }
    }
}
