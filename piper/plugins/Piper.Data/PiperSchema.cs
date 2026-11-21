using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Piper.Runtime;

namespace Piper.Data;

/// <summary>
/// A database's recorded shape (<c>TrackerDb.schema.json</c>, which the data plugin writes as the program builds) as an
/// EF Core model, and the SQL taking a database from one shape to the next, in a provider's own dialect. Each table is
/// a shared-type entity of property bags, its columns of the types they're stored as, so a model can be made from a
/// shape without the program's types: an older shape, whose types have since changed, just as well as the current one.
/// EF Core's model differ then finds the operations (<c>CreateTable</c>, <c>AddColumn</c>, …), and the provider's
/// <see cref="IMigrationsSqlGenerator"/> writes them, as <c>EnsureCreated</c> does for a whole model.
/// </summary>
public static class PiperSchema
{
    /// <summary>Where one statement of a script ends and the next begins, on a line of its own.</summary>
    public const string Separator = "GO";

    /// <summary>The first line of a statement that runs outside the migration's transaction (SQLite's <c>PRAGMA foreign_keys</c>).</summary>
    public const string NoTransaction = "-- piper: outside the transaction";

    /// <summary>The databases whose EF Core provider the program has (its package in piper.json), by name: sqlite, sqlserver, postgres.</summary>
    public static string[] Providers() =>
        [.. DatabaseEventStore.Databases.Where(d => Type.GetType($"{d.Extensions}, {d.Package}") is not null).Select(d => d.Name)];

    /// <summary>
    /// The statements taking a database of shape <paramref name="from"/> (null: an empty database) to shape
    /// <paramref name="to"/>, in <paramref name="provider"/>'s SQL, each followed by a <see cref="Separator"/> line; empty
    /// if nothing changes. The shapes are the JSON the data plugin records.
    /// </summary>
    public static string Script(string? from, string to, string provider)
    {
        var options = Options(provider);
        var target = Shape.Parse(to);
        using var targetContext = new ShapeContext(options, target);
        using var sourceContext = from is null ? null : new ShapeContext(options, Shape.Parse(from));
        var targetModel = targetContext.GetService<IDesignTimeModel>().Model;
        var differ = targetContext.GetService<IMigrationsModelDiffer>();
        var operations = differ.GetDifferences(sourceContext?.GetService<IDesignTimeModel>().Model.GetRelationalModel(), targetModel.GetRelationalModel());
        foreach (var create in operations.OfType<CreateTableOperation>())
            InDeclaredOrder(create, target);
        var commands = targetContext.GetService<IMigrationsSqlGenerator>().Generate(operations, targetModel);

        var script = new StringBuilder();
        foreach (var command in commands)
        {
            if (command.TransactionSuppressed)
                script.Append(NoTransaction).Append('\n');
            script.Append(command.CommandText.ReplaceLineEndings("\n").Trim()).Append('\n').Append(Separator).Append("\n\n");
        }
        return script.ToString();
    }

    /// <summary>
    /// A new table's columns as its record declares them, the key first, rather than EF Core's order for property bags
    /// (alphabetical), so the table reads as one <c>EnsureCreated</c> makes does. Only a new table's: databases add a
    /// column to an existing table at its end, whatever the order asked.
    /// </summary>
    private static void InDeclaredOrder(CreateTableOperation create, Shape shape)
    {
        if (!shape.Tables.TryGetValue(create.Name, out var table))
            return;
        var order = table.Key.Concat(table.Columns.Select(c => c.Name).Except(table.Key)).Select((name, i) => (name, i)).ToDictionary(c => c.name, c => c.i);
        var columns = create.Columns.OrderBy(c => order.TryGetValue(c.Name, out var i) ? i : int.MaxValue).ToList();
        create.Columns.Clear();
        create.Columns.AddRange(columns);
    }

    /// <summary>Options for <paramref name="provider"/>, to generate its SQL: a connection is never opened.</summary>
    private static DbContextOptions Options(string provider)
    {
        var database = DatabaseEventStore.Databases.FirstOrDefault(d => d.Name == provider);
        if (database.Name is null)
            throw new ProviderException($"'{provider}' isn't a database Piper knows: {string.Join(", ", DatabaseEventStore.Databases.Select(d => d.Name))}.");
        var connection = provider switch
        {
            "sqlite" => "Data Source=:memory:",
            "sqlserver" => "Server=localhost",
            _ => "Host=localhost",
        };
        return DatabaseEventStore.Options(database.Name, database.Package, database.Extensions, database.Method,
            new ProviderSettings("Database", [new("Provider", provider), new("Connection", connection)]));
    }

    /// <summary>The types a shape's columns can be stored as, by the names it records them by.</summary>
    internal static readonly Dictionary<string, Type> StoredTypes = new()
    {
        ["int"] = typeof(int),
        ["long"] = typeof(long),
        ["short"] = typeof(short),
        ["byte"] = typeof(byte),
        ["bool"] = typeof(bool),
        ["double"] = typeof(double),
        ["float"] = typeof(float),
        ["decimal"] = typeof(decimal),
        ["char"] = typeof(char),
        ["string"] = typeof(string),
        ["bytes"] = typeof(byte[]),
        ["Guid"] = typeof(Guid),
        ["DateTime"] = typeof(DateTime),
        ["DateTimeOffset"] = typeof(DateTimeOffset),
        ["DateOnly"] = typeof(DateOnly),
        ["TimeOnly"] = typeof(TimeOnly),
        ["TimeSpan"] = typeof(TimeSpan),
    };

    internal sealed record Column(string Name, Type Stored, bool Optional, bool Newtype);

    internal sealed record Table(string Name, List<string> Key, bool Generated, List<Column> Columns, List<List<string>> Indexes);

    /// <summary>A recorded shape: its tables, and Piper's own tables it has (<c>outbox</c>, <c>idempotency</c>), each at its version (<see cref="PiperTables"/>).</summary>
    internal sealed record Shape(string Text, Dictionary<string, Table> Tables, Dictionary<string, int> Piper)
    {
        public static Shape Parse(string json)
        {
            var root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("A database's shape is a JSON object.");
            var tables = new Dictionary<string, Table>();
            foreach (var (name, node) in root["tables"] as JsonObject ?? [])
            {
                if (node is not JsonObject table)
                    continue;
                var columns = new List<Column>();
                foreach (var (column, value) in table["columns"] as JsonObject ?? [])
                {
                    var stored = (string?)value?["stored"] ?? "";
                    if (!StoredTypes.TryGetValue(stored, out var type))
                        throw new FormatException($"Column '{name}.{column}' is stored as '{stored}', which isn't a type Piper stores.");
                    columns.Add(new Column(column, type, (bool?)value?["optional"] ?? false, (bool?)value?["newtype"] ?? false));
                }
                tables[name] = new Table(
                    name,
                    [.. (table["key"] as JsonArray ?? []).Select(k => (string)k!)],
                    (bool?)table["generated"] ?? false,
                    columns,
                    [.. (table["indexes"] as JsonArray ?? []).Select(i => (i as JsonArray ?? []).Select(c => (string)c!).ToList())]);
            }
            return new Shape(json, tables, PiperTables.Recorded(root["piper"]));
        }
    }

    /// <summary>A context whose model is a shape's, cached by the shape's text (EF Core caches a model per context type otherwise).</summary>
    internal sealed class ShapeContext(DbContextOptions options, Shape shape) : DbContext(options)
    {
        public Shape Shape => shape;

        protected override void OnConfiguring(DbContextOptionsBuilder builder) =>
            builder.ReplaceService<IModelCacheKeyFactory, ShapeKeys>();

        protected override void ConfigureConventions(ModelConfigurationBuilder configuration)
        {
            // A newtype's column through a converter, as Piper.Data stores a newtype (see <see cref="Newtype{T}"/>).
            foreach (var stored in shape.Tables.Values.SelectMany(t => t.Columns).Where(c => c.Newtype).Select(c => c.Stored).Distinct())
                configuration.Properties(typeof(Newtype<>).MakeGenericType(stored)).HaveConversion(typeof(NewtypeConverter<>).MakeGenericType(stored));
            PiperModel.Conventions(configuration);
        }

        protected override void OnModelCreating(ModelBuilder model)
        {
            foreach (var table in shape.Tables.Values)
            {
                model.SharedTypeEntity<Dictionary<string, object>>(table.Name, entity =>
                {
                    entity.ToTable(table.Name);
                    foreach (var column in table.Columns)
                    {
                        // A value type's optional column is its Nullable; a reference type's is said to be optional.
                        var value = column.Newtype ? typeof(Newtype<>).MakeGenericType(column.Stored) : column.Stored;
                        var type = column.Optional && value.IsValueType ? typeof(Nullable<>).MakeGenericType(value) : value;
                        var property = entity.IndexerProperty(type, column.Name);
                        if (!value.IsValueType)
                            property.IsRequired(!column.Optional);
                    }
                    if (table.Key.Count == 0)
                    {
                        entity.HasNoKey();
                    }
                    else
                    {
                        entity.HasKey([.. table.Key]);
                        foreach (var key in table.Key)
                        {
                            if (table.Generated)
                                entity.Property(key).ValueGeneratedOnAdd();
                            else
                                entity.Property(key).ValueGeneratedNever();
                        }
                    }
                    foreach (var index in table.Indexes)
                        entity.HasIndex([.. index]);
                });
            }
            // Piper's own tables as the versions the shape records, so a later version's changes are a migration too.
            foreach (var (table, version) in shape.Piper.OrderBy(p => p.Key, StringComparer.Ordinal))
                PiperTables.Configure(model, table, version);
            PiperModel.Configure(model);
        }
    }

    /// <summary>
    /// A newtype's column in a shape's model: a struct stored through a converter as its value, as a program's
    /// newtypes are (<see cref="PiperNewtypeConverter{TNewtype, TValue}"/>), so a provider makes the same column of it.
    /// It matters for a key: providers number a converted key's rows differently (SQLite without AUTOINCREMENT).
    /// </summary>
    internal readonly record struct Newtype<T>(T Value);

    internal sealed class NewtypeConverter<T>() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<Newtype<T>, T>(
        newtype => newtype.Value, value => new Newtype<T>(value));

    private sealed class ShapeKeys : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) =>
            (context.GetType(), ((ShapeContext)context).Shape.Text, designTime);
    }
}
