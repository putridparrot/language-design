using System.Collections.Immutable;
using System.Reflection;
using Piper.Syntax;

namespace Piper.Binding;

/// <summary>
/// Counting from the end, <c>xs[^1]</c>, and slicing, <c>xs[1..3]</c>, on arrays, strings and .NET collections. They're
/// written as C#'s <c>^</c> and <c>..</c>, which C# turns into <c>Length</c>/<c>Count</c> arithmetic, <c>Substring</c>,
/// <c>Slice</c> or an array copy. A Piper range includes its end unless it's <c>..&lt;</c>, as in a <c>for</c> loop,
/// so <c>xs[1..3]</c> is three elements; C#'s excludes it, so the end is moved one on.
/// </summary>
public sealed partial class Binder
{
    private static bool IsFromEnd(ExpressionSyntax syntax) => syntax is UnaryExpressionSyntax { Operator.Kind: TokenKind.Caret };

    /// <summary>Whether <paramref name="type"/> can be indexed from the end: it has an int indexer and a Length or Count, as C# requires.</summary>
    private static bool CountsFromEnd(TypeSymbol type)
    {
        if (type is ClrTypeSymbol { IsArray: true } array)
            return array.Rank == 1;
        if (!IsClrBacked(type))
            return false;
        var (clr, _) = ClrContext(type);
        return HasCount(clr) && clr.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Any(p => p.GetIndexParameters() is [{ ParameterType: var parameter }] && parameter == typeof(int) && p.GetMethod is { IsPublic: true });
    }

    private static bool HasCount(Type type) =>
        type.GetProperty("Length", BindingFlags.Public | BindingFlags.Instance) is { PropertyType: var length } && length == typeof(int) ||
        type.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance) is { PropertyType: var count } && count == typeof(int);

    /// <summary>
    /// What slicing <paramref name="type"/> gives, or null if it can't be: an array or string gives another, and a .NET
    /// type with a Length or Count and a <c>Slice(int, int)</c> returning its own type (List, Span) gives that.
    /// </summary>
    private static TypeSymbol? SliceType(TypeSymbol type)
    {
        if (type is ClrTypeSymbol { IsArray: true } array)
            return array.Rank == 1 ? type : null;
        if (type == TypeSymbol.String)
            return type;
        if (!IsClrBacked(type))
            return null;
        var (clr, _) = ClrContext(type);
        var slice = clr.GetMethod("Slice", BindingFlags.Public | BindingFlags.Instance, [typeof(int), typeof(int)]);
        return slice is not null && slice.ReturnType == clr && HasCount(clr) ? type : null;
    }

    /// <summary>The bound index arguments, with the one counting from the end (<paramref name="caret"/>'s) as C#'s <c>^n</c>.</summary>
    private ImmutableArray<BoundExpression> FromEnd(ImmutableArray<BoundExpression> arguments, UnaryExpressionSyntax? caret)
    {
        if (caret is null)
            return arguments;
        if (ConstantFolding.Value(arguments[0]) is 0)
            _diagnostics.ReportPastTheEnd(caret.Span);
        return [new BoundIndexFromEnd(arguments[0])];
    }

    /// <summary><c>xs[1..3]</c>, <c>xs[2..]</c>, <c>xs[..&lt;^1]</c>: a slice of an array, string or .NET collection.</summary>
    private BoundExpression BindSlice(BoundExpression target, RangeExpressionSyntax range, IndexExpressionSyntax syntax)
    {
        var start = range.Start is null ? null : BindSliceBound(range.Start, inclusiveEnd: false);
        BoundExpression? end = null;
        if (range.End is not null)
            end = BindSliceBound(range.End, inclusiveEnd: range.IsInclusive);
        else if (!range.IsInclusive)
            _diagnostics.ReportExclusiveRangeWithoutEnd(range.Operator.Span);

        if (SliceType(target.Type) is not { } sliced)
        {
            _diagnostics.ReportCannotSlice(syntax.Target.Span, target.Type);
            return new BoundErrorExpression();
        }
        if (start?.Type == TypeSymbol.Error || end?.Type == TypeSymbol.Error)
            return new BoundErrorExpression();
        return new BoundIndexExpression(target, null, [new BoundSliceRange(start, end)], sliced);
    }

    /// <summary>
    /// One end of a slice, as C# writes it: an int, or <c>^n</c> from the end. An end the range includes is moved one
    /// on, past it: <c>..3</c> is C#'s <c>..4</c>, and <c>..^1</c> (to the last element) is C#'s <c>..^0</c>.
    /// </summary>
    private BoundExpression BindSliceBound(ExpressionSyntax syntax, bool inclusiveEnd)
    {
        if (syntax is UnaryExpressionSyntax { Operator.Kind: TokenKind.Caret } caret)
        {
            var offset = BindInt(caret.Operand);
            if (inclusiveEnd && ConstantFolding.Value(offset) is 0)
                _diagnostics.ReportPastTheEnd(caret.Span);
            return offset.Type == TypeSymbol.Error ? offset : new BoundIndexFromEnd(inclusiveEnd ? Step(offset, -1) : offset);
        }
        var value = BindInt(syntax);
        return inclusiveEnd && value.Type != TypeSymbol.Error ? Step(value, 1) : value;
    }

    private BoundExpression BindInt(ExpressionSyntax syntax) =>
        Convert(RequireValue(BindExpression(syntax, TypeSymbol.Int), syntax.Span), TypeSymbol.Int, syntax.Span);

    /// <summary><paramref name="value"/> plus <paramref name="by"/>, folded when it's a constant.</summary>
    private static BoundExpression Step(BoundExpression value, int by) => ConstantFolding.Value(value) is int constant
        ? new BoundLiteralExpression(constant + by, TypeSymbol.Int)
        : new BoundBinaryExpression(value, by > 0 ? BoundBinaryOperatorKind.Add : BoundBinaryOperatorKind.Subtract,
            new BoundLiteralExpression(Math.Abs(by), TypeSymbol.Int), TypeSymbol.Int);
}
