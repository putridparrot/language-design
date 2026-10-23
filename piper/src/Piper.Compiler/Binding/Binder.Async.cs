using System.Reflection;
using Piper.Syntax;

namespace Piper.Binding;

/// <summary>
/// async and await, as in C#: an <c>async fn</c> (or async lambda) returns a Task, Task&lt;T&gt;, ValueTask or
/// ValueTask&lt;T&gt; and its body produces the result (T, or unit); <c>await x</c> works on anything with C#'s
/// awaitable pattern, a <c>GetAwaiter()</c> whose awaiter's <c>GetResult()</c> gives the result.
/// </summary>
public sealed partial class Binder
{
    private static readonly ClrTypeSymbol TaskType = ClrTypeSymbol.For(typeof(Task));
    private static readonly ClrTypeSymbol GenericTaskType = ClrTypeSymbol.For(typeof(Task<>));

    /// <summary>Whether an <c>await</c> has been bound in the function or lambda being bound (for the no-await warning).</summary>
    private bool _sawAwait;

    /// <summary>What an async function returning <paramref name="returnType"/> produces in its body, or null if it can't be async.</summary>
    private static TypeSymbol? AsyncBodyType(TypeSymbol returnType)
    {
        if (returnType is not ClrTypeSymbol clr)
            return null;
        var definition = clr.Definition.Type;
        if (definition == typeof(Task) || definition == typeof(ValueTask))
            return TypeSymbol.Unit;
        if ((definition == typeof(Task<>) || definition == typeof(ValueTask<>)) && clr.TypeArguments.Length == 1)
            return clr.TypeArguments[0];
        return null;
    }

    /// <summary>The Task an async lambda whose body produces <paramref name="body"/> returns: Task&lt;T&gt;, or Task for unit.</summary>
    private static TypeSymbol AsyncReturnType(TypeSymbol body) =>
        body == TypeSymbol.Unit || body == TypeSymbol.Never ? TaskType
        : body == TypeSymbol.Error ? TypeSymbol.Error
        : GenericTaskType.Construct([body]);

    /// <summary>The result of awaiting a <paramref name="type"/>, by C#'s awaitable pattern; null if it can't be awaited.</summary>
    private TypeSymbol? AwaitResultType(TypeSymbol type)
    {
        if (!IsClrBacked(type))
            return null;
        var (clrType, map) = ClrContext(type);
        if (clrType.GetMethod("GetAwaiter", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes) is not { } getAwaiter ||
            ClrTypes.FromClr(getAwaiter.ReturnType, map, _option) is not ClrTypeSymbol awaiter)
        {
            return null;
        }

        var (awaiterType, awaiterMap) = ClrContext(awaiter);
        return awaiterType.GetMethod("GetResult", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes) is { } getResult
            ? ClrTypes.FromClr(getResult.ReturnType, awaiterMap, _option)
            : null;
    }

    /// <summary>`await`, `await for` and `await using` need an async function or lambda; reports if not.</summary>
    private bool CheckAwaitAllowed(Token keyword)
    {
        if (_return is not { IsAsync: true })
        {
            _diagnostics.ReportAwaitOutsideAsync(keyword.Span);
            return false;
        }
        _sawAwait = true;
        return true;
    }

    /// <summary>T for an IAsyncEnumerable&lt;T&gt; (what `await for` iterates), or null.</summary>
    private TypeSymbol? AsyncElementTypeOf(TypeSymbol type) =>
        SelfAndBaseTypes(type)
            .OfType<ClrTypeSymbol>()
            .FirstOrDefault(t => t.Definition.Type == typeof(IAsyncEnumerable<>) && t.TypeArguments.Length == 1)
            ?.TypeArguments[0];

    private BoundExpression BindAwait(AwaitExpressionSyntax syntax)
    {
        var operand = RequireValue(BindExpression(syntax.Operand), syntax.Operand.Span);
        if (!CheckAwaitAllowed(syntax.Keyword))
            return new BoundErrorExpression();
        if (operand.Type == TypeSymbol.Error)
            return new BoundErrorExpression();

        if (AwaitResultType(operand.Type) is not { } result)
        {
            _diagnostics.ReportNotAwaitable(syntax.Operand.Span, operand.Type);
            return new BoundErrorExpression();
        }
        return new BoundAwaitExpression(operand, result);
    }
}
