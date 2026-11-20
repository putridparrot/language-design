using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Piper.Plugins;
using Piper.Text;

namespace Piper.Tests;

/// <summary>
/// A database's shape (<c>Shop.schema.json</c>, recorded as the program builds, its breaking changes errors) and its
/// migrations: <c>piper db migrate</c>'s SQL scripts, written from the change between shapes, and applied by the
/// program (<c>db.Migrate()</c>, or a host's <c>database … migrate;</c>), once each.
/// </summary>
public class MigrationTests
{
    private static readonly Lazy<(ImmutableArray<IPiperPlugin> Plugins, ProgramReferences References)> Plugins = new(() =>
    {
        var (plugins, errors) = PluginLoader.Load(PluginsDirectory(),
            ["Piper.Plugins.Data/bin/plugin/Piper.Plugins.Data.dll", "Piper.Plugins.Hosting/bin/plugin/Piper.Plugins.Hosting.dll"]);
        Assert.Empty(errors);
        var packages = plugins.SelectMany(p => p.Packages).ToDictionary(p => p.Key, p => p.Value);
        packages["Microsoft.EntityFrameworkCore.Sqlite"] = "10.0.14";
        var (references, referenceErrors) = ProgramReferences.Resolve(
            Path.Combine(Path.GetTempPath(), "piper-migration-tests"), packages,
            [.. plugins.SelectMany(p => p.Frameworks).Distinct()], [.. plugins.SelectMany(PluginLoader.References)]);
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

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public JsonObject Schema => (JsonObject)JsonNode.Parse(System.IO.File.ReadAllText(File("Shop.schema.json")))!;

        public DeclaredDatabase Database => new("Shop", File("Shop.schema.json"));

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Writes <paramref name="source"/> as shop.piper in <paramref name="folder"/> and compiles it, as <c>piper build</c> would.</summary>
    private static CompilationResult Compile(Folder folder, string source, WireShapeMode mode = WireShapeMode.Record)
    {
        var path = folder.File("shop.piper");
        File.WriteAllText(path, source);
        return Compilation.Compile([new SourceText(source, path)],
            new CompilationOptions { Plugins = Plugins.Value.Plugins, References = Plugins.Value.References, WireShapes = mode });
    }

    private static string Run(CompilationResult result)
    {
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors) + "\n" + run.Output);
        return run.Output.ReplaceLineEndings("\n");
    }

    private const string Usings = """
        using System;
        using System.Linq;
        using Microsoft.EntityFrameworkCore;

        """;

    private const string Types = """
        newtype OrderId = int;
        enum Status { Placed, Shipped }

        """;

    private static string Shop(string order, string tables = "table Orders: Order key id index customer;", string main = "fn Main() { }") =>
        Usings + Types + $"record Order({order});\n\ndatabase Shop {{\n    {tables}\n}}\n\n{main}\n";

    private const string Order = "id: OrderId, customer: string, total: decimal, status: Status, note: string?, placed: DateTime";

    [Fact]
    public void Building_records_a_databases_shape_as_its_columns_are_stored()
    {
        using var folder = new Folder();
        var result = Compile(folder, Shop(Order, "table Orders: Order key id index customer;\n    outbox;"));
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));

        var orders = folder.Schema["tables"]!["Orders"]!;
        Assert.Equal("""["id"]""", orders["key"]!.ToJsonString());
        Assert.True((bool)orders["generated"]!);
        var columns = (JsonObject)orders["columns"]!;
        Assert.Equal(["id", "customer", "total", "status", "note", "placed"], columns.Select(c => c.Key));
        Assert.Equal("""{"type":"OrderId","stored":"int","newtype":true}""", columns["id"]!.ToJsonString());
        Assert.Equal("""{"type":"Status","stored":"string"}""", columns["status"]!.ToJsonString());
        Assert.Equal("""{"type":"string?","stored":"string","optional":true}""", columns["note"]!.ToJsonString());
        Assert.Equal("""{"type":"DateTime","stored":"DateTime"}""", columns["placed"]!.ToJsonString());
        Assert.Equal("""[["customer"]]""", orders["indexes"]!.ToJsonString());
        Assert.Equal("""["outbox"]""", folder.Schema["piper"]!.ToJsonString());
    }

    [Fact]
    public void Without_a_key_the_shape_has_the_one_EF_Core_finds()
    {
        using var folder = new Folder();
        Assert.True(Compile(folder, Shop("orderId: Guid, customer: string", "table Orders: Order;")).Success);
        var orders = folder.Schema["tables"]!["Orders"]!;
        Assert.Equal("""["orderId"]""", orders["key"]!.ToJsonString());
        Assert.Null(orders["generated"]);
    }

    [Fact]
    public void A_column_added_as_an_option_is_compatible_and_recorded()
    {
        using var folder = new Folder();
        Assert.True(Compile(folder, Shop(Order)).Success);
        var result = Compile(folder, Shop(Order + ", region: string?"));
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.NotNull(folder.Schema["tables"]!["Orders"]!["columns"]!["region"]);
    }

    [Theory]
    [InlineData("id: OrderId, customer: string, total: decimal, status: Status, placed: DateTime", "table Orders: Order key id;",
        "column 'Orders.note' is gone, which the release running now still reads and writes")]
    [InlineData(Order + ", region: string", "table Orders: Order key id;",
        "column 'Orders.region' is new and required, but the rows the release running now adds won't have it (add it as an Option, `string?`, first")]
    [InlineData("id: OrderId, customer: string, total: double, status: Status, note: string?, placed: DateTime", "table Orders: Order key id;",
        "column 'Orders.total' is stored as double now, not decimal")]
    [InlineData("id: OrderId, customer: string, total: decimal, status: Status, note: string, placed: DateTime", "table Orders: Order key id;",
        "column 'Orders.note' is required now, but the release running now leaves it empty")]
    [InlineData(Order, "table Sales: Order key id;", "table 'Orders' is gone")]
    [InlineData(Order, "table Orders: Order key (id, customer);", "table 'Orders''s key changed from (id) to (id, customer)")]
    public void Breaking_changes_to_a_database_are_errors_unless_accepted(string order, string tables, string message)
    {
        using var folder = new Folder();
        Assert.True(Compile(folder, Shop(Order, "table Orders: Order key id;")).Success);

        var result = Compile(folder, Shop(order, tables));
        Assert.False(result.Success);
        var error = Assert.Single(result.FormattedDiagnostics, d => d.Contains("PIP0503"));
        Assert.Contains($"A breaking change to Shop: {message}", error);
        Assert.Contains("build with --accept-breaking", error);

        Assert.True(Compile(folder, Shop(order, tables), WireShapeMode.AcceptBreaking).Success);
        Assert.True(Compile(folder, Shop(order, tables)).Success);
    }

    [Fact]
    public void A_column_migrations_cant_store_is_a_warning()
    {
        using var folder = new Folder();
        var result = Compile(folder, "using System.Collections.Generic;\n" + Shop("id: int, tags: List<string>", "table Orders: Order;"));
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Contains(result.FormattedDiagnostics,
            d => d.Contains("warning") && d.Contains("'Order.tags' is a List<string>, which migrations don't know how to store, so they leave it out of table 'Orders'."));
    }

    /// <summary>
    /// A program comparing the schema of a database migrated with one EnsureCreated made: each table's columns (type, NOT
    /// NULL, place in the key) and whether its key is AUTOINCREMENT, and each index's columns. Not the tables' SQL, which
    /// SQLite rewrites as columns are added.
    /// </summary>
    private const string CompareSchemas = """
        fn Open(name: string) => Shop {
            let db = Shop(DbContextOptionsBuilder<Shop>().UseSqlite($"Data Source={name};Mode=Memory;Cache=Shared").Options);
            db.Database.OpenConnection();
            db
        }

        fn Schema(db: Shop) => string {
            let tables = "m.type = 'table' AND m.name NOT LIKE 'sqlite%' AND m.name <> 'PiperMigrations'";
            let columns = db.Database.SqlQueryRaw<string>(
                $"SELECT m.name || '.' || c.name || ' ' || c.type || ' ' || c.\"notnull\" || ' ' || c.pk || ' ' || (m.sql LIKE '%AUTOINCREMENT%') AS Value FROM sqlite_master m JOIN pragma_table_info(m.name) c WHERE {tables} ORDER BY m.name, c.name").ToList();
            let indexes = db.Database.SqlQueryRaw<string>(
                "SELECT i.name || ' ' || i.tbl_name || ' ' || c.seqno || ' ' || c.name AS Value FROM sqlite_master i JOIN pragma_index_info(i.name) c WHERE i.type = 'index' AND i.name NOT LIKE 'sqlite%' ORDER BY i.name, c.seqno").ToList();
            string.Join("\n", columns.Concat(indexes))
        }

        fn Main() {
            using let migrated = Open("migrated");
            Print(string.Join(", ", migrated.Migrate()));
            Print(migrated.Migrate().Count);
            using let created = Open("created");
            created.Database.EnsureCreated();
            Print(Schema(migrated) == Schema(created));
            Print(Schema(migrated).Contains("Orders.id INTEGER 1 1 0"));
            for status in migrated.MigrationStatus() {
                Print($"{status.Id} {status.Applied is Some(_)}");
            }
        }
        """;

    [Fact]
    public void Migrations_make_the_schema_EnsureCreated_does()
    {
        using var folder = new Folder();
        Assert.True(Compile(folder, Shop(Order, "table Orders: Order key id index customer;\n    outbox;\n    idempotency;")).Success);
        var written = DatabaseMigrations.Write(Plugins.Value.References, folder.Database);
        Assert.Equal(["0001-baseline.json", "0001-baseline.sql"], written.Files.Select(Path.GetFileName));
        Assert.Empty(written.Warnings);
        var script = File.ReadAllText(folder.File("Shop.migrations/sqlite/0001-baseline.sql"));
        Assert.StartsWith("-- Shop migration 0001-baseline, for sqlite: written by `piper db migrate` from Shop.migrations/0001-baseline.json.", script);

        // Its scripts are compiled into the database's class.
        var output = Run(Compile(folder, Shop(Order, "table Orders: Order key id index customer;\n    outbox;\n    idempotency;", CompareSchemas)));
        Assert.Equal("0001-baseline\n0\nTrue\nTrue\n0001-baseline True\n", output);
    }

    [Fact]
    public void A_change_to_the_shape_is_the_next_migration_and_an_unchanged_one_writes_nothing()
    {
        using var folder = new Folder();
        Assert.True(Compile(folder, Shop(Order)).Success);
        DatabaseMigrations.Write(Plugins.Value.References, folder.Database);
        Assert.Empty(DatabaseMigrations.Write(Plugins.Value.References, folder.Database).Files);
        Assert.False(folder.Database.Changed);

        Assert.True(Compile(folder, Shop(Order + ", region: string?", "table Orders: Order key id index customer index region;")).Success);
        Assert.True(folder.Database.Changed);
        var written = DatabaseMigrations.Write(Plugins.Value.References, folder.Database);
        Assert.Equal(["0002-orders-region-and-more.json", "0002-orders-region-and-more.sql"], written.Files.Select(Path.GetFileName));
        var script = File.ReadAllText(folder.File("Shop.migrations/sqlite/0002-orders-region-and-more.sql"));
        Assert.Contains("ALTER TABLE \"Orders\" ADD \"region\" TEXT NULL;\nGO", script);
        Assert.Contains("CREATE INDEX \"IX_Orders_region\" ON \"Orders\" (\"region\");\nGO", script);

        // Named as asked.
        Assert.True(Compile(folder, Shop(Order + ", region: string?, channel: string?", "table Orders: Order key id index customer index region;")).Success);
        Assert.Equal("0003-sales-channel.json", Path.GetFileName(DatabaseMigrations.Write(Plugins.Value.References, folder.Database, "Sales channel").Files[0]));

        // Each migration applies in turn, to a database that had the ones before.
        var output = Run(Compile(folder, Shop(Order + ", region: string?, channel: string?", "table Orders: Order key id index customer index region;", CompareSchemas)));
        Assert.Equal("0001-baseline, 0002-orders-region-and-more, 0003-sales-channel\n0\nTrue\nTrue\n" +
                     "0001-baseline True\n0002-orders-region-and-more True\n0003-sales-channel True\n", output);
    }

    [Fact]
    public void The_baseline_on_a_database_made_before_migrations_makes_only_whats_missing()
    {
        using var folder = new Folder();
        const string tables = "table Orders: Order key id;\n    outbox;";
        Assert.True(Compile(folder, Shop(Order, tables)).Success);
        DatabaseMigrations.Write(Plugins.Value.References, folder.Database);

        // A database EnsureCreated made, without the outbox, as one made before the outbox was added would be.
        var output = Run(Compile(folder, Shop(Order, tables, """
            fn Main() {
                let options = DbContextOptionsBuilder<Shop>().UseSqlite("Data Source=existing;Mode=Memory;Cache=Shared").Options;
                using let db = Shop(options);
                db.Database.OpenConnection();
                db.Database.EnsureCreated();
                db.Orders.Add(Order(OrderId(0), "ann", 12.5m, Status.Placed, None, DateTime.UtcNow));
                db.SaveChanges();
                db.Database.ExecuteSqlRaw("DROP TABLE PiperOutbox");
                Print(string.Join(", ", db.Migrate()));
                Print(db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table' AND name = 'PiperOutbox'").ToList().Count);
                Print(db.Orders.Count());
            }
            """)));
        Assert.Equal("0001-baseline\n1\n1\n", output);
    }

    [Fact]
    public void A_migration_changed_after_it_was_applied_is_an_error()
    {
        using var folder = new Folder();
        Assert.True(Compile(folder, Shop(Order)).Success);
        DatabaseMigrations.Write(Plugins.Value.References, folder.Database);
        var database = folder.File("shop.db").Replace('\\', '/');
        var main = $$"""
            fn Main() {
                using let db = Shop(DbContextOptionsBuilder<Shop>().UseSqlite("Data Source={{database}};Pooling=False").Options);
                try {
                    Print(string.Join(", ", db.Migrate()));
                } catch (e: InvalidOperationException) {
                    Print(e.Message);
                }
            }
            """;
        Assert.Equal("0001-baseline\n", Run(Compile(folder, Shop(Order, main: main))));

        File.AppendAllText(folder.File("Shop.migrations/sqlite/0001-baseline.sql"), "-- edited\n");
        Assert.Equal("Migration 0001-baseline has changed since it was applied. A script applied to a database mustn't change: " +
                     "put it back as it was, and make the change in a new one (`piper db migrate`).\n", Run(Compile(folder, Shop(Order, main: main))));

        // And the CLI's way, from the folder: the same check.
        var error = Assert.Throws<InvalidOperationException>(() =>
            DatabaseMigrations.Apply(Plugins.Value.References, folder.Database, "sqlite", $"Data Source={database};Pooling=False"));
        Assert.StartsWith("Migration 0001-baseline has changed since it was applied.", error.Message);
    }

    [Fact]
    public void Applying_from_the_folder_applies_whats_waiting_and_says_so()
    {
        using var folder = new Folder();
        Assert.True(Compile(folder, Shop(Order)).Success);
        DatabaseMigrations.Write(Plugins.Value.References, folder.Database);
        var connection = $"Data Source={folder.File("cli.db")};Pooling=False";

        Assert.Equal(["  0001-baseline                            waiting"],
            DatabaseMigrations.Status(Plugins.Value.References, folder.Database, "sqlite", connection));
        Assert.Equal(["Applied 0001-baseline"], DatabaseMigrations.Apply(Plugins.Value.References, folder.Database, "sqlite", connection));
        Assert.Equal(["Nothing to apply: the database is up to date."], DatabaseMigrations.Apply(Plugins.Value.References, folder.Database, "sqlite", connection));
        Assert.StartsWith("  0001-baseline                            applied ",
            Assert.Single(DatabaseMigrations.Status(Plugins.Value.References, folder.Database, "sqlite", connection)));

        var error = Assert.Throws<InvalidOperationException>(() =>
            DatabaseMigrations.Apply(Plugins.Value.References, folder.Database, "postgres", "Host=localhost"));
        Assert.StartsWith("There are no postgres migrations", error.Message);
    }

    [Fact]
    public void A_database_without_migrations_says_how_to_write_them()
    {
        using var folder = new Folder();
        var output = Run(Compile(folder, Shop(Order, main: """
            fn Main() {
                using let db = Shop(DbContextOptionsBuilder<Shop>().UseSqlite("Data Source=:memory:").Options);
                try {
                    db.Migrate();
                } catch (e: InvalidOperationException) {
                    Print(e.Message);
                }
            }
            """)));
        Assert.Equal("Shop has no migrations yet: `piper db migrate <folder>` writes them.\n", output);
    }

    [Fact]
    public void A_hosts_database_clause_can_migrate_as_it_starts()
    {
        using var folder = new Folder();
        Assert.True(Compile(folder, Shop(Order)).Success);
        DatabaseMigrations.Write(Plugins.Value.References, folder.Database);
        var database = folder.File("hosted.db").Replace('\\', '/');
        var output = Run(Compile(folder, "using Microsoft.Extensions.DependencyInjection;\nusing Microsoft.Extensions.Hosting;\n" + Shop(Order, main: $$"""
            host App(connection: string) {
                logging off;
                database Shop("sqlite", connection) migrate;
            }

            fn Main() {
                using let app = App("Data Source={{database}};Pooling=False");
                using let scope = app.Services.CreateScope();
                let shop = scope.ServiceProvider.GetRequiredService<Shop>();
                Print(shop.Orders.Count());
                Print(string.Join(", ", shop.MigrationStatus().Select(s => s.Id)));
            }
            """)));
        Assert.Equal("0\n0001-baseline\n", output);
    }

    [Fact]
    public void A_provider_added_later_gets_scripts_for_every_migration_in_its_own_sql()
    {
        using var folder = new Folder();
        Assert.True(Compile(folder, Shop(Order)).Success);
        DatabaseMigrations.Write(Plugins.Value.References, folder.Database);
        Assert.True(Compile(folder, Shop(Order + ", region: string?")).Success);
        DatabaseMigrations.Write(Plugins.Value.References, folder.Database);

        // The program now has SQL Server's package too.
        var packages = Plugins.Value.Plugins.SelectMany(p => p.Packages).ToDictionary(p => p.Key, p => p.Value);
        packages["Microsoft.EntityFrameworkCore.Sqlite"] = "10.0.14";
        packages["Microsoft.EntityFrameworkCore.SqlServer"] = "10.0.14";
        var (references, errors) = ProgramReferences.Resolve(
            Path.Combine(Path.GetTempPath(), "piper-migration-tests-sqlserver"), packages, [], [.. Plugins.Value.Plugins.SelectMany(PluginLoader.References)]);
        Assert.Empty(errors);
        Assert.Equal(["sqlite", "sqlserver"], DatabaseMigrations.Providers(references));

        var written = DatabaseMigrations.Write(references, folder.Database);
        Assert.Equal(["0001-baseline.sql", "0002-orders-region.sql"], written.Files.Select(Path.GetFileName));
        Assert.All(written.Files, f => Assert.Equal("sqlserver", Path.GetFileName(Path.GetDirectoryName(f))));
        var baseline = File.ReadAllText(written.Files[0]);
        Assert.Contains("CREATE TABLE [Orders] (\n    [id] int NOT NULL IDENTITY,\n    [customer] nvarchar(450) NOT NULL,", baseline.ReplaceLineEndings("\n"));
        Assert.Contains("ALTER TABLE [Orders] ADD [region] nvarchar(max) NULL;", File.ReadAllText(written.Files[1]));
    }

    [Fact]
    public void Migrations_are_named_after_their_first_change()
    {
        static JsonObject Shape(string tables, string? piper = null) =>
            (JsonObject)JsonNode.Parse($$"""{ "tables": { {{tables}} } {{(piper is null ? "" : $", \"piper\": [\"{piper}\"]")}} }""")!;
        const string orders = "\"Orders\": { \"columns\": { \"id\": {}, \"customerId\": {} } }";

        Assert.Equal("orders", DatabaseMigrations.Describe(Shape(""), Shape(orders)));
        Assert.Equal("orders-region", DatabaseMigrations.Describe(Shape(orders), Shape("\"Orders\": { \"columns\": { \"id\": {}, \"customerId\": {}, \"region\": {} } }")));
        Assert.Equal("drop-orders-customer-id", DatabaseMigrations.Describe(Shape(orders), Shape("\"Orders\": { \"columns\": { \"id\": {} } }")));
        Assert.Equal("drop-orders", DatabaseMigrations.Describe(Shape(orders), Shape("")));
        Assert.Equal("idempotency", DatabaseMigrations.Describe(Shape(orders), Shape(orders, "idempotency")));
        Assert.Equal("changes", DatabaseMigrations.Describe(Shape(orders), Shape(orders)));
    }
}
