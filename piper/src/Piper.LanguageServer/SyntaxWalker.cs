using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using Piper.Syntax;

namespace Piper.LanguageServer;

/// <summary>Enumerates a syntax tree's nodes, finding each node type's children (its node and node-array properties) by reflection.</summary>
public static class SyntaxWalker
{
    private static readonly ConcurrentDictionary<Type, Func<SyntaxNode, IEnumerable<SyntaxNode>>> ChildAccessors = new();

    private static readonly MethodInfo ItemsMethod = typeof(SyntaxWalker).GetMethod(nameof(Items), BindingFlags.NonPublic | BindingFlags.Static)!;

    public static IEnumerable<SyntaxNode> DescendantsAndSelf(SyntaxNode node)
    {
        var pending = new Stack<SyntaxNode>();
        pending.Push(node);
        while (pending.TryPop(out var current))
        {
            yield return current;
            foreach (var child in ChildrenOf(current))
                pending.Push(child);
        }
    }

    public static IEnumerable<SyntaxNode> ChildrenOf(SyntaxNode node) => ChildAccessors.GetOrAdd(node.GetType(), ChildAccessor)(node);

    private static Func<SyntaxNode, IEnumerable<SyntaxNode>> ChildAccessor(Type type)
    {
        var getters = new List<Func<SyntaxNode, IEnumerable<SyntaxNode>>>();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0))
        {
            var propertyType = property.PropertyType;
            if (typeof(SyntaxNode).IsAssignableFrom(propertyType))
            {
                getters.Add(node => property.GetValue(node) is SyntaxNode child ? [child] : []);
            }
            else if (propertyType.IsGenericType && propertyType.GetGenericTypeDefinition() == typeof(ImmutableArray<>) &&
                     typeof(SyntaxNode).IsAssignableFrom(propertyType.GetGenericArguments()[0]))
            {
                var items = ItemsMethod.MakeGenericMethod(propertyType.GetGenericArguments()[0])
                    .CreateDelegate<Func<object?, IEnumerable<SyntaxNode>>>();
                getters.Add(node => items(property.GetValue(node)));
            }
        }
        return node => getters.SelectMany(getter => getter(node));
    }

    private static IEnumerable<SyntaxNode> Items<T>(object? value) where T : SyntaxNode =>
        value is ImmutableArray<T> { IsDefault: false } array ? array : [];
}
