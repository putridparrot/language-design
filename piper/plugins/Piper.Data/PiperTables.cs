using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace Piper.Data;

/// <summary>
/// The tables Piper.Data keeps in a program's database (its outbox, its idempotency keys, its record of migrations),
/// each with every version it has had. A program's databases are migrated by scripts written from one recorded shape
/// to the next (<see cref="PiperSchema.Script"/>), so when a later Piper.Data changes one of its tables, the shape
/// must say which version a database has, or the change would never reach it: both shapes would make today's.
///
/// <para>
/// A shape names Piper's tables in <c>"piper"</c>. The data plugin records them by name (<c>["outbox"]</c>: this
/// Piper.Data's, whatever version that is); <c>piper db migrate</c> stamps each with its version as it writes a
/// migration (<c>{"outbox": 1}</c>, <see cref="Stamp"/>), so the next migration after a Piper upgrade is the change
/// from the version the database has to this one. A migration written before versions were stamped names them as a
/// list, which is version 1 of each: what every one was then.
/// </para>
///
/// <para>
/// <b>Changing one of Piper's tables</b> (for Piper's own developers): add its next version's configuration to
/// <see cref="Versions"/>, keeping the old ones as they were (an old version's configures the same row type, ignoring
/// what's new: <c>rows.Ignore(r => r.Attempts)</c>). The change must suit the release still running beside the new
/// one during a rolling update, as a program's own must: a table or index, or a column that's optional or has a
/// default, never one removed or renamed.
/// </para>
/// </summary>
public static class PiperTables
{
    /// <summary>Each of Piper's tables, by the name a shape gives it, and its versions' configurations, version 1 first.</summary>
    internal static readonly IReadOnlyDictionary<string, IReadOnlyList<Action<ModelBuilder>>> Versions = new Dictionary<string, IReadOnlyList<Action<ModelBuilder>>>
    {
        ["outbox"] = [DatabaseOutbox.Configure],
        ["idempotency"] = [DatabaseIdempotency.Configure],
        ["migrations"] = [PiperMigrations.Configure],
        // Not one of Piper's tables, and never in a shape the data plugin records: two versions of a table of its own,
        // so Piper's tests can check that a table's next version reaches databases before a real one needs it to.
        ["probe"] = [Probe.Version1, Probe.Version2],
    };

    /// <summary>The version of <paramref name="table"/> this Piper.Data makes.</summary>
    public static int Current(string table) =>
        Versions.TryGetValue(table, out var versions) ? versions.Count : throw Unknown(table);

    /// <summary>
    /// A shape (the JSON the data plugin records) with each of Piper's tables it names stamped with the version this
    /// Piper.Data makes, as a migration records it: <c>"piper": ["outbox"]</c> becomes <c>"piper": {"outbox": 1}</c>.
    /// One already stamped is left as it is. The text is indented as the plugin writes shapes.
    /// </summary>
    public static string Stamp(string shape)
    {
        var root = JsonNode.Parse(shape) as JsonObject ?? throw new FormatException("A database's shape is a JSON object.");
        if (root["piper"] is not JsonArray names)
            return shape;
        var stamped = new JsonObject();
        foreach (var name in names.Select(n => (string)n!))
            stamped[name] = Current(name);
        root["piper"] = stamped;
        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";
    }

    /// <summary>
    /// The versions of Piper's tables a migration's shape records: stamped (<c>{"outbox": 2}</c>), or named before
    /// versions were (<c>["outbox"]</c>), which is version 1 of each.
    /// </summary>
    internal static Dictionary<string, int> Recorded(JsonNode? piper) => piper switch
    {
        JsonObject stamped => stamped.ToDictionary(p => p.Key, p => (int)p.Value!),
        JsonArray names => names.Select(n => (string)n!).ToDictionary(n => n, _ => 1),
        _ => [],
    };

    /// <summary>Configures <paramref name="table"/> as its <paramref name="version"/> was.</summary>
    internal static void Configure(ModelBuilder model, string table, int version)
    {
        if (!Versions.TryGetValue(table, out var versions))
            throw Unknown(table);
        if (version < 1 || version > versions.Count)
        {
            throw new FormatException(
                $"The shape has Piper's {table} table at version {version}, but this Piper.Data has versions 1 to {versions.Count}: " +
                "was the migration written by a later Piper? Use that one.");
        }
        versions[version - 1](model);
    }

    private static FormatException Unknown(string table) =>
        new($"'{table}' isn't one of Piper's tables: {string.Join(", ", Versions.Keys.Where(k => k != "probe"))}.");

    /// <summary>The test table's two versions: the second adds an optional column and an index.</summary>
    private static class Probe
    {
        public static void Version1(ModelBuilder model) => model.SharedTypeEntity<Dictionary<string, object>>("PiperProbe", rows =>
        {
            rows.ToTable("PiperProbe");
            rows.IndexerProperty<int>("Id").ValueGeneratedNever();
            rows.HasKey("Id");
        });

        public static void Version2(ModelBuilder model) => model.SharedTypeEntity<Dictionary<string, object>>("PiperProbe", rows =>
        {
            rows.ToTable("PiperProbe");
            rows.IndexerProperty<int>("Id").ValueGeneratedNever();
            rows.IndexerProperty<string>("Note").HasMaxLength(100).IsRequired(false);
            rows.HasKey("Id");
            rows.HasIndex("Note");
        });
    }
}
