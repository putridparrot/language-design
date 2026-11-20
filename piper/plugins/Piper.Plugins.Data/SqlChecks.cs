using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Piper.Binding;
using Piper.Text;

namespace Piper.Plugins.Data;

/// <summary>
/// <c>sql$"…"</c> queries checked against the database they run on, as the program compiles, from its recorded shape:
/// no database to connect to. Those given straight to the data plugin's <c>Query</c>, <c>Execute</c> and
/// <c>ExecuteAsync</c> are checked: their tables and columns exist, each hole's type suits the column it's compared
/// with or put in, and what a query gives fits what reads it (<c>db.Query&lt;T&gt;</c>'s <c>T</c>, a table's rows).
/// A query built elsewhere (a variable, a function's result), or with an <c>Sql</c> in a hole (<c>Sql.Raw</c>), and SQL
/// the reader can't follow (see <see cref="SqlStatement"/>), are left unchecked.
/// </summary>
internal static class SqlChecks
{
    private const string Extensions = "Microsoft.EntityFrameworkCore.PiperSqlExtensions";

    public static void Check(PluginAnalysis analysis, IReadOnlyList<(DataPlugin.DatabaseData Database, JsonObject Shape)> databases)
    {
        if (databases.Count == 0)
            return;
        var schemas = databases.GroupBy(d => d.Database.Name.Text).ToDictionary(g => g.Key, g => SchemaOf(g.First().Shape));
        var program = analysis.Program;
        foreach (var function in program.Functions.Concat(program.Methods).Concat(program.Tests.Select(t => t.Function)))
        {
            if (function.Symbol.DeclaringFile is not { } file)
                continue;
            foreach (var call in BoundTreeWalker.DescendantsAndSelf(function.Body).OfType<BoundClrInvocationExpression>())
            {
                if (call.Member.DeclaringType?.FullName != Extensions || call.Member.Name is not ("Query" or "Execute" or "ExecuteAsync"))
                    continue;
                BoundExpression[] arguments = call.Receiver is null ? [.. call.Arguments] : [call.Receiver, .. call.Arguments];
                if (arguments.Length >= 2)
                    CheckCall(analysis, file, call, arguments[0], arguments[1], schemas);
            }
        }
    }

    /// <summary>A database's tables, each with its columns' stored types, from its recorded shape.</summary>
    private static Schema SchemaOf(JsonObject shape)
    {
        var tables = new Dictionary<string, SqlTable>(StringComparer.OrdinalIgnoreCase);
        var entities = new Dictionary<string, string>();
        foreach (var (name, node) in shape["tables"] as JsonObject ?? [])
        {
            var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (column, value) in node?["columns"] as JsonObject ?? [])
                columns[column] = (string?)value?["stored"] ?? "";
            tables[name] = new SqlTable(name, columns);
            if ((string?)node?["entity"] is { } entity)
                entities.TryAdd(entity, name);
        }
        return new Schema((string?)shape["name"] ?? "", tables, entities);
    }

    private sealed record Schema(string Name, Dictionary<string, SqlTable> Tables, Dictionary<string, string> Entities);

    private static void CheckCall(PluginAnalysis analysis, SourceText file, BoundClrInvocationExpression call, BoundExpression target, BoundExpression sql,
        Dictionary<string, Schema> schemas)
    {
        // What it runs on: a table (`db.Orders`, a DbSet), or a database.
        TypeSymbol? entity = IsDbSet(target.Type) ? target.Type.TypeArguments.FirstOrDefault() : null;
        Schema? schema = schemas.GetValueOrDefault(target.Type.Name);
        if (schema is null && entity is not null)
        {
            schema = BoundTreeWalker.DescendantsAndSelf(target).OfType<BoundExpression>().Select(e => schemas.GetValueOrDefault(e.Type.Name)).FirstOrDefault(s => s is not null)
                ?? (schemas.Values.Where(s => s.Entities.ContainsKey(entity.Name)).ToList() is [var only] ? only : null);
        }
        if (schema is null || Literal(sql, file) is not { } literal)
            return;

        var analysisResult = SqlStatement.Analyze(literal.Text, schema.Name, schema.Tables, literal.Holes);
        if (!analysisResult.Followed)
            return;
        var span = sql.SourceSpan ?? call.SourceSpan;
        if (span is not { } at)
            return;

        foreach (var problem in analysisResult.Problems)
        {
            var near = Near(file, at, problem.Near);
            if (problem.Warning)
                analysis.ReportWarning(file, near, Sentence(problem.Message));
            else
                analysis.ReportError(file, near, Sentence(problem.Message));
        }
        if (call.Member.Name != "Query" || analysisResult.Results is not { } results)
            return;

        // What reads the rows: a table's whole rows (`db.Orders.Query(…)`), or `db.Query<T>(…)`'s T.
        var given = results.OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (entity is not null)
        {
            if (!schema.Entities.TryGetValue(entity.Name, out var tableName))
                return;
            var missing = schema.Tables[tableName].Columns.Keys.Where(c => !given.Contains(c)).ToList();
            if (missing.Count > 0)
            {
                analysis.ReportError(file, at,
                    $"Querying {tableName} reads whole rows of it, but this select doesn't give {Names(missing)}: select every column (select * gives them all).");
            }
            return;
        }
        var read = call.TypeArguments.FirstOrDefault() ?? call.Type.TypeArguments.FirstOrDefault();
        if (read is null)
            return;
        if (DatabaseSchema.Stored(read) is not null)
        {
            // EF Core's SqlQuery of a value reads one column, called Value.
            if (results.Count != 1)
                analysis.ReportError(file, at, $"Query<{read}> reads one column, named Value, but this select gives {results.Count}.");
            else if (!string.Equals(results[0], "Value", StringComparison.OrdinalIgnoreCase))
                analysis.ReportError(file, at, $"Query<{read}> reads a column named Value: name it, as in `select {results[0] ?? "count(*)"} as Value from …`.");
            return;
        }
        IEnumerable<string>? fields = read.OriginalDefinition switch
        {
            RecordSymbol record => record.Fields.Select(f => f.Name),
            ClassSymbol @class => @class.Fields.Where(f => !f.IsStatic && (f.IsProperty ? f.IsAutomatic : f.IsPublic)).Select(f => f.Name),
            _ => null,
        };
        if (fields?.Where(f => !given.Contains(f)).ToList() is { Count: > 0 } absent)
            analysis.ReportError(file, at, $"Query<{read}> reads a column for each of its fields, but this select doesn't give {Names(absent)}.");
    }

    private static string Names(IReadOnlyList<string> names) =>
        names.Count == 1 ? $"'{names[0]}'" : string.Join(", ", names.Take(names.Count - 1).Select(n => $"'{n}'")) + $" or '{names[^1]}'";

    private static string Sentence(string message) => char.ToUpperInvariant(message[0]) + message[1..] + ".";

    private static bool IsDbSet(TypeSymbol type) =>
        type is ClrTypeSymbol { Type: var clr } && (clr.IsGenericType ? clr.GetGenericTypeDefinition() : clr).FullName == "Microsoft.EntityFrameworkCore.DbSet`1";

    /// <summary>The name <paramref name="near"/> where it's written in the literal at <paramref name="span"/>, or the whole literal.</summary>
    private static TextSpan Near(SourceText file, TextSpan span, string? near)
    {
        if (near is null || span.Start + span.Length > file.Length)
            return span;
        var match = Regex.Match(file.ToString(span), $@"(?<![\w$]){Regex.Escape(near)}(?!\w)", RegexOptions.IgnoreCase);
        return match.Success ? new TextSpan(span.Start + match.Index, match.Length) : span;
    }

    /// <summary>
    /// The SQL of an <c>sql$"…"</c> (its builder's calls: <c>Sql.Builder().Text("…").Hole(x)….Build()</c>), its holes
    /// marked, and what each holds; null if it's not a literal, or a hole holds SQL (which can't be followed).
    /// </summary>
    private static (string Text, List<SqlHole> Holes)? Literal(BoundExpression sql, SourceText file)
    {
        if (sql is not BoundClrInvocationExpression { Member.Name: "Build" } build)
            return null;
        var parts = new List<object>();
        var current = build.Receiver;
        while (current is BoundClrInvocationExpression { Member.DeclaringType.Name: "SqlBuilder" } part)
        {
            if (part.Member.Name == "Text" && part.Arguments is [BoundLiteralExpression { Value: string text }])
                parts.Add(text);
            else if (part.Member.Name == "Hole" && part.Arguments is [var value])
                parts.Add(value);
            else
                return null;
            current = part.Receiver;
        }
        if (current is not BoundClrInvocationExpression { Member.Name: "Builder" })
            return null;
        parts.Reverse();

        var builder = new StringBuilder();
        var holes = new List<SqlHole>();
        foreach (var part in parts)
        {
            if (part is string text)
            {
                builder.Append(text);
                continue;
            }
            var value = (BoundExpression)part;
            if (value.Type is ClrTypeSymbol { Type.FullName: "Piper.Runtime.Sql" })
                return null;
            var type = value.Type;
            bool list = false;
            if (Element(type) is { } element)
                (type, list) = (element, true);
            var stored = DatabaseSchema.Stored(type);
            builder.Append(SqlStatement.Hole(holes.Count));
            var written = value.SourceSpan is { } at && at.Start + at.Length <= file.Length ? $"`{{{file.ToString(at)}}}`" : "A hole";
            holes.Add(new SqlHole($"{written} ({value.Type})", stored?.Stored, stored?.Optional ?? false, list));
        }
        return (builder.ToString(), holes);
    }

    /// <summary>A list's element, for `in ({ids})`: a List, an array (but not bytes, which are one value) or a sequence.</summary>
    private static TypeSymbol? Element(TypeSymbol type)
    {
        if (type is not ClrTypeSymbol { Type: var clr } || type.TypeArguments is not [var element])
            return null;
        if (clr.IsArray)
            return element == TypeSymbol.Byte ? null : element;
        var definition = clr.IsGenericType ? clr.GetGenericTypeDefinition().FullName : null;
        return definition is "System.Collections.Generic.List`1" or "System.Collections.Generic.IEnumerable`1" or "System.Collections.Generic.IReadOnlyList`1"
            or "System.Collections.Generic.IList`1" or "System.Collections.Generic.ICollection`1" or "System.Collections.Generic.HashSet`1"
            or "System.Collections.Immutable.ImmutableArray`1" or "System.Collections.Generic.IReadOnlyCollection`1"
            ? element : null;
    }
}
