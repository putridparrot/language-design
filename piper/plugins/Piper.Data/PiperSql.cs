using Piper.Runtime;

// In EF Core's namespace, as its providers' extensions are (UseSqlite), so the `using` a program has for EF Core finds them.
namespace Microsoft.EntityFrameworkCore;

/// <summary>
/// Raw SQL in a query or command, written <c>sql$"…"</c> (an <see cref="Sql"/>), whose holes are parameters:
/// <c>shop.Orders.Query(sql$"select * from Orders where total > {limit}")</c>. EF Core's own <c>FromSql($"…")</c>
/// does the same for a plain <c>$"…"</c>; its <c>…Raw</c> methods, given an interpolated string, don't.
/// </summary>
public static class PiperSqlExtensions
{
    /// <summary>The rows <paramref name="sql"/> selects from this table, as a query LINQ can go on with.</summary>
    public static IQueryable<T> Query<T>(this DbSet<T> set, Sql sql) where T : class =>
        set.FromSql(sql.ToFormattableString());

    /// <summary>Values of <typeparamref name="T"/> (a number, string, or a type EF Core maps) that <paramref name="sql"/> selects.</summary>
    public static IQueryable<T> Query<T>(this DbContext context, Sql sql) =>
        context.Database.SqlQuery<T>(sql.ToFormattableString());

    /// <summary>Runs <paramref name="sql"/> (an insert, update or delete), giving the number of rows it changed.</summary>
    public static int Execute(this DbContext context, Sql sql) =>
        context.Database.ExecuteSql(sql.ToFormattableString());

    /// <summary>Runs <paramref name="sql"/> (an insert, update or delete), giving the number of rows it changed.</summary>
    public static Task<int> ExecuteAsync(this DbContext context, Sql sql, CancellationToken cancellation = default) =>
        context.Database.ExecuteSqlAsync(sql.ToFormattableString(), cancellation);
}
