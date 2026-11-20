using System.Text.Json.Nodes;
using Piper.Binding;
using Piper.Syntax;
using Piper.Text;

namespace Piper.Plugins.Data;

/// <summary>
/// A database's shape, as recorded in <c>TrackerDb.schema.json</c> beside its declaration: each table's columns, as
/// Piper types and as what they're stored as (a newtype as its value, an Option as a nullable column, a fieldless enum
/// as its variant's name, as Piper.Data's converters store them), its key and its indexes, and which of Piper's own
/// tables it has. Logical rather than SQL, so one shape serves every provider; <c>piper db migrate</c> turns the change
/// between two shapes into each provider's SQL. A change that would break the release still running beside the new one
/// (a column removed, or added required) is an error, unless it's accepted.
/// </summary>
internal static class DatabaseSchema
{
    /// <summary>The shape of <paramref name="database"/>, and warnings for columns it can't describe.</summary>
    public static JsonObject Describe(DataPlugin.DatabaseData database, BoundProgram program, Action<TextSpan, string> warn)
    {
        var tables = new JsonObject();
        foreach (var table in database.Tables)
        {
            if (table.Entity is not NamedTypeSyntax { Name.Text: var entityName, TypeArguments.IsDefaultOrEmpty: true })
            {
                warn(table.Entity.Span, $"Table '{table.Table.Text}' is of a generic type, whose columns migrations can't see, so they leave it out.");
                continue;
            }
            var type = program.Types.FirstOrDefault(t => t.Name == entityName);
            IEnumerable<FieldSymbol>? fields = type switch
            {
                RecordSymbol record => record.Fields,
                // A class's columns are its public fields and stored properties, as EF Core maps them.
                ClassSymbol @class => @class.Fields.Where(f => !f.IsStatic && (f.IsProperty ? f.IsAutomatic : f.IsPublic)),
                _ => null,
            };
            if (fields is null)
            {
                warn(table.Entity.Span, $"'{entityName}' is a .NET type, whose columns migrations can't see, so they leave table '{table.Table.Text}' out.");
                continue;
            }

            var columns = new JsonObject();
            var stored = new Dictionary<string, (string Stored, bool Newtype)>();
            foreach (var field in fields)
            {
                if (Stored(field.Type) is not { } column)
                {
                    warn(table.Table.Span,
                        $"'{entityName}.{field.Name}' is {Article(field.Type)}, which migrations don't know how to store, so they leave it out of table '{table.Table.Text}'.");
                    continue;
                }
                var entry = new JsonObject { ["type"] = field.Type.ToString(), ["stored"] = column.Stored };
                if (column.Optional)
                    entry["optional"] = true;
                // Stored through a converter, as Piper.Data stores a newtype, which providers number differently as a key.
                if (column.Newtype)
                    entry["newtype"] = true;
                columns[field.Name] = entry;
                stored[field.Name] = (column.Stored, column.Newtype);
            }

            // Without a `key`, EF Core's: a field named `id`, or the type's name and `Id`, in any case.
            var found = stored.Keys.FirstOrDefault(c => c.Equals("id", StringComparison.OrdinalIgnoreCase))
                ?? stored.Keys.FirstOrDefault(c => c.Equals(entityName + "Id", StringComparison.OrdinalIgnoreCase));
            var key = table.Key ?? (found is null ? [] : [found]);
            var shape = new JsonObject
            {
                ["entity"] = entityName,
                ["key"] = new JsonArray([.. key.Select(k => (JsonNode)k)]),
            };
            // EF Core numbers the rows of a single integer key itself (Piper.Data, a newtype of an int or long's).
            if (key is [var only] && stored.TryGetValue(only, out var keyColumn) &&
                (keyColumn.Stored is "int" or "long" || !keyColumn.Newtype && keyColumn.Stored is "short" or "byte"))
            {
                shape["generated"] = true;
            }
            shape["columns"] = columns;
            if (table.Indexes.Count > 0)
                shape["indexes"] = new JsonArray([.. table.Indexes.Select(i => (JsonNode)new JsonArray([.. i.Select(c => (JsonNode)c)]))]);
            tables[table.Table.Text] = shape;
        }

        var described = new JsonObject { ["name"] = database.Name.Text, ["tables"] = tables };
        var piper = new JsonArray();
        if (database.Outbox)
            piper.Add("outbox");
        if (database.Idempotency)
            piper.Add("idempotency");
        if (piper.Count > 0)
            described["piper"] = piper;
        return described;
    }

    private static string Article(TypeSymbol type) =>
        type.ToString() is var name && name.Length > 0 && "AEIOUaeiou".Contains(name[0]) ? $"an {name}" : $"a {name}";

    /// <summary>What a field of <paramref name="type"/> is stored as, and whether it may be NULL; null if it can't be a column.</summary>
    private static (string Stored, bool Optional, bool Newtype)? Stored(TypeSymbol type)
    {
        if (type.IsOption)
            return Stored(type.TypeArguments[0]) is { Optional: false } inner ? inner with { Optional = true } : null;
        if (type == TypeSymbol.Int) return ("int", false, false);
        if (type == TypeSymbol.Long) return ("long", false, false);
        if (type == TypeSymbol.Short) return ("short", false, false);
        if (type == TypeSymbol.Byte) return ("byte", false, false);
        if (type == TypeSymbol.Bool) return ("bool", false, false);
        if (type == TypeSymbol.Double) return ("double", false, false);
        if (type == TypeSymbol.Float) return ("float", false, false);
        if (type == TypeSymbol.Decimal) return ("decimal", false, false);
        if (type == TypeSymbol.Char) return ("char", false, false);
        if (type == TypeSymbol.String) return ("string", false, false);
        switch (type)
        {
            case RecordSymbol { IsNewtype: true, Fields: [var value] }:
                return Stored(value.Type) is { Optional: false } inner ? inner with { Newtype = true } : null;
            case EnumSymbol { Declaration: not null } @enum when @enum.Variants.All(v => v.Fields.IsEmpty):
                return ("string", false, false);
            case ClrTypeSymbol { IsArray: true, TypeArguments: [var element] } when element == TypeSymbol.Byte:
                return ("bytes", false, false);
            case ClrTypeSymbol { IsArray: false } clr:
                return clr.Type.FullName switch
                {
                    "System.Guid" => ("Guid", false, false),
                    "System.DateTime" => ("DateTime", false, false),
                    "System.DateTimeOffset" => ("DateTimeOffset", false, false),
                    "System.DateOnly" => ("DateOnly", false, false),
                    "System.TimeOnly" => ("TimeOnly", false, false),
                    "System.TimeSpan" => ("TimeSpan", false, false),
                    _ => null,
                };
            default:
                return null;
        }
    }

    /// <summary>
    /// The changes from <paramref name="before"/> to <paramref name="now"/> that the release running now, still reading and
    /// writing the database, wouldn't survive, each at the table it's in (or the database, for a table gone).
    /// </summary>
    public static IEnumerable<(TextSpan Span, string Change)> Breaking(JsonObject before, JsonObject now, DataPlugin.DatabaseData database)
    {
        TextSpan At(string table) => database.Tables.FirstOrDefault(t => t.Table.Text == table)?.Table.Span ?? database.Name.Span;
        const string Expand = "change a schema in two releases: add the new alongside the old, and remove the old once every instance runs the new";

        var oldTables = before["tables"] as JsonObject ?? [];
        var newTables = now["tables"] as JsonObject ?? [];
        foreach (var (name, node) in oldTables)
        {
            if (newTables[name] is not JsonObject table)
            {
                yield return (database.Name.Span, $"table '{name}' is gone, which the release running now still reads and writes ({Expand})");
                continue;
            }
            if (node is not JsonObject old)
                continue;
            var oldKey = string.Join(", ", (old["key"] as JsonArray ?? []).Select(k => (string?)k));
            var newKey = string.Join(", ", (table["key"] as JsonArray ?? []).Select(k => (string?)k));
            if (oldKey != newKey)
                yield return (At(name), $"table '{name}''s key changed from ({oldKey}) to ({newKey})");

            var oldColumns = old["columns"] as JsonObject ?? [];
            var newColumns = table["columns"] as JsonObject ?? [];
            foreach (var (column, value) in oldColumns)
            {
                if (newColumns[column] is not JsonObject current)
                {
                    yield return (At(name), $"column '{name}.{column}' is gone, which the release running now still reads and writes ({Expand})");
                    continue;
                }
                var (oldStored, newStored) = ((string?)value?["stored"], (string?)current["stored"]);
                if (oldStored != newStored)
                    yield return (At(name), $"column '{name}.{column}' is stored as {newStored} now, not {oldStored}, which the release running now reads and writes ({Expand})");
                else if (((bool?)value?["optional"] ?? false) && !((bool?)current["optional"] ?? false))
                    yield return (At(name), $"column '{name}.{column}' is required now, but the release running now leaves it empty (make it required once every instance writes it, and every row has it)");
            }
            foreach (var (column, value) in newColumns)
            {
                if (!oldColumns.ContainsKey(column) && !((bool?)value?["optional"] ?? false))
                {
                    yield return (At(name),
                        $"column '{name}.{column}' is new and required, but the rows the release running now adds won't have it (add it as an Option, `{value?["type"]}?`, first, and make it required once every instance writes it)");
                }
            }
        }
        var newPiper = (now["piper"] as JsonArray ?? []).Select(p => (string?)p).ToHashSet();
        foreach (var piper in (before["piper"] as JsonArray ?? []).Select(p => (string?)p))
        {
            if (!newPiper.Contains(piper))
                yield return (database.Name.Span, $"its {piper} table is gone, which the release running now still uses");
        }
    }
}
