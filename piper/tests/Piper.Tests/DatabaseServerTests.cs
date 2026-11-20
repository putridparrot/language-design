using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Piper.Plugins;
using Piper.Text;
using Xunit.Abstractions;

namespace Piper.Tests;

/// <summary>
/// PostgreSQL (postgres:17-alpine) and SQL Server (mcr.microsoft.com/mssql/server:2022-latest) for the tests, each in a
/// container on a free port, started when a test first asks for it and removed when they're done. With no Docker (or
/// no image), a server's connection is null and its tests say they didn't run. <c>PIPER_POSTGRES</c> and
/// <c>PIPER_SQLSERVER</c> name servers to use instead (a connection string without a database, which the tests add).
/// </summary>
public sealed class DatabaseServers : IDisposable
{
    private const string SqlServerPassword = "Piper_tests_1";
    private readonly List<string> _containers = [];
    private readonly Lazy<string?> _postgres;
    private readonly Lazy<string?> _sqlServer;

    public DatabaseServers()
    {
        _postgres = new(() => Environment.GetEnvironmentVariable("PIPER_POSTGRES") is { Length: > 0 } given ? given : Start(
            "-e POSTGRES_PASSWORD=piper postgres:17-alpine", 5432,
            id => Docker($"exec {id} pg_isready -h 127.0.0.1 -U postgres").Ok,
            port => $"Host=127.0.0.1;Port={port};Username=postgres;Password=piper;Pooling=false"));
        _sqlServer = new(() => Environment.GetEnvironmentVariable("PIPER_SQLSERVER") is { Length: > 0 } given ? given : Start(
            $"-e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD={SqlServerPassword} mcr.microsoft.com/mssql/server:2022-latest", 1433,
            id => Docker($"exec {id} /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P {SqlServerPassword} -Q \"SELECT 1\"").Ok,
            port => $"Server=127.0.0.1,{port};User Id=sa;Password={SqlServerPassword};TrustServerCertificate=True;Encrypt=False;Pooling=false"));
    }

    /// <summary>A connection string for <paramref name="provider"/>'s server (postgres or sqlserver), without a database; null if there's none.</summary>
    public string? Server(string provider) => provider == "postgres" ? _postgres.Value : _sqlServer.Value;

    private string? Start(string arguments, int port, Func<string, bool> ready, Func<int, string> connection)
    {
        try
        {
            var (started, id) = Docker($"run -d --rm -p 127.0.0.1::{port} {arguments}");
            if (!started)
                return null;
            var container = id.Trim();
            lock (_containers)
                _containers.Add(container);
            var (_, mapping) = Docker($"port {container} {port}");
            var mapped = int.Parse(mapping.Trim().Split('\n')[0].Split(':')[^1]);
            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (!ready(container))
            {
                if (DateTime.UtcNow > deadline)
                    return null;
                Thread.Sleep(1000);
            }
            return connection(mapped);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static (bool Ok, string Output) Docker(string arguments)
    {
        var start = new ProcessStartInfo("docker", arguments) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode == 0, output);
    }

    public void Dispose()
    {
        foreach (var container in _containers)
            Docker($"rm -f {container}");
    }
}

[CollectionDefinition("database servers")]
public sealed class DatabaseServersCollection : ICollectionFixture<DatabaseServers>;

/// <summary>
/// Migrations on PostgreSQL and SQL Server, as <see cref="MigrationTests"/> checks them on SQLite: each provider's
/// scripts, applied in turn, make the schema EnsureCreated does; the baseline on a database EnsureCreated made makes
/// only what's missing; and programs migrating at once (pods starting together) apply each migration once, under the
/// database's lock.
/// </summary>
[Collection("database servers")]
public class DatabaseServerTests(DatabaseServers servers, ITestOutputHelper output)
{
    private static readonly Lazy<(ImmutableArray<IPiperPlugin> Plugins, ProgramReferences References)> Plugins = new(() =>
    {
        var (plugins, errors) = PluginLoader.Load(PluginsDirectory(), ["Piper.Plugins.Data/bin/plugin/Piper.Plugins.Data.dll"]);
        Assert.Empty(errors);
        var packages = plugins.SelectMany(p => p.Packages).ToDictionary(p => p.Key, p => p.Value);
        // SQLite's too, though these tests don't use it: a program registers its databases as providers by name, in
        // Piper.Runtime's one registry, which the tests' programs share as they run in one process. Without it, a
        // test asking for "sqlite" just after one of these registered would get theirs, which can't find SQLite.
        packages["Microsoft.EntityFrameworkCore.Sqlite"] = "10.0.14";
        packages["Microsoft.EntityFrameworkCore.SqlServer"] = "10.0.14";
        packages["Npgsql.EntityFrameworkCore.PostgreSQL"] = "10.0.3";
        var (references, referenceErrors) = ProgramReferences.Resolve(
            Path.Combine(Path.GetTempPath(), "piper-database-server-tests"), packages, [], [.. plugins.SelectMany(PluginLoader.References)]);
        Assert.Empty(referenceErrors);
        return (plugins, references);
    });

    private static string PluginsDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "plugins"));

    /// <summary>A folder of its own for a test's program, its shape and its migrations.</summary>
    private sealed class Folder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "piper-migrations", Guid.NewGuid().ToString("N"));

        public Folder() => Directory.CreateDirectory(Path);

        public DeclaredDatabase Database => new("Shop", System.IO.Path.Combine(Path, "Shop.schema.json"));

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static CompilationResult Compile(Folder folder, string source)
    {
        var path = System.IO.Path.Combine(folder.Path, "shop.piper");
        File.WriteAllText(path, source);
        var result = Compilation.Compile([new SourceText(source, path)],
            new CompilationOptions { Plugins = Plugins.Value.Plugins, References = Plugins.Value.References, WireShapes = WireShapeMode.Record });
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        return result;
    }

    /// <summary>Compiles the program's shape and writes its next migration, for every provider.</summary>
    private static void Migration(Folder folder, string source)
    {
        Compile(folder, source);
        var written = DatabaseMigrations.Write(Plugins.Value.References, folder.Database);
        Assert.Contains(written.Files, f => f.EndsWith(".sql") && f.Contains("postgres"));
        Assert.Contains(written.Files, f => f.EndsWith(".sql") && f.Contains("sqlserver"));
    }

    private static string Run(CompilationResult result)
    {
        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors) + "\n" + run.Output);
        return run.Output.ReplaceLineEndings("\n");
    }

    /// <summary>The server for <paramref name="provider"/>, or null (and why the test didn't run) if there isn't one.</summary>
    private string? Server(string provider)
    {
        var server = servers.Server(provider);
        if (server is null)
            output.WriteLine($"Not run: no {provider} server (no Docker, or no image; or set PIPER_{provider.ToUpperInvariant()}).");
        return server;
    }

    private static string Database() => $"piper_{Guid.NewGuid():N}"[..20];

    private const string Prelude = """
        using System;
        using System.Linq;
        using System.Threading.Tasks;
        using Microsoft.EntityFrameworkCore;

        newtype OrderId = int;
        enum Status { Placed, Shipped }

        """;

    private const string Order = "id: OrderId, customer: string, total: decimal, status: Status, note: string?, placed: DateTime";

    private static string Shop(string order, string tables, string main = "fn Main() { }") =>
        Prelude + $"record Order({order});\nrecord Customer(id: int, name: string, email: string?);\n\ndatabase Shop {{\n    {tables}\n}}\n\n{main}\n";

    /// <summary>Each table's columns (type, length, nullable, identity) and each index's columns, as the server describes them, outside PiperMigrations.</summary>
    private static string SchemaQuery(string provider) => provider == "postgres"
        ? """
          "SELECT c.table_name || '.' || c.column_name || ' ' || c.data_type || ' ' || coalesce(c.character_maximum_length::text, '') || ' ' || c.is_nullable || ' ' || c.is_identity AS \"Value\" FROM information_schema.columns c WHERE c.table_schema = current_schema() AND c.table_name <> 'PiperMigrations' UNION ALL SELECT tablename || ' ' || indexdef FROM pg_indexes WHERE schemaname = current_schema() AND tablename <> 'PiperMigrations' ORDER BY 1"
          """
        : """
          "SELECT t.name + '.' + c.name + ' ' + ty.name + ' ' + CAST(c.max_length AS varchar) + ' ' + CAST(c.is_nullable AS varchar) + ' ' + CAST(c.is_identity AS varchar) AS Value FROM sys.columns c JOIN sys.tables t ON c.object_id = t.object_id JOIN sys.types ty ON c.user_type_id = ty.user_type_id WHERE t.name <> 'PiperMigrations' UNION ALL SELECT t.name + '.' + i.name + ' ' + STRING_AGG(col.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal) FROM sys.indexes i JOIN sys.tables t ON i.object_id = t.object_id JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id WHERE i.name IS NOT NULL AND t.name <> 'PiperMigrations' GROUP BY t.name, i.name ORDER BY 1"
          """;

    private static string Open(string provider, string server) => $$"""
        fn Open(database: string) => Shop = Piper.Data.PiperDatabase.Factory<Shop>("{{provider}}", "{{server}};Database=" + database)();

        fn Schema(db: Shop) => string = string.Join("\n", db.Database.SqlQueryRaw<string>({{SchemaQuery(provider)}}).ToList());

        """;

    [Fact]
    public void A_packages_assembly_for_this_platform_is_the_one_referenced()
    {
        // SqlClient's lib/ assembly only throws PlatformNotSupportedException; runtimes/win (or unix)/lib has the real one.
        var sqlClient = Assert.Single(Plugins.Value.References.Assemblies, a => Path.GetFileName(a) == "Microsoft.Data.SqlClient.dll");
        var os = OperatingSystem.IsWindows() ? "win" : "unix";
        Assert.Contains($"{Path.DirectorySeparatorChar}runtimes{Path.DirectorySeparatorChar}{os}{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}", sqlClient);
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    public void Every_migration_in_turn_makes_the_schema_EnsureCreated_does(string provider)
    {
        if (Server(provider) is not { } server)
            return;
        using var folder = new Folder();
        var migrated = Database();
        const string first = "table Orders: Order key id index customer;\n    outbox;";
        Migration(folder, Shop(Order, first));
        // The first applied the CLI's way, before the others were written, as a deploy would.
        Assert.Equal(["Applied 0001-baseline"],
            DatabaseMigrations.Apply(Plugins.Value.References, folder.Database, provider, $"{server};Database={migrated}"));

        Migration(folder, Shop(Order + ", region: string?", "table Orders: Order key id index customer index region;\n    outbox;"));
        const string tables = "table Orders: Order key id index customer index region;\n    table Customers: Customer key id index email;\n    outbox;\n    idempotency;";
        Migration(folder, Shop(Order + ", region: string?", tables));

        var result = Compile(folder, Shop(Order + ", region: string?", tables, Open(provider, server) + $$"""
            fn Main() {
                using let migrated = Open("{{migrated}}");
                Print(string.Join(", ", migrated.Migrate()));
                Print(migrated.Migrate().Count);
                using let created = Open("{{Database()}}");
                created.Database.EnsureCreated();
                Print(Schema(migrated) == Schema(created));
                if Schema(migrated) != Schema(created) {
                    Print(Schema(migrated));
                    Print("----");
                    Print(Schema(created));
                }
                Print(Schema(migrated).Contains("Orders.region"));
                migrated.Orders.Add(Order(OrderId(0), "ann", 12.5m, Status.Placed, None, DateTime.UtcNow, Some("eu")));
                migrated.SaveChanges();
                Print(migrated.Orders.Single().id);
            }
            """));
        Assert.Equal("0002-orders-region-and-more, 0003-customers-and-more\n0\nTrue\nTrue\n1\n", Run(result));
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    public void The_baseline_on_a_database_EnsureCreated_made_makes_only_whats_missing(string provider)
    {
        if (Server(provider) is not { } server)
            return;
        using var folder = new Folder();
        const string tables = "table Orders: Order key id;\n    outbox;";
        Migration(folder, Shop(Order, tables));
        var result = Compile(folder, Shop(Order, tables, Open(provider, server) + $$"""
            fn Main() {
                using let db = Open("{{Database()}}");
                db.Database.EnsureCreated();
                db.Orders.Add(Order(OrderId(0), "ann", 12.5m, Status.Placed, None, DateTime.UtcNow));
                db.SaveChanges();
                db.Database.ExecuteSqlRaw("DROP TABLE \"PiperOutbox\"");
                Print(string.Join(", ", db.Migrate()));
                Print(Schema(db).Contains("PiperOutbox.Destination"));
                Print(db.Orders.Count());
            }
            """));
        Assert.Equal("0001-baseline\nTrue\n1\n", Run(result));
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    public void Programs_migrating_at_once_apply_each_migration_once(string provider)
    {
        if (Server(provider) is not { } server)
            return;
        using var folder = new Folder();
        Migration(folder, Shop(Order, "table Orders: Order key id;"));
        Migration(folder, Shop(Order + ", region: string?", "table Orders: Order key id;\n    table Customers: Customer key id;"));
        Migration(folder, Shop(Order + ", region: string?", "table Orders: Order key id index region;\n    table Customers: Customer key id;"));

        // Eight at once, each with its own connection, on a database made empty first (by a database without tables).
        var result = Compile(folder, Shop(Order + ", region: string?", "table Orders: Order key id index region;\n    table Customers: Customer key id;",
            Open(provider, server) + $$"""
            database Empty { }

            async fn Main() {
                let name = "{{Database()}}";
                using let empty = Piper.Data.PiperDatabase.Factory<Empty>("{{provider}}", "{{server}};Database=" + name)();
                empty.Database.EnsureCreated();
                let runs = Enumerable.Range(0, 8).Select(_ => Task.Run(() => {
                    using let db = Open(name);
                    db.Migrate()
                })).ToList();
                let applied = await Task.WhenAll(runs);
                Print(string.Join(", ", applied.SelectMany(a => a).Order()));
                using let db = Open(name);
                Print(string.Join(", ", db.MigrationStatus().Select(s => $"{s.Id} {s.Applied is Some(_)}")));
            }
            """));
        Assert.Equal("0001-baseline, 0002-orders-region-and-more, 0003-orders-indexes\n" +
                     "0001-baseline True, 0002-orders-region-and-more True, 0003-orders-indexes True\n", Run(result));
    }
}
