using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Piper.Diagnostics;
using Piper.Plugins;
using Piper.Text;

namespace Piper.Tests;

/// <summary>
/// The data plugin (plugins/Piper.Plugins.Data): `database` declarations over EF Core, run against SQLite in memory.
/// EF Core and its SQLite provider are restored as a program's packages would be (from the NuGet cache when they're
/// there), with SQLite's native library loaded for the run.
/// </summary>
[Collection("data plugin")]
public class DataPluginTests
{
    private static readonly Lazy<(ImmutableArray<IPiperPlugin> Plugins, ProgramReferences References)> Data = new(() =>
    {
        var (plugins, errors) = PluginLoader.Load(PluginsDirectory(), ["Piper.Plugins.Data/bin/plugin/Piper.Plugins.Data.dll"]);
        Assert.Empty(errors);
        var packages = plugins.SelectMany(p => p.Packages).ToDictionary(p => p.Key, p => p.Value);
        packages["Microsoft.EntityFrameworkCore.Sqlite"] = "10.0.14";
        var (references, referenceErrors) = ProgramReferences.Resolve(
            Path.Combine(Path.GetTempPath(), "piper-data-tests"), packages, [], [.. plugins.SelectMany(PluginLoader.References)]);
        Assert.Empty(referenceErrors);
        return (plugins, references);
    });

    private static string PluginsDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "plugins"));

    internal static CompilationResult Compile(string text) =>
        Compilation.Compile([new SourceText(text, "main.piper")],
            new CompilationOptions { Plugins = Data.Value.Plugins, References = Data.Value.References });

    internal static string Run(string text)
    {
        var result = Compile(text);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Empty(result.Diagnostics);
        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        return run.Output.Replace("\r\n", "\n");
    }

    internal const string Shop = """
        using System;
        using System.Linq;
        using System.Threading.Tasks;
        using Microsoft.EntityFrameworkCore;

        class Customer(id: int, name: string) {
            public let id = id;
            public mut name = name;
            public mut email: string? = None;
        }

        record Order(id: int, customerId: int, total: decimal, note: string?);

        database Shop {
            table Customers: Customer key id;
            table Orders: Order key id index customerId index (customerId, total);
        }

        async fn Open() => Task<Shop> {
            let shop = Shop(DbContextOptionsBuilder<Shop>().UseSqlite("Data Source=:memory:").Options);
            await shop.Database.OpenConnectionAsync();
            await shop.Database.EnsureCreatedAsync();
            let ada = Customer(1, "Ada");
            ada.email = Some("ada@example.com");
            shop.Customers.Add(ada);
            shop.Customers.Add(Customer(2, "Alan"));
            shop.Orders.Add(Order(1, 1, 150m, Some("gift")));
            shop.Orders.Add(Order(2, 2, 50m, None));
            await shop.SaveChangesAsync();
            shop.ChangeTracker.Clear();
            shop
        }

        """;

    [Fact]
    public void A_database_stores_and_queries_records_and_classes_in_SQL()
    {
        var output = Run(Shop + """
            async fn Main() {
                using let shop = await Open();
                let big = shop.Orders.Where(o => o.total > 100m).OrderBy(o => o.id);
                Print(big.ToQueryString().Contains("WHERE"));
                for o in await big.ToListAsync() { Print(o); }
                for o in await shop.Orders.OrderBy(o => o.id).ToListAsync() { Print(o.note); }

                // A class is tracked: changing it and saving updates its row.
                let ada = await shop.Customers.SingleAsync(c => c.id == 1);
                ada.name = "Augusta";
                await shop.SaveChangesAsync();
                shop.ChangeTracker.Clear();
                Print(await shop.Customers.Where(c => c.id == 1).Select(c => c.name).SingleAsync());

                shop.Orders.Remove(await shop.Orders.SingleAsync(o => o.id == 2));
                await shop.SaveChangesAsync();
                Print(await shop.Orders.CountAsync());
            }
            """);
        Assert.Equal("""
            True
            Order { id = 1, customerId = 1, total = 150.0, note = Some(gift) }
            Some(gift)
            None
            Augusta
            1

            """, output);
    }

    [Fact]
    public void Options_are_nullable_columns_compared_as_SQL_does()
    {
        var output = Run(Shop + """
            async fn Main() {
                using let shop = await Open();
                let noEmail = shop.Customers.Where(c => c.email == None).Select(c => c.name);
                Print(noEmail.ToQueryString().Contains("IS NULL"));
                Print(string.Join(", ", await noEmail.ToListAsync()));
                Print(string.Join(", ", await shop.Customers.Where(c => c.email != None).Select(c => c.name).ToListAsync()));
                Print(string.Join(", ", await shop.Customers.Where(c => c.email == Some("ada@example.com")).Select(c => c.name).ToListAsync()));
                let wanted = "ada@example.com";
                Print(string.Join(", ", await shop.Customers.Where(c => c.email == Some(wanted)).Select(c => c.name).ToListAsync()));
                Print(shop.Database.GenerateCreateScript().Contains("\"email\" TEXT NULL"));
            }
            """);
        Assert.Equal("True\nAlan\nAda\nAda\nAda\nTrue\n", output);
    }

    /// <summary>A Piper enum without fields is a column holding the variant's name; a `let` field is a column too.</summary>
    [Fact]
    public void Enums_and_let_fields_are_columns()
    {
        var output = Run("""
            using System;
            using System.Linq;
            using System.Threading.Tasks;
            using Microsoft.EntityFrameworkCore;

            enum Priority { Low, High }

            class Task(id: int, created: DateTime, priority: Priority) {
                public let id = id;
                public let created = created;
                public mut priority = priority;
                public mut previous: Priority? = None;
                prop Age => TimeSpan = DateTime(2026, 1, 2) - created;     // computed: not a column
            }

            database Work {
                table Tasks: Task key id;
            }

            async fn Main() {
                using let work = Work(DbContextOptionsBuilder<Work>().UseSqlite("Data Source=:memory:").Options);
                await work.Database.OpenConnectionAsync();
                await work.Database.EnsureCreatedAsync();
                work.Tasks.Add(Task(1, DateTime(2026, 1, 1), Priority.High));
                work.Tasks.Add(Task(2, DateTime(2026, 1, 1), Priority.Low));
                await work.SaveChangesAsync();
                work.ChangeTracker.Clear();
                let high = await work.Tasks.Where(t => t.priority == Priority.High).SingleAsync();
                Print($"{high.id} {high.created:yyyy-MM-dd} {high.priority} {high.previous} {high.Age.Days}");
                let script = work.Database.GenerateCreateScript();
                Print($"{script.Contains("\"priority\" TEXT NOT NULL")} {script.Contains("\"previous\" TEXT NULL")} {script.Contains("Age")}");
            }
            """);
        Assert.Equal("1 2026-01-01 High None 1\nTrue True False\n", output);
    }

    [Fact]
    public void Newtypes_are_stored_as_their_values_and_keys_of_them_are_numbered()
    {
        var output = Run("""
            using System;
            using System.Linq;
            using System.Threading.Tasks;
            using Microsoft.EntityFrameworkCore;

            newtype ProjectId = int;
            newtype IssueId = long;
            newtype Email = string requires value.Contains("@");

            record Project(id: ProjectId, name: string);
            record Issue(id: IssueId, project: ProjectId, reporter: Email, watcher: Email?);

            database Work {
                table Projects: Project key id;
                table Issues: Issue key id index project;
            }

            async fn Main() {
                using let work = Work(DbContextOptionsBuilder<Work>().UseSqlite("Data Source=:memory:").Options);
                await work.Database.OpenConnectionAsync();
                await work.Database.EnsureCreatedAsync();
                let web = Project(ProjectId(0), "Website");          // 0: the database numbers it
                work.Projects.Add(web);
                await work.SaveChangesAsync();
                let first = Issue(IssueId(0), web.id, Email("ada@example.com"), None);
                let second = Issue(IssueId(0), web.id, Email("alan@example.com"), Some(Email("ada@example.com")));
                work.Issues.AddRange(first, second);
                await work.SaveChangesAsync();
                work.ChangeTracker.Clear();

                let wanted = ProjectId(1);
                let found = await work.Issues.Where(i => i.project == wanted).OrderBy(i => i.id).ToListAsync();
                Print(string.Join(", ", found.Select(i => $"{i.id} {i.reporter} {i.watcher}")));
                let watched = await work.Issues.SingleAsync(i => i.watcher == Some(Email("ada@example.com")));
                Print(watched.id);
                let script = work.Database.GenerateCreateScript();
                Print($"{script.Contains("\"id\" INTEGER NOT NULL CONSTRAINT")} {script.Contains("\"reporter\" TEXT NOT NULL")} {script.Contains("\"watcher\" TEXT NULL")}");
            }
            """);
        Assert.Equal("1 ada@example.com None, 2 alan@example.com Some(ada@example.com)\n2\nTrue True True\n", output);
    }

    [Fact]
    public void Comparing_an_option_column_with_an_option_variable_says_how_to_write_it()
    {
        var result = Compile(Shop + """
            async fn Main() {
                using let shop = await Open();
                let wanted: string? = None;
                Print(await shop.Customers.Where(c => c.email == wanted).CountAsync());
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var run = Runner.Run(result.Files, references: result.References);
        Assert.False(run.Success);
        Assert.Contains("not with an optional variable", string.Join("\n", run.Errors));
    }

    [Fact]
    public void A_key_or_index_naming_a_missing_field_is_an_error()
    {
        var text = """
            class Customer(id: int) { public let id = id; mut secret = 0; }
            record Order(id: int, total: decimal);

            database Shop {
                table Customers: Customer key secret;
                table Orders: Order key id index (id, totl);
            }

            fn Main() { }
            """;
        var errors = Compile(text).Diagnostics.OrderBy(d => d.Span.Start).ToList();
        Assert.Equal(
            [
                ("PIP0503", text.IndexOf("secret;", StringComparison.Ordinal)),
                ("PIP0503", text.IndexOf("totl", StringComparison.Ordinal)),
            ],
            errors.Select(e => (e.Code, e.Span.Start)));
        Assert.Contains("'Customer' has no public field or property 'secret'", errors[0].Message);
        Assert.Contains("'Order' has no field 'totl'", errors[1].Message);
    }

    [Fact]
    public void A_table_declared_twice_is_an_error()
    {
        var text = """
            record Order(id: int);
            database Shop {
                table Orders: Order;
                table Orders: Order;
            }
            fn Main() { }
            """;
        var error = Assert.Single(Compile(text).Diagnostics);
        Assert.Contains("There's already a table 'Orders'", error.Message);
    }
}

// In the same collection as DataPluginTests, so the two don't run SQLite programs at once.
[Collection("data plugin")]
public class DataPluginSqlTests
{
    [Fact]
    public void Raw_sql_takes_its_holes_as_parameters()
    {
        var output = DataPluginTests.Run(DataPluginTests.Shop + """
            async fn Main() {
                using let shop = await Open();
                let customer = 1;
                let name = "Ada' or '1'='1";
                for o in await shop.Orders.Query(sql$"select * from Orders where customerId = {customer}").ToListAsync() { Print(o.id); }
                // EF Core's own FromSql($"…"), whose holes are parameters too.
                Print(await shop.Customers.FromSql($"select * from Customers where name = {name}").CountAsync());
                let ids = [1, 2];
                let none: string? = None;
                Print(await shop.Orders.Query(sql$"select * from Orders where id in ({ids}) and note is not {none}").CountAsync());
                Print(await shop.Query<string>(sql$"select name as Value from Customers where id = {2}").SingleAsync());
                Print(await shop.ExecuteAsync(sql$"update Customers set name = {"Grace"} where id = {1}"));
                Print(await shop.Customers.Where(c => c.id == 1).Select(c => c.name).SingleAsync());
            }
            """);
        Assert.Equal("1\n0\n1\nAlan\n1\nGrace\n", output);
    }

    [Theory]
    [InlineData("shop.Orders.FromSqlRaw($\"select * from Orders where id = {id}\")")]
    [InlineData("shop.Database.ExecuteSqlRaw($\"delete from Orders where id = {id}\")")]
    public void An_interpolated_string_given_to_a_raw_sql_method_is_a_warning(string call)
    {
        var result = DataPluginTests.Compile(DataPluginTests.Shop + $$"""
            async fn Main() {
                using let shop = await Open();
                let id = 1;
                let q = {{call}};
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var warning = Assert.Single(result.Diagnostics);
        Assert.Equal(("PIP0405", DiagnosticSeverity.Warning), (warning.Code, warning.Severity));
    }
}
