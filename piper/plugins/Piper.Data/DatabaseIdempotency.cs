using Microsoft.EntityFrameworkCore;
using Piper.Runtime;

namespace Piper.Data;

/// <summary>An idempotency key in the <c>PiperIdempotency</c> table: the request holding it, then its response.</summary>
public sealed class IdempotencyRow
{
    public string Key { get; set; } = "";
    public string Fingerprint { get; set; } = "";

    /// <summary>Until when the key is held: the running request's lease, then how long its response is kept.</summary>
    public DateTime Until { get; set; }

    /// <summary>The response's status; null while the request runs.</summary>
    public int? Status { get; set; }
    public string? ContentType { get; set; }
    public string? Location { get; set; }
    public byte[]? Body { get; set; }
}

/// <summary>
/// Idempotency keys in a database (the data plugin's <c>idempotency;</c> adds the table), so every pod of a service
/// answers a repeated request with the response of whichever pod ran it first. A key is claimed by inserting its row,
/// which only one request can do; a row whose time is up is taken over by an update that only one request can make.
/// Registered for a server's idempotent endpoints as
/// <c>singleton IIdempotencyStore = DatabaseIdempotency.Store(() => Shop(options));</c>.
/// </summary>
public static class DatabaseIdempotency
{
    /// <summary>The table in a database's model: what <c>idempotency;</c> adds to its <c>OnModelCreating</c>.</summary>
    public static void Configure(ModelBuilder model)
    {
        var rows = model.Entity<IdempotencyRow>();
        rows.ToTable("PiperIdempotency");
        rows.HasKey(r => r.Key);
        rows.HasIndex(r => r.Until);
        rows.Property(r => r.Key).HasMaxLength(600);
        rows.Property(r => r.Fingerprint).HasMaxLength(64);
        rows.Property(r => r.ContentType).HasMaxLength(200);
        rows.Property(r => r.Location).HasMaxLength(2000);
    }

    /// <summary>The store, each operation in a database made by <paramref name="open"/> (a new one each time).</summary>
    public static IIdempotencyStore Store<TDatabase>(Func<TDatabase> open) where TDatabase : DbContext => new RowStore(open);

    private sealed class RowStore(Func<DbContext> open) : IIdempotencyStore
    {
        private readonly DatabaseClock _clock = new();

        public async Task<IdempotencyClaim> ClaimAsync(string key, string fingerprint, TimeSpan lease)
        {
            // Another request may insert or take the key between the tries; then this one looks again.
            for (var attempt = 0; attempt < 5; attempt++)
            {
                if (await TryClaimAsync(key, fingerprint, lease) is { } claim)
                    return claim;
            }
            return new IdempotencyClaim(IdempotencyState.Busy);
        }

        private async Task<IdempotencyClaim?> TryClaimAsync(string key, string fingerprint, TimeSpan lease)
        {
            await using var database = open();
            var rows = database.Set<IdempotencyRow>();
            var now = PiperRuntime.Current.UtcNow;
            var seconds = lease.TotalSeconds;
            var row = await rows.AsNoTracking().SingleOrDefaultAsync(r => r.Key == key);
            if (row is null)
            {
                rows.Add(new IdempotencyRow { Key = key, Fingerprint = fingerprint, Until = now + lease });
                try
                {
                    await database.SaveChangesAsync();
                    // Timed by the database's clock, as the rows' others are, now that it's this request's.
                    await _clock.Run(
                        () => rows.Where(r => r.Key == key && r.Status == null).ExecuteUpdateAsync(s => s.SetProperty(r => r.Until, r => DateTime.UtcNow.AddSeconds(seconds))),
                        () => Task.FromResult(0));
                    return new IdempotencyClaim(IdempotencyState.Claimed);
                }
                catch (DbUpdateException)
                {
                    return null;   // another request inserted it first
                }
            }

            // Its time is up (a request that never finished, or a response kept long enough): taken over, by one request.
            var taken = await _clock.Run(
                () => rows.Where(r => r.Key == key && r.Until < DateTime.UtcNow).ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Fingerprint, fingerprint).SetProperty(r => r.Status, (int?)null).SetProperty(r => r.ContentType, (string?)null)
                    .SetProperty(r => r.Location, (string?)null).SetProperty(r => r.Body, (byte[]?)null)
                    .SetProperty(r => r.Until, r => DateTime.UtcNow.AddSeconds(seconds))),
                () => rows.Where(r => r.Key == key && r.Until < now).ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Fingerprint, fingerprint).SetProperty(r => r.Status, (int?)null).SetProperty(r => r.ContentType, (string?)null)
                    .SetProperty(r => r.Location, (string?)null).SetProperty(r => r.Body, (byte[]?)null)
                    .SetProperty(r => r.Until, now + lease)));
            if (taken == 1)
                return new IdempotencyClaim(IdempotencyState.Claimed);
            if (taken == 0 && await rows.AsNoTracking().SingleOrDefaultAsync(r => r.Key == key) is { } held)
            {
                return held.Fingerprint != fingerprint ? new IdempotencyClaim(IdempotencyState.Mismatch)
                    : held.Status is { } status ? new IdempotencyClaim(IdempotencyState.Done, new IdempotentResponse(status, held.ContentType, held.Location, held.Body ?? []))
                    : new IdempotencyClaim(IdempotencyState.Busy);
            }
            return null;   // deleted meanwhile (released): look again
        }

        public async Task CompleteAsync(string key, IdempotentResponse response, TimeSpan keep)
        {
            await using var database = open();
            var rows = database.Set<IdempotencyRow>();
            var now = PiperRuntime.Current.UtcNow;
            var seconds = keep.TotalSeconds;
            await _clock.Run(
                () => rows.Where(r => r.Key == key && r.Status == null).ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, response.Status).SetProperty(r => r.ContentType, response.ContentType)
                    .SetProperty(r => r.Location, response.Location).SetProperty(r => r.Body, response.Body)
                    .SetProperty(r => r.Until, r => DateTime.UtcNow.AddSeconds(seconds))),
                () => rows.Where(r => r.Key == key && r.Status == null).ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, response.Status).SetProperty(r => r.ContentType, response.ContentType)
                    .SetProperty(r => r.Location, response.Location).SetProperty(r => r.Body, response.Body)
                    .SetProperty(r => r.Until, now + keep)));
            // Keys whose time is long up: cleared as requests go on, so the table doesn't grow.
            await _clock.Run(
                () => rows.Where(r => r.Until < DateTime.UtcNow.AddHours(-1)).ExecuteDeleteAsync(),
                () => rows.Where(r => r.Until < now.AddHours(-1)).ExecuteDeleteAsync());
        }

        public async Task ReleaseAsync(string key)
        {
            await using var database = open();
            await database.Set<IdempotencyRow>().Where(r => r.Key == key && r.Status == null).ExecuteDeleteAsync();
        }
    }
}
