using System.Collections.Concurrent;
using System.Text.Json;
using Piper.Runtime;

namespace Piper.Workflows;

/// <summary>
/// Runs durable workflows: functions that may take minutes or weeks (an order paid, packed, shipped, refunded if it
/// isn't), surviving restarts by replaying their history of steps, as Temporal and Azure Durable Functions do.
/// <code>
/// let engine = WorkflowEngine(store);                       // an IEventStore: the database the actors use
/// engine.Register&lt;Order, Receipt&gt;("Fulfil", async (flow, order) => {
///     let payment = await flow.Activity("Charge", () => Charge(order));      // run once; its result recorded
///     await flow.Delay(TimeSpan.FromDays(2));                               // durable: survives restarts
///     if !(await flow.Activity("Shipped", () => Shipped(order.id))) {
///         await flow.Activity("Refund", () => Refund(payment));
///     }
///     Receipt(order.id, payment)
/// });
/// await engine.StartAsync("Fulfil", order.id, order);       // once per id: starting it again is a no-op
/// await engine.ResumeAsync();                               // as the program starts: those a restart interrupted
/// </code>
/// <list type="bullet">
/// <item><b>History:</b> each instance's steps are events of its stream (<c>Workflow/Fulfil/order-42</c>): an
/// activity's result (or failure), a timer's due time and its firing, a value recorded (<see cref="WorkflowContext.Now"/>),
/// and how it ended. Running it runs the function from the start: a step in the history gives its recorded result at
/// once, and a new one runs and is added. So after a restart it replays to where it was, and carries on.</item>
/// <item><b>Determinism:</b> replaying must take the same path, so a workflow's code must decide only by its input and
/// its steps' results, not the clock, random numbers or I/O of its own (those go in activities, or
/// <see cref="WorkflowContext.Record{T}"/>). A replay asking for a different step than the history has fails the workflow,
/// saying where.</item>
/// <item><b>Activities</b> run at least once: one interrupted after it ran but before its result was saved runs again
/// on resuming, so they should be idempotent (as message handlers are). One that throws is retried
/// (<see cref="ActivityOptions"/>), and after its last attempt the failure is recorded, and thrown in the workflow as an
/// <see cref="ActivityFailedException"/>, which it can catch (to compensate).</item>
/// <item><b>One runner:</b> an instance is run by one engine at a time; another writing its history meanwhile (two
/// processes resuming it) is refused by the store, and stops.</item>
/// </list>
/// Time comes from the runtime (<see cref="PiperRuntime.Current"/>), so in a simulation a two-day timer passes at once.
/// </summary>
public sealed class WorkflowEngine : IAsyncDisposable
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { IncludeFields = true };

    /// <summary>The stream listing the instances started and finished, so <see cref="ResumeAsync"/> can find the rest.</summary>
    private const string Index = "Workflows";

    private readonly IEventStore _store;
    private readonly ConcurrentDictionary<(string Type, int Version), Registered> _definitions = new();
    private readonly ConcurrentDictionary<string, Running> _running = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly PiperRuntime _runtime = PiperRuntime.Current;

    public WorkflowEngine(IEventStore store) => _store = store;

    /// <summary>
    /// Declares a workflow called <paramref name="type"/>: <paramref name="body"/>, given its context and its input. It's
    /// the code an instance's history is replayed against, so a change to it must keep instances already running on
    /// the same path (or wait for them to finish).
    /// </summary>
    public void Register<TInput, TResult>(string type, Func<WorkflowContext, TInput, Task<TResult>> body) => Register(type, 1, body);

    /// <summary>
    /// Declares version <paramref name="version"/> of the workflow <paramref name="type"/>. Several versions can be
    /// registered at once: an instance is started on the latest, and replayed on the version it started on, so code
    /// that changed in a way running instances couldn't replay is a new version, with the old kept for them until they
    /// finish (the plugin's <c>workflow FulfilV1 as Fulfil version 1</c>).
    /// </summary>
    public void Register<TInput, TResult>(string type, int version, Func<WorkflowContext, TInput, Task<TResult>> body)
    {
        if (version < 1)
            throw new ArgumentOutOfRangeException(nameof(version), "A workflow's versions count from 1.");
        if (!_definitions.TryAdd((type, version), new Registered(
                async (context, input) => JsonSerializer.Serialize(await body(context, JsonSerializer.Deserialize<TInput>(input, Json)!), Json))))
            throw new InvalidOperationException($"There's already a workflow called '{type}'{(version > 1 ? $", version {version}" : "")}.");
    }

    /// <summary>The latest version of <paramref name="type"/> registered here: what new instances start on.</summary>
    public int LatestVersion(string type) => _definitions.Keys.Where(k => k.Type == type).Select(k => k.Version).DefaultIfEmpty(0).Max() is > 0 and var latest
        ? latest
        : throw new InvalidOperationException($"There's no workflow called '{type}': Register it first.");

    /// <summary>
    /// Starts an instance of <paramref name="type"/> called <paramref name="id"/> with <paramref name="input"/>, unless
    /// one is already there (started before, here or by another process): starting is idempotent, so a request retried
    /// starts one workflow. Answers at once; the workflow runs in the background.
    /// </summary>
    public async Task StartAsync<TInput>(string type, string id, TInput input)
    {
        var version = LatestVersion(type);
        var stream = Stream(type, id);
        if ((await _store.LoadAsync(stream)).Count == 0)
        {
            try
            {
                await _store.AppendAsync(stream, 0, [Event(new HistoryEvent("started", Name: type, Json: JsonSerializer.Serialize(input, Json),
                    At: _runtime.UtcNow, Version: version))]);
                await IndexAsync("started", type, id);
            }
            catch (EventStreamConflictException)
            {
                // Started meanwhile by someone else: that one's running it.
                return;
            }
        }
        Run(type, id);
    }

    /// <summary>
    /// Resumes every instance started but not finished (here, or by a process that's gone): what a program does as it
    /// starts, so a restart interrupts nothing for long. Answers how many it resumed.
    /// </summary>
    public async Task<int> ResumeAsync()
    {
        var started = new List<(string Type, string Id)>();
        var ended = new HashSet<(string, string)>();
        foreach (var line in await _store.LoadAsync(Index))
        {
            var entry = JsonSerializer.Deserialize<IndexEntry>(line, Json)!;
            if (entry.Kind == "started")
                started.Add((entry.Type, entry.Id));
            else
                ended.Add((entry.Type, entry.Id));
        }
        var resumed = 0;
        foreach (var (type, id) in started.Where(s => !ended.Contains(s) && _definitions.Keys.Any(k => k.Type == s.Type)))
        {
            Run(type, id);
            resumed++;
        }
        return resumed;
    }

    /// <summary>
    /// The instance's result once it finishes: an <see cref="WorkflowFailedException"/> if it failed. Waits for one
    /// running here; for one running elsewhere, reads its history every <paramref name="poll"/> (a second) until it ends.
    /// </summary>
    public async Task<TResult> ResultAsync<TResult>(string type, string id, TimeSpan? poll = null)
    {
        while (true)
        {
            if (_running.TryGetValue(Key(type, id), out var running))
                await running.Task;
            var history = await HistoryAsync(type, id);
            switch (history.LastOrDefault())
            {
                case { Kind: "completed", Json: var json }:
                    return JsonSerializer.Deserialize<TResult>(json!, Json)!;
                case { Kind: "failed", Error: var error }:
                    throw new WorkflowFailedException(type, id, error ?? "");
                case null:
                    throw new InvalidOperationException($"There's no workflow {type} called '{id}'.");
            }
            await _runtime.Delay(poll ?? TimeSpan.FromSeconds(1), _stopping.Token);
        }
    }

    /// <summary>Where the instance is: not started, running (or waiting), completed or failed.</summary>
    public async Task<WorkflowStatus> StatusAsync(string type, string id) => (await HistoryAsync(type, id)).LastOrDefault() switch
    {
        null => WorkflowStatus.NotStarted,
        { Kind: "completed" } => WorkflowStatus.Completed,
        { Kind: "failed" } => WorkflowStatus.Failed,
        _ => WorkflowStatus.Running,
    };

    /// <summary>The instance's history, as its steps were recorded: for looking at what it did.</summary>
    public async Task<IReadOnlyList<string>> StepsAsync(string type, string id) =>
        [.. (await HistoryAsync(type, id)).Select(e => e.ToString())];

    private async Task<List<HistoryEvent>> HistoryAsync(string type, string id) =>
        [.. (await _store.LoadAsync(Stream(type, id))).Select(line => JsonSerializer.Deserialize<HistoryEvent>(line, Json)!)];

    /// <summary>Runs the instance in the background, unless it's running here already.</summary>
    private void Run(string type, string id)
    {
        var running = new Running();
        if (!_running.TryAdd(Key(type, id), running))
            return;
        running.Task = _runtime.Start(async () =>
        {
            try
            {
                await ExecuteAsync(type, id);
            }
            finally
            {
                _running.TryRemove(Key(type, id), out _);
            }
        });
    }

    /// <summary>Replays the instance's history against its code, then runs it on from there, to its end or a wait.</summary>
    private async Task ExecuteAsync(string type, string id)
    {
        var history = await HistoryAsync(type, id);
        if (history is [] || history[^1].Kind is "completed" or "failed")
            return;
        // Replayed on the version it started on (1 for one started before versions were recorded). Without that version
        // here, it's left as it is, not failed: another process may have it, or this one may again once it's put back.
        var version = history[0].Version ?? 1;
        if (!_definitions.TryGetValue((type, version), out var definition))
        {
            Console.Error.WriteLine($"Piper.Workflows: {type} '{id}' started on version {version}, which isn't registered here, so it's left " +
                                    $"waiting. Register that version (keep its code as `workflow {type}V{version} as {type} version {version}`) to finish it.");
            return;
        }
        var context = new WorkflowContext(this, type, id, history, _runtime, _stopping.Token);
        HistoryEvent outcome;
        try
        {
            var result = await definition.Body(context, history[0].Json!);
            context.Finished();
            outcome = new HistoryEvent("completed", Json: result, At: _runtime.UtcNow);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            return;   // the engine is stopping: the instance resumes from its history next time
        }
        catch (WorkflowOwnedElsewhereException)
        {
            return;   // another runner wrote its history meanwhile: that one carries on
        }
        catch (Exception error)
        {
            outcome = new HistoryEvent("failed", Error: Describe(error), At: _runtime.UtcNow);
        }
        try
        {
            await context.AppendAsync(outcome);
            await IndexAsync("ended", type, id);
        }
        catch (WorkflowOwnedElsewhereException)
        {
        }
    }

    private static string Describe(Exception error) => error switch
    {
        NonDeterministicWorkflowException or ActivityFailedException => error.Message,
        _ => $"{error.GetType().Name}: {error.Message}",
    };

    internal Task AppendAsync(string type, string id, int expected, HistoryEvent @event) =>
        _store.AppendAsync(Stream(type, id), expected, [Event(@event)]);

    /// <summary>Adds an instance's start or end to the index, trying again if another engine added to it meanwhile.</summary>
    private async Task IndexAsync(string kind, string type, string id)
    {
        var line = JsonSerializer.Serialize(new IndexEntry(kind, type, id), Json);
        for (var attempt = 0; ; attempt++)
        {
            var count = (await _store.LoadAsync(Index)).Count;
            try
            {
                await _store.AppendAsync(Index, count, [line]);
                return;
            }
            catch (EventStreamConflictException) when (attempt < 50)
            {
            }
        }
    }

    private static string Stream(string type, string id) => $"Workflow/{type}/{id}";

    private static string Key(string type, string id) => $"{type}/{id}";

    private static string Event(HistoryEvent @event) => JsonSerializer.Serialize(@event, Json);

    /// <summary>
    /// Sends the instance a signal called <paramref name="name"/> with <paramref name="value"/> (a payment that arrived,
    /// an approval), for a <see cref="WorkflowContext.Signal{T}(string)"/> waiting for one: kept in its inbox (its own
    /// stream, <c>Workflow/Fulfil/o-1/signals</c>) until it's taken, so one sent while the workflow isn't waiting yet, or
    /// isn't running here, isn't lost. A message's consumer can pass a message on to a workflow so.
    /// </summary>
    public async Task SignalAsync<T>(string type, string id, string name, T value)
    {
        if ((await _store.LoadAsync(Stream(type, id))).Count == 0)
            throw new InvalidOperationException($"There's no workflow {type} called '{id}' to signal.");
        var inbox = Inbox(type, id);
        for (var attempt = 0; ; attempt++)
        {
            var count = (await _store.LoadAsync(inbox)).Count;
            try
            {
                await _store.AppendAsync(inbox, count, [JsonSerializer.Serialize(new SignalEntry(count, name, JsonSerializer.Serialize(value, Json)), Json)]);
                break;
            }
            catch (EventStreamConflictException) when (attempt < 50)
            {
            }
        }
        // Wakes the instance, if it's waiting here; one waiting elsewhere finds it when it next looks.
        if (_signals.TryRemove(Key(type, id), out var waiting))
            waiting.TrySetResult();
    }

    /// <summary>The instance's inbox: every signal sent it, in order.</summary>
    internal async Task<List<SignalEntry>> InboxAsync(string type, string id) =>
        [.. (await _store.LoadAsync(Inbox(type, id))).Select(line => JsonSerializer.Deserialize<SignalEntry>(line, Json)!)];

    /// <summary>Done when a signal is next sent to the instance through this engine.</summary>
    internal Task Signalled(string type, string id) =>
        _signals.GetOrAdd(Key(type, id), _ => new TaskCompletionSource(_runtime.Continuations)).Task;

    private readonly ConcurrentDictionary<string, TaskCompletionSource> _signals = new();

    private static string Inbox(string type, string id) => $"{Stream(type, id)}/signals";

    /// <summary>
    /// Resumes the unfinished instances, runs until <paramref name="stopping"/> is cancelled, then stops them (they resume
    /// from their history next time): what a server's or host's <c>workflows … from store;</c> runs as a worker.
    /// </summary>
    public async Task RunUntil(CancellationToken stopping)
    {
        await ResumeAsync();
        try
        {
            await _runtime.Delay(Timeout.InfiniteTimeSpan, stopping);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await DisposeAsync();
        }
    }

    /// <summary>Stops the instances running here (they resume from their history next time); waits for them to stop.</summary>
    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        foreach (var running in _running.Values.ToList())
        {
            try
            {
                await running.Task;
            }
            catch (Exception)
            {
            }
        }
        _stopping.Dispose();
    }

    private sealed record Registered(Func<WorkflowContext, string, Task<string>> Body);

    private sealed class Running
    {
        public Task Task { get; set; } = Task.CompletedTask;
    }

    private sealed record IndexEntry(string Kind, string Type, string Id);
}

/// <summary>Where a workflow instance is.</summary>
public enum WorkflowStatus
{
    NotStarted,
    Running,
    Completed,
    Failed,
}

/// <summary>
/// One step of a workflow's history: <c>started</c> (its input), <c>activity</c> (a result) or <c>activityFailed</c>,
/// <c>timer</c> (when it's due) and <c>fired</c>, <c>recorded</c> (a value), and <c>completed</c> or <c>failed</c>.
/// </summary>
internal sealed record HistoryEvent(
    string Kind, int Step = 0, string? Name = null, string? Json = null, string? Error = null, DateTime? At = null, int? Index = null, int? Version = null)
{
    public override string ToString() => Kind switch
    {
        "started" => $"started {Name}{(Version is > 1 ? $" version {Version}" : "")} with {Json}",
        "activity" => $"{Step}: {Name} gave {Json}",
        "activityFailed" => $"{Step}: {Name} failed: {Error}",
        "timer" => $"{Step}: waiting until {At:yyyy-MM-dd HH:mm:ss}",
        "fired" => $"{Step}: waited",
        "recorded" => $"{Step}: {Name} was {Json}",
        "awaiting" => At is { } due ? $"{Step}: waiting for {Name} until {due:yyyy-MM-dd HH:mm:ss}" : $"{Step}: waiting for {Name}",
        "signal" => $"{Step}: {Name} came, {Json}",
        "signalTimedOut" => $"{Step}: {Name} didn't come in time",
        "completed" => $"completed with {Json}",
        "failed" => $"failed: {Error}",
        _ => Kind,
    };
}

/// <summary>How an activity is retried: up to <see cref="Attempts"/> in all, waiting <see cref="Backoff"/>, doubled
/// each time, before the next.</summary>
public sealed record ActivityOptions
{
    public int Attempts { get; init; } = 3;

    public TimeSpan Backoff { get; init; } = TimeSpan.FromSeconds(1);

    public static readonly ActivityOptions Default = new();
}

/// <summary>A workflow ended with an exception: the message says which, and why.</summary>
public sealed class WorkflowFailedException(string type, string id, string error)
    : Exception($"The workflow {type} '{id}' failed: {error}")
{
    public string Error => error;
}

/// <summary>An activity failed on its last attempt, as the workflow sees it (on replay too): it can be caught, to compensate.</summary>
public sealed class ActivityFailedException(string activity, string error)
    : Exception($"The activity {activity} failed: {error}")
{
    public string Activity => activity;

    public string Error => error;
}

/// <summary>
/// The workflow's code asked for a different step than its history recorded at that point: it changed while the
/// instance was running, or decides by something other than its input and its steps' results.
/// </summary>
public sealed class NonDeterministicWorkflowException(string message) : Exception(message);

/// <summary>Another runner wrote this instance's history meanwhile: this one stops, as that one carries on.</summary>
internal sealed class WorkflowOwnedElsewhereException() : Exception("Another runner is running this workflow.");

/// <summary>A signal in an instance's inbox: its place there, its name, and its value as JSON.</summary>
internal sealed record SignalEntry(int Index, string Name, string Json);
