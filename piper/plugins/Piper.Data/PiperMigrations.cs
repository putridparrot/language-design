using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Piper.Runtime;

namespace Piper.Data;

/// <summary>
/// One script of a database's migrations, <c>TrackerDb.migrations/sqlite/0002-issues-priority.sql</c>, as
/// <c>piper db migrate</c> wrote it: SQL for one provider, its statements separated by <see cref="PiperSchema.Separator"/>
/// lines. A database's class has its scripts compiled in (its <c>Migrations</c>), so a program carries them.
/// </summary>
public sealed record PiperMigration(string Provider, int Number, string Name, string Script)
{
    /// <summary>As its file is named: <c>0002-issues-priority</c>.</summary>
    public string Id => $"{Number:D4}-{Name}";

    /// <summary>The script's SHA-256, whatever its line endings (git may change them), to tell if it changed after it was applied.</summary>
    public string Hash => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Script.ReplaceLineEndings("\n"))));

    private static readonly Regex FileName = new(@"^(\d+)-(.+)\.sql$", RegexOptions.CultureInvariant);

    /// <summary>The scripts in <paramref name="folder"/> (one provider's), in order.</summary>
    public static IReadOnlyList<PiperMigration> Read(string folder, string provider)
    {
        if (!Directory.Exists(folder))
            return [];
        var scripts = new List<PiperMigration>();
        foreach (var path in Directory.GetFiles(folder, "*.sql"))
        {
            if (FileName.Match(Path.GetFileName(path)) is not { Success: true } match)
                continue;
            scripts.Add(new PiperMigration(provider, int.Parse(match.Groups[1].Value), match.Groups[2].Value, File.ReadAllText(path)));
        }
        if (scripts.GroupBy(s => s.Number).FirstOrDefault(g => g.Count() > 1) is { } twice)
            throw new InvalidOperationException($"{folder} has two migrations numbered {twice.Key}: {string.Join(" and ", twice.Select(s => s.Id))}.");
        return [.. scripts.OrderBy(s => s.Number)];
    }
}

/// <summary>A migration, and when it was applied to a database, if it has been.</summary>
public sealed record PiperMigrationStatus(int Number, string Name, DateTimeOffset? Applied)
{
    public string Id => $"{Number:D4}-{Name}";
}

/// <summary>A row of <c>PiperMigrations</c>: a migration applied to the database.</summary>
public sealed class PiperMigrationRow
{
    public int Number { get; set; }

    public string Name { get; set; } = "";

    public string Hash { get; set; } = "";

    /// <summary>When, as ISO 8601 text, which every provider stores alike.</summary>
    public string Applied { get; set; } = "";
}

/// <summary>
/// Applies a database's migrations: each script not yet applied, in order, in a transaction with the row recording it
/// in <c>PiperMigrations</c> (its number, name and hash). A script whose hash has changed since it was applied is an
/// error, as is a database with a migration the program doesn't have. Applying holds a lock where the database has one
/// (SQL Server's and PostgreSQL's application locks; SQLite's write lock, each migration's transaction taking it), so
/// two processes starting together don't both apply one.
/// <para>
/// The first migration, the baseline, may meet a database made before there were migrations (by <c>EnsureCreated</c>):
/// its statements making a table or an index already there are left out, and the rest (a table added since) run.
/// </para>
/// </summary>
public static class PiperMigrations
{
    private const string Table = "PiperMigrations";

    /// <summary>The table of applied migrations, in a model.</summary>
    public static void Configure(ModelBuilder model)
    {
        var rows = model.Entity<PiperMigrationRow>();
        rows.ToTable(Table);
        rows.HasKey(r => r.Number);
        rows.Property(r => r.Number).ValueGeneratedNever();
        rows.Property(r => r.Name).HasMaxLength(200);
        rows.Property(r => r.Hash).HasMaxLength(64);
        rows.Property(r => r.Applied).HasMaxLength(40);
    }

    /// <summary>
    /// Applies those of <paramref name="migrations"/> (a database class's <c>Migrations</c>) for
    /// <paramref name="db"/>'s provider that aren't applied yet, returning their ids.
    /// </summary>
    public static IReadOnlyList<string> Migrate(DbContext db, IEnumerable<PiperMigration> migrations)
    {
        var provider = Provider(db);
        return Run(db, provider, Scripts(db, provider, migrations), apply: true).Applied;
    }

    /// <summary>
    /// A server's or host's <c>database … migrate;</c>: a database from <paramref name="open"/>, its waiting migrations
    /// applied by <paramref name="migrate"/> (its class's <c>Migrate</c>), and closed again.
    /// </summary>
    public static IReadOnlyList<string> Migrate<T>(Func<T> open, Func<T, IReadOnlyList<string>> migrate) where T : DbContext
    {
        using var db = open();
        return migrate(db);
    }

    /// <summary>Each of <paramref name="migrations"/> for <paramref name="db"/>'s provider, and whether it's applied.</summary>
    public static IReadOnlyList<PiperMigrationStatus> Status(DbContext db, IEnumerable<PiperMigration> migrations)
    {
        var provider = Provider(db);
        return Run(db, provider, Scripts(db, provider, migrations), apply: false).Status;
    }

    /// <summary>For <c>piper db migrate --apply</c>: applies <paramref name="folder"/>'s scripts, returning what it did, a line each.</summary>
    public static string[] ApplyFolder(string provider, string connection, string folder)
    {
        var scripts = Folder(folder, provider);
        using var db = Open(provider, connection);
        var (applied, _) = Run(db, provider, scripts, apply: true);
        return applied.Count == 0 ? ["Nothing to apply: the database is up to date."] : [.. applied.Select(id => $"Applied {id}")];
    }

    /// <summary>For <c>piper db status</c>: each of <paramref name="folder"/>'s scripts, applied (and when) or waiting.</summary>
    public static string[] StatusFolder(string provider, string connection, string folder)
    {
        var scripts = Folder(folder, provider);
        using var db = Open(provider, connection);
        var (_, status) = Run(db, provider, scripts, apply: false);
        return [.. status.Select(s => $"  {s.Id,-40} {(s.Applied is { } when ? $"applied {when:yyyy-MM-dd HH:mm:ss}Z" : "waiting")}")];
    }

    private static IReadOnlyList<PiperMigration> Folder(string folder, string provider) =>
        PiperMigration.Read(folder, provider) is { Count: > 0 } scripts
            ? scripts
            : throw new InvalidOperationException($"There are no {provider} migrations, in {folder}: `piper db migrate` writes them, for each provider whose EF Core package the program has.");

    private static DbContext Open(string provider, string connection)
    {
        var database = DatabaseEventStore.Databases.FirstOrDefault(d => d.Name == provider);
        if (database.Name is null)
            throw new ProviderException($"'{provider}' isn't a database Piper knows: {string.Join(", ", DatabaseEventStore.Databases.Select(d => d.Name))}.");
        return new DbContext(DatabaseEventStore.Options(database.Name, database.Package, database.Extensions, database.Method,
            new ProviderSettings("Database", [new("Provider", provider), new("Connection", connection)])));
    }

    /// <summary>The name Piper knows <paramref name="db"/>'s provider by: sqlite, sqlserver, postgres.</summary>
    private static string Provider(DbContext db)
    {
        var name = db.Database.ProviderName;
        return DatabaseEventStore.Databases.FirstOrDefault(d => d.Package == name).Name
            ?? throw new InvalidOperationException($"{db.GetType().Name} is on {name}, which Piper has no migrations for.");
    }

    private static List<PiperMigration> Scripts(DbContext db, string provider, IEnumerable<PiperMigration> migrations)
    {
        var all = migrations.ToList();
        if (all.Count == 0)
            throw new InvalidOperationException($"{db.GetType().Name} has no migrations yet: `piper db migrate <folder>` writes them.");
        var scripts = all.Where(m => m.Provider == provider).OrderBy(m => m.Number).ToList();
        if (scripts.Count == 0)
        {
            throw new InvalidOperationException(
                $"{db.GetType().Name} has migrations for {string.Join(", ", all.Select(m => m.Provider).Distinct())}, not {provider}: " +
                $"add {provider}'s EF Core package to piper.json and run `piper db migrate <folder>` to write them.");
        }
        return scripts;
    }

    private static (List<string> Applied, List<PiperMigrationStatus> Status) Run(DbContext db, string provider, IReadOnlyList<PiperMigration> scripts, bool apply)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened)
            connection.Open();
        try
        {
            Lock(connection, provider, take: true);
            try
            {
                // Status only reads: a database never migrated has no table of migrations, and none applied.
                if (apply)
                    EnsureTable(connection, provider);
                var rows = apply || Exists(connection, null, provider, "TABLE", Table) ? Rows(connection, null) : [];
                Check(rows, scripts);
                var applied = new List<string>();
                if (apply)
                {
                    foreach (var script in scripts.Where(s => !rows.ContainsKey(s.Number)))
                    {
                        if (Apply(connection, provider, script, baseline: script == scripts[0]))
                            applied.Add(script.Id);
                    }
                    rows = Rows(connection, null);
                }
                var status = scripts.Select(s => new PiperMigrationStatus(s.Number, s.Name,
                    rows.TryGetValue(s.Number, out var row) && DateTimeOffset.TryParse(row.Applied, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var when) ? when : null)).ToList();
                return (applied, status);
            }
            finally
            {
                Lock(connection, provider, take: false);
            }
        }
        finally
        {
            if (opened)
                connection.Close();
        }
    }

    /// <summary>Applied migrations must still be the program's, unchanged.</summary>
    private static void Check(Dictionary<int, PiperMigrationRow> rows, IReadOnlyList<PiperMigration> scripts)
    {
        var byNumber = scripts.ToDictionary(s => s.Number);
        foreach (var row in rows.Values.OrderBy(r => r.Number))
        {
            if (!byNumber.TryGetValue(row.Number, out var script))
            {
                throw new InvalidOperationException(
                    $"The database has migration {row.Number:D4}-{row.Name}, which this program doesn't: it was applied by another version of it.");
            }
            if (script.Hash != row.Hash)
            {
                throw new InvalidOperationException(
                    $"Migration {script.Id} has changed since it was applied. A script applied to a database mustn't change: " +
                    "put it back as it was, and make the change in a new one (`piper db migrate`).");
            }
        }
    }

    /// <summary>Applies one script, in a transaction with its row; false if another process applied it meanwhile.</summary>
    private static bool Apply(DbConnection connection, string provider, PiperMigration script, bool baseline)
    {
        var transaction = connection.BeginTransaction();
        try
        {
            if (Rows(connection, transaction).ContainsKey(script.Number))
            {
                transaction.Rollback();
                return false;
            }
            foreach (var (sql, outside) in Statements(script.Script))
            {
                if (baseline && Creates(sql) is { } made && Exists(connection, transaction, provider, made.Kind, made.Name))
                    continue;
                if (outside)
                {
                    transaction.Commit();
                    transaction.Dispose();
                    Execute(connection, null, sql);
                    transaction = connection.BeginTransaction();
                    continue;
                }
                Execute(connection, transaction, sql);
            }
            Execute(connection, transaction, $"""INSERT INTO "{Table}" ("Number", "Name", "Hash", "Applied") VALUES (@number, @name, @hash, @applied)""",
                ("number", script.Number), ("name", script.Name), ("hash", script.Hash), ("applied", DateTimeOffset.UtcNow.ToString("o")));
            transaction.Commit();
            return true;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            transaction.Rollback();
            throw new InvalidOperationException($"Migration {script.Id} failed, and was rolled back: {ex.Message}", ex);
        }
        finally
        {
            transaction.Dispose();
        }
    }

    /// <summary>A script's statements, and whether each runs outside the transaction.</summary>
    internal static IEnumerable<(string Sql, bool Outside)> Statements(string script)
    {
        var lines = new List<string>();
        var outside = false;
        foreach (var line in script.ReplaceLineEndings("\n").Split('\n').Append(PiperSchema.Separator))
        {
            var trimmed = line.Trim();
            if (trimmed.Equals(PiperSchema.Separator, StringComparison.OrdinalIgnoreCase))
            {
                if (lines.Any(l => l.Trim() is { Length: > 0 } text && !text.StartsWith("--", StringComparison.Ordinal)))
                    yield return (string.Join("\n", lines).Trim(), outside);
                lines.Clear();
                outside = false;
                continue;
            }
            if (trimmed == PiperSchema.NoTransaction)
                outside = true;
            else
                lines.Add(line);
        }
    }

    private static readonly Regex Create = new(
        @"^\s*(?:--[^\n]*\n\s*)*CREATE\s+(?:UNIQUE\s+)?(TABLE|INDEX)\s+(?:IF\s+NOT\s+EXISTS\s+)?[\[""`]?([^\s\]""`(]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The table or index a statement makes, if it makes one.</summary>
    private static (string Kind, string Name)? Creates(string sql) =>
        Create.Match(sql) is { Success: true } match ? (match.Groups[1].Value.ToUpperInvariant(), match.Groups[2].Value) : null;

    private static bool Exists(DbConnection connection, DbTransaction? transaction, string provider, string kind, string name)
    {
        var sql = (provider, kind) switch
        {
            ("sqlite", "TABLE") => "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name",
            ("sqlite", _) => "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = @name",
            ("sqlserver", "TABLE") => "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = @name",
            ("sqlserver", _) => "SELECT COUNT(*) FROM sys.indexes WHERE name = @name",
            ("postgres", "TABLE") => "SELECT COUNT(*) FROM information_schema.tables WHERE table_name = @name AND table_schema = current_schema()",
            _ => "SELECT COUNT(*) FROM pg_indexes WHERE indexname = @name AND schemaname = current_schema()",
        };
        using var command = Command(connection, transaction, sql, ("name", name));
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    /// <summary>Makes <c>PiperMigrations</c> if it isn't there, in the provider's SQL.</summary>
    private static void EnsureTable(DbConnection connection, string provider)
    {
        using var transaction = connection.BeginTransaction();
        if (!Exists(connection, transaction, provider, "TABLE", Table))
        {
            foreach (var (sql, _) in Statements(PiperSchema.Script(null, """{ "piper": ["migrations"] }""", provider)))
                Execute(connection, transaction, sql);
        }
        transaction.Commit();
    }

    private static Dictionary<int, PiperMigrationRow> Rows(DbConnection connection, DbTransaction? transaction)
    {
        using var command = Command(connection, transaction, $"""SELECT "Number", "Name", "Hash", "Applied" FROM "{Table}" """);
        using var reader = command.ExecuteReader();
        var rows = new Dictionary<int, PiperMigrationRow>();
        while (reader.Read())
        {
            var row = new PiperMigrationRow { Number = reader.GetInt32(0), Name = reader.GetString(1), Hash = reader.GetString(2), Applied = reader.GetString(3) };
            rows[row.Number] = row;
        }
        return rows;
    }

    /// <summary>Takes or releases the database's lock on migrating, where it has one; SQLite's transactions take its write lock.</summary>
    private static void Lock(DbConnection connection, string provider, bool take)
    {
        var sql = (provider, take) switch
        {
            ("sqlserver", true) => $"EXEC sp_getapplock @Resource = '{Table}', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 600000",
            ("sqlserver", false) => $"EXEC sp_releaseapplock @Resource = '{Table}', @LockOwner = 'Session'",
            ("postgres", true) => "SELECT pg_advisory_lock(7310426541839)",
            ("postgres", false) => "SELECT pg_advisory_unlock(7310426541839)",
            _ => null,
        };
        if (sql is not null)
            Execute(connection, null, sql);
    }

    private static void Execute(DbConnection connection, DbTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        command.ExecuteNonQuery();
    }

    private static DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@" + name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        return command;
    }
}
