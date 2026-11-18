using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Piper.Runtime;

namespace Piper.Actors.Remote;

/// <summary>
/// An actor's address: the node it runs on (host:port) and its name there. Or, for an actor in a cluster
/// (<see cref="InCluster"/>), no node: each message goes to whichever node owns it at the time.
/// </summary>
public sealed record ActorRef(string Address, string Name)
{
    /// <summary>The address of an actor in a cluster, which has no fixed node.</summary>
    public const string ClusterAddress = "cluster";

    /// <summary>The <paramref name="kind"/> of actor with <paramref name="key"/> in the cluster: named <c>kind/key</c> on its owner.</summary>
    public static ActorRef InCluster(string kind, string key) => new(ClusterAddress, $"{kind}/{key}");

    /// <summary>Whether this is an actor in a cluster, rather than on a node.</summary>
    public bool IsClustered => Address == ClusterAddress;

    public override string ToString() => $"{Address}/{Name}";
}

/// <summary>The first message every actor handles, before any sent to it: a persisted actor replays its events then.</summary>
public sealed class Started
{
    public static readonly Started Instance = new();

    private Started()
    {
    }
}

/// <summary>An ask, or a stop, sent to an actor that isn't there: never started, stopped, or its node restarted without it.</summary>
public sealed class DeadLetterException(ActorRef target) : Exception($"{target} no longer exists")
{
    public ActorRef Target { get; } = target;
}

/// <summary>
/// An ask whose actor's node was found down (by the cluster) before it answered: it fails then, rather than waiting out
/// its timeout. Whether the actor handled the message isn't known (the node may have crashed before or after), so it
/// isn't retried; the caller can, if asking again is safe. A <see cref="TimeoutException"/>, as an unanswered ask is.
/// </summary>
public sealed class NodeDownException(ActorRef target)
    : TimeoutException($"{target}'s node went down before it answered; whether it handled the message isn't known.")
{
    public ActorRef Target { get; } = target;
}

/// <summary>What an actor's receive function is handed: the message, who it is, and how to answer an ask.</summary>
public sealed class ActorContext
{
    private readonly ActorSystem.Reply? _reply;

    internal ActorContext(ActorSystem system, ActorRef self, object message, IReadOnlyDictionary<string, string> headers, ActorSystem.Reply? reply)
    {
        System = system;
        Self = self;
        Message = message;
        Headers = headers;
        _reply = reply;
    }

    public ActorSystem System { get; }

    public ActorRef Self { get; }

    public object Message { get; }

    /// <summary>What the sender sent with the message: its trace context (<c>traceparent</c>), if it was in a trace.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>Whether the sender is waiting for an answer (an ask), rather than carrying on (a tell).</summary>
    public bool IsAsk => _reply is not null;

    /// <summary>Answers an ask with <paramref name="value"/>, sent to another node as a <typeparamref name="T"/>; a tell's answer goes nowhere.</summary>
    public void Respond<T>(T value) => _reply?.Answer(value);

    /// <summary>Answers an ask with an error: the sender's await throws an InvalidOperationException with <paramref name="error"/>.</summary>
    public void Fail(string error) => _reply?.Fail(error);

    /// <summary>Stops this actor once the current message is handled: later messages aren't, and asks among them fail.</summary>
    public void Stop() => System.StopLocal(Self.Name);
}

/// <summary>
/// A node: the actors hosted in this process, each handling one message at a time from its mailbox, and how they reach
/// actors on other nodes, through an <see cref="IActorTransport"/> (over the network, or in memory). Messages to this
/// node's actors are handed over as they are; to another node's, they're sent as JSON of the type they're declared as
/// (the actor's message enum, an ask's result type), so what crosses the wire is what the plugin's shape check records.
/// Delivery is at most once, in the order each sender sent them: a message to a node that can't be reached is lost, and
/// an ask whose message (or answer) is lost times out.
/// </summary>
public sealed class ActorSystem : IAsyncDisposable
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { IncludeFields = true };

    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    /// <summary>The actor whose handler is running, so it isn't waited for by a stop it asks for itself.</summary>
    private static readonly AsyncLocal<LocalActor?> Current = new();

    private readonly IActorTransport _transport;
    private readonly ConcurrentDictionary<string, LocalActor> _actors = new();
    private readonly ConcurrentDictionary<long, PendingAsk> _pending = new();
    private long _nextAsk;
    private int _stopped;

    private readonly PiperRuntime _runtime = PiperRuntime.Current;

    private ActorSystem(IActorTransport transport) => _transport = transport;

    /// <summary>Where this node takes its time, randomness and background work from: the runtime it was started in (a
    /// simulation's, in one).</summary>
    internal PiperRuntime Runtime => _runtime;

    /// <summary>Starts a node on <paramref name="transport"/>, which gives its address.</summary>
    public static async Task<ActorSystem> StartAsync(IActorTransport transport)
    {
        var system = new ActorSystem(transport);
        system.Address = await transport.StartAsync(system.Receive);
        return system;
    }

    /// <summary>Where other nodes reach this one: host:port.</summary>
    public string Address { get; private set; } = "";

    /// <summary>The cluster this node has joined (<see cref="Remote.Cluster.JoinAsync"/>): an InvalidOperationException if it hasn't joined one.</summary>
    public Cluster Cluster => Joined ?? throw new InvalidOperationException($"The node at {Address} hasn't joined a cluster (Cluster.JoinAsync).");

    /// <summary>Whether this node has joined a cluster.</summary>
    public bool InCluster => Joined is not null;

    /// <summary>The cluster this node has joined, or null.</summary>
    internal Cluster? Joined { get; set; }

    /// <summary>Whether this node has stopped (or crashed, in a simulation): its asks to the cluster aren't retried.</summary>
    internal bool IsStopped => Volatile.Read(ref _stopped) != 0;

    /// <summary>The names of the actors running on this node.</summary>
    public IReadOnlyList<string> Actors => [.. _actors.Keys.Order(StringComparer.Ordinal)];

    /// <summary>
    /// Starts an actor here called <paramref name="name"/>, which other nodes reach as <c>Address/name</c>, taking
    /// <typeparamref name="TMessage"/>s: <paramref name="receive"/> handles each, one at a time, after <see cref="Started"/>.
    /// </summary>
    public ActorRef Spawn<TMessage>(string name, Func<ActorContext, Task> receive)
    {
        var self = new ActorRef(Address, name);
        var actor = new LocalActor(this, self, bytes => JsonSerializer.Deserialize<TMessage>(bytes.Span, Json)!, receive);
        if (Volatile.Read(ref _stopped) != 0)
            throw new InvalidOperationException($"The node at {Address} has stopped.");
        if (!_actors.TryAdd(name, actor))
            throw new InvalidOperationException($"There's already an actor called '{name}' at {Address}.");
        actor.Start();
        return self;
    }

    /// <summary>
    /// Tells <paramref name="target"/> <paramref name="message"/>, carrying on at once. A test's faults
    /// (<see cref="PiperFaults"/>) drop or delay it.
    /// </summary>
    public void Send<TMessage>(ActorRef target, TMessage message, IReadOnlyDictionary<string, string>? headers = null)
    {
        target = Route(target);
        if (PiperFaults.For(Address, target.Address) is { } fault)
        {
            if (fault > TimeSpan.Zero)
                _ = _runtime.Start(async () => { await _runtime.Delay(fault); Deliver(target, message, headers); });
            return;
        }
        Deliver(target, message, headers);
    }

    private void Deliver<TMessage>(ActorRef target, TMessage message, IReadOnlyDictionary<string, string>? headers)
    {
        if (target.Address == Address)
        {
            Find(target.Name)?.Post(new Envelope(message!, headers ?? NoHeaders, null));
            return;
        }
        Transmit(target.Address, new Frame(FrameKind.Tell, 0, Address, target.Name, headers, JsonSerializer.SerializeToUtf8Bytes(message, Json)));
    }

    /// <summary>
    /// Asks <paramref name="target"/>, waiting up to <paramref name="timeout"/> for its answer: a <see cref="TimeoutException"/>
    /// without one, a <see cref="DeadLetterException"/> if there's no such actor, or an InvalidOperationException if its handler threw.
    /// An actor in a cluster that isn't where this node expected (it's moving between nodes, or the nodes don't agree yet on
    /// who's a member) is asked again on its owner, until the time's up. A test's faults (<see cref="PiperFaults"/>) drop or delay it.
    /// </summary>
    public async Task<TReply> RequestAsync<TMessage, TReply>(ActorRef target, TMessage message, TimeSpan timeout, IReadOnlyDictionary<string, string>? headers = null)
    {
        if (!target.IsClustered)
            return await AskAsync<TMessage, TReply>(target, message, timeout, headers);
        var started = _runtime.Time.GetTimestamp();
        for (var attempt = 1; ; attempt++)
        {
            var left = timeout - _runtime.Time.GetElapsedTime(started);
            try
            {
                return await AskAsync<TMessage, TReply>(Route(target), message, left, headers);
            }
            catch (DeadLetterException) when (!IsStopped)
            {
                var wait = TimeSpan.FromMilliseconds(Math.Min(25 * (1 << Math.Min(attempt, 5)), 500));
                if (timeout - _runtime.Time.GetElapsedTime(started) <= wait)
                    throw new DeadLetterException(target);
                await _runtime.Delay(wait);
            }
        }
    }

    private async Task<TReply> AskAsync<TMessage, TReply>(ActorRef target, TMessage message, TimeSpan timeout, IReadOnlyDictionary<string, string>? headers)
    {
        // A dropped ask times out, as one whose message was lost does; a delayed one waits first, out of its time.
        if (PiperFaults.For(Address, target.Address) is { } fault)
        {
            if (fault == TimeSpan.Zero || fault >= timeout)
            {
                await _runtime.Delay(timeout);
                throw new TimeoutException($"{target} didn't answer within {timeout}.");
            }
            await _runtime.Delay(fault);
            timeout -= fault;
        }
        var answer = new TaskCompletionSource<object?>(_runtime.Continuations);
        long id = 0;
        if (target.Address == Address)
        {
            if (Find(target.Name) is not { } actor || !actor.Post(new Envelope(message!, headers ?? NoHeaders, new LocalReply(answer, target))))
                throw new DeadLetterException(target);
        }
        else
        {
            id = Interlocked.Increment(ref _nextAsk);
            _pending[id] = new PendingAsk(answer, target, bytes => JsonSerializer.Deserialize<TReply>(bytes.Span, Json));
            Transmit(target.Address, new Frame(FrameKind.Ask, id, Address, target.Name, headers, JsonSerializer.SerializeToUtf8Bytes(message, Json)));
        }
        try
        {
            return (TReply)(await answer.Task.WaitAsync(timeout, _runtime.Time))!;
        }
        catch (TimeoutException error) when (error is not NodeDownException)
        {
            _pending.TryRemove(id, out _);
            throw new TimeoutException($"{target} didn't answer within {timeout}.");
        }
    }

    /// <summary>
    /// Stops <paramref name="target"/>, wherever it runs, once the message it's handling is done (messages after it
    /// aren't handled, and asks among them fail). Nothing happens for an actor that isn't there.
    /// </summary>
    public async Task StopAsync(ActorRef target)
    {
        target = Route(target);
        if (target.Address == Address)
        {
            if (StopLocal(target.Name) is { } actor && !ReferenceEquals(Current.Value, actor))
                await actor.Completion;
            return;
        }
        var answer = new TaskCompletionSource<object?>(_runtime.Continuations);
        var id = Interlocked.Increment(ref _nextAsk);
        _pending[id] = new PendingAsk(answer, target, _ => null);
        Transmit(target.Address, new Frame(FrameKind.Stop, id, Address, target.Name, null, default));
        try
        {
            await answer.Task.WaitAsync(TimeSpan.FromSeconds(10), _runtime.Time);
        }
        catch (Exception error) when (error is DeadLetterException or NodeDownException)
        {
        }
        catch (TimeoutException)
        {
            _pending.TryRemove(id, out _);
            throw new TimeoutException($"{target} didn't confirm it stopped within 10 seconds.");
        }
    }

    /// <summary>
    /// Stops the node: it leaves its cluster, if it's in one (handing its actors over), takes no more messages from
    /// others, and its actors stop (each finishing the message it's handling, for up to five seconds). Asks it's
    /// waiting on time out.
    /// </summary>
    public async Task StopAsync()
    {
        if (Joined is { } cluster)
            await cluster.LeaveAsync();
        await AbortAsync();
    }

    /// <summary>Stops the node without leaving its cluster, as one that crashed: the others find it gone when it stops answering.</summary>
    internal async Task AbortAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;
        if (Joined is { } cluster)
            await cluster.StopHeartbeatAsync();
        await _transport.DisposeAsync();
        var actors = _actors.Values.OrderBy(a => a.Self.Name, StringComparer.Ordinal).ToList();
        _actors.Clear();
        foreach (var actor in actors)
            actor.Stop();
        try
        {
            await Task.WhenAll(actors.Where(a => !ReferenceEquals(Current.Value, a)).Select(a => a.Completion)).WaitAsync(TimeSpan.FromSeconds(5), _runtime.Time);
        }
        catch (TimeoutException)
        {
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    /// <summary>
    /// Fails the asks waiting on the nodes at <paramref name="down"/> (found down by the cluster) with a
    /// <see cref="NodeDownException"/>, rather than leaving them to time out.
    /// </summary>
    internal void FailAsksTo(IReadOnlySet<string> down)
    {
        foreach (var (id, pending) in _pending.OrderBy(p => p.Key))
        {
            if (down.Contains(pending.Target.Address) && _pending.TryRemove(id, out _))
                pending.Answer.TrySetException(new NodeDownException(pending.Target));
        }
    }

    /// <summary>Stops this node's actor called <paramref name="name"/>, without waiting: it, or null if there's none.</summary>
    internal LocalActor? StopLocal(string name)
    {
        if (!_actors.TryRemove(name, out var actor))
            return null;
        actor.Stop();
        return actor;
    }

    /// <summary>Where a message to <paramref name="target"/> goes: for an actor in a cluster, its owner now.</summary>
    private ActorRef Route(ActorRef target) =>
        !target.IsClustered ? target
        : Joined is { } cluster ? new ActorRef(cluster.OwnerOf(target.Name), target.Name)
        : throw new InvalidOperationException($"{target.Name} is an actor in a cluster, but the node at {Address} hasn't joined one (Cluster.JoinAsync).");

    /// <summary>This node's actor called <paramref name="name"/>; one its cluster starts here, if it's an actor this node owns; or null.</summary>
    private LocalActor? Find(string name)
    {
        if (_actors.TryGetValue(name, out var actor))
            return actor;
        return Joined is { } cluster && cluster.TryActivate(name) && _actors.TryGetValue(name, out actor) ? actor : null;
    }

    private void Transmit(string address, Frame frame)
    {
        if (Volatile.Read(ref _stopped) == 0)
            _transport.Send(address, frame.Encode());
    }

    /// <summary>A frame from another node: a message for one of this node's actors, or the answer to an ask of one here.</summary>
    private void Receive(ReadOnlyMemory<byte> bytes)
    {
        Frame frame;
        try
        {
            frame = Frame.Decode(bytes);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Piper.Actors: a message from another node couldn't be read: {error.Message}");
            return;
        }
        switch (frame.Kind)
        {
            case FrameKind.Tell or FrameKind.Ask:
                Deliver(frame);
                break;
            case FrameKind.Stop:
                _ = StopForAsync(frame);
                break;
            case FrameKind.Reply or FrameKind.Stopped:
                if (_pending.TryRemove(frame.Id, out var replied))
                    replied.Complete(frame.Payload);
                break;
            case FrameKind.Fault:
                if (_pending.TryRemove(frame.Id, out var failed))
                    failed.Answer.TrySetException(new InvalidOperationException(System.Text.Encoding.UTF8.GetString(frame.Payload.Span)));
                break;
            case FrameKind.DeadLetter:
                if (_pending.TryRemove(frame.Id, out var missing))
                    missing.Answer.TrySetException(new DeadLetterException(missing.Target));
                break;
        }
    }

    private void Deliver(Frame frame)
    {
        var reply = frame.Kind == FrameKind.Ask ? new RemoteReply(this, frame.From, frame.Id) : null;
        if (Find(frame.To) is not { } actor)
        {
            reply?.DeadLetter();
            return;
        }
        object message;
        try
        {
            message = actor.Read(frame.Payload);
        }
        catch (Exception error)
        {
            // A sender built from other code than this node's (mid-rollout): its message isn't one this actor knows.
            var why = $"{actor.Self} couldn't read a message from {frame.From}: {error.Message}";
            if (reply is null)
                Console.Error.WriteLine($"Piper.Actors: {why}");
            reply?.Fail(why);
            return;
        }
        if (!actor.Post(new Envelope(message, frame.Headers ?? NoHeaders, reply)))
            reply?.DeadLetter();
    }

    private async Task StopForAsync(Frame frame)
    {
        if (StopLocal(frame.To) is not { } actor)
        {
            Transmit(frame.From, new Frame(FrameKind.DeadLetter, frame.Id, Address, "", null, default));
            return;
        }
        await actor.Completion;
        Transmit(frame.From, new Frame(FrameKind.Stopped, frame.Id, Address, "", null, default));
    }

    /// <summary>An actor hosted on this node: its mailbox, and the loop handling what's in it one message at a time.</summary>
    internal sealed class LocalActor(ActorSystem system, ActorRef self, Func<ReadOnlyMemory<byte>, object> read, Func<ActorContext, Task> receive)
    {
        private readonly Channel<Envelope> _mailbox = Channel.CreateUnbounded<Envelope>(new UnboundedChannelOptions { SingleReader = true });
        private volatile bool _stopped;

        public ActorRef Self => self;

        public Task Completion { get; private set; } = Task.CompletedTask;

        /// <summary>A message from another node, read as the actor's message type.</summary>
        public object Read(ReadOnlyMemory<byte> bytes) => read(bytes);

        public void Start() => Completion = system.Runtime.Start(RunAsync);

        /// <summary>Puts a message in the mailbox: false if the actor has stopped.</summary>
        public bool Post(Envelope envelope) => !_stopped && _mailbox.Writer.TryWrite(envelope);

        public void Stop()
        {
            _stopped = true;
            _mailbox.Writer.TryComplete();
        }

        private async Task RunAsync()
        {
            Current.Value = this;
            await HandleAsync(new Envelope(Started.Instance, NoHeaders, null));
            // Not ReadAllAsync, which leaves for the thread pool between messages: in a simulation, nothing may.
            while (await _mailbox.Reader.WaitToReadAsync())
            {
                while (_mailbox.Reader.TryRead(out var envelope))
                {
                    // Stopped while these waited: asks among them fail rather than wait for ever.
                    if (_stopped)
                        envelope.Reply?.DeadLetter();
                    else
                        await HandleAsync(envelope);
                }
            }
        }

        private async Task HandleAsync(Envelope envelope)
        {
            try
            {
                await receive(new ActorContext(system, self, envelope.Message, envelope.Headers, envelope.Reply));
            }
            catch (Exception error)
            {
                // The plugin's code catches a handler's exceptions; this is anything else, which mustn't end the loop.
                Console.Error.WriteLine($"{self.Name}: {error.Message}");
                envelope.Reply?.Fail(error.Message);
            }
        }
    }

    internal sealed record Envelope(object Message, IReadOnlyDictionary<string, string> Headers, Reply? Reply);

    /// <summary>An ask of an actor on another node, waiting for its answer, which <c>Read</c> reads as the ask's result type.</summary>
    private sealed record PendingAsk(TaskCompletionSource<object?> Answer, ActorRef Target, Func<ReadOnlyMemory<byte>, object?> Read)
    {
        public void Complete(ReadOnlyMemory<byte> payload)
        {
            try
            {
                Answer.TrySetResult(Read(payload));
            }
            catch (Exception error)
            {
                Answer.TrySetException(new InvalidOperationException($"{Target}'s answer couldn't be read: {error.Message}", error));
            }
        }
    }

    /// <summary>Where an ask's answer goes: back to the sender on this node, or on another.</summary>
    internal abstract class Reply
    {
        private int _answered;

        /// <summary>True the first time, so an ask is answered once.</summary>
        protected bool First() => Interlocked.Exchange(ref _answered, 1) == 0;

        public abstract void Answer<T>(T value);

        public abstract void Fail(string error);

        public abstract void DeadLetter();
    }

    private sealed class LocalReply(TaskCompletionSource<object?> answer, ActorRef target) : Reply
    {
        public override void Answer<T>(T value) => answer.TrySetResult(value);

        public override void Fail(string error) => answer.TrySetException(new InvalidOperationException(error));

        public override void DeadLetter() => answer.TrySetException(new DeadLetterException(target));
    }

    private sealed class RemoteReply(ActorSystem system, string sender, long id) : Reply
    {
        public override void Answer<T>(T value)
        {
            if (!First())
                return;
            byte[] payload;
            try
            {
                payload = JsonSerializer.SerializeToUtf8Bytes(value, Json);
            }
            catch (Exception error)
            {
                Send(FrameKind.Fault, System.Text.Encoding.UTF8.GetBytes($"The answer couldn't be sent: {error.Message}"));
                return;
            }
            Send(FrameKind.Reply, payload);
        }

        public override void Fail(string error)
        {
            if (First())
                Send(FrameKind.Fault, System.Text.Encoding.UTF8.GetBytes(error));
        }

        public override void DeadLetter()
        {
            if (First())
                Send(FrameKind.DeadLetter, default);
        }

        private void Send(FrameKind kind, ReadOnlyMemory<byte> payload) => system.Transmit(sender, new Frame(kind, id, system.Address, "", null, payload));
    }
}
