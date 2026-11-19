using System.Reflection;

namespace Piper.Binding;

/// <summary>
/// Warns when a <c>TaskScope</c> escapes its body (PIP0410): the <c>scope</c> a <c>TaskScope.Run { scope => … }</c>
/// gives is stored (in a variable, a field, an Option, a tuple, a collection or an object), returned, or held by a
/// lambda that is (an event handler, a callback kept for later). Once <c>Run</c> has finished, the scope can't start
/// tasks, and cancelling it does nothing, so code using it later fails or is ignored. Using it in the body, in the
/// tasks it starts, and passing it to a function (<c>StartWorkers(scope)</c>) are fine.
/// </summary>
public sealed partial class Binder
{
    private void CheckScopeEscapes(IEnumerable<BoundFunction> functions)
    {
        foreach (var function in functions)
        {
            if (function.Symbol.DeclaringFile is not { } file)
                continue;
            _diagnostics.CurrentSource = file;
            foreach (var node in BoundTreeWalker.DescendantsAndSelf(function.Body))
            {
                if (node is BoundClrInvocationExpression { Member: MethodInfo { Name: "Run", DeclaringType.FullName: "Piper.Runtime.TaskScope" } } run &&
                    run.Arguments.Select(Lambda).OfType<BoundLambdaExpression>().FirstOrDefault() is { Parameters: [var scope] } body)
                    CheckScopeEscapes(scope, body);
            }
        }
    }

    private void CheckScopeEscapes(VariableSymbol scope, BoundLambdaExpression body)
    {
        var reported = new HashSet<BoundExpression>(ReferenceEqualityComparer.Instance);
        void Check(BoundExpression? value)
        {
            if (value is not null && Holds(value, scope) && value.SourceSpan is { } span && reported.Add(value))
                _diagnostics.ReportScopeEscapes(span, scope.Name);
        }

        // The body's own value: `TaskScope.Run { scope => scope }`.
        if (body.Body is BoundExpression whole)
            Check(whole is BoundBlockExpression { Trailing: { } trailing } ? trailing : whole);
        var nodes = BoundTreeWalker.DescendantsAndSelf(body.Body).ToList();
        // Variables declared in the body end with it, so keeping the scope in one isn't an escape.
        var local = nodes.OfType<BoundVariableDeclaration>().Select(d => d.Variable)
            .Concat(nodes.OfType<BoundLambdaExpression>().SelectMany(l => l.Parameters)).ToHashSet();
        foreach (var node in nodes)
        {
            switch (node)
            {
                case BoundAssignmentExpression assignment when !local.Contains(assignment.Variable):
                    Check(assignment.Value);
                    break;
                case BoundFieldAssignmentExpression assignment:
                    Check(assignment.Value);
                    break;
                case BoundClrAssignmentExpression assignment:
                    Check(assignment.Value);
                    break;
                case BoundReturnStatement { Value: var value }:
                    Check(value);
                    break;
                case BoundEventSubscription { Add: true, Handler: var handler }:
                    Check(handler);
                    break;
                // A .NET event's `+=`: its add accessor.
                case BoundClrInvocationExpression { Member: MethodInfo { IsSpecialName: true, Name: var name } } call when name.StartsWith("add_", StringComparison.Ordinal):
                    foreach (var argument in call.Arguments)
                        Check(argument);
                    break;
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="value"/> keeps <paramref name="scope"/>: is it, wraps it (an Option, a tuple, a collection,
    /// a new object), or is a lambda using it. A call given it (<c>StartWorkers(scope)</c>) and a member of it
    /// (<c>scope.Token</c>) don't.
    /// </summary>
    private static bool Holds(BoundExpression value, VariableSymbol scope) => Unwrap(value) switch
    {
        BoundVariableExpression { Variable: var variable } => variable == scope,
        BoundBlockExpression { Trailing: { } trailing } => Holds(trailing, scope),
        BoundLambdaExpression lambda => BoundTreeWalker.DescendantsAndSelf(lambda.Body).Any(n => n is BoundVariableExpression { Variable: var v } && v == scope),
        BoundVariantCreationExpression creation => creation.Arguments.Any(a => Holds(a, scope)),
        BoundTupleExpression tuple => tuple.Elements.Any(e => Holds(e, scope)),
        BoundCollectionExpression collection => collection.Elements.Any(e => Holds(e, scope)),
        BoundRecordCreationExpression record => record.Arguments.Any(a => Holds(a, scope)),
        BoundObjectCreationExpression created => created.Arguments.Any(a => Holds(a, scope)),
        _ => false,
    };
}
