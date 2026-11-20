using System.Text;

namespace Piper.Plugins.Data;

/// <summary>A table as a query is checked against it: its columns, each with what it's stored as (int, string, …).</summary>
internal sealed record SqlTable(string Name, IReadOnlyDictionary<string, string> Columns);

/// <summary>What a hole holds, as stored: its type's stored name (null if it isn't one), whether it's an Option, and a list.</summary>
internal sealed record SqlHole(string Display, string? Stored, bool Optional, bool List);

/// <summary>Something wrong with a query, and the name in it to point at (the table or column), if there is one.</summary>
internal sealed record SqlProblem(string Message, string? Near, bool Warning = false);

/// <summary>
/// What reading a query found: whether it could follow it at all (if not, nothing is reported), its problems, and the
/// columns a select gives, by name (null for one it can't name, such as <c>count(*)</c> without an alias), or null
/// if it can't tell (a <c>*</c> over a table it doesn't know).
/// </summary>
internal sealed record SqlAnalysis(bool Followed, List<SqlProblem> Problems, List<string?>? Results)
{
    public static readonly SqlAnalysis NotFollowed = new(false, [], null);
}

/// <summary>
/// Reads a statement of SQL far enough to check it against a database's shape: the tables it names (after
/// <c>from</c>, <c>join</c>, <c>into</c> and <c>update</c>, each with its alias), the columns it names, the column each
/// hole is compared with, inserted into or set to, and the columns a select gives. A light reader for the SQL every
/// provider speaks: it doesn't validate SQL, and what it can't follow (a subquery, <c>union</c>, <c>with</c>, a name
/// with a schema, a table Piper doesn't know of such as <c>sqlite_master</c>) is left unchecked, never refused.
/// </summary>
internal static class SqlStatement
{
    /// <summary>Marks a hole in the text: <c>\u0001</c>, its number, <c>\u0001</c>.</summary>
    public const char HoleMark = '\u0001';

    public static string Hole(int index) => $"{HoleMark}{index}{HoleMark}";

    private static readonly HashSet<string> Numbers = ["int", "long", "short", "byte", "decimal", "double", "float"];

    /// <summary>
    /// Words that are SQL rather than names, across the providers' dialects (and the parts of a few functions written
    /// without parentheses, as <c>extract(year from …)</c>), so they're never taken for columns.
    /// </summary>
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "select", "from", "where", "and", "or", "not", "in", "is", "null", "like", "ilike", "glob", "regexp", "similar",
        "between", "order", "by", "group", "having", "limit", "offset", "fetch", "next", "first", "last", "rows", "row",
        "only", "top", "percent", "ties", "asc", "desc", "nulls", "as", "on", "using", "join", "inner", "left", "right",
        "outer", "cross", "full", "natural", "insert", "into", "values", "update", "set", "delete", "distinct", "all",
        "any", "some", "exists", "case", "when", "then", "else", "end", "true", "false", "unknown", "collate", "escape",
        "nocase", "binary", "rtrim", "cast", "convert", "interval", "date", "time", "timestamp", "zone", "at", "year",
        "month", "day", "hour", "minute", "second", "week", "quarter", "epoch", "current_timestamp", "current_date",
        "current_time", "localtimestamp", "localtime", "current_user", "default", "returning", "output", "inserted",
        "deleted", "conflict", "do", "nothing", "excluded", "ignore", "replace", "abort", "fail", "rollback", "for",
        "share", "nowait", "skip", "locked", "with", "nolock", "rowlock", "updlock", "holdlock", "readpast", "over",
        "partition", "within", "filter", "window", "range", "unbounded", "preceding", "following", "current", "lateral",
        "union", "intersect", "except", "recursive", "of", "to", "varchar", "nvarchar", "char", "nchar", "text", "integer",
        "int", "bigint", "smallint", "tinyint", "real", "float", "double", "precision", "numeric", "decimal", "boolean",
        "bit", "uuid", "uniqueidentifier", "datetime", "datetime2", "datetimeoffset", "blob", "varbinary", "max",
    };

    private enum Kind { Word, Quoted, Text, Number, Hole, Parameter, Symbol }

    private sealed record Token(Kind Kind, string Text, int Hole = -1)
    {
        public bool IsName => Kind is Kind.Quoted || (Kind is Kind.Word && !Keywords.Contains(Text));

        public bool Is(string text) => (Kind is Kind.Word or Kind.Symbol) && Text.Equals(text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads <paramref name="sql"/> (with its holes marked: <see cref="Hole"/>) against <paramref name="tables"/>, the
    /// database's, by name. <paramref name="holes"/> are what each hole holds.
    /// </summary>
    public static SqlAnalysis Analyze(string sql, string database, IReadOnlyDictionary<string, SqlTable> tables, IReadOnlyList<SqlHole> holes)
    {
        if (Tokenize(sql) is not { Count: > 0 } tokens)
            return SqlAnalysis.NotFollowed;
        if (tokens[^1].Is(";"))
            tokens.RemoveAt(tokens.Count - 1);
        var statement = tokens[0].Kind == Kind.Word ? tokens[0].Text.ToLowerInvariant() : "";
        if (statement is not ("select" or "insert" or "update" or "delete") || tokens.Any(t => t.Is(";")) ||
            tokens.Skip(1).Any(t => t.Is("select") || t.Is("union") || t.Is("intersect") || t.Is("except") || t.Is("with")))
        {
            return SqlAnalysis.NotFollowed;
        }

        var reader = new Reader(tokens, database, tables);
        if (!reader.ReadTables(statement))
            return SqlAnalysis.NotFollowed;
        reader.CheckColumns();
        reader.CheckHoles(holes);
        if (statement == "insert")
            reader.CheckInsert(holes);
        return new SqlAnalysis(true, reader.Problems, statement == "select" ? reader.Results() : null);
    }

    /// <summary>Whether a value stored as <paramref name="hole"/> suits a column stored as <paramref name="column"/>.</summary>
    public static bool Suits(string hole, string column) =>
        hole == column || (Numbers.Contains(hole) && Numbers.Contains(column)) || (hole is "string" or "char" && column is "string" or "char");

    private sealed class Reader(List<Token> tokens, string database, IReadOnlyDictionary<string, SqlTable> tables)
    {
        /// <summary>The tables in scope: each's alias (or its own name), and the table, or null for one Piper can't check.</summary>
        private readonly List<(string Alias, string Name, SqlTable? Table)> _scope = [];

        /// <summary>Tokens read as a table, an alias or a column already: not names to check again.</summary>
        private readonly HashSet<int> _read = [];

        private readonly HashSet<string> _aliases = new(StringComparer.OrdinalIgnoreCase);

        public List<SqlProblem> Problems { get; } = [];

        private Token At(int i) => i >= 0 && i < tokens.Count ? tokens[i] : new Token(Kind.Symbol, "");

        /// <summary>The tables after <c>from</c>, <c>join</c>, <c>into</c> and <c>update</c>; false if it can't follow them.</summary>
        public bool ReadTables(string statement)
        {
            int depth = 0;
            for (int i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.Is("("))
                    depth++;
                else if (token.Is(")"))
                    depth--;
                if (depth != 0)
                    continue;
                bool from = token.Is("from") || (token.Is("join")) || (token.Is("update") && i == 0) || (token.Is("into") && statement == "insert");
                if (!from)
                    continue;
                // `extract(year from …)` is inside parentheses, so never here.
                i = ReadTable(i + 1);
                if (i < 0)
                    return false;
                i--;
            }
            // Column names before `as` and after a select's expressions are aliases, which `order by` may use.
            for (int i = 0; i + 1 < tokens.Count; i++)
            {
                if (tokens[i].Is("as") && tokens[i + 1].Kind is Kind.Word or Kind.Quoted)
                {
                    _aliases.Add(tokens[i + 1].Text);
                    _read.Add(i + 1);
                }
            }
            if (statement == "select")
            {
                foreach (var item in SelectList())
                {
                    if (item.Count >= 2 && At(item[^1]).IsName && (At(item[^2]).Kind is Kind.Word or Kind.Quoted or Kind.Number or Kind.Text || At(item[^2]).Is(")")) &&
                        !At(item[^2]).Is("as") && !At(item[^2]).Is("."))
                    {
                        _aliases.Add(At(item[^1]).Text);
                        _read.Add(item[^1]);
                    }
                }
            }
            return true;
        }

        /// <summary>Reads a table and its alias from <paramref name="i"/>, then any more after commas: the index after them, or -1.</summary>
        private int ReadTable(int i)
        {
            while (true)
            {
                var name = At(i);
                if (name.Kind is not (Kind.Word or Kind.Quoted) || At(i + 1).Is("."))
                    return -1;
                _read.Add(i);
                i++;
                var alias = name.Text;
                if (At(i).Is("as") && At(i + 1).Kind is Kind.Word or Kind.Quoted)
                {
                    alias = At(i + 1).Text;
                    _read.Add(i + 1);
                    i += 2;
                }
                else if (At(i).IsName)
                {
                    alias = At(i).Text;
                    _read.Add(i);
                    i++;
                }
                var table = tables.FirstOrDefault(t => t.Key.Equals(name.Text, StringComparison.OrdinalIgnoreCase)).Value;
                if (table is null && !IsSystem(name.Text))
                    Problems.Add(new($"{database} has no table '{name.Text}'", name.Text));
                _scope.Add((alias, name.Text, table));
                if (!At(i).Is(","))
                    return i;
                // `from a, b`: another table. (`insert into t (a, b)` never reaches here: `(` isn't a comma.)
                i++;
            }
        }

        /// <summary>Tables a query may name that aren't the program's: the databases' own, and Piper's.</summary>
        private static bool IsSystem(string name) =>
            name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("pg_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("sys", StringComparison.OrdinalIgnoreCase) || name.StartsWith("information_schema", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Piper", StringComparison.Ordinal) || name.Equals("dual", StringComparison.OrdinalIgnoreCase);

        /// <summary>Every name used as a column is one of a table in scope (unless one of them is a table Piper can't check).</summary>
        public void CheckColumns()
        {
            for (int i = 0; i < tokens.Count; i++)
            {
                if (_read.Contains(i) || !tokens[i].IsName)
                    continue;
                // A function (`lower(…)`), a type after `::`.
                if (At(i + 1).Is("(") || At(i - 1).Is("::"))
                    continue;
                if (At(i + 1).Is("."))
                {
                    // `o.total`: the column of the table `o` names, if it names one.
                    var column = At(i + 2);
                    _read.Add(i + 2);
                    if (Scoped(tokens[i].Text) is { } scoped && scoped.Table is { } table && column.Kind is Kind.Word or Kind.Quoted && !Has(table, column.Text))
                        Problems.Add(new($"'{table.Name}' has no column '{column.Text}'", column.Text));
                    i += 2;
                    continue;
                }
                var name = tokens[i].Text;
                if (_aliases.Contains(name) || Scoped(name) is not null)
                    continue;
                if (_scope.Count == 0 || _scope.Any(s => s.Table is null) || _scope.Any(s => Has(s.Table!, name)))
                    continue;
                Problems.Add(new(_scope.Count == 1
                    ? $"'{_scope[0].Table!.Name}' has no column '{name}'"
                    : $"none of {string.Join(", ", _scope.Select(s => $"'{s.Table!.Name}'"))} has a column '{name}'", name));
            }
        }

        private (string Alias, string Name, SqlTable? Table)? Scoped(string name) =>
            _scope.Cast<(string Alias, string Name, SqlTable? Table)?>().FirstOrDefault(s =>
                s!.Value.Alias.Equals(name, StringComparison.OrdinalIgnoreCase) || s.Value.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        private static bool Has(SqlTable table, string column) => table.Columns.Keys.Any(c => c.Equals(column, StringComparison.OrdinalIgnoreCase));

        private static string? Stored(SqlTable table, string column) =>
            table.Columns.FirstOrDefault(c => c.Key.Equals(column, StringComparison.OrdinalIgnoreCase)).Value;

        /// <summary>The column the name ending at <paramref name="i"/> is (<c>total</c>, <c>o.total</c>), if it's one Piper knows.</summary>
        private (SqlTable Table, string Column, string Stored)? Column(int i)
        {
            var token = At(i);
            if (token.Kind is not (Kind.Word or Kind.Quoted) || (token.Kind == Kind.Word && Keywords.Contains(token.Text)))
                return null;
            if (At(i - 1).Is("."))
            {
                return Scoped(At(i - 2).Text) is { Table: { } table } && Stored(table, token.Text) is { } stored
                    ? (table, token.Text, stored) : null;
            }
            foreach (var (_, _, table) in _scope)
            {
                if (table is not null && Stored(table, token.Text) is { } stored)
                    return (table, token.Text, stored);
            }
            return null;
        }

        private static readonly string[] Comparisons = ["=", "<>", "!=", "<", ">", "<=", ">=", "like", "ilike"];

        /// <summary>Each hole compared with a column (or set to one) holds what suits it; an Option compared with `=` is a warning.</summary>
        public void CheckHoles(IReadOnlyList<SqlHole> holes)
        {
            for (int i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].Kind != Kind.Hole || tokens[i].Hole >= holes.Count)
                    continue;
                var hole = holes[tokens[i].Hole];
                string? comparison = null;
                (SqlTable Table, string Column, string Stored)? column = null;
                bool list = false;
                if (Comparisons.Any(c => At(i - 1).Is(c)) && Column(i - 2) is { } left)
                    (column, comparison) = (left, At(i - 1).Text);
                else if (Comparisons.Any(c => At(i + 1).Is(c)) && Column(i + 2) is { } right)
                    (column, comparison) = (right, At(i + 1).Text);
                else if (At(i - 1).Is("(") && At(i - 2).Is("in") && At(i + 1).Is(")") && Column(i - 3) is { } member)
                    (column, list) = (member, true);
                else if (At(i - 1).Is("between") && Column(i - 2) is { } low)
                    column = low;
                else if (At(i - 1).Is("and") && At(i - 3).Is("between") && Column(i - 4) is { } high)
                    column = high;
                if (column is not { } found)
                    continue;
                Suit(hole, found.Table, found.Column, found.Stored, list);
                if (hole.Optional && comparison is "=" or "<>" or "!=" && !(comparison == "=" && Assigns(i)))
                {
                    Problems.Add(new(
                        $"{hole.Display} is optional, and `{found.Column} {comparison} NULL` is never true, so when it's None this matches no rows: " +
                        $"compare with `{found.Column} IS NULL` for None, or choose the query by whether it has a value", found.Column, Warning: true));
                }
            }
        }

        /// <summary>Whether the token at <paramref name="i"/> is in an update's `set` list, where `=` assigns rather than compares.</summary>
        private bool Assigns(int i)
        {
            int set = tokens.FindIndex(t => t.Is("set"));
            if (set < 0 || i < set || !tokens[0].Is("update"))
                return false;
            int depth = 0;
            for (int j = set + 1; j < i; j++)
            {
                if (tokens[j].Is("("))
                    depth++;
                else if (tokens[j].Is(")"))
                    depth--;
                else if (depth == 0 && (tokens[j].Is("where") || tokens[j].Is("from") || tokens[j].Is("returning")))
                    return false;
            }
            return true;
        }

        private void Suit(SqlHole hole, SqlTable table, string column, string stored, bool list)
        {
            if (hole.Stored is null || (hole.List && !list))
                return;
            if (!Suits(hole.Stored, stored))
                Problems.Add(new($"{hole.Display} doesn't suit '{table.Name}.{column}', which is stored as {stored}", column));
        }

        /// <summary>`insert into t (a, b) values ({x}, {y})`: as many values as columns, each hole suiting its column.</summary>
        public void CheckInsert(IReadOnlyList<SqlHole> holes)
        {
            if (_scope is not [(_, _, { } table)])
                return;
            int open = tokens.FindIndex(t => t.Is("("));
            int values = tokens.FindIndex(t => t.Is("values"));
            if (open < 0 || values < open)
                return;
            var columns = Items(open).Select(item => item is [var only] ? At(only).Text : null).ToList();
            foreach (var row in Rows(values + 1))
            {
                if (row.Count != columns.Count)
                {
                    Problems.Add(new($"the insert names {columns.Count} {(columns.Count == 1 ? "column" : "columns")} but gives {row.Count} {(row.Count == 1 ? "value" : "values")}", null));
                    return;
                }
                for (int k = 0; k < row.Count; k++)
                {
                    if (row[k] is [var only] && At(only).Kind == Kind.Hole && At(only).Hole < holes.Count &&
                        columns[k] is { } name && Stored(table, name) is { } stored)
                    {
                        Suit(holes[At(only).Hole], table, name, stored, list: false);
                    }
                }
            }
        }

        /// <summary>The comma-separated items inside the parentheses opening at <paramref name="open"/>, as token indexes.</summary>
        private List<List<int>> Items(int open)
        {
            var items = new List<List<int>> { new() };
            int depth = 0;
            for (int i = open + 1; i < tokens.Count; i++)
            {
                if (tokens[i].Is("("))
                    depth++;
                else if (tokens[i].Is(")") && depth-- == 0)
                    break;
                if (depth == 0 && tokens[i].Is(","))
                    items.Add([]);
                else
                    items[^1].Add(i);
            }
            return items;
        }

        /// <summary>`values (…), (…)`: each row's items.</summary>
        private IEnumerable<List<List<int>>> Rows(int i)
        {
            while (At(i).Is("("))
            {
                var row = Items(i);
                yield return row;
                int depth = 0;
                for (; i < tokens.Count; i++)
                {
                    if (tokens[i].Is("("))
                        depth++;
                    else if (tokens[i].Is(")") && --depth == 0)
                        break;
                }
                i++;
                if (!At(i).Is(","))
                    yield break;
                i++;
            }
        }

        /// <summary>A select's items, between `select` (and `distinct`) and `from`, as token indexes.</summary>
        private List<List<int>> SelectList()
        {
            var items = new List<List<int>> { new() };
            int i = 1;
            while (At(i).Is("distinct") || At(i).Is("all"))
                i++;
            int depth = 0;
            for (; i < tokens.Count; i++)
            {
                if (tokens[i].Is("("))
                    depth++;
                else if (tokens[i].Is(")"))
                    depth--;
                if (depth == 0 && (tokens[i].Is("from") || tokens[i].Is("where") || tokens[i].Is("order") || tokens[i].Is("limit")))
                    break;
                if (depth == 0 && tokens[i].Is(","))
                    items.Add([]);
                else
                    items[^1].Add(i);
            }
            return [.. items.Where(item => item.Count > 0)];
        }

        /// <summary>The columns the select gives, by name; null if it can't tell.</summary>
        public List<string?>? Results()
        {
            var results = new List<string?>();
            foreach (var item in SelectList())
            {
                if (item is [var star] && At(star).Is("*"))
                {
                    if (_scope.Any(s => s.Table is null))
                        return null;
                    results.AddRange(_scope.SelectMany(s => s.Table!.Columns.Keys));
                }
                else if (item is [var table, var dot, var all] && At(dot).Is(".") && At(all).Is("*"))
                {
                    if (Scoped(At(table).Text) is not { Table: { } found })
                        return null;
                    results.AddRange(found.Columns.Keys);
                }
                else if (item.Count >= 2 && At(item[^2]).Is("as"))
                    results.Add(At(item[^1]).Text);
                else if (item.Count >= 2 && _aliases.Contains(At(item[^1]).Text) && _read.Contains(item[^1]))
                    results.Add(At(item[^1]).Text);
                else if (item is [var bare] && At(bare).Kind is Kind.Word or Kind.Quoted)
                    results.Add(At(bare).Text);
                else if (item is [_, var qualified, var name] && At(qualified).Is("."))
                    results.Add(At(name).Text);
                else
                    results.Add(null);
            }
            return results;
        }
    }

    /// <summary>The statement's tokens; null if it can't be read (an unclosed string or name).</summary>
    private static List<Token>? Tokenize(string sql)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < sql.Length)
        {
            char c = sql[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '-' && At(1) == '-')
            {
                while (i < sql.Length && sql[i] != '\n')
                    i++;
            }
            else if (c == '/' && At(1) == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                    return null;
                i = end + 2;
            }
            else if (c == HoleMark)
            {
                var end = sql.IndexOf(HoleMark, i + 1);
                tokens.Add(new Token(Kind.Hole, "?", int.Parse(sql[(i + 1)..end])));
                i = end + 1;
            }
            else if (c == '\'')
            {
                if (Quoted('\'', '\'') is not { } text)
                    return null;
                tokens.Add(new Token(Kind.Text, text));
            }
            else if (c is '"' or '[' or '`')
            {
                if (Quoted(c, c == '[' ? ']' : c) is not { } name)
                    return null;
                tokens.Add(new Token(Kind.Quoted, name));
            }
            else if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '$'))
                    i++;
                tokens.Add(new Token(Kind.Word, sql[start..i]));
            }
            else if (char.IsDigit(c))
            {
                int start = i;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '.'))
                    i++;
                tokens.Add(new Token(Kind.Number, sql[start..i]));
            }
            else if (c is '@' or ':' or '$' or '?' && (c == '?' || (At(1) is var next && (char.IsLetterOrDigit(next) || next == '_'))))
            {
                // A parameter written in the SQL itself: @name, :name, $1, ?.
                int start = i++;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_'))
                    i++;
                tokens.Add(new Token(Kind.Parameter, sql[start..i]));
            }
            else
            {
                var two = i + 1 < sql.Length ? sql.Substring(i, 2) : "";
                var symbol = two is "<=" or ">=" or "<>" or "!=" or "||" or "::" ? two : c.ToString();
                tokens.Add(new Token(Kind.Symbol, symbol));
                i += symbol.Length;
            }
        }
        return tokens;

        char At(int offset) => i + offset < sql.Length ? sql[i + offset] : '\0';

        string? Quoted(char open, char close)
        {
            var text = new StringBuilder();
            i++;
            while (i < sql.Length)
            {
                if (sql[i] == close)
                {
                    // Doubled, it's the character itself: 'it''s'.
                    if (i + 1 < sql.Length && sql[i + 1] == close && open == close)
                    {
                        text.Append(close);
                        i += 2;
                        continue;
                    }
                    i++;
                    return text.ToString();
                }
                text.Append(sql[i++]);
            }
            return null;
        }
    }
}
