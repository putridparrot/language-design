using System.Reflection;

namespace Piper.Binding;

/// <summary>
/// Warns about a task nothing observes (PIP0409): a call giving a <c>Task</c>, <c>Task&lt;T&gt;</c>, <c>ValueTask</c> or
/// <c>ValueTask&lt;T&gt;</c> whose value is thrown away (<c>Save(order);</c>), so nothing waits for it and its exception
/// is lost. That's a statement, the last expression of a block, <c>if</c> or <c>match</c> whose value isn't used, or the
/// body of a lambda that returns nothing (an event handler, an <c>Action</c>): <c>button.Click += (_, _) => Save();</c>.
/// It's reported anywhere, not only in async functions as C#'s CS4014 is. <c>let _ = Save(order);</c> drops a task on
/// purpose. A method marked <c>[ObservedTask]</c> (<c>TaskScope.Start</c>) gives a task something else observes.
/// </summary>
public sealed partial class Binder
{
    private void CheckUnobservedTasks(IEnumerable<BoundFunction> functions)
    {
        foreach (var function in functions)
        {
            if (function.Symbol.DeclaringFile is not { } file)
                continue;
            _diagnostics.CurrentSource = file;
            var reported = new HashSet<BoundExpression>(ReferenceEqualityComparer.Instance);
            // `fn Last() { Plain() }`: a body producing nothing throws its last value away.
            if (function.Symbol.BodyReturnType == TypeSymbol.Unit && function.Body is BoundExpression whole)
                Discarded(whole, reported);
            foreach (var node in BoundTreeWalker.DescendantsAndSelf(function.Body))
            {
                switch (node)
                {
                    case BoundExpressionStatement statement:
                        Discarded(statement.Expression, reported);
                        break;
                    case BoundBlockExpression { Trailing: { } trailing } block when block.Type == TypeSymbol.Unit:
                        Discarded(trailing, reported);
                        break;
                    case BoundIfExpression branch when branch.Type == TypeSymbol.Unit:
                        Discarded(branch.Then, reported);
                        if (branch.Else is { } otherwise)
                            Discarded(otherwise, reported);
                        break;
                    case BoundMatchExpression match when match.Type == TypeSymbol.Unit:
                        foreach (var arm in match.Arms)
                            Discarded(arm.Body, reported);
                        break;
                    // An Action's body, or an async lambda's that produces nothing (`async () => { Ping() }`): for an async
                    // lambda, ReturnType is already the Task's result.
                    case BoundLambdaExpression { Body: BoundExpression body } lambda when lambda.ReturnType == TypeSymbol.Unit:
                        Discarded(body, reported);
                        break;
                }
            }
        }
    }

    /// <summary>Reports <paramref name="expression"/>, whose value is thrown away, if it's a call giving a task.</summary>
    private void Discarded(BoundExpression expression, HashSet<BoundExpression> reported)
    {
        switch (expression)
        {
            case BoundConversionExpression conversion:
                Discarded(conversion.Expression, reported);
                break;
            case BoundBlockExpression { Trailing: { } trailing }:
                Discarded(trailing, reported);
                break;
            case BoundIfExpression branch:
                Discarded(branch.Then, reported);
                if (branch.Else is { } otherwise)
                    Discarded(otherwise, reported);
                break;
            case BoundMatchExpression match:
                foreach (var arm in match.Arms)
                    Discarded(arm.Body, reported);
                break;
            case BoundCallExpression or BoundMethodCallExpression or BoundClrInvocationExpression or BoundDelegateInvocationExpression
                when IsTask(expression.Type) && !GivesObservedTask(expression) && expression.SourceSpan is { } span && reported.Add(expression):
                _diagnostics.ReportUnobservedTask(span, expression.Type);
                break;
        }
    }

    private static bool IsTask(TypeSymbol type) =>
        type is ClrTypeSymbol clr && clr.Definition.Type is var definition &&
        (definition == typeof(Task) || definition == typeof(Task<>) || definition == typeof(ValueTask) || definition == typeof(ValueTask<>));

    /// <summary>A .NET method marked <c>[ObservedTask]</c>: its task is observed by something else (a TaskScope).</summary>
    private static bool GivesObservedTask(BoundExpression call) =>
        call is BoundClrInvocationExpression { Member: MethodInfo method } &&
        method.CustomAttributes.Any(a => a.AttributeType.FullName == "Piper.Runtime.ObservedTaskAttribute");
}
