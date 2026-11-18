using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Piper.Actors.Remote;

/// <summary>
/// What the remote actors plugin's code calls to send, ask and handle: it traces and times messages with .NET's own
/// <see cref="Activity"/> and <see cref="Meter"/>, so whatever collects them (OpenTelemetry, through the telemetry plugin;
/// Aspire's dashboard; a test's listener) sees one request as one trace: through HTTP, node A, and the actor on node B
/// it asked. The sender puts its trace context (<c>traceparent</c>, W3C's) in the message's headers, so the messages
/// themselves don't change; each handler runs in a span that continues it. With nothing listening, a span costs nothing
/// and no header is sent.
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

    /// <summary>Tells <paramref name="target"/> <paramref name="message"/>, with the trace it's part of.</summary>
    public static void Send<TMessage>(ActorSystem system, ActorRef target, TMessage message) => system.Send(target, message, Headers());

    /// <summary>
    /// Asks <paramref name="target"/>, with the trace it's part of, waiting up to <paramref name="timeout"/>: its answer,
    /// a <see cref="TimeoutException"/> without one, or a <see cref="DeadLetterException"/> at once if there's no such actor.
    /// </summary>
    public static Task<TReply> RequestAsync<TMessage, TReply>(ActorSystem system, ActorRef target, TMessage message, TimeSpan timeout) =>
        system.RequestAsync<TMessage, TReply>(target, message, timeout, Headers());

    /// <summary>The current trace's context, as message headers; null outside a trace (or with nothing listening).</summary>
    private static Dictionary<string, string>? Headers()
    {
        if (Activity.Current is not { } current)
            return null;
        var headers = new Dictionary<string, string> { [TraceParent] = current.Id! };
        if (current.TraceStateString is { Length: > 0 } state)
            headers[TraceState] = state;
        return headers;
    }

    /// <summary>
    /// The span a handler of <paramref name="actor"/> runs in, continuing the sender's trace from the message's headers,
    /// and timing it; one that does nothing for the actor system's own messages (<see cref="Started"/>), which aren't
    /// <typeparamref name="TMessage"/>. Disposing it ends the span and records the time.
    /// </summary>
    public static HandlerSpan Start<TMessage>(ActorContext context, string actor)
    {
        if (context.Message is not TMessage message)
            return HandlerSpan.None;
        var name = message.GetType().Name;
        ActivityContext.TryParse(context.Headers.GetValueOrDefault(TraceParent), context.Headers.GetValueOrDefault(TraceState), out var parent);
        // An ask is a call waiting for its answer; a tell is a message to be done in its time.
        var activity = Source.StartActivity($"{actor}.{name}", context.IsAsk ? ActivityKind.Server : ActivityKind.Consumer, parent);
        activity?.SetTag("actor.name", actor);
        activity?.SetTag("actor.message", name);
        activity?.SetTag("actor.id", context.Self.Name);
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
