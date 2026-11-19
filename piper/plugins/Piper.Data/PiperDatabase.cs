using Microsoft.EntityFrameworkCore;
using Piper.Runtime;

namespace Piper.Data;

/// <summary>
/// A server's or host's <c>database</c> clause: <c>database db: TrackerDb from "Database";</c> reads
/// <c>Database:Provider</c> (<c>sqlite</c>, the local one, if it isn't set; <c>sqlserver</c>; <c>postgres</c>) and
/// <c>Database:Connection</c>, registers the database as a scoped service (one per request, as EF Core's
/// <c>AddDbContext</c> does), and names <c>db</c>, a function making one, for the clauses after
/// (<c>relay DatabaseOutbox.Store(db);</c>). Which database a program uses is configuration, not code: the same
/// program runs on a SQLite file on a laptop and on PostgreSQL in a cloud, each provider's EF Core package brought in
/// piper.json.
/// </summary>
public static class PiperDatabase
{
    /// <summary>
    /// A function making a <typeparamref name="T"/> (a <c>database</c> declaration's class) for the database
    /// <paramref name="settings"/> choose. A provider Piper doesn't know, a missing connection or a missing package
    /// stops the program as it starts, saying which.
    /// </summary>
    /// <summary>
    /// The same for a provider and connection the program read from its configuration itself, as a <c>config</c>
    /// record's fields: <c>database db: TrackerDb(settings.provider, settings.connection);</c>.
    /// </summary>
    public static Func<T> Factory<T>(string provider, string connection) where T : DbContext =>
        Factory<T>(new ProviderSettings(typeof(T).Name, [new("Provider", provider), new("Connection", connection)]));

    public static Func<T> Factory<T>(ProviderSettings settings) where T : DbContext
    {
        var name = settings["Provider"] ?? "sqlite";
        var database = DatabaseEventStore.Databases.FirstOrDefault(d => d.Name == name);
        if (database.Name is null)
        {
            throw new ProviderException(
                $"{settings.Section}:Provider is '{name}', which isn't a database Piper knows: {string.Join(", ", DatabaseEventStore.Databases.Select(d => d.Name))}.");
        }
        var builder = new DbContextOptionsBuilder<T>();
        DatabaseEventStore.Configure(builder, database.Name, database.Package, database.Extensions, database.Method, settings);
        var options = builder.Options;
        var constructor = typeof(T).GetConstructor([typeof(DbContextOptions<T>)])
            ?? throw new ProviderException($"{typeof(T).Name} has no constructor taking its DbContextOptions: declare it with `database {typeof(T).Name} {{ … }}`.");
        return () => (T)constructor.Invoke([options]);
    }
}
