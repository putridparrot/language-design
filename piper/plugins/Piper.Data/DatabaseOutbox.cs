using Microsoft.EntityFrameworkCore;
using Piper.Runtime;

namespace Piper.Data;

/// <summary>A message waiting in a database's outbox: a row of its <c>PiperOutbox</c> table.</summary>
public sealed class OutboxRow
{
    public Guid Id { get; set; }
    public string Destination { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Type { get; set; } = "";
    public string Body { get; set; } = "";
    public string? Key { get; set; }
    public DateTime? NotBefore { get; set; }
    public DateTime Created { get; set; }

    /// <summary>The relay sending it, until <see cref="ClaimedUntil"/>, when another may.</summary>
    public string? Claimant { get; set; }
    public DateTime? ClaimedUntil { get; set; }

    /// <summary>When it was sent; null while it waits.</summary>
    public DateTime? Sent { get; set; }
}

/// <summary>
/// A database's outbox (the data plugin's <c>outbox;</c> in a <c>database</c>): messages saved in a
/// <c>PiperOutbox</c> table by the same <c>SaveChangesAsync</c> as the change they tell of, so a change and its message
/// can't be separated, then sent by a relay.
/// <code>
/// database Shop { table Orders: Order key id; outbox; }
///
/// shop.Orders.Add(order);
/// await bus.In(shop.Outbox).Publish(OrderPlaced(order.id, order.total));
/// await shop.SaveChangesAsync();                                         // the order and its message, or neither
///
/// await using let relay = bus.Relay(DatabaseOutbox.Store(() => Shop(options)));   // sends them, in the background
/// </code>
/// Relays in several processes can share the table: each claims a batch, timed by the database's clock, before sending
/// it. Sent messages are kept a day (to see what went), then deleted as relays go on.
/// </summary>
public static class DatabaseOutbox
{
    /// <summary>The outbox table in a database's model: what <c>outbox;</c> adds to its <c>OnModelCreating</c>.</summary>
    public static void Configure(ModelBuilder model)
    {
        var rows = model.Entity<OutboxRow>();
        rows.ToTable("PiperOutbox");
        rows.HasKey(r => r.Id);
        rows.HasIndex(r => new { r.Sent, r.Created });
        rows.Property(r => r.Destination).HasMaxLength(260);
        rows.Property(r => r.Kind).HasMaxLength(20);
        rows.Property(r => r.Type).HasMaxLength(260);
        rows.Property(r => r.Key).HasMaxLength(400);
        rows.Property(r => r.Claimant).HasMaxLength(100);
    }

    /// <summary>
    /// <paramref name="database"/>'s outbox, which messages are added to as rows of its unit of work, saved by its next
    /// <c>SaveChangesAsync</c> (with the change they tell of) and not otherwise: what <c>outbox;</c>'s <c>Outbox</c> is.
    /// </summary>
    public static IOutbox For(DbContext database) => new Adder(database);

    /// <summary>
    /// The outbox's saved messages, for a relay to send, each operation in a database made by <paramref name="open"/>
    /// (a new one each time, as a DbContext is for one operation at a time). Sent messages are deleted after
    /// <paramref name="keepSent"/> (a day).
    /// </summary>
    public static IOutboxStore Store<TDatabase>(Func<TDatabase> open, TimeSpan? keepSent = null) where TDatabase : DbContext =>
        new RowStore(open, keepSent ?? TimeSpan.FromDays(1));

    /// <summary>How many messages are waiting to be sent: the outbox's backlog, for a health check or a dashboard.</summary>
    public static Task<int> WaitingAsync(DbContext database) => database.Set<OutboxRow>().CountAsync(r => r.Sent == null);

    private sealed class Adder(DbContext database) : IOutbox
    {
        public void Add(OutboxMessage message) => database.Set<OutboxRow>().Add(new OutboxRow
        {
            Id = message.Id,
            Destination = message.Destination,
            Kind = message.Kind,
            Type = message.Type,
            Body = message.Body,
            Key = message.Key,
            NotBefore = message.NotBefore?.UtcDateTime,
            Created = message.Created,
        });
    }

    private sealed class RowStore(Func<DbContext> open, TimeSpan keepSent) : IOutboxStore
    {
        private readonly DatabaseClock _clock = new();

        public async Task<IReadOnlyList<OutboxMessage>> ClaimAsync(string claimant, int max, TimeSpan lease)
        {
            // Another relay may take the same oldest messages first; then this one looks again, at those still free.
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var (claimed, more) = await TryClaimAsync(claimant, max, lease);
                if (claimed.Count > 0 || !more)
                    return claimed;
            }
            return [];
        }

        /// <summary>One try at claiming: what it claimed, and whether there were messages free to claim.</summary>
        private async Task<(IReadOnlyList<OutboxMessage> Claimed, bool More)> TryClaimAsync(string claimant, int max, TimeSpan lease)
        {
            await using var database = open();
            var rows = database.Set<OutboxRow>();
            var now = PiperRuntime.Current.UtcNow;
            // The oldest unsent messages no relay holds; then claimed, only if still unclaimed (another relay may have
            // claimed some meanwhile); then those this claim holds.
            var candidates = await _clock.Run(
                () => rows.Where(r => r.Sent == null && (r.ClaimedUntil == null || r.ClaimedUntil < DateTime.UtcNow))
                    .OrderBy(r => r.Created).ThenBy(r => r.Id).Select(r => r.Id).Take(max).ToListAsync(),
                () => rows.Where(r => r.Sent == null && (r.ClaimedUntil == null || r.ClaimedUntil < now))
                    .OrderBy(r => r.Created).ThenBy(r => r.Id).Select(r => r.Id).Take(max).ToListAsync());
            if (candidates.Count == 0)
                return ([], false);
            var seconds = lease.TotalSeconds;
            var free = rows.Where(r => candidates.Contains(r.Id) && r.Sent == null);
            await _clock.Run(
                () => free.Where(r => r.ClaimedUntil == null || r.ClaimedUntil < DateTime.UtcNow).ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Claimant, claimant)
                    .SetProperty(r => r.ClaimedUntil, r => DateTime.UtcNow.AddSeconds(seconds))),
                () => free.Where(r => r.ClaimedUntil == null || r.ClaimedUntil < now).ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Claimant, claimant)
                    .SetProperty(r => r.ClaimedUntil, now + lease)));
            var claimed = await rows.AsNoTracking().Where(r => candidates.Contains(r.Id) && r.Claimant == claimant && r.Sent == null)
                .OrderBy(r => r.Created).ThenBy(r => r.Id).ToListAsync();
            return ([.. claimed.Select(r => new OutboxMessage(r.Id, r.Destination, r.Kind, r.Type, r.Body, r.Key,
                r.NotBefore is { } at ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)) : null, r.Created))], true);
        }

        public async Task MarkSentAsync(IReadOnlyList<Guid> ids)
        {
            await using var database = open();
            var rows = database.Set<OutboxRow>();
            var now = PiperRuntime.Current.UtcNow;
            var keep = keepSent.TotalSeconds;
            await _clock.Run(
                () => rows.Where(r => ids.Contains(r.Id)).ExecuteUpdateAsync(s => s.SetProperty(r => r.Sent, r => DateTime.UtcNow)),
                () => rows.Where(r => ids.Contains(r.Id)).ExecuteUpdateAsync(s => s.SetProperty(r => r.Sent, now)));
            // Messages sent long ago: cleared as relays go on, so the table doesn't grow.
            await _clock.Run(
                () => rows.Where(r => r.Sent != null && r.Sent < DateTime.UtcNow.AddSeconds(-keep)).ExecuteDeleteAsync(),
                () => rows.Where(r => r.Sent != null && r.Sent < now - keepSent).ExecuteDeleteAsync());
        }
    }
}
