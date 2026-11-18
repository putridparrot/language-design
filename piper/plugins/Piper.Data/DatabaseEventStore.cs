using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Piper.Runtime;

namespace Piper.Data;

/// <summary>
/// Persisted actors' events in a database, through EF Core, so they outlive the process: a stock pod that restarts
/// replays its products' events and carries on. Each event is a row of the <c>PiperEvents</c> table: its stream
/// (<c>Product/tea</c>), its place in it, its JSON, and when it was saved. The stream and place are the key, so two
/// actors writing one stream at once can't both succeed: the second's save fails as a conflict, having written nothing.
/// <code>
/// let store = await DatabaseEventStore.OpenAsync(DbContextOptionsBuilder().UseSqlite("Data Source=events.db").Options);
/// let tea = Product.Spawn(store, "tea");
/// </code>
/// Any EF Core provider works (SQLite, SQL Server, PostgreSQL), and the table can share a database with the program's
/// own: opening the store makes it if it isn't there.
/// </summary>
public sealed class DatabaseEventStore : IEventStore
{
    private readonly DbContextOptions _options;

    private DatabaseEventStore(DbContextOptions options) => _options = options;

    /// <summary>The same, made as a program starts (a server's or host's <c>provider</c> clause), not awaited.</summary>
    public static DatabaseEventStore Open(DbContextOptions options) => OpenAsync(options).GetAwaiter().GetResult();

    /// <summary>
    /// The databases an app can keep its events in by configuration (<c>"Events": { "Provider": "postgres",
    /// "Connection": "…" }</c>), each through its EF Core provider, which the app brings as a package: SQLite (a local
    /// file), SQL Server (Azure SQL, Amazon RDS) and PostgreSQL (Amazon RDS and Aurora, Google Cloud SQL, Azure's).
    /// </summary>
    internal static readonly (string Name, string Package, string Extensions, string Method)[] Databases =
    [
        ("sqlite", "Microsoft.EntityFrameworkCore.Sqlite", "Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions", "UseSqlite"),
        ("sqlserver", "Microsoft.EntityFrameworkCore.SqlServer", "Microsoft.EntityFrameworkCore.SqlServerDbContextOptionsBuilderExtensions", "UseSqlServer"),
        ("postgres", "Npgsql.EntityFrameworkCore.PostgreSQL", "Microsoft.EntityFrameworkCore.NpgsqlDbContextOptionsBuilderExtensions", "UseNpgsql"),
    ];

    /// <summary>
    /// Registers the databases above as providers of <see cref="IEventStore"/>, by name (the data plugin's programs call
    /// this as they start). Each needs its <c>Connection</c> setting, and its EF Core package in the program's piper.json.
    /// </summary>
    public static void RegisterProviders()
    {
        foreach (var (name, package, extensions, method) in Databases)
            PiperProviders.Register<IEventStore>(name, settings => Open(Options(name, package, extensions, method, settings)));
    }

    /// <summary>The options of a database chosen by name, through its EF Core provider's <c>Use…</c>, found in its package.</summary>
    internal static DbContextOptions Options(string name, string package, string extensions, string method, ProviderSettings settings)
    {
        var connection = settings.Required("Connection");
        var type = Type.GetType($"{extensions}, {package}")
            ?? throw new ProviderException($"The '{name}' provider of {settings.Section} needs EF Core's {package} package: add it to piper.json's \"packages\".");
        var use = type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static).FirstOrDefault(m =>
            m.Name == method && !m.IsGenericMethod && m.GetParameters() is [{ ParameterType: var builder }, { ParameterType: var text }, ..] rest &&
            builder == typeof(DbContextOptionsBuilder) && text == typeof(string) && rest.Skip(2).All(p => p.IsOptional))
            ?? throw new ProviderException($"{package} has no {method}(DbContextOptionsBuilder, string) that this version of Piper knows.");
        var options = new DbContextOptionsBuilder();
        use.Invoke(null, [options, connection, .. use.GetParameters().Skip(2).Select(p => p.DefaultValue is DBNull ? null : p.DefaultValue)]);
        return options.Options;
    }

    /// <summary>
    /// A store in the database <paramref name="options"/> configure, with its table made if it isn't there. Several
    /// processes can open one database at once (a cluster's nodes starting together, sharing their events): one that
    /// finds another has just made the database or a table carries on.
    /// </summary>
    public static async Task<DatabaseEventStore> OpenAsync(DbContextOptions options)
    {
        var store = new DatabaseEventStore(options);
        await using var db = store.Open();
        var creator = db.GetService<IRelationalDatabaseCreator>();
        await EnsureAsync(creator.ExistsAsync, () => creator.CreateAsync());
        await EnsureAsync(() => TableExists(db.Events), () => creator.CreateTablesAsync());
        // Snapshots have a table of their own, made apart, so a database whose events were kept before them gains it.
        await using var snapshots = new SnapshotsDb(options);
        await EnsureAsync(() => TableExists(snapshots.Snapshots), () => snapshots.GetService<IRelationalDatabaseCreator>().CreateTablesAsync());
        return store;
    }

    /// <summary>Makes something with <paramref name="create"/> unless it <paramref name="exists"/>, or another process made it meanwhile.</summary>
    internal static async Task EnsureAsync(Func<CancellationToken, Task<bool>> exists, Func<Task> create)
    {
        if (await exists(default))
            return;
        try
        {
            await create();
        }
        catch (Exception)
        {
            // Made by another process between the check and this: it's there, which is all that's wanted.
            if (!await exists(default))
                throw;
        }
    }

    internal static Task EnsureAsync(Func<Task<bool>> exists, Func<Task> create) => EnsureAsync(_ => exists(), create);

    public Task<IReadOnlyList<string>> LoadAsync(string stream) => LoadAsync(stream, 0);

    public async Task<IReadOnlyList<string>> LoadAsync(string stream, int from)
    {
        await using var db = Open();
        return await db.Events.Where(e => e.Stream == stream && e.Version >= from).OrderBy(e => e.Version).Select(e => e.Json).ToListAsync();
    }

    public async Task<StoredSnapshot?> LoadSnapshotAsync(string stream)
    {
        await using var db = new SnapshotsDb(_options);
        return await db.Snapshots.Where(s => s.Stream == stream).Select(s => new StoredSnapshot(s.Version, s.Json)).FirstOrDefaultAsync();
    }

    /// <summary>Keeps the stream's latest snapshot, one row for each stream, replacing an earlier one.</summary>
    public async Task SaveSnapshotAsync(string stream, StoredSnapshot snapshot)
    {
        await using var db = new SnapshotsDb(_options);
        var kept = await db.Snapshots.FirstOrDefaultAsync(s => s.Stream == stream);
        if (kept is not null && kept.Version >= snapshot.Version)
            return;
        if (kept is null)
            db.Snapshots.Add(new StoredState { Stream = stream, Version = snapshot.Version, Json = snapshot.State, Saved = DateTime.UtcNow });
        else
            (kept.Version, kept.Json, kept.Saved) = (snapshot.Version, snapshot.State, DateTime.UtcNow);
        await db.SaveChangesAsync();
    }

    public async Task AppendAsync(string stream, int expected, IReadOnlyList<string> events)
    {
        await using var db = Open();
        var actual = await db.Events.CountAsync(e => e.Stream == stream);
        if (actual != expected)
            throw new EventStreamConflictException(stream, expected, actual);
        var saved = DateTime.UtcNow;
        for (int i = 0; i < events.Count; i++)
            db.Events.Add(new StoredEvent { Stream = stream, Version = expected + i, Json = events[i], Saved = saved });
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Another writer took the same places between the count and the save: the key refused them.
            throw new EventStreamConflictException(stream, expected, await CountAsync(stream));
        }
    }

    private async Task<int> CountAsync(string stream)
    {
        await using var db = Open();
        return await db.Events.CountAsync(e => e.Stream == stream);
    }

    /// <summary>A context for one operation: actors use the store at once, and a DbContext is for one at a time.</summary>
    private EventsDb Open() => new(_options);

    internal static async Task<bool> TableExists<T>(IQueryable<T> table)
    {
        try
        {
            await table.AnyAsync();
            return true;
        }
        catch (DbException)
        {
            return false;
        }
    }

    private sealed class StoredEvent
    {
        public string Stream { get; set; } = "";
        public int Version { get; set; }
        public string Json { get; set; } = "";
        public DateTime Saved { get; set; }
    }

    private sealed class EventsDb(DbContextOptions options) : DbContext(options)
    {
        public DbSet<StoredEvent> Events => Set<StoredEvent>();

        protected override void OnModelCreating(ModelBuilder model)
        {
            var events = model.Entity<StoredEvent>();
            events.ToTable("PiperEvents");
            events.HasKey(e => new { e.Stream, e.Version });
            events.Property(e => e.Stream).HasMaxLength(400);
        }
    }

    /// <summary>A stream's latest snapshot: its actor's state after its first <c>Version</c> events.</summary>
    private sealed class StoredState
    {
        public string Stream { get; set; } = "";
        public int Version { get; set; }
        public string Json { get; set; } = "";
        public DateTime Saved { get; set; }
    }

    private sealed class SnapshotsDb(DbContextOptions options) : DbContext(options)
    {
        public DbSet<StoredState> Snapshots => Set<StoredState>();

        protected override void OnModelCreating(ModelBuilder model)
        {
            var snapshots = model.Entity<StoredState>();
            snapshots.ToTable("PiperSnapshots");
            snapshots.HasKey(s => s.Stream);
            snapshots.Property(s => s.Stream).HasMaxLength(400);
        }
    }
}
