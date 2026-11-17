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

    /// <summary>A store in the database <paramref name="options"/> configure, with its table made if it isn't there.</summary>
    public static async Task<DatabaseEventStore> OpenAsync(DbContextOptions options)
    {
        var store = new DatabaseEventStore(options);
        await using var db = store.Open();
        var creator = db.GetService<IRelationalDatabaseCreator>();
        if (!await creator.ExistsAsync())
        {
            await creator.CreateAsync();
            await creator.CreateTablesAsync();
        }
        else if (!await TableExists(db))
        {
            await creator.CreateTablesAsync();
        }
        return store;
    }

    public async Task<IReadOnlyList<string>> LoadAsync(string stream)
    {
        await using var db = Open();
        return await db.Events.Where(e => e.Stream == stream).OrderBy(e => e.Version).Select(e => e.Json).ToListAsync();
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

    private static async Task<bool> TableExists(EventsDb db)
    {
        try
        {
            await db.Events.AnyAsync();
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
}
