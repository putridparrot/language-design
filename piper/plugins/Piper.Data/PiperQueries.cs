using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Piper.Runtime;

namespace Piper.Data;

/// <summary>
/// Lets EF Core translate comparisons with an Option column: <c>c.email == None</c> to <c>"email" IS NULL</c>, and
/// <c>c.email == Some("a@b")</c> or a comparison with an Option variable to the value or NULL. EF Core sees Option as an
/// ordinary struct, so on its own it would compare the column with a NULL parameter (<c>"email" = @p</c>), which SQL
/// never finds true. Before a query is compiled, this rewrites each such comparison in terms of the column's stored
/// value (<c>EF.Property&lt;string&gt;(c, "email") == null</c>), which EF Core understands.
/// </summary>
public sealed class PiperQueries : IQueryExpressionInterceptor
{
    public static PiperQueries Instance { get; } = new();

    public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData) =>
        new OptionComparisons().Visit(queryExpression);
}

/// <summary>
/// Keeps EF Core from making an Option in a query a parameter (<c>None</c>, or a captured Option variable), so
/// <see cref="PiperQueries"/> sees it: an Option parameter of None would compare as <c>= NULL</c>, which SQL never finds
/// true. <c>Some(x)</c> is still a parameter: the column's converter makes it x, and <c>= @p</c> compares correctly.
/// </summary>
public sealed class PiperEvaluatableFilter(
    Microsoft.EntityFrameworkCore.Query.EvaluatableExpressionFilterDependencies dependencies,
    Microsoft.EntityFrameworkCore.Query.RelationalEvaluatableExpressionFilterDependencies relationalDependencies)
    : Microsoft.EntityFrameworkCore.Query.RelationalEvaluatableExpressionFilter(dependencies, relationalDependencies)
{
    public override bool IsEvaluatableExpression(Expression expression, Microsoft.EntityFrameworkCore.Metadata.IModel model) =>
        (OptionComparisons.OptionValueType(expression.Type) is null || OptionComparisons.IsSome(expression)) &&
        base.IsEvaluatableExpression(expression, model);
}

/// <summary>
/// Rewrites comparisons of Option columns with None (see <see cref="PiperQueries"/>): <c>c.email == None</c> becomes
/// <c>EF.Property&lt;string&gt;(c, "email") == null</c>, which EF Core translates to <c>IS NULL</c>.
/// </summary>
internal sealed class OptionComparisons : ExpressionVisitor
{
    private static readonly MethodInfo Property = typeof(EF).GetMethod(nameof(EF.Property))!;

    /// <summary><c>Option&lt;T&gt;.Some(x)</c>, which EF Core can make a parameter, converted by the column's converter.</summary>
    internal static bool IsSome(Expression expression) =>
        expression is MethodCallExpression { Object: null, Method.Name: "Some" } call && OptionValueType(call.Type) is not null;

    protected override Expression VisitBinary(BinaryExpression node)
    {
        if (node.NodeType is ExpressionType.Equal or ExpressionType.NotEqual && OptionValueType(node.Left.Type) is { } stored &&
            node.Left.Type == node.Right.Type)
        {
            var (left, right) = (Column(node.Left, stored), Column(node.Right, stored));
            if (left is not null && Value(node.Right, stored) is { } rightNull)
                return Expression.MakeBinary(node.NodeType, left, rightNull);
            if (right is not null && Value(node.Left, stored) is { } leftNull)
                return Expression.MakeBinary(node.NodeType, leftNull, right);
        }
        return base.VisitBinary(node);
    }

    /// <summary>
    /// An entity's Option property, as the value its column stores: <c>EF.Property&lt;string&gt;(c, "email")</c>. Null if
    /// <paramref name="expression"/> isn't one.
    /// </summary>
    private Expression? Column(Expression expression, Type stored) =>
        expression is MemberExpression { Expression: { } target, Member: PropertyInfo or FieldInfo } member && UsesRows(target)
            ? Expression.Call(Property.MakeGenericMethod(stored), Visit(target), Expression.Constant(member.Member.Name))
            : null;

    /// <summary>
    /// What a column is compared with, if the comparison needs rewriting: <c>None</c>, as null. Null for anything EF Core
    /// handles itself: another column, or <c>Some(v)</c>, which became a parameter that the column's converter makes v.
    /// </summary>
    private static Expression? Value(Expression option, Type stored)
    {
        if (option is MemberExpression { Expression: null, Member.Name: "None" })
            return Expression.Constant(null, stored);
        // `Some("a@b")` that EF Core folded into a constant: None is NULL, and a value its converter writes.
        if (option is ConstantExpression { Value: var constant })
            return constant is null || !(bool)option.Type.GetProperty("IsSome")!.GetValue(constant)! ? Expression.Constant(null, stored) : null;
        if (option.NodeType == ExpressionType.Extension || IsSome(option) || UsesRows(option))
            return null;
        // An optional variable: EF Core made what holds it a parameter, so its value can't be had here.
        throw new InvalidOperationException(
            "A query can compare an optional column with None, or with Some(value), but not with an optional variable. Choose the query by the variable: " +
            "`if wanted is Some(w) { q.Where(c => c.email == Some(w)) } else { q.Where(c => c.email == None) }`.");
    }

    /// <summary>Whether <paramref name="expression"/> refers to a lambda's parameter (a row), so can't be read before the query runs.</summary>
    private static bool UsesRows(Expression expression)
    {
        var finder = new ParameterFinder();
        finder.Visit(expression);
        return finder.Found;
    }

    private sealed class ParameterFinder : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            Found = true;
            return node;
        }
    }

    /// <summary>For <c>Option&lt;T&gt;</c>, the type its column holds: T for a reference type, <c>T?</c> for a value type.</summary>
    internal static Type? OptionValueType(Type type)
    {
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(Option<>))
            return null;
        var inner = type.GetGenericArguments()[0];
        return inner.IsValueType ? typeof(Nullable<>).MakeGenericType(inner) : inner;
    }
}
