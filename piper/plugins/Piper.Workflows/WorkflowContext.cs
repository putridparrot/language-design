using System.Text.Json;
using Piper.Runtime;

namespace Piper.Workflows;

/// <summary>
/// What a workflow's code does its steps through, each numbered in the order the code asks for them: an activity (work
/// with effects, run once, its result recorded), a durable timer, or a value recorded (the time now). Replaying, a step
/// the history has gives its recorded result at once; asking for one it doesn't match (another activity, or a timer
/// where an activity was) is a <see cref="NonDeterministicWorkflowException"/>.
/// </summary>
public sealed class WorkflowContext
{
    private readonly WorkflowEngine _engine;
    private readonly Dictionary<int, List<HistoryEvent>> _steps;
    private readonly int _lastStep;
    private readonly PiperRuntime _runtime;
    private readonly CancellationToken _stopping;
    private int _version;
    private int _step;

    internal WorkflowContext(WorkflowEngine engine, string type, string id, List<HistoryEvent> history, PiperRuntime runtime, CancellationToken stopping)
    {
        _engine = engine;
        Type = type;
        Id = id;
        _steps = history.Where(e => e.Step > 0).GroupBy(e => e.Step).ToDictionary(g => g.Key, g => g.ToList());
        _lastStep = _steps.Keys.DefaultIfEmpty(0).Max();
        _runtime = runtime;
        _stopping = stopping;
        _version = history.Count;
    }

    /// <summary>The workflow's name, as registered.</summary>
    public string Type { get; }

    /// <summary>This instance's id: what it was started as (an order's id).</summary>
    public string Id { get; }

    /// <summary>Whether it's replaying steps already in its history, rather than running new ones.</summary>
    public bool IsReplaying => _step < _lastStep;

    /// <summary>
    /// Runs the activity called <paramref name="name"/>, once: its result is recorded, and replaying gives it back without
    /// running it again. It's retried as <paramref name="options"/> say if it throws; after the last attempt the failure
    /// is recorded and thrown, as an <see cref="ActivityFailedException"/>, now and on every replay.
    /// </summary>
    public async Task<T> Activity<T>(string name, [PiperEffects] Func<Task<T>> run, ActivityOptions? options = null)
    {
        var step = ++_step;
        if (Recorded(step, name, "activity", "activityFailed") is { } done)
            return done.Kind == "activity" ? Read<T>(done.Json) : throw new ActivityFailedException(name, done.Error ?? "");
        options ??= ActivityOptions.Default;
        for (var attempt = 1; ; attempt++)
        {
            _stopping.ThrowIfCancellationRequested();
            T result;
            try
            {
                result = await run();
            }
            catch (Exception error) when (error is not OperationCanceledException || !_stopping.IsCancellationRequested)
            {
                if (attempt >= options.Attempts)
                {
                    var why = $"{error.GetType().Name}: {error.Message}";
                    await AppendAsync(new HistoryEvent("activityFailed", step, name, Error: why, At: _runtime.UtcNow));
                    throw new ActivityFailedException(name, why);
                }
                await _runtime.Delay(options.Backoff * Math.Pow(2, attempt - 1), _stopping);
                continue;
            }
            var json = JsonSerializer.Serialize(result, WorkflowEngine.Json);
            await AppendAsync(new HistoryEvent("activity", step, name, json, At: _runtime.UtcNow));
            // What it recorded, read back: the same as a replay gives, so the workflow can't tell them apart.
            return Read<T>(json);
        }
    }

    /// <summary>An activity that isn't async: run once, its result recorded.</summary>
    public Task<T> Activity<T>(string name, [PiperEffects] Func<T> run, ActivityOptions? options = null) =>
        Activity(name, () => Task.FromResult(run()), options);

    /// <summary>An async activity with no result: run once, recorded as done.</summary>
    public async Task Activity(string name, [PiperEffects] Func<Task> run, ActivityOptions? options = null) =>
        await Activity<bool>(name, async () => { await run(); return true; }, options);

    /// <summary>An activity with no result that isn't async: run once, recorded as done.</summary>
    public async Task Activity(string name, [PiperEffects] Action run, ActivityOptions? options = null) =>
        await Activity<bool>(name, () => { run(); return Task.FromResult(true); }, options);

    /// <summary>
    /// Waits <paramref name="wait"/>, durably: the time it's due is recorded, so a workflow restarted meanwhile waits only
    /// what's left, and one restarted after it's due carries on at once.
    /// </summary>
    public async Task Delay(TimeSpan wait)
    {
        var step = ++_step;
        DateTime due;
        if (Recorded(step, "timer", "timer") is { } timer)
        {
            if (_steps[step].Any(e => e.Kind == "fired"))
                return;
            due = timer.At ?? _runtime.UtcNow;
        }
        else
        {
            due = _runtime.UtcNow + wait;
            await AppendAsync(new HistoryEvent("timer", step, "timer", At: due));
        }
        var left = due - _runtime.UtcNow;
        if (left > TimeSpan.Zero)
            await _runtime.Delay(left, _stopping);
        await AppendAsync(new HistoryEvent("fired", step, "timer", At: _runtime.UtcNow));
    }

    /// <summary>
    /// A value worked out once and recorded (an id, a random number, something read that mustn't change on replay), as
    /// <paramref name="value"/> gives it the first time.
    /// </summary>
    public async Task<T> Record<T>(string name, [PiperEffects] Func<T> value)
    {
        var step = ++_step;
        if (Recorded(step, name, "recorded") is { } done)
            return Read<T>(done.Json);
        var json = JsonSerializer.Serialize(value(), WorkflowEngine.Json);
        await AppendAsync(new HistoryEvent("recorded", step, name, json, At: _runtime.UtcNow));
        return Read<T>(json);
    }

    /// <summary>
    /// Waits for a signal called <paramref name="name"/> (<see cref="WorkflowEngine.SignalAsync{T}"/>: a payment arriving,
    /// an approval), for as long as it takes, and gives its value. Which signal it took is recorded, so a replay takes the
    /// same one; one sent before the workflow got here is waiting in its inbox.
    /// </summary>
    public async Task<T> Signal<T>(string name) => (await WaitForSignal<T>(name, null)).Value;

    /// <summary>
    /// The same, for up to <paramref name="within"/> (durably: a restart meanwhile waits only what's left): None if it
    /// didn't come in time, which a saga takes as its cue to give up (cancel the order, refund the payment).
    /// </summary>
    public Task<Option<T>> Signal<T>(string name, TimeSpan within) => WaitForSignal<T>(name, within);

    private async Task<Option<T>> WaitForSignal<T>(string name, TimeSpan? within)
    {
        var step = ++_step;
        DateTime? due;
        if (Recorded(step, name, "awaiting") is { } awaiting)
        {
            if (_steps[step].FirstOrDefault(e => e.Kind is "signal" or "signalTimedOut") is { } outcome)
                return outcome.Kind == "signal" ? Option<T>.Some(Read<T>(outcome.Json)) : Option<T>.None;
            due = awaiting.At;
        }
        else
        {
            due = within is { } wait ? _runtime.UtcNow + wait : null;
            await AppendAsync(new HistoryEvent("awaiting", step, name, At: due));
        }
        while (true)
        {
            // Asked first, so a signal sent while the inbox is being read still wakes it.
            var signalled = _engine.Signalled(Type, Id);
            var taken = _steps.Values.SelectMany(events => events).Where(e => e.Kind == "signal" && e.Index is not null)
                .Select(e => e.Index!.Value).Concat(_taken).ToHashSet();
            if ((await _engine.InboxAsync(Type, Id)).FirstOrDefault(entry => entry.Name == name && !taken.Contains(entry.Index)) is { } entry)
            {
                await AppendAsync(new HistoryEvent("signal", step, name, entry.Json, At: _runtime.UtcNow, Index: entry.Index));
                _taken.Add(entry.Index);
                return Option<T>.Some(Read<T>(entry.Json));
            }
            var now = _runtime.UtcNow;
            if (due is { } until && now >= until)
            {
                await AppendAsync(new HistoryEvent("signalTimedOut", step, name, At: now));
                return Option<T>.None;
            }
            // Until a signal comes here, the time's up, or a second passes (for one sent through another process).
            var pause = TimeSpan.FromSeconds(1);
            if (due is { } deadline && deadline - now < pause)
                pause = deadline - now;
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
            await Task.WhenAny(signalled, _runtime.Delay(pause, waiting.Token));
            waiting.Cancel();
            _stopping.ThrowIfCancellationRequested();
        }
    }

    /// <summary>The inbox's signals taken in this run, beside those its history says were taken before.</summary>
    private readonly HashSet<int> _taken = [];

    /// <summary>The time now, recorded, so a replay sees the time the first run did.</summary>
    public Task<DateTime> Now() => Record("now", () => _runtime.UtcNow);

    /// <summary>A new id, recorded.</summary>
    public Task<Guid> NewGuid() => Record("guid", PiperRouting.NewGuid);

    /// <summary>The step's first event, if the history has it, checked to be what the code asks for now.</summary>
    private HistoryEvent? Recorded(int step, string name, params string[] kinds)
    {
        if (!_steps.TryGetValue(step, out var events))
            return null;
        var first = events[0];
        if (!kinds.Contains(first.Kind) || first.Name != name)
        {
            var asked = kinds[0] == "timer" ? "a timer" : kinds[0] == "recorded" ? $"the value {name}" : $"the activity {name}";
            throw new NonDeterministicWorkflowException(
                $"Replaying {Type} '{Id}', step {step} asked for {asked}, but its history has {Describe(first)}. " +
                "Its code changed while it was running, or decides by something other than its input and its steps' results.");
        }
        return first;
    }

    /// <summary>Checks the replay used every step the history has: a workflow that now finishes sooner took another path.</summary>
    internal void Finished()
    {
        if (_step < _lastStep)
            throw new NonDeterministicWorkflowException(
                $"Replaying {Type} '{Id}', it finished after step {_step}, but its history goes on to step {_lastStep} " +
                $"({Describe(_steps[_step + 1][0])}). Its code changed while it was running, or decides by something other than " +
                "its input and its steps' results.");
    }

    private static string Describe(HistoryEvent e) => e.Kind switch
    {
        "timer" or "fired" => "a timer",
        "recorded" => $"the value {e.Name}",
        _ => $"the activity {e.Name}",
    };

    internal async Task AppendAsync(HistoryEvent @event)
    {
        try
        {
            await _engine.AppendAsync(Type, Id, _version, @event);
            _version++;
        }
        catch (EventStreamConflictException)
        {
            throw new WorkflowOwnedElsewhereException();
        }
    }

    private static T Read<T>(string? json) => JsonSerializer.Deserialize<T>(json ?? "null", WorkflowEngine.Json)!;
}
