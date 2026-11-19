using System.Collections.Immutable;
using System.Reflection;

namespace Piper.Binding;

/// <summary>
/// Warns about data races: code that runs on several threads at once changing what they share without a lock. That's
/// the body of a <c>Parallel.For</c>, <c>Parallel.ForEach</c> or <c>Parallel.Invoke</c> lambda (what <c>parallel for</c>
/// becomes), and each <c>Task.Run</c> lambda passed to <c>Task.WhenAll</c> or <c>Task.WaitAll</c> together (what
/// <c>concurrency { }</c> becomes), and the tasks a <c>TaskScope</c> starts (<c>scope.Start { … }</c>). In those, it reports:
/// <list type="bullet">
/// <item>assigning a variable from outside the lambda (<c>total += n</c>), which loses updates;</item>
/// <item>assigning a field of an object from outside it (<c>this.count += 1</c>);</item>
/// <item>changing a List, Dictionary, HashSet or other collection from outside it (<c>names.Add(n)</c>), which
/// isn't safe on several threads and can corrupt it.</item>
/// </list>
/// What's inside a <c>lock</c> is fine, as is <c>Interlocked</c> (which takes the variable by <c>ref</c>). It's a
/// warning: the threads may write different parts, or the program may tolerate it, which the compiler can't tell.
/// </summary>
public sealed partial class Binder
{
    private static readonly HashSet<string> ParallelMethods = ["For", "ForEach", "ForEachAsync", "Invoke"];

    private static readonly HashSet<string> TaskGroupMethods = ["WhenAll", "WaitAll", "WhenAny", "WaitAny"];

    private static readonly HashSet<Type> UnsafeCollections =
    [
        typeof(List<>), typeof(Dictionary<,>), typeof(HashSet<>), typeof(Queue<>), typeof(Stack<>), typeof(LinkedList<>),
        typeof(SortedDictionary<,>), typeof(SortedList<,>), typeof(SortedSet<>),
    ];

    private static readonly HashSet<string> MutatingMethods =
    [
        "Add", "AddRange", "Insert", "InsertRange", "Remove", "RemoveAt", "RemoveAll", "RemoveRange", "Clear", "Sort", "Reverse",
        "Enqueue", "Dequeue", "TryDequeue", "Push", "Pop", "TryPop", "TryAdd", "AddFirst", "AddLast", "RemoveFirst", "RemoveLast",
        "UnionWith", "ExceptWith", "IntersectWith", "SymmetricExceptWith", "EnsureCapacity", "TrimExcess",
    ];

    private void CheckDataRaces(IEnumerable<BoundFunction> functions)
    {
        foreach (var function in functions)
        {
            if (function.Symbol.DeclaringFile is not { } file)
                continue;
            _diagnostics.CurrentSource = file;
            foreach (var (lambda, others) in BoundTreeWalker.DescendantsAndSelf(function.Body).SelectMany(ConcurrentLambdas))
                CheckConcurrentLambda(lambda, others);
        }
    }

    /// <summary>
    /// The lambdas <paramref name="node"/> runs on several threads at once, if it's a parallel loop or a group of tasks,
    /// each with those it runs alongside: for a loop, none (default), as each runs alongside itself; for a group of
    /// tasks, the others in the group.
    /// </summary>
    private static IEnumerable<(BoundLambdaExpression Lambda, ImmutableArray<BoundLambdaExpression> Others)> ConcurrentLambdas(BoundNode node)
    {
        if (node is not BoundClrInvocationExpression { Member: MethodInfo { DeclaringType: { } declaring } method } call)
            return [];
        if (declaring == typeof(System.Threading.Tasks.Parallel) && ParallelMethods.Contains(method.Name))
            return Flatten(call.Arguments).Select(Lambda).OfType<BoundLambdaExpression>().Select(l => (l, default(ImmutableArray<BoundLambdaExpression>)));
        if (declaring == typeof(System.Threading.Tasks.Task) && TaskGroupMethods.Contains(method.Name))
        {
            var runs = Flatten(call.Arguments).Select(Unwrap).OfType<BoundClrInvocationExpression>()
                .Where(run => run.Member is MethodInfo { Name: "Run", DeclaringType: var type } && type == typeof(System.Threading.Tasks.Task))
                .ToList();
            // One task runs alongside nothing else in the group.
            var group = runs.SelectMany(run => run.Arguments).Select(Lambda).OfType<BoundLambdaExpression>().ToImmutableArray();
            return runs.Count < 2 ? [] : group.Select(l => (l, group.Remove(l)));
        }
        if (declaring.FullName == "Piper.Runtime.TaskScope" && method.Name == "Run")
            return ScopeTasks(call);
        return [];

        static IEnumerable<BoundExpression> Flatten(ImmutableArray<BoundExpression> arguments) =>
            arguments.SelectMany(a => Unwrap(a) is BoundCollectionExpression collection ? collection.Elements : [a]);
    }

    /// <summary>
    /// The tasks a <c>TaskScope.Run</c> body starts (<c>scope.Start { … }</c>), which run alongside each other: each
    /// with the others, or, started in a loop, alongside itself as well, as a parallel loop's body is.
    /// </summary>
    private static IEnumerable<(BoundLambdaExpression Lambda, ImmutableArray<BoundLambdaExpression> Others)> ScopeTasks(BoundClrInvocationExpression run)
    {
        if (run.Arguments.Select(Lambda).OfType<BoundLambdaExpression>().FirstOrDefault() is not { Parameters: [var scope] } body)
            return [];
        // `scope.Start { … }` on this scope: its own, not a scope nested in it, whose tasks are checked as its own group.
        bool IsStart(BoundNode node) =>
            node is BoundClrInvocationExpression { Member.Name: "Start", Receiver: { } receiver } &&
            Unwrap(receiver) is BoundVariableExpression { Variable: var variable } && variable == scope;
        var starts = BoundTreeWalker.DescendantsAndSelf(body.Body).Where(IsStart).Cast<BoundClrInvocationExpression>().ToList();
        var inLoops = BoundTreeWalker.DescendantsAndSelf(body.Body)
            .Where(n => n is BoundWhileStatement or BoundForStatement or BoundForEachStatement)
            .SelectMany(BoundTreeWalker.DescendantsAndSelf).Where(IsStart).ToHashSet(ReferenceEqualityComparer.Instance);
        var tasks = starts.Select(start => (Start: start, Lambda: start.Arguments.Select(Lambda).OfType<BoundLambdaExpression>().FirstOrDefault()))
            .Where(t => t.Lambda is not null).ToList();
        var lambdas = tasks.Select(t => t.Lambda!).ToImmutableArray();
        return tasks
            .Where(t => inLoops.Contains(t.Start) || tasks.Count > 1)
            .Select(t => (t.Lambda!, inLoops.Contains(t.Start) ? default : lambdas.Remove(t.Lambda!)));
    }

    private static BoundLambdaExpression? Lambda(BoundExpression expression) => Unwrap(expression) as BoundLambdaExpression;

    private static BoundExpression Unwrap(BoundExpression expression) =>
        expression is BoundConversionExpression conversion ? Unwrap(conversion.Expression) : expression;

    /// <param name="others">The lambdas running alongside it; default if it runs alongside itself (a loop's body).</param>
    private void CheckConcurrentLambda(BoundLambdaExpression lambda, ImmutableArray<BoundLambdaExpression> others)
    {
        // A task in a group races only on what another task in it uses too; a loop's body, on all it changes.
        var elsewhere = others.IsDefault ? null : others.SelectMany(o => BoundTreeWalker.DescendantsAndSelf(o.Body)).ToList();
        bool UsedElsewhere(Symbol symbol) => elsewhere is null || elsewhere.Any(n => n switch
        {
            BoundVariableExpression { Variable: var v } => v == symbol,
            BoundAssignmentExpression { Variable: var v } => v == symbol,
            BoundFieldAccessExpression { Field: var f } => f == symbol,
            BoundFieldAssignmentExpression { Field: var f } => f == symbol,
            _ => false,
        });

        var nodes = BoundTreeWalker.DescendantsAndSelf(lambda.Body).ToList();
        // What the lambda itself declares belongs to one thread.
        var own = nodes.OfType<BoundVariableDeclaration>().Select(d => d.Variable)
            .Concat(lambda.Parameters)
            .Concat(nodes.OfType<BoundLambdaExpression>().SelectMany(l => l.Parameters))
            .ToHashSet();
        var locked = nodes.OfType<BoundLockStatement>().SelectMany(l => BoundTreeWalker.DescendantsAndSelf(l.Body)).ToHashSet(ReferenceEqualityComparer.Instance);
        var reported = new HashSet<string>();

        foreach (var node in nodes.Where(n => !locked.Contains(n)))
        {
            switch (node)
            {
                case BoundAssignmentExpression { Variable: var variable } when !own.Contains(variable) && UsedElsewhere(variable):
                    Report(node, variable.Name,
                        $"'{variable.Name}' is changed on several threads at once here, so updates can be lost. Use Interlocked " +
                        $"('Interlocked.Add(ref {variable.Name}, …)'), a 'lock', or compute the result with LINQ (Sum, Aggregate).");
                    break;
                case BoundFieldAssignmentExpression { Target: var target, Field: var field } when !OwnedBy(target, own) && UsedElsewhere(field):
                    Report(node, field.Name,
                        $"'{field.Name}' is changed on several threads at once here, so updates can be lost. Use a 'lock', or Interlocked.");
                    break;
                case BoundClrInvocationExpression { Receiver: { } receiver, Member.Name: var name } when MutatingMethods.Contains(name) && Shared(receiver, own) is { } collection && UsedElsewhere(collection.Variable):
                    ReportCollection(node, collection);
                    break;
                case BoundClrAssignmentExpression { Target: BoundIndexExpression { Target: var indexed } } when Shared(indexed, own) is { } collection && UsedElsewhere(collection.Variable):
                    ReportCollection(node, collection);
                    break;
            }
        }

        void Report(BoundNode node, string name, string message)
        {
            if (node.SourceSpan is { } span && reported.Add(name))
                _diagnostics.ReportDataRace(span, message);
        }

        void ReportCollection(BoundNode node, (VariableSymbol Variable, string Name, TypeSymbol Type) collection) =>
            Report(node, collection.Name,
                $"'{collection.Name}' is a {collection.Type}, which isn't safe to change on several threads at once. Use a 'lock', " +
                "or a concurrent collection such as ConcurrentBag<T>, ConcurrentQueue<T> or ConcurrentDictionary<K, V>.");
    }

    /// <summary>Whether <paramref name="target"/> is an object the lambda made itself: a variable it declares.</summary>
    private static bool OwnedBy(BoundExpression target, HashSet<VariableSymbol> own) =>
        Unwrap(target) is BoundVariableExpression { Variable: var variable } && own.Contains(variable);

    /// <summary>A collection from outside the lambda that isn't safe to change on several threads, with its name; null otherwise.</summary>
    private static (VariableSymbol Variable, string Name, TypeSymbol Type)? Shared(BoundExpression receiver, HashSet<VariableSymbol> own) =>
        Unwrap(receiver) is BoundVariableExpression { Variable: var variable } && !own.Contains(variable) &&
        variable.Type is ClrTypeSymbol clr && UnsafeCollections.Contains(clr.Definition.Type)
            ? (variable, variable.Name, variable.Type)
            : null;
}
