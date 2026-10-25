using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Piper.Binding;

/// <summary>Walks a bound tree by reflection, finding every node under a node, including those inside tuples and lists.</summary>
public static class BoundTreeWalker
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Properties = new();

    public static IEnumerable<BoundNode> DescendantsAndSelf(BoundNode node)
    {
        var pending = new Stack<object>();
        pending.Push(node);
        while (pending.TryPop(out var current))
        {
            switch (current)
            {
                case BoundNode bound:
                    yield return bound;
                    foreach (var property in Properties.GetOrAdd(bound.GetType(), ChildProperties))
                    {
                        if (property.GetValue(bound) is { } child)
                            pending.Push(child);
                    }
                    break;
                case ITuple tuple:
                    for (int i = 0; i < tuple.Length; i++)
                    {
                        if (tuple[i] is { } element)
                            pending.Push(element);
                    }
                    break;
                case IEnumerable when current.GetType().GetProperty("IsDefault")?.GetValue(current) is true:
                    break;
                case IEnumerable items and not string:
                    foreach (var item in items)
                    {
                        if (item is not null)
                            pending.Push(item);
                    }
                    break;
            }
        }
    }

    /// <summary>Whether a lambda under <paramref name="node"/> uses <paramref name="variable"/>, so captures it.</summary>
    public static bool IsCapturedIn(VariableSymbol variable, BoundNode node) =>
        DescendantsAndSelf(node).OfType<BoundLambdaExpression>().Any(lambda =>
            DescendantsAndSelf(lambda.Body).Any(n => n is BoundVariableExpression { Variable: var used } && used == variable));

    /// <summary>The properties that can hold nodes: of a node type, or a list or tuple (which may hold them).</summary>
    private static PropertyInfo[] ChildProperties(Type type) =>
        [.. type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.Name != nameof(BoundNode.SourceSpan) &&
                        (typeof(BoundNode).IsAssignableFrom(p.PropertyType) ||
                         (typeof(IEnumerable).IsAssignableFrom(p.PropertyType) && p.PropertyType != typeof(string)) ||
                         typeof(ITuple).IsAssignableFrom(p.PropertyType)))];
}
