using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Piper;

/// <summary>A migration of a database, recorded beside its declaration: <c>TrackerDb.migrations/0002-issues-priority.json</c>.</summary>
/// <param name="Shape">The shape it migrates the database to, as the data plugin records shapes.</param>
public sealed record RecordedMigration(int Number, string Name, string Shape)
{
    public string Id => $"{Number:D4}-{Name}";
}

/// <summary>A database a program declares: its recorded shape, <c>TrackerDb.schema.json</c>, and its migrations' folder beside it.</summary>
public sealed record DeclaredDatabase(string Name, string Schema)
{
    public string Migrations => Path.Combine(Path.GetDirectoryName(Schema)!, $"{Name}.migrations");

    /// <summary>The migrations recorded so far, in order.</summary>
    public IReadOnlyList<RecordedMigration> Recorded() => DatabaseMigrations.Recorded(Migrations);

    /// <summary>Whether its shape has changed since its last migration (or it has none), so <c>piper db migrate</c> would write one.</summary>
    public bool Changed => Recorded() is var recorded && (recorded.Count == 0 || !DatabaseMigrations.Same(recorded[^1].Shape, File.ReadAllText(Schema)));
}

/// <summary>What <see cref="DatabaseMigrations.Write"/> did: the files it wrote, and what it warns of.</summary>
public sealed record WrittenMigrations(ImmutableArray<string> Files, ImmutableArray<string> Warnings);

/// <summary>
/// A program's databases' migrations, for <c>piper db</c>. Building records each database's shape in
/// <c>TrackerDb.schema.json</c> (the data plugin); <see cref="Write"/> compares it with the shape the last migration
/// reached, kept in <c>TrackerDb.migrations/</c> as <c>0002-issues-priority.json</c>, and when it changed, writes the
/// next migration: that shape, and a SQL script for each provider the program has the EF Core package of
/// (<c>sqlite/0002-issues-priority.sql</c>), for review. A provider added later gets scripts for every migration before
/// too, from their shapes. The SQL comes from Piper.Data in the program's references (which have EF Core and the
/// providers), loaded as a run loads them; failures there are <see cref="InvalidOperationException"/>s saying why.
/// </summary>
public static partial class DatabaseMigrations
{
    /// <summary>The databases whose shapes the data plugin recorded beside <paramref name="sourceFiles"/>, by name.</summary>
    public static IReadOnlyList<DeclaredDatabase> Databases(IEnumerable<string> sourceFiles) =>
        [.. sourceFiles.Select(f => Path.GetDirectoryName(Path.GetFullPath(f))!).Distinct()
            .SelectMany(directory => Directory.GetFiles(directory, "*.schema.json"))
            .Select(path => new DeclaredDatabase(Path.GetFileName(path)[..^".schema.json".Length], path))
            .OrderBy(d => d.Name, StringComparer.Ordinal)];

    /// <summary>The databases the program has the EF Core provider of: sqlite, sqlserver, postgres.</summary>
    public static IReadOnlyList<string> Providers(ProgramReferences references) =>
        (string[])Call(references, "Piper.Data.PiperSchema", "Providers")!;

    /// <summary>
    /// Writes <paramref name="database"/>'s next migration if its shape changed (named <paramref name="name"/>, or after
    /// its first change, or <c>baseline</c> for the first), and any script a provider is missing (one added since).
    /// </summary>
    public static WrittenMigrations Write(ProgramReferences references, DeclaredDatabase database, string? name = null)
    {
        var providers = Providers(references);
        if (providers.Count == 0)
        {
            throw new InvalidOperationException(
                $"{database.Name}: the program has no database provider's EF Core package to write SQL for: add one to piper.json's \"packages\", " +
                "such as \"Microsoft.EntityFrameworkCore.Sqlite\".");
        }
        var files = new List<string>();
        var warnings = new List<string>();
        var folder = database.Migrations;
        foreach (var stale in Directory.Exists(folder) ? Directory.GetDirectories(folder).Select(Path.GetFileName).OfType<string>().Except(providers) : [])
            warnings.Add($"{database.Name}'s {stale} scripts aren't being written: the program doesn't have that provider's EF Core package.");

        var recorded = Recorded(folder).ToList();
        var now = File.ReadAllText(database.Schema).ReplaceLineEndings("\n");
        if (recorded.Count == 0 || !Same(recorded[^1].Shape, now))
        {
            var number = recorded.Count == 0 ? 1 : recorded[^1].Number + 1;
            var title = name is not null ? Slug(name)
                : recorded.Count == 0 ? "baseline"
                : Describe(JsonNode.Parse(recorded[^1].Shape) as JsonObject, JsonNode.Parse(now) as JsonObject);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"{number:D4}-{title}.json");
            File.WriteAllText(path, now);
            files.Add(path);
            recorded.Add(new RecordedMigration(number, title, now));
        }

        // Each provider's script for each migration it hasn't got: the new one, or every one for a provider added.
        foreach (var provider in providers)
        {
            for (int i = 0; i < recorded.Count; i++)
            {
                var migration = recorded[i];
                var path = Path.Combine(folder, provider, $"{migration.Id}.sql");
                if (File.Exists(path))
                    continue;
                var sql = (string)Call(references, "Piper.Data.PiperSchema", "Script", i == 0 ? null : recorded[i - 1].Shape, migration.Shape, provider)!;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path,
                    $"-- {database.Name} migration {migration.Id}, for {provider}: written by `piper db migrate` from {database.Name}.migrations/{migration.Id}.json.\n" +
                    "-- Review it before it's applied; once it has been, leave it as it is (a change is a new migration).\n\n" + sql);
                files.Add(path);
            }
        }
        return new WrittenMigrations([.. files], [.. warnings]);
    }

    /// <summary>Applies the migrations a database (<paramref name="provider"/>'s, at <paramref name="connection"/>) doesn't have yet: a line for each.</summary>
    public static IReadOnlyList<string> Apply(ProgramReferences references, DeclaredDatabase database, string provider, string connection) =>
        (string[])Call(references, "Piper.Data.PiperMigrations", "ApplyFolder", provider, connection, Path.Combine(database.Migrations, provider))!;

    /// <summary>Which migrations a database has, a line for each.</summary>
    public static IReadOnlyList<string> Status(ProgramReferences references, DeclaredDatabase database, string provider, string connection) =>
        (string[])Call(references, "Piper.Data.PiperMigrations", "StatusFolder", provider, connection, Path.Combine(database.Migrations, provider))!;

    /// <summary>The migrations recorded in <paramref name="folder"/>, in order.</summary>
    public static IReadOnlyList<RecordedMigration> Recorded(string folder)
    {
        if (!Directory.Exists(folder))
            return [];
        return [.. Directory.GetFiles(folder, "*.json")
            .Select(path => (Path: path, Match: MigrationFile().Match(Path.GetFileName(path))))
            .Where(m => m.Match.Success)
            .Select(m => new RecordedMigration(int.Parse(m.Match.Groups[1].Value), m.Match.Groups[2].Value, File.ReadAllText(m.Path)))
            .OrderBy(m => m.Number)];
    }

    internal static bool Same(string a, string b) => JsonNode.DeepEquals(JsonNode.Parse(a), JsonNode.Parse(b));

    /// <summary>A name for the change from <paramref name="before"/> to <paramref name="now"/>, from its first part: <c>issues-priority</c>.</summary>
    public static string Describe(JsonObject? before, JsonObject? now)
    {
        var oldTables = before?["tables"] as JsonObject ?? [];
        var newTables = now?["tables"] as JsonObject ?? [];
        var names = new List<string>();
        foreach (var (table, node) in newTables)
        {
            if (!oldTables.ContainsKey(table))
            {
                names.Add(table);
                continue;
            }
            var oldColumns = oldTables[table]?["columns"] as JsonObject ?? [];
            var newColumns = node?["columns"] as JsonObject ?? [];
            names.AddRange(newColumns.Where(c => !oldColumns.ContainsKey(c.Key)).Select(c => $"{table}-{c.Key}"));
            names.AddRange(oldColumns.Where(c => !newColumns.ContainsKey(c.Key)).Select(c => $"drop-{table}-{c.Key}"));
            if (!JsonNode.DeepEquals(oldTables[table]?["indexes"], node?["indexes"]))
                names.Add($"{table}-indexes");
        }
        names.AddRange(oldTables.Where(t => !newTables.ContainsKey(t.Key)).Select(t => $"drop-{t.Key}"));
        var piper = (now?["piper"] as JsonArray ?? []).Select(p => (string?)p).Except((before?["piper"] as JsonArray ?? []).Select(p => (string?)p));
        names.AddRange(piper.OfType<string>());
        return names.Count == 0 ? "changes" : Slug(names[0] + (names.Count > 1 ? "-and-more" : ""));
    }

    /// <summary>Lower case, words joined by dashes, as a migration's file is named: <c>Issues-projectId</c> is <c>issues-project-id</c>.</summary>
    public static string Slug(string text) =>
        Regex.Replace(Regex.Replace(text, "([a-z0-9])([A-Z])", "$1-$2"), "[^A-Za-z0-9]+", "-").Trim('-').ToLowerInvariant() is { Length: > 0 } slug ? slug : "changes";

    [GeneratedRegex(@"^(\d+)-(.+)\.json$")]
    private static partial Regex MigrationFile();

    /// <summary>
    /// Calls a static method of Piper.Data, as the program's references load it (with EF Core and the providers it has),
    /// passing and getting back only strings, which every context shares.
    /// </summary>
    private static object? Call(ProgramReferences references, string type, string method, params object?[] arguments)
    {
        if (!references.Assemblies.Any(a => Path.GetFileNameWithoutExtension(a).Equals("Piper.Data", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The program doesn't use the data plugin, whose databases have migrations: add \"data\" to piper.json's \"plugins\".");
        var context = ReferenceLoadContext.For(references.Assemblies);
        context.UseNativeLibraries(references.NativeLibraries);
        using (context.EnterContextualReflection())
        {
            var assembly = context.LoadFromAssemblyName(new AssemblyName("Piper.Data"));
            var found = assembly.GetType(type)?.GetMethod(method, BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException($"This Piper.Data has no {type}.{method}.");
            try
            {
                return found.Invoke(null, arguments);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is { } inner)
            {
                throw new InvalidOperationException(inner.Message, inner);
            }
        }
    }
}
