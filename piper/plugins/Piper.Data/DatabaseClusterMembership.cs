using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Piper.Runtime;

namespace Piper.Data;

/// <summary>
/// A cluster's members kept in a database the nodes share, so no node needs to be told the others' addresses: each
/// holds a lease, a row of the <c>PiperMembers</c> table (its cluster, its address, and when the lease runs out), which
/// it renews while it's a member and removes as it leaves. A node that crashes stops renewing, and its row runs out.
/// <code>
/// let membership = await DatabaseClusterMembership.OpenAsync(DbContextOptionsBuilder().UseNpgsql(connection).Options);
/// let cluster = await Cluster.JoinAsync(node, membership, "stock");
/// </code>
/// Chosen by configuration as the event store is (<c>"Cluster": { "Provider": "postgres", "Connection": "…" }</c>,
/// with <c>sqlite</c> and <c>sqlserver</c> too), it suits nodes that already share a database for their actors' events.
/// The table only says who may be up: the nodes still ping each other, so a node whose lease hasn't run out but which
/// doesn't answer is down, and the cluster works out its members as before. Leases are timed by the database's clock,
/// the one clock the nodes share, so nodes whose own clocks disagree still agree on whose lease has run out (a node's
/// clock only paces how often it renews and reads). Should a provider not translate the database's clock, this node's
/// is used instead.
/// </summary>
public sealed class DatabaseClusterMembership : IClusterMembership
{
    /// <summary>How long a lease lasts unless it's renewed: thirty seconds.</summary>
    public static readonly TimeSpan DefaultLease = TimeSpan.FromSeconds(30);

    private readonly DbContextOptions _options;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<(string Cluster, string Address), DateTimeOffset> _renewed = new();
    private readonly ConcurrentDictionary<string, (DateTimeOffset Read, IReadOnlyList<string> Members)> _read = new();

    private DatabaseClusterMembership(DbContextOptions options, TimeSpan lease, TimeProvider time)
    {
        if (lease <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lease), "A lease must last some time.");
        _options = options;
        Lease = lease;
        _time = time;
    }

    /// <summary>How long a node's lease lasts unless it's renewed. It's renewed every third of this, and the members read
    /// at most every sixth (the nodes' pings, not this table, notice a node going down quickly).</summary>
    public TimeSpan Lease { get; }

    /// <summary>
    /// The membership kept in the database <paramref name="options"/> configure, with its table made if it isn't there
    /// (several nodes may make it at once). <paramref name="time"/> is the clock leases are timed by, for tests.
    /// </summary>
    public static async Task<DatabaseClusterMembership> OpenAsync(DbContextOptions options, TimeSpan? lease = null, TimeProvider? time = null)
    {
        var membership = new DatabaseClusterMembership(options, lease ?? DefaultLease, time ?? TimeProvider.System);
        await using var db = new MembersDb(options);
        var creator = db.GetService<IRelationalDatabaseCreator>();
        await DatabaseEventStore.EnsureAsync(creator.ExistsAsync, () => creator.CreateAsync());
        await DatabaseEventStore.EnsureAsync(() => DatabaseEventStore.TableExists(db.Members), () => creator.CreateTablesAsync());
        return membership;
    }

    /// <summary>The same, made as a program starts (a provider chosen by configuration), not awaited.</summary>
    public static DatabaseClusterMembership Open(DbContextOptions options, TimeSpan? lease = null) =>
        OpenAsync(options, lease).GetAwaiter().GetResult();

    /// <summary>
    /// Registers the event store's databases as providers of <see cref="IClusterMembership"/> too, by the same names
    /// (<c>sqlite</c>, <c>sqlserver</c>, <c>postgres</c>): each needs <c>Connection</c>, and takes <c>Lease</c>, in
    /// seconds, if thirty won't do.
    /// </summary>
    public static void RegisterProviders()
    {
        foreach (var (name, package, extensions, method) in DatabaseEventStore.Databases)
            PiperProviders.Register<IClusterMembership>(name, settings => Open(
                DatabaseEventStore.Options(name, package, extensions, method, settings), LeaseOf(settings)));
    }

    private static TimeSpan? LeaseOf(ProviderSettings settings) =>
        settings["Lease"] is not { } text ? null
        : double.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0 ? TimeSpan.FromSeconds(seconds)
        : throw new ProviderException($"{settings.Section}:Lease must be a number of seconds, not '{text}'.");

    /// <summary>Takes or renews the node's lease, unless it was renewed within a third of the lease.</summary>
    public async Task JoinAsync(string cluster, string address)
    {
        var now = _time.GetUtcNow();
        if (_renewed.TryGetValue((cluster, address), out var renewed) && now - renewed < Lease / 3)
            return;
        var seconds = Lease.TotalSeconds;
        await using var db = new MembersDb(_options);
        var mine = db.Members.Where(m => m.Cluster == cluster && m.Address == address);
        // The expiry is the database's time plus the lease, so every node's lease is timed by the one clock they share.
        var renewedRows = await OnDatabaseClock(
            () => mine.ExecuteUpdateAsync(s => s.SetProperty(m => m.Expires, m => DateTime.UtcNow.AddSeconds(seconds))),
            () => mine.ExecuteUpdateAsync(s => s.SetProperty(m => m.Expires, (now + Lease).UtcDateTime)));
        if (renewedRows == 0)
        {
            // A new lease: written, then timed by the database's clock as a renewal is.
            db.Members.Add(new Member { Cluster = cluster, Address = address, Expires = (now + Lease).UtcDateTime });
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // Only this node writes its own row, so another took it meanwhile only if this node joined twice at once.
            }
            await OnDatabaseClock(
                () => mine.ExecuteUpdateAsync(s => s.SetProperty(m => m.Expires, m => DateTime.UtcNow.AddSeconds(seconds))),
                () => Task.FromResult(0));
        }
        _renewed[(cluster, address)] = now;
        // Leases long run out are pods long gone: cleared as nodes renew, so the table doesn't grow.
        var gone = db.Members.Where(m => m.Cluster == cluster);
        await OnDatabaseClock(
            () => gone.Where(m => m.Expires < DateTime.UtcNow.AddSeconds(-seconds)).ExecuteDeleteAsync(),
            () => gone.Where(m => m.Expires < (now - Lease).UtcDateTime).ExecuteDeleteAsync());
        _read.TryRemove(cluster, out _);
    }

    /// <summary>Leases are timed by the database's clock, so nodes whose clocks disagree still agree on them.</summary>
    private readonly DatabaseClock _clock = new();

    private Task<T> OnDatabaseClock<T>(Func<Task<T>> database, Func<Task<T>> local) => _clock.Run(database, local);

    /// <summary>The nodes whose leases haven't run out, read again at most every sixth of the lease.</summary>
    public async Task<IReadOnlyList<string>> MembersAsync(string cluster)
    {
        var now = _time.GetUtcNow();
        if (_read.TryGetValue(cluster, out var read) && now - read.Read < Lease / 6)
            return read.Members;
        var at = now.UtcDateTime;
        await using var db = new MembersDb(_options);
        var all = db.Members.Where(m => m.Cluster == cluster);
        IReadOnlyList<string> members = await OnDatabaseClock(
            () => all.Where(m => m.Expires > DateTime.UtcNow).OrderBy(m => m.Address).Select(m => m.Address).ToListAsync(),
            () => all.Where(m => m.Expires > at).OrderBy(m => m.Address).Select(m => m.Address).ToListAsync());
        _read[cluster] = (now, members);
        return members;
    }

    /// <summary>Gives up the node's lease, so the others' next read doesn't list it.</summary>
    public async Task LeaveAsync(string cluster, string address)
    {
        _renewed.TryRemove((cluster, address), out _);
        _read.TryRemove(cluster, out _);
        await using var db = new MembersDb(_options);
        await db.Members.Where(m => m.Cluster == cluster && m.Address == address).ExecuteDeleteAsync();
    }

    private sealed class Member
    {
        public string Cluster { get; set; } = "";
        public string Address { get; set; } = "";
        public DateTime Expires { get; set; }
    }

    private sealed class MembersDb(DbContextOptions options) : DbContext(options)
    {
        public DbSet<Member> Members => Set<Member>();

        protected override void OnModelCreating(ModelBuilder model)
        {
            var members = model.Entity<Member>();
            members.ToTable("PiperMembers");
            members.HasKey(m => new { m.Cluster, m.Address });
            members.Property(m => m.Cluster).HasMaxLength(200);
            members.Property(m => m.Address).HasMaxLength(400);
        }
    }
}
