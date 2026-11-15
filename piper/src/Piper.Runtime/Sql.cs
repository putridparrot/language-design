using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text;

namespace Piper.Runtime;

/// <summary>
/// Marks a type a prefixed interpolated string makes: <c>[InterpolationKind("sql")]</c> on <see cref="Sql"/> makes
/// <c>sql$"…"</c> one. The type has a static <c>Builder()</c>, whose builder has <c>Text(string)</c>, <c>Hole</c>
/// overloads (and, to allow formats, <c>Hole(value, format)</c> overloads), each returning the builder, and
/// <c>Build()</c>. A hole is checked against the <c>Hole</c> overloads, so what can't go in one is a compile error.
/// A plain <c>$"…"</c> given where the type is wanted is one too.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class InterpolationKindAttribute(string prefix) : Attribute
{
    /// <summary>What goes before <c>$"</c>: <c>sql</c> for <c>sql$"…"</c>.</summary>
    public string Prefix { get; } = prefix;

    /// <summary>What a hole can hold, in words, for the error when one holds something else.</summary>
    public string? Holds { get; init; }
}

/// <summary>
/// A SQL command and its parameters, as <c>sql$"select * from lots where id = {id}"</c> makes it: each hole is a
/// parameter (<c>@p0</c>, <c>@p1</c>, …), never text, so SQL can't be injected by accident. A hole that's an
/// <c>Sql</c> goes in as SQL, with its parameters; a list is a parameter for each item (<c>in ({ids})</c>); None is
/// NULL. <see cref="Raw"/> is the one way to make SQL from a string, for what can't be a parameter (a table's name).
/// </summary>
[InterpolationKind("sql", Holds = "a parameter's value (a number, text, a char, a bool, a date or time, a Guid or bytes, an Option " +
                                   "of one, a newtype of one, or a list of them, for 'in (…)'), or another Sql; for SQL itself, such as a table's name, Sql.Raw(…)")]
public sealed class Sql : IEquatable<Sql>
{
    // The text around the parameters: one more piece than there are parameters.
    private readonly string[] _pieces;
    private readonly object?[] _parameters;

    internal Sql(string[] pieces, object?[] parameters)
    {
        _pieces = pieces;
        _parameters = parameters;
    }

    /// <summary>No SQL.</summary>
    public static Sql Empty { get; } = new([""], []);

    /// <summary>SQL from a string, as it is: for SQL from somewhere trusted, such as a table's name the program chose.</summary>
    public static Sql Raw(string text) => new([text], []);

    /// <summary>What <c>sql$"…"</c> compiles to.</summary>
    public static SqlBuilder Builder() => new();

    /// <summary>The pieces joined by <paramref name="separator"/> (SQL, as written): <c>Sql.Join(" and ", conditions)</c>.</summary>
    public static Sql Join(string separator, IEnumerable<Sql> parts)
    {
        var builder = new SqlBuilder();
        bool first = true;
        foreach (var part in parts)
        {
            if (!first)
                builder.Text(separator);
            builder.Hole(part);
            first = false;
        }
        return builder.Build();
    }

    /// <summary>The command's text, its parameters named <c>@p0</c>, <c>@p1</c>, ….</summary>
    public string Text
    {
        get
        {
            var text = new StringBuilder(_pieces[0]);
            for (int i = 0; i < _parameters.Length; i++)
                text.Append("@p").Append(i).Append(_pieces[i + 1]);
            return text.ToString();
        }
    }

    /// <summary>The parameters' values, in order: <c>@p0</c>'s first. None is null (NULL in a command).</summary>
    public IReadOnlyList<object?> Parameters => _parameters;

    /// <summary>A command on <paramref name="connection"/> with this text and parameters.</summary>
    public DbCommand ToCommand(DbConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = Text;
        for (int i = 0; i < _parameters.Length; i++)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@p" + i;
            parameter.Value = _parameters[i] ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        return command;
    }

    /// <summary>
    /// The SQL as a <see cref="FormattableString"/> whose arguments are the parameters, as EF Core's <c>FromSql</c>
    /// and <c>ExecuteSql</c> take it (they make each argument a parameter).
    /// </summary>
    public FormattableString ToFormattableString()
    {
        var format = new StringBuilder(Escape(_pieces[0]));
        for (int i = 0; i < _parameters.Length; i++)
            format.Append('{').Append(i).Append('}').Append(Escape(_pieces[i + 1]));
        return FormattableStringFactory.Create(format.ToString(), _parameters);

        static string Escape(string text) => text.Replace("{", "{{").Replace("}", "}}");
    }

    /// <summary>The command's text (see <see cref="Text"/>); the parameters aren't in it.</summary>
    public override string ToString() => Text;

    public bool Equals(Sql? other) =>
        other is not null && _pieces.AsSpan().SequenceEqual(other._pieces) && _parameters.SequenceEqual(other._parameters);

    public override bool Equals(object? obj) => obj is Sql other && Equals(other);

    public override int GetHashCode() => Text.GetHashCode();

    public static bool operator ==(Sql? a, Sql? b) => a is null ? b is null : a.Equals(b);

    public static bool operator !=(Sql? a, Sql? b) => !(a == b);

    internal string[] Pieces => _pieces;

    internal object?[] Values => _parameters;
}

/// <summary>
/// Builds an <see cref="Sql"/>: what <c>sql$"…"</c> compiles to. Its <c>Hole</c> overloads are what can be a
/// parameter: numbers, text, chars, bools, dates and times, Guids and bytes, Options of them, and lists of them, or
/// another <see cref="Sql"/>. Anything else in a hole is a compile error.
/// </summary>
public sealed class SqlBuilder
{
    private readonly List<string> _pieces = [""];
    private readonly List<object?> _parameters = [];

    /// <summary>SQL written in the literal.</summary>
    public SqlBuilder Text(string text)
    {
        _pieces[^1] += text;
        return this;
    }

    /// <summary>SQL, with its parameters, which keep their values (and are renumbered).</summary>
    public SqlBuilder Hole(Sql sql)
    {
        _pieces[^1] += sql.Pieces[0];
        for (int i = 0; i < sql.Values.Length; i++)
        {
            _parameters.Add(sql.Values[i]);
            _pieces.Add(sql.Pieces[i + 1]);
        }
        return this;
    }

    public SqlBuilder Hole(string value) => Parameter(value);
    public SqlBuilder Hole(char value) => Parameter(value);
    public SqlBuilder Hole(bool value) => Parameter(value);
    public SqlBuilder Hole(byte value) => Parameter(value);
    public SqlBuilder Hole(short value) => Parameter(value);
    public SqlBuilder Hole(int value) => Parameter(value);
    public SqlBuilder Hole(long value) => Parameter(value);
    public SqlBuilder Hole(float value) => Parameter(value);
    public SqlBuilder Hole(double value) => Parameter(value);
    public SqlBuilder Hole(decimal value) => Parameter(value);
    public SqlBuilder Hole(Guid value) => Parameter(value);
    public SqlBuilder Hole(DateTime value) => Parameter(value);
    public SqlBuilder Hole(DateTimeOffset value) => Parameter(value);
    public SqlBuilder Hole(DateOnly value) => Parameter(value);
    public SqlBuilder Hole(TimeOnly value) => Parameter(value);
    public SqlBuilder Hole(TimeSpan value) => Parameter(value);
    public SqlBuilder Hole(byte[] value) => Parameter(value);

    public SqlBuilder Hole(Option<string> value) => Optional(value);
    public SqlBuilder Hole(Option<char> value) => Optional(value);
    public SqlBuilder Hole(Option<bool> value) => Optional(value);
    public SqlBuilder Hole(Option<byte> value) => Optional(value);
    public SqlBuilder Hole(Option<short> value) => Optional(value);
    public SqlBuilder Hole(Option<int> value) => Optional(value);
    public SqlBuilder Hole(Option<long> value) => Optional(value);
    public SqlBuilder Hole(Option<float> value) => Optional(value);
    public SqlBuilder Hole(Option<double> value) => Optional(value);
    public SqlBuilder Hole(Option<decimal> value) => Optional(value);
    public SqlBuilder Hole(Option<Guid> value) => Optional(value);
    public SqlBuilder Hole(Option<DateTime> value) => Optional(value);
    public SqlBuilder Hole(Option<DateTimeOffset> value) => Optional(value);
    public SqlBuilder Hole(Option<DateOnly> value) => Optional(value);
    public SqlBuilder Hole(Option<TimeOnly> value) => Optional(value);
    public SqlBuilder Hole(Option<TimeSpan> value) => Optional(value);
    public SqlBuilder Hole(Option<byte[]> value) => Optional(value);

    public SqlBuilder Hole(IEnumerable<string> values) => Each(values);
    public SqlBuilder Hole(IEnumerable<char> values) => Each(values);
    public SqlBuilder Hole(IEnumerable<short> values) => Each(values);
    public SqlBuilder Hole(IEnumerable<int> values) => Each(values);
    public SqlBuilder Hole(IEnumerable<long> values) => Each(values);
    public SqlBuilder Hole(IEnumerable<float> values) => Each(values);
    public SqlBuilder Hole(IEnumerable<double> values) => Each(values);
    public SqlBuilder Hole(IEnumerable<decimal> values) => Each(values);
    public SqlBuilder Hole(IEnumerable<Guid> values) => Each(values);
    public SqlBuilder Hole(IEnumerable<DateTime> values) => Each(values);
    public SqlBuilder Hole(IEnumerable<DateTimeOffset> values) => Each(values);
    public SqlBuilder Hole(IEnumerable<DateOnly> values) => Each(values);

    public Sql Build() => new([.. _pieces], [.. _parameters]);

    private SqlBuilder Parameter(object? value)
    {
        _parameters.Add(value);
        _pieces.Add("");
        return this;
    }

    private SqlBuilder Optional<T>(Option<T> value) => Parameter(value.IsSome ? value.Value : null);

    /// <summary>A parameter for each value, separated by commas, for <c>in (…)</c>; none is NULL, which nothing is in.</summary>
    private SqlBuilder Each<T>(IEnumerable<T> values)
    {
        bool any = false;
        foreach (var value in values)
        {
            if (any)
                Text(", ");
            Parameter(value);
            any = true;
        }
        return any ? this : Text("NULL");
    }
}
