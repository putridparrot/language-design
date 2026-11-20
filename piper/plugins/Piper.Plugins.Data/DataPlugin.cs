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

    /// <summary>
    /// Persisted actors' event stores, and clusters' memberships (leases in a shared table), in a database chosen by
    /// configuration: sqlite, sqlserver, postgres.
    /// </summary>
    public IReadOnlyList<string> Providers => ["Piper.Data.DatabaseEventStore.RegisterProviders", "Piper.Data.DatabaseClusterMembership.RegisterProviders"];

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
                ["outbox"] = "A table of messages saved with the changes they tell of (`PiperOutbox`), and an `Outbox` property: " +
                             "`bus.In(db.Outbox).Publish(…)` before `SaveChangesAsync()`, and a relay sends them once saved.",
                ["idempotency"] = "A table of idempotent requests' keys and responses (`PiperIdempotency`), shared by every pod: register it for a server's `idempotent` endpoints with `singleton IIdempotencyStore = DatabaseIdempotency.Store(() => Db(options));`.",
            },
        },
    ];

    /// <summary>
    /// A table, for the analysis: the columns its `key` and `index` name (to check against its type), and its key and
    /// indexes (for the database's shape). <see cref="Key"/> is null when it has no `key`, and EF Core finds one.
    /// </summary>
    internal sealed record TableColumns(Token Table, TypeSyntax Entity, List<LiteralExpressionSyntax> Columns, List<string>? Key, List<List<string>> Indexes);

    /// <summary>A database, for the analysis: its tables, and whether it has Piper's own.</summary>
    internal sealed record DatabaseData(Token Name, List<TableColumns> Tables, bool Outbox, bool Idempotency);

    /// <summary>
    /// database = "database" IDENT "{" { table | "outbox" ";" | "idempotency" ";" } "}" ;
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
        var outbox = false;
        var idempotency = false;
        while (p.Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile))
        {
            // `outbox;`: a table of messages saved with the changes they tell of (Piper.Data's DatabaseOutbox).
            if (p.AtWord("outbox"))
            {
                var word = p.ReadWord();
                p.Expect(TokenKind.Semicolon);
                if (outbox)
                    p.Report(word.Span, "There's already an outbox.");
                outbox = true;
                continue;
            }
            // `idempotency;`: a table of idempotent requests' keys and responses (Piper.Data's DatabaseIdempotency).
            if (p.AtWord("idempotency"))
            {
                var word = p.ReadWord();
                p.Expect(TokenKind.Semicolon);
                if (idempotency)
                    p.Report(word.Span, "There's already an idempotency table.");
                idempotency = true;
                continue;
            }
            if (!p.AtWord("table"))
            {
                p.Report(p.Current.Span, "Expected 'table', such as table Orders: Order key id; or outbox; or idempotency;");
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
            List<string>? key = null;
            var indexes = new List<List<string>>();
            while (p.AtWord("key") || p.AtWord("index"))
            {
                var word = p.ReadWord();
                var named = Columns(p);
                columns.AddRange(named);
                if (word.Text == "key")
                    key = [.. named.Select(n => (string)n.Value!)];
                else
                    indexes.Add([.. named.Select(n => (string)n.Value!)]);
                model.Add(p.Template.Statement($"model.Entity<$entity>().{(word.Text == "key" ? "HasKey" : "HasIndex")}($columns);",
                    ("entity", entity), ("columns", named.Cast<ExpressionSyntax>().ToList())));
            }
            p.Expect(TokenKind.Semicolon);
            if (duplicate)
                continue;
            tables.Add(new TableColumns(table, entity, columns, key, indexes));

            // `prop Orders => DbSet<Order> = Set<Order>();`
            int i = properties.Count;
            properties.Add($"prop $table{i} => {EF}.DbSet<$entity{i}> = Set<$entity{i}>();");
            holes.Add(($"table{i}", table));
            holes.Add(($"entity{i}", entity));
        }
        p.Expect(TokenKind.CloseBrace);
        p.Data = new DatabaseData(name, tables, outbox, idempotency);
        if (outbox)
        {
            // `prop Outbox => IOutbox`, which Piper.Messaging's `bus.In(db.Outbox)` adds messages to, and its table.
            properties.Add("prop Outbox => Piper.Runtime.IOutbox = Piper.Data.DatabaseOutbox.For(this);");
            model.Add(p.Template.Statement("Piper.Data.DatabaseOutbox.Configure(model);"));
        }
        if (idempotency)
            model.Add(p.Template.Statement("Piper.Data.DatabaseIdempotency.Configure(model);"));
        holes.Add(("model", model));
        holes.Add(("migrations", Migrations(p, name)));

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

                /// The scripts `piper db migrate` wrote for this database, for each provider, in order.
                prop Migrations => System.Collections.Generic.List<Piper.Data.PiperMigration> = [$migrations];

                /// Applies the migrations not yet applied to this database (`piper db migrate` writes them), returning
                /// their names. A database made before there were migrations takes the first, the baseline, as applied.
                fn Migrate() => System.Collections.Generic.IReadOnlyList<string> = Piper.Data.PiperMigrations.Migrate(this, Migrations);

                /// Each migration for this database's provider, and whether it's applied.
                fn MigrationStatus() => System.Collections.Generic.IReadOnlyList<Piper.Data.PiperMigrationStatus> = Piper.Data.PiperMigrations.Status(this, Migrations);
            }
            """, [.. holes]);
    }

    /// <summary>
    /// The scripts <c>piper db migrate</c> wrote beside the declaration, <c>TrackerDb.migrations/sqlite/0001-baseline.sql</c>,
    /// a folder per provider, as <c>Piper.Data.PiperMigration(provider, number, name, script)</c>s: compiled in, so the
    /// program carries its migrations wherever it's deployed.
    /// </summary>
    private static List<ExpressionSyntax> Migrations(PluginParser p, Token name)
    {
        var migrations = new List<ExpressionSyntax>();
        if (name.IsMissing || !File.Exists(p.File.FileName))
            return migrations;
        var folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(p.File.FileName))!, $"{name.Text}.migrations");
        if (!Directory.Exists(folder))
            return migrations;
        foreach (var providerFolder in Directory.GetDirectories(folder).Order(StringComparer.Ordinal))
        {
            var provider = Path.GetFileName(providerFolder);
            foreach (var path in Directory.GetFiles(providerFolder, "*.sql").Order(StringComparer.Ordinal))
            {
                var file = Path.GetFileNameWithoutExtension(path);
                var dash = file.IndexOf('-');
                if (dash <= 0 || !int.TryParse(file[..dash], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number))
                    continue;
                migrations.Add(p.Template.Expression("Piper.Data.PiperMigration($provider, $number, $name, $script)",
                    ("provider", Text(p, provider)),
                    ("number", new LiteralExpressionSyntax(p.Span, new Token(TokenKind.IntegerLiteral, p.Span, number.ToString(System.Globalization.CultureInfo.InvariantCulture), number), number)),
                    ("name", Text(p, file[(dash + 1)..])),
                    ("script", Text(p, File.ReadAllText(path).ReplaceLineEndings("\n")))));
            }
        }
        return migrations;

        static LiteralExpressionSyntax Text(PluginParser p, string value) =>
            new(p.Span, new Token(TokenKind.StringLiteral, p.Span, $"\"{value}\"", value), value);
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
    /// Then records each database's shape in <c>Name.schema.json</c> beside it, checked against the one recorded (see
    /// <see cref="DatabaseSchema"/>), as <c>piper db migrate</c> writes migrations from it.
    /// </summary>
    public void Analyze(PluginAnalysis analysis)
    {
        var shapes = new List<(DatabaseData, System.Text.Json.Nodes.JsonObject)>();
        foreach (var expansion in analysis.Expansions)
        {
            if (expansion.Data is not DatabaseData database)
                continue;
            foreach (var table in database.Tables)
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

            // Its shape, recorded in `Name.schema.json` beside it, and checked against the last recorded.
            if (database.Name.IsMissing)
                continue;
            var shape = DatabaseSchema.Describe(database, analysis.Program, (span, message) => analysis.ReportWarning(expansion.File, span, message));
            shapes.Add((database, shape));
            analysis.CheckRecordedShape(expansion.File, database.Name.Span, database.Name.Text, $"{database.Name.Text}.schema.json", shape,
                before => DatabaseSchema.Breaking(before, shape, database));
        }

        // The program's `sql$"…"` queries, checked against the databases they run on.
        SqlChecks.Check(analysis, shapes);
    }
}
