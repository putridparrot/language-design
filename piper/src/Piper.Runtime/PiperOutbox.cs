namespace Piper.Runtime;

/// <summary>
/// A message waiting in an outbox to be sent: what Piper.Messaging's bus would have sent, kept instead in the same
/// database transaction as the change it tells of, so the change and the message can't be separated (a change saved
/// with its message lost, or a message sent for a change that failed). A relay sends it afterwards.
/// </summary>
public sealed record OutboxMessage(
    Guid Id, string Destination, string Kind, string Type, string Body, string? Key, DateTimeOffset? NotBefore, DateTime Created);

/// <summary>
/// Where messages wait to be sent with a change: a database's (the data plugin's <c>outbox;</c>, in the change's own
/// unit of work, saved by its <c>SaveChangesAsync</c>), or <see cref="MemoryOutbox"/>. Piper.Messaging's
/// <c>bus.In(outbox)</c> sends and publishes into it.
/// </summary>
public interface IOutbox
{
    /// <summary>Adds a message, to be saved with the change (it's not saved, and so never sent, if the change isn't).</summary>
    void Add(OutboxMessage message);
}

/// <summary>
/// The saved messages a relay sends: claimed a few at a time, for a while, so several relays (one in each of a
/// service's pods) can share one outbox without sending a message twice; then marked sent. A relay that stops between
/// sending and marking leaves its claim to run out, and the message is sent again: delivery is at least once, so
/// handlers must be idempotent (each message keeps its id, for them to tell).
/// </summary>
public interface IOutboxStore
{
    /// <summary>Claims up to <paramref name="max"/> unsent messages, oldest first, for <paramref name="lease"/>.</summary>
    Task<IReadOnlyList<OutboxMessage>> ClaimAsync(string claimant, int max, TimeSpan lease);

    /// <summary>Marks messages sent, so no relay sends them again.</summary>
    Task MarkSentAsync(IReadOnlyList<Guid> ids);
}

/// <summary>
/// An outbox in memory, for tests and programs with no database: <see cref="Add"/> is saved at once, as there's no
/// transaction to wait for. What's waiting and what's sent can be looked at.
/// </summary>
public sealed class MemoryOutbox : IOutbox, IOutboxStore
{
    private readonly List<(OutboxMessage Message, string? Claimant, DateTime ClaimedUntil, bool Sent)> _messages = [];
    private readonly Lock _gate = new();

    public void Add(OutboxMessage message)
    {
        lock (_gate)
            _messages.Add((message, null, DateTime.MinValue, false));
    }

    /// <summary>The messages not sent yet.</summary>
    public IReadOnlyList<OutboxMessage> Waiting
    {
        get
        {
            lock (_gate)
                return [.. _messages.Where(m => !m.Sent).Select(m => m.Message)];
        }
    }

    public Task<IReadOnlyList<OutboxMessage>> ClaimAsync(string claimant, int max, TimeSpan lease)
    {
        var now = PiperRuntime.Current.UtcNow;
        lock (_gate)
        {
            var claimed = new List<OutboxMessage>();
            for (var i = 0; i < _messages.Count && claimed.Count < max; i++)
            {
                var (message, _, until, sent) = _messages[i];
                if (sent || until > now)
                    continue;
                _messages[i] = (message, claimant, now + lease, false);
                claimed.Add(message);
            }
            return Task.FromResult<IReadOnlyList<OutboxMessage>>(claimed);
        }
    }

    public Task MarkSentAsync(IReadOnlyList<Guid> ids)
    {
        lock (_gate)
        {
            for (var i = 0; i < _messages.Count; i++)
            {
                if (ids.Contains(_messages[i].Message.Id))
                    _messages[i] = _messages[i] with { Sent = true };
            }
        }
        return Task.CompletedTask;
    }
}
