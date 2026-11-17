using Piper.Binding;
using Piper.Plugins;
using Piper.Syntax;

namespace Piper.Plugins.Data;

/// <summary>
/// EF Core databases: <c>database Shop { table Orders: Order key id index customerId; }</c> becomes a class deriving from
/// <c>DbContext</c>, in ordinary Piper: a <c>DbSet&lt;Order&gt; Orders</c> property per table, with its keys and indexes
/// in <c>OnModelCreating</c>, and Options stored as nullable columns (through Piper.Data, which comes with the plugin).
/// Queries are LINQ over the sets, whose lambdas are expression trees, so EF Core translates them to SQL.
/// </summary>
public sealed class DataPlugin : IPiperPlugin
{
    private const string EF = "Microsoft.EntityFrameworkCore";

    public string Name => "data";

    /// <summary>EF Core's relational core; a program adds its database's provider, such as <c>Microsoft.EntityFrameworkCore.Sqlite</c>.</summary>
    public IReadOnlyDictionary<string, string> Packages { get; } = new Dictionary<string, string> { [EF + ".Relational"] = "10.0.14" };

    /// <summary>Piper.Data, storing Options as nullable columns.</summary>
    public IReadOnlyList<string> References => ["Piper.Data.dll"];

    /// <summary>Persisted actors' event stores in a database, chosen by configuration: sqlite, sqlserver, postgres.</summary>
    public IReadOnlyList<string> Providers => ["Piper.Data.DatabaseEventStore.RegisterProviders"];

    public IReadOnlyList<PluginKeyword> Keywords { get; } =
    [
        new DeclarationKeyword("database", Database)
        {
            Description = "Declares an EF Core database, as `class Name(options: DbContextOptions<Name>): DbContext(options)`, with a `DbSet` property per table. " +
                          "Make one with the options for your database's provider: `Name(DbContextOptionsBuilder<Name>().UseSqlite(\"Data Source=app.db\").Options)`.",
            Snippet = "database ${1:AppDb} {\n\ttable ${2:Items}: ${3:Item} key ${4:id};\n\t$0\n}",
            Words = new Dictionary<string, string>
            {
                ["table"] = "A table, and the type of its rows: a `DbSet` property of the database. A record or a class with public fields or properties.",
                ["key"] = "The field (or fields, in parentheses) identifying each row: `HasKey`. Without it, EF Core looks for one named `id` or `<type>Id`.",
                ["index"] = "An index on the field (or fields, in parentheses), for faster lookups: `HasIndex`.",
            },
        },
    ];

    /// <summary>A table's columns named by `key` or `index`, for the analysis to check against its type.</summary>
    private sealed record TableColumns(Token Table, TypeSyntax Entity, List<LiteralExpressionSyntax> Columns);

    /// <summary>
    /// database = "database" IDENT "{" { table } "}" ;
    /// table    = "table" IDENT ":" type { ( "key" | "index" ) columns } ";" ;
    /// columns  = IDENT | "(" IDENT { "," IDENT } ")" ;
    /// </summary>
    private static IEnumerable<DeclarationSyntax> Database(PluginParser p)
    {
        var name = p.Expect(TokenKind.Identifier);
        p.Expect(TokenKind.OpenBrace);

        var properties = new List<string>();
        var holes = new List<(string, object)> { ("name", name) };
        var model = new List<StatementSyntax>();
        var tables = new List<TableColumns>();
        var seen = new HashSet<string>();
        while (p.Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile))
        {
            if (!p.AtWord("table"))
            {
                p.Report(p.Current.Span, "Expected 'table', such as table Orders: Order key id;");
                while (p.Current.Kind is not (TokenKind.Semicolon or TokenKind.CloseBrace or TokenKind.EndOfFile))
                    p.NextToken();
                if (p.Current.Kind == TokenKind.Semicolon)
                    p.NextToken();
                continue;
            }
            p.ReadWord();
            var table = p.Expect(TokenKind.Identifier);
            p.Expect(TokenKind.Colon);
            var entity = p.ParseType();
            // A second table of a name is reported, and left out, as its property would clash with the first's.
            bool duplicate = !table.IsMissing && !seen.Add(table.Text);
            if (duplicate)
                p.Report(table.Span, $"There's already a table '{table.Text}'.");

            var columns = new List<LiteralExpressionSyntax>();
            while (p.AtWord("key") || p.AtWord("index"))
            {
                var word = p.ReadWord();
                var named = Columns(p);
                columns.AddRange(named);
                model.Add(p.Template.Statement($"model.Entity<$entity>().{(word.Text == "key" ? "HasKey" : "HasIndex")}($columns);",
                    ("entity", entity), ("columns", named.Cast<ExpressionSyntax>().ToList())));
            }
            p.Expect(TokenKind.Semicolon);
            if (duplicate)
                continue;
            tables.Add(new TableColumns(table, entity, columns));

            // `prop Orders => DbSet<Order> = Set<Order>();`
            int i = properties.Count;
            properties.Add($"prop $table{i} => {EF}.DbSet<$entity{i}> = Set<$entity{i}>();");
            holes.Add(($"table{i}", table));
            holes.Add(($"entity{i}", entity));
        }
        p.Expect(TokenKind.CloseBrace);
        p.Data = tables;
        holes.Add(("model", model));

        return p.Template.Declarations($$"""
            class $name(options: {{EF}}.DbContextOptions<$name>): {{EF}}.DbContext(options) {
                {{string.Join("\n    ", properties)}}

                override fn OnModelCreating(model: {{EF}}.ModelBuilder) {
                    $model;
                    Piper.Data.PiperModel.Configure(model);
                }

                override fn ConfigureConventions(configuration: {{EF}}.ModelConfigurationBuilder) {
                    Piper.Data.PiperModel.Conventions(configuration, this);
                }

                override fn OnConfiguring(builder: {{EF}}.DbContextOptionsBuilder) {
                    Piper.Data.PiperModel.Configuring(builder);
                }
            }
            """, [.. holes]);
    }

    /// <summary>A field's name, <c>id</c>, or several in parentheses, <c>(customerId, placed)</c>, as strings for EF Core.</summary>
    private static List<LiteralExpressionSyntax> Columns(PluginParser p)
    {
        var names = new List<Token>();
        if (p.Current.Kind == TokenKind.OpenParen)
        {
            p.NextToken();
            names.Add(p.Expect(TokenKind.Identifier));
            while (p.Current.Kind == TokenKind.Comma)
            {
                p.NextToken();
                names.Add(p.Expect(TokenKind.Identifier));
            }
            p.Expect(TokenKind.CloseParen);
        }
        else
        {
            names.Add(p.Expect(TokenKind.Identifier));
        }
        return [.. names.Select(n => new LiteralExpressionSyntax(n.Span, new Token(TokenKind.StringLiteral, n.Span, $"\"{n.Text}\"", n.Text), n.Text))];
    }

    /// <summary>
    /// Checks each column a `key` or `index` names against its table's type, once types are known: EF Core would only
    /// find a missing one when the model is first built. A record's fields count, and a class's public fields and
    /// properties (its private ones are C# fields, which EF Core doesn't map).
    /// </summary>
    public void Analyze(PluginAnalysis analysis)
    {
        foreach (var expansion in analysis.Expansions)
        {
            if (expansion.Data is not List<TableColumns> tables)
                continue;
            foreach (var table in tables)
            {
                if (table.Entity is not NamedTypeSyntax { Name.Text: var entityName })
                    continue;
                var type = analysis.Program.Types.FirstOrDefault(t => t.Name == entityName);
                HashSet<string>? columns = type switch
                {
                    RecordSymbol record => [.. record.Fields.Select(f => f.Name)],
                    ClassSymbol @class => [.. @class.Declaration.Fields.Where(f => f.IsProperty || f.Public is not null).Select(f => f.Name.Text)],
                    _ => null,   // a .NET type: EF Core checks it
                };
                if (columns is null)
                    continue;
                foreach (var column in table.Columns)
                {
                    var name = (string)column.Value!;
                    if (!columns.Contains(name))
                        analysis.ReportError(expansion.File, column.Span,
                            $"'{entityName}' has no {(type is ClassSymbol ? "public field or property" : "field")} '{name}', which table '{table.Table.Text}' names.");
                }
            }
        }
    }
}
