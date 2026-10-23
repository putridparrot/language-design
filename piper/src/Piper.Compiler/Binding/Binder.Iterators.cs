using System.Collections;
using Piper.Syntax;

namespace Piper.Binding;

/// <summary>
/// Iterators, as in C#: a function whose body contains <c>yield</c> returns IEnumerable&lt;T&gt; or IEnumerator&lt;T&gt;
/// (IAsyncEnumerable&lt;T&gt; or IAsyncEnumerator&lt;T&gt; when async), produces values with <c>yield return</c> and stops
/// with <c>yield break</c> or by reaching its end. It runs lazily, as the caller iterates.
/// </summary>
public sealed partial class Binder
{
    /// <summary>
    /// Why <c>yield</c> isn't allowed where binding is (C# forbids it inside a try with a catch, and in catch and finally
    /// blocks), or null if it is. <see cref="_yieldBreakForbidden"/> is the same for <c>yield break</c> (only finally).
    /// </summary>
    private string? _yieldReturnForbidden;
    private string? _yieldBreakForbidden;

    /// <summary>What an iterator returning <paramref name="returnType"/> yields, or null if it can't be an iterator.</summary>
    private static TypeSymbol? IteratorElementType(TypeSymbol returnType, bool isAsync)
    {
        if (returnType is not ClrTypeSymbol clr)
            return null;
        var definition = clr.Definition.Type;
        if (clr.TypeArguments.Length == 1)
        {
            bool fits = isAsync
                ? definition == typeof(IAsyncEnumerable<>) || definition == typeof(IAsyncEnumerator<>)
                : definition == typeof(IEnumerable<>) || definition == typeof(IEnumerator<>);
            return fits ? clr.TypeArguments[0] : null;
        }
        // The non-generic IEnumerable yields objects.
        return !isAsync && (definition == typeof(IEnumerable) || definition == typeof(IEnumerator))
            ? ClrTypeSymbol.For(typeof(object))
            : null;
    }

    private BoundStatement BindYield(YieldStatementSyntax syntax)
    {
        if (_return?.IteratorElement is not { } element)
        {
            if (syntax.Value is not null)
                BindExpression(syntax.Value);
            _diagnostics.ReportYieldOutsideIterator(syntax.Keyword.Span);
            return new BoundYieldStatement(null);
        }

        if (syntax.Value is null)
        {
            if (_yieldBreakForbidden is { } where)
                _diagnostics.ReportYieldInTry(syntax.Keyword.Span, where);
            return new BoundYieldStatement(null);
        }

        if (_yieldReturnForbidden is { } forbidden)
            _diagnostics.ReportYieldInTry(syntax.Keyword.Span, forbidden);
        var value = RequireValue(BindExpression(syntax.Value, element), syntax.Value.Span);
        return new BoundYieldStatement(Convert(value, element, syntax.Value.Span));
    }
}
