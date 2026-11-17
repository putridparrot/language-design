using System.Diagnostics;
using System.Diagnostics.Metrics;
using Proto;

namespace Piper.Actors.Remote;

/// <summary>
/// Traces and times remote actors' messages, with .NET's own <see cref="Activity"/> and <see cref="Meter"/>, so whatever
/// collects them (OpenTelemetry, through the telemetry plugin; Aspire's dashboard; a test's listener) sees one request as
/// one trace: through HTTP, node A, and the actor on node B it asked. The sender puts its trace context
/// (<c>traceparent</c>, W3C's) in the message's Proto.Actor headers, so the messages themselves don't change; each handler
/// runs in a span that continues it. With nothing listening, a span costs nothing and no header is sent.
/// </summary>
public static class PiperActorTracing
{
    /// <summary>The name to listen to, or to add to OpenTelemetry's tracing and metrics: <c>Piper.Actors</c>.</summary>
    public const string Name = "Piper.Actors";

    private const string TraceParent = "traceparent", TraceState = "tracestate";

    private static readonly ActivitySource Source = new(Name);

    private static readonly Meter Meter = new(Name);

    private static readonly Histogram<double> HandlerTime = Meter.CreateHistogram<double>(
        "piper.actor.handler.duration", "s", "How long an actor's handler took, from taking the message to finishing it.");

    /// <summary>Tells the actor at <paramref name="pid"/> <paramref name="message"/>, with the trace it's part of.</summary>
    public static void Send(ActorSystem system, PID pid, object message)
    {
        // A test's faults (PiperCluster): dropped, or sent later.
        if (PiperFaults.For(system.Address, pid.Address) is { } fault)
        {
            if (fault > TimeSpan.Zero)
                Task.Delay(fault).ContinueWith(_ => Deliver(system, pid, message), TaskScheduler.Default);
            return;
        }
        Deliver(system, pid, message);
    }

    private static void Deliver(ActorSystem system, PID pid, object message)
    {
        if (Headers() is { } headers)
            system.Root.Send(pid, new MessageEnvelope(message, null, headers));
        else
            system.Root.Send(pid, message);
    }

    /// <summary>
    /// Asks the actor at <paramref name="pid"/>, with the trace it's part of, waiting up to <paramref name="timeout"/>:
    /// its reply, a <see cref="TimeoutException"/> without one, or Proto.Actor's <see cref="DeadLetterException"/> at once
    /// if there's no such actor, as Proto.Actor's RequestAsync does.
    /// </summary>
    public static async Task<object> RequestAsync(ActorSystem system, PID pid, object message, TimeSpan timeout)
    {
        // A test's faults (PiperCluster): a dropped ask times out, as one whose message was lost does; a delayed one
        // waits first, out of its time.
        if (PiperFaults.For(system.Address, pid.Address) is { } fault)
        {
            if (fault == TimeSpan.Zero || fault >= timeout)
            {
                await Task.Delay(timeout);
                throw new TimeoutException($"Request didn't receive any Response within the expected time ({timeout}).");
            }
            await Task.Delay(fault);
            timeout -= fault;
        }
        if (Headers() is not { } headers)
            return await system.Root.RequestAsync<object>(pid, message, timeout);
        using var cancel = new CancellationTokenSource(timeout);
        try
        {
            var (reply, _) = await system.Root.RequestWithHeadersAsync<object>(pid, message, headers, cancel.Token);
            return reply;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw new TimeoutException($"Request didn't receive any Response within the expected time ({timeout}).");
        }
    }

    /// <summary>The current trace's context, as message headers; null outside a trace (or with nothing listening).</summary>
    private static MessageHeader? Headers()
    {
        if (Activity.Current is not { } current)
            return null;
        var headers = MessageHeader.Empty.With(TraceParent, current.Id!);
        return current.TraceStateString is { Length: > 0 } state ? headers.With(TraceState, state) : headers;
    }

    /// <summary>
    /// The span a handler of <paramref name="actor"/> runs in, continuing the sender's trace from the message's headers,
    /// and timing it; one that does nothing for Proto.Actor's own messages (Started, Stopping), which aren't
    /// <typeparamref name="TMessage"/>. Disposing it ends the span and records the time.
    /// </summary>
    public static HandlerSpan Start<TMessage>(IContext context, string actor)
    {
        if (context.Message is not TMessage message)
            return HandlerSpan.None;
        var name = message.GetType().Name;
        ActivityContext.TryParse(context.Headers.GetOrDefault(TraceParent), context.Headers.GetOrDefault(TraceState), out var parent);
        // An ask is a call waiting for its answer; a tell is a message to be done in its time.
        var activity = Source.StartActivity($"{actor}.{name}", context.Sender is null ? ActivityKind.Consumer : ActivityKind.Server, parent);
        activity?.SetTag("actor.name", actor);
        activity?.SetTag("actor.message", name);
        activity?.SetTag("actor.id", context.Self.Id);
        activity?.SetTag("actor.node", context.System.Address);
        return new HandlerSpan(activity, actor, name);
    }

    /// <summary>Marks the handler's span as failed, with <paramref name="error"/>: the handler threw.</summary>
    public static void Failed(Exception error)
    {
        if (Activity.Current is not { Source.Name: Name } activity)
            return;
        activity.SetStatus(ActivityStatusCode.Error, error.Message);
        activity.AddException(error);
    }

    /// <summary>A handler's span and timing: <see cref="Start{TMessage}"/>'s, ended by <see cref="Dispose"/>.</summary>
    public sealed class HandlerSpan(Activity? activity, string? actor, string? message) : IDisposable
    {
        /// <summary>No span, and no timing: for a message that isn't one of the actor's.</summary>
        internal static readonly HandlerSpan None = new(null, null, null);

        private readonly long _started = Stopwatch.GetTimestamp();

        public void Dispose()
        {
            activity?.Dispose();
            if (actor is not null && HandlerTime.Enabled)
            {
                HandlerTime.Record(Stopwatch.GetElapsedTime(_started).TotalSeconds,
                    new KeyValuePair<string, object?>("actor.name", actor), new KeyValuePair<string, object?>("actor.message", message));
            }
        }
    }
}
