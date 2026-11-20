using Piper.Diagnostics;

namespace Piper.Tests;

/// <summary>
/// <c>sql$"…"</c> queries checked against the database they run on, from its shape, as the program compiles: tables
/// and columns that exist, holes that suit their columns, and selects that give what reads them. What the checks
/// can't follow is left alone.
/// </summary>
[Collection("data plugin")]
public class SqlCheckTests
{
    private const string Prelude = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using Microsoft.EntityFrameworkCore;

        newtype OrderId = int;
        record Order(id: OrderId, customer: string, total: decimal, note: string?, placed: DateTime);
        record Line(id: int, orderId: OrderId, product: string, quantity: int);
        record Summary(customer: string, spent: decimal);

        database Shop {
            table Orders: Order key id;
            table Lines: Line key id;
        }

        """;

    /// <summary>The diagnostics of a function using <c>db: Shop</c>, each as its code, the text it points at, and its message.</summary>
    private static List<(string Code, string At, string Message)> Diagnostics(string body)
    {
        var source = Prelude + $"fn Use(db: Shop, name: string, limit: decimal, id: OrderId, ids: List<int>, note: string?, when: DateTime) {{\n{body}\n}}\n";
        var result = DataPluginTests.Compile(source);
        return [.. result.Diagnostics.Select(d => (d.Code, source.Substring(d.Span.Start, d.Span.Length), d.Message))];
    }

    private static void Clean(string body)
    {
        var diagnostics = Diagnostics(body);
        Assert.True(diagnostics.Count == 0, string.Join("\n", diagnostics.Select(d => $"{d.Code} '{d.At}': {d.Message}")));
    }

    [Theory]
    [InlineData("""db.Orders.Query(sql$"select * from Orders where customer = {name} and total > {limit} order by placed desc");""")]
    [InlineData("""db.Orders.Query(sql$"SELECT o.* FROM Orders o WHERE o.id = {id}");""")]
    [InlineData("""db.Query<string>(sql$"select customer as Value from Orders where id in ({ids})");""")]
    [InlineData("""db.Query<int>(sql$"select count(*) as Value from Orders where placed >= {when} and lower(customer) like {name}");""")]
    [InlineData("""db.Query<Summary>(sql$"select customer, sum(total) as spent from Orders group by customer having sum(total) > {limit} order by spent");""")]
    [InlineData("""db.Query<Summary>(sql$"select o.customer, sum(l.quantity * o.total) spent from Orders o join Lines l on l.orderId = o.id group by o.customer");""")]
    [InlineData("""db.Execute(sql$"update Orders set note = {note}, total = {limit} where id = {id}");""")]
    [InlineData("""db.Execute(sql$"insert into Lines (orderId, product, quantity) values ({id}, {name}, {3})");""")]
    [InlineData("""db.Execute(sql$"delete from Lines where orderId = {id} and quantity between {1} and {10}");""")]
    [InlineData("""db.Execute(sql$"update Orders set note = 'it''s \"done\"' where note is null and customer = 'ann'");""")]
    public void Queries_that_fit_the_database_are_fine(string body) => Clean(body);

    [Theory]
    // Subqueries, unions and the databases' own tables aren't followed; nor is SQL put in a hole, or built elsewhere.
    [InlineData("""db.Orders.Query(sql$"select * from Orders where id in (select orderId from Lines where nope = 1)");""")]
    [InlineData("""db.Query<string>(sql$"select name as Value from sqlite_master where type = 'table'");""")]
    [InlineData("""db.Orders.Query(sql$"select * from Orders order by {Sql.Raw("nope")}");""")]
    [InlineData("""let query = sql$"select * from Nope"; db.Orders.Query(query);""")]
    [InlineData("""db.Query<string>(sql$"select a.b as Value from other.Things a");""")]
    public void What_the_checks_cant_follow_is_left_alone(string body) => Clean(body);

    [Theory]
    [InlineData("""db.Orders.Query(sql$"select * from Ordrs");""", "Ordrs", "Shop has no table 'Ordrs'.")]
    [InlineData("""db.Orders.Query(sql$"select * from Orders where custmer = {name}");""", "custmer", "'Orders' has no column 'custmer'.")]
    [InlineData("""db.Orders.Query(sql$"select o.* from Orders o where o.totl > {limit}");""", "totl", "'Orders' has no column 'totl'.")]
    [InlineData("""db.Execute(sql$"update Lines set qty = 2 where id = 1");""", "qty", "'Lines' has no column 'qty'.")]
    [InlineData("""db.Query<int>(sql$"select count(*) as Value from Orders o join Lines l on l.order = o.id");""", "order", "'Lines' has no column 'order'.")]
    [InlineData("""db.Query<int>(sql$"select count(*) as Value from Orders, Lines where product = customer and colour = 'red'");""", "colour",
        "None of 'Orders', 'Lines' has a column 'colour'.")]
    public void A_table_or_column_the_database_hasnt_got_is_an_error_where_its_named(string body, string at, string message)
    {
        var diagnostic = Assert.Single(Diagnostics(body));
        Assert.Equal("PIP0503", diagnostic.Code);
        Assert.Equal(at, diagnostic.At);
        Assert.Equal(message + " (data)", diagnostic.Message);
    }

    [Theory]
    [InlineData("""db.Orders.Query(sql$"select * from Orders where total > {name}");""", "total",
        "`{name}` (string) doesn't suit 'Orders.total', which is stored as decimal.")]
    [InlineData("""db.Orders.Query(sql$"select * from Orders where {when} = customer");""", "customer",
        "`{when}` (DateTime) doesn't suit 'Orders.customer', which is stored as string.")]
    [InlineData("""db.Orders.Query(sql$"select * from Orders where customer in ({ids})");""", "customer",
        "`{ids}` (List<int>) doesn't suit 'Orders.customer', which is stored as string.")]
    [InlineData("""db.Execute(sql$"insert into Lines (orderId, product, quantity) values ({id}, {name}, {name})");""", "quantity",
        "`{name}` (string) doesn't suit 'Lines.quantity', which is stored as int.")]
    [InlineData("""db.Execute(sql$"update Orders set placed = {limit} where id = {id}");""", "placed",
        "`{limit}` (decimal) doesn't suit 'Orders.placed', which is stored as DateTime.")]
    public void A_hole_that_doesnt_suit_its_column_is_an_error(string body, string at, string message)
    {
        var diagnostic = Assert.Single(Diagnostics(body));
        Assert.Equal(at, diagnostic.At);
        Assert.Equal(message + " (data)", diagnostic.Message);
    }

    [Fact]
    public void An_optional_hole_compared_with_equals_is_a_warning()
    {
        var diagnostic = Assert.Single(Diagnostics("""db.Orders.Query(sql$"select * from Orders where note = {note}");"""));
        Assert.Equal("PIP0505", diagnostic.Code);
        Assert.Equal("note", diagnostic.At);
        Assert.StartsWith("`{note}` (string?) is optional, and `note = NULL` is never true, so when it's None this matches no rows", diagnostic.Message);
        // Set to one, it's NULL, as meant.
        Clean("""db.Execute(sql$"update Orders set note = {note} where id = {id}");""");
    }

    [Theory]
    [InlineData("""db.Query<string>(sql$"select customer from Orders");""",
        "Query<string> reads a column named Value: name it, as in `select customer as Value from …`.")]
    [InlineData("""db.Query<int>(sql$"select count(*), max(id) as Value from Orders");""",
        "Query<int> reads one column, named Value, but this select gives 2.")]
    [InlineData("""db.Query<Summary>(sql$"select customer from Orders");""",
        "Query<Summary> reads a column for each of its fields, but this select doesn't give 'spent'.")]
    [InlineData("""db.Orders.Query(sql$"select id, customer from Orders");""",
        "Querying Orders reads whole rows of it, but this select doesn't give 'total', 'note' or 'placed': select every column (select * gives them all).")]
    [InlineData("""db.Execute(sql$"insert into Lines (orderId, product) values ({id}, {name}, {3})");""",
        "The insert names 2 columns but gives 3 values.")]
    public void A_select_must_give_what_reads_it(string body, string message)
    {
        var diagnostic = Assert.Single(Diagnostics(body));
        Assert.Equal(message + " (data)", diagnostic.Message);
    }

    [Fact]
    public void Each_query_is_checked_against_the_database_it_runs_on()
    {
        var source = Prelude + """
            record Visit(id: int, page: string);
            database Stats {
                table Visits: Visit key id;
            }

            fn Use(shop: Shop, stats: Stats) {
                stats.Query<int>(sql$"select count(*) as Value from Visits where page = 'home'");
                shop.Query<int>(sql$"select count(*) as Value from Visits");
            }
            """;
        var diagnostic = Assert.Single(DataPluginTests.Compile(source).Diagnostics);
        Assert.Equal("Shop has no table 'Visits'. (data)", diagnostic.Message);
        Assert.Equal("Visits", source.Substring(diagnostic.Span.Start, diagnostic.Span.Length));
    }

    [Fact]
    public void Checked_queries_run_as_written()
    {
        var output = DataPluginTests.Run(Prelude + """
            fn Main() {
                using let db = Shop(DbContextOptionsBuilder<Shop>().UseSqlite("Data Source=:memory:").Options);
                db.Database.OpenConnection();
                db.Database.EnsureCreated();
                db.Orders.Add(Order(OrderId(1), "ann", 30m, None, DateTime(2026, 1, 2)));
                db.Orders.Add(Order(OrderId(2), "bob", 5m, Some("gift"), DateTime(2026, 1, 3)));
                db.SaveChanges();
                Print(db.Orders.Query(sql$"select * from Orders where customer = {"bob"}").Single().total);
                Print(db.Query<Summary>(sql$"select customer, sum(total) as spent from Orders group by customer order by spent").First().customer);
                Print(db.Execute(sql$"update Orders set note = {"rush"} where id = {OrderId(1)}"));
            }
            """);
        Assert.Equal("5\nbob\n1\n", output);
    }
}
