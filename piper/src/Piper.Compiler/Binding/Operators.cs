using Piper.Syntax;

namespace Piper.Binding;

public enum BoundUnaryOperatorKind
{
    Negate,
    LogicalNot,
}

public enum BoundBinaryOperatorKind
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Remainder,
    Equals,
    NotEquals,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
    LogicalAnd,
    LogicalOr,
}

/// <summary>The result of resolving an operator: operands are converted to <see cref="OperandType"/> first.</summary>
public sealed record BinaryOperatorResolution(BoundBinaryOperatorKind Kind, TypeSymbol OperandType, TypeSymbol ResultType);

public static class Operators
{
    public static BoundUnaryOperatorKind? GetUnaryKind(TokenKind kind) => kind switch
    {
        TokenKind.Minus => BoundUnaryOperatorKind.Negate,
        TokenKind.Bang => BoundUnaryOperatorKind.LogicalNot,
        _ => null,
    };

    public static BoundBinaryOperatorKind? GetBinaryKind(TokenKind kind) => kind switch
    {
        TokenKind.Plus => BoundBinaryOperatorKind.Add,
        TokenKind.Minus => BoundBinaryOperatorKind.Subtract,
        TokenKind.Star => BoundBinaryOperatorKind.Multiply,
        TokenKind.Slash => BoundBinaryOperatorKind.Divide,
        TokenKind.Percent => BoundBinaryOperatorKind.Remainder,
        TokenKind.EqualsEquals => BoundBinaryOperatorKind.Equals,
        TokenKind.BangEquals => BoundBinaryOperatorKind.NotEquals,
        TokenKind.Less => BoundBinaryOperatorKind.Less,
        TokenKind.LessEquals => BoundBinaryOperatorKind.LessOrEqual,
        TokenKind.Greater => BoundBinaryOperatorKind.Greater,
        TokenKind.GreaterEquals => BoundBinaryOperatorKind.GreaterOrEqual,
        TokenKind.AmpersandAmpersand => BoundBinaryOperatorKind.LogicalAnd,
        TokenKind.PipePipe => BoundBinaryOperatorKind.LogicalOr,
        _ => null,
    };

    /// <summary>The operand's type after promotion (the binder converts the operand to it), or null if undefined.</summary>
    public static TypeSymbol? ResolveUnary(BoundUnaryOperatorKind kind, TypeSymbol operand) => kind switch
    {
        // As in C#: small integers and Char negate as Int, UInt as Long, and ULong can't be negated.
        BoundUnaryOperatorKind.Negate when operand == TypeSymbol.UInt => TypeSymbol.Long,
        BoundUnaryOperatorKind.Negate when operand == TypeSymbol.ULong => null,
        BoundUnaryOperatorKind.Negate when IsArithmetic(operand) => Promote(operand, operand),
        BoundUnaryOperatorKind.LogicalNot when operand == TypeSymbol.Bool => TypeSymbol.Bool,
        _ => null,
    };

    public static BinaryOperatorResolution? ResolveBinary(BoundBinaryOperatorKind kind, TypeSymbol left, TypeSymbol right)
    {
        var numeric = Promote(left, right);
        switch (kind)
        {
            case BoundBinaryOperatorKind.Add when left == TypeSymbol.String && right == TypeSymbol.String:
                return new(kind, TypeSymbol.String, TypeSymbol.String);

            // Chars compare as Chars (C# would promote both to int, with the same result).
            case BoundBinaryOperatorKind.Less or BoundBinaryOperatorKind.LessOrEqual or BoundBinaryOperatorKind.Greater
                or BoundBinaryOperatorKind.GreaterOrEqual or BoundBinaryOperatorKind.Equals or BoundBinaryOperatorKind.NotEquals
                when left == TypeSymbol.Char && right == TypeSymbol.Char:
                return new(kind, TypeSymbol.Char, TypeSymbol.Bool);

            case BoundBinaryOperatorKind.Add or BoundBinaryOperatorKind.Subtract or BoundBinaryOperatorKind.Multiply
                or BoundBinaryOperatorKind.Divide or BoundBinaryOperatorKind.Remainder when numeric is not null:
                return new(kind, numeric, numeric);

            case BoundBinaryOperatorKind.Less or BoundBinaryOperatorKind.LessOrEqual
                or BoundBinaryOperatorKind.Greater or BoundBinaryOperatorKind.GreaterOrEqual:
                if (numeric is not null)
                    return new(kind, numeric, TypeSymbol.Bool);
                return null;

            case BoundBinaryOperatorKind.Equals or BoundBinaryOperatorKind.NotEquals:
                if (numeric is not null)
                    return new(kind, numeric, TypeSymbol.Bool);
                // Tuples compare element by element, as in C#, so each element must support ==.
                if (left == right && left is TupleTypeSymbol tuple)
                    return tuple.ElementTypes.All(e => ResolveBinary(kind, e, e) is not null) ? new(kind, left, TypeSymbol.Bool) : null;
                // Everything else compares by value, type parameters and traits included: C# can't apply == to an
                // unconstrained type parameter, and on an interface it compares references, so the emitter uses
                // EqualityComparer<T> for those.
                if (left == right && left.IsValueType)
                    return new(kind, left, TypeSymbol.Bool);
                return null;

            case BoundBinaryOperatorKind.LogicalAnd or BoundBinaryOperatorKind.LogicalOr
                when left == TypeSymbol.Bool && right == TypeSymbol.Bool:
                return new(kind, TypeSymbol.Bool, TypeSymbol.Bool);

            default:
                return null;
        }
    }

    /// <summary>C#'s implicit numeric conversions (none of which lose magnitude), including those from Char.</summary>
    private static readonly Dictionary<TypeSymbol, TypeSymbol[]> ImplicitNumeric = new()
    {
        [TypeSymbol.SByte] = [TypeSymbol.Short, TypeSymbol.Int, TypeSymbol.Long, TypeSymbol.Float, TypeSymbol.Double, TypeSymbol.Decimal],
        [TypeSymbol.Byte] =
        [
            TypeSymbol.Short, TypeSymbol.UShort, TypeSymbol.Int, TypeSymbol.UInt, TypeSymbol.Long, TypeSymbol.ULong,
            TypeSymbol.Float, TypeSymbol.Double, TypeSymbol.Decimal,
        ],
        [TypeSymbol.Short] = [TypeSymbol.Int, TypeSymbol.Long, TypeSymbol.Float, TypeSymbol.Double, TypeSymbol.Decimal],
        [TypeSymbol.UShort] =
            [TypeSymbol.Int, TypeSymbol.UInt, TypeSymbol.Long, TypeSymbol.ULong, TypeSymbol.Float, TypeSymbol.Double, TypeSymbol.Decimal],
        [TypeSymbol.Int] = [TypeSymbol.Long, TypeSymbol.Float, TypeSymbol.Double, TypeSymbol.Decimal],
        [TypeSymbol.UInt] = [TypeSymbol.Long, TypeSymbol.ULong, TypeSymbol.Float, TypeSymbol.Double, TypeSymbol.Decimal],
        [TypeSymbol.Long] = [TypeSymbol.Float, TypeSymbol.Double, TypeSymbol.Decimal],
        [TypeSymbol.ULong] = [TypeSymbol.Float, TypeSymbol.Double, TypeSymbol.Decimal],
        [TypeSymbol.Char] =
        [
            TypeSymbol.UShort, TypeSymbol.Int, TypeSymbol.UInt, TypeSymbol.Long, TypeSymbol.ULong,
            TypeSymbol.Float, TypeSymbol.Double, TypeSymbol.Decimal,
        ],
        [TypeSymbol.Float] = [TypeSymbol.Double],
    };

    /// <summary>The implicit conversions between numeric types, as in C#: e.g. Int → Long → Double, Byte → Int, Char → Int.</summary>
    public static bool IsImplicitlyConvertible(TypeSymbol from, TypeSymbol to)
    {
        if (from == to || from == TypeSymbol.Error || to == TypeSymbol.Error || from == TypeSymbol.Never)
            return true;
        return ImplicitNumeric.TryGetValue(from, out var targets) && targets.Contains(to);
    }

    /// <summary>A number or a Char: the types arithmetic is defined on (a Char takes part as its code).</summary>
    public static bool IsArithmetic(TypeSymbol type) => type.IsNumeric || type == TypeSymbol.Char;

    /// <summary>
    /// C#'s binary numeric promotion: the type both operands are converted to, or null where C# has no such operator
    /// (Decimal with Float/Double, ULong with a signed type). Anything smaller than Int becomes Int, so Byte + Byte is an Int.
    /// </summary>
    public static TypeSymbol? Promote(TypeSymbol a, TypeSymbol b)
    {
        if (!IsArithmetic(a) || !IsArithmetic(b))
            return null;
        bool Either(TypeSymbol type) => a == type || b == type;
        bool EitherSigned(params TypeSymbol[] types) => types.Contains(a) || types.Contains(b);

        if (Either(TypeSymbol.Decimal))
            return Either(TypeSymbol.Float) || Either(TypeSymbol.Double) ? null : TypeSymbol.Decimal;
        if (Either(TypeSymbol.Double))
            return TypeSymbol.Double;
        if (Either(TypeSymbol.Float))
            return TypeSymbol.Float;
        if (Either(TypeSymbol.ULong))
            return EitherSigned(TypeSymbol.SByte, TypeSymbol.Short, TypeSymbol.Int, TypeSymbol.Long) ? null : TypeSymbol.ULong;
        if (Either(TypeSymbol.Long))
            return TypeSymbol.Long;
        if (Either(TypeSymbol.UInt))
            return EitherSigned(TypeSymbol.SByte, TypeSymbol.Short, TypeSymbol.Int) ? TypeSymbol.Long : TypeSymbol.UInt;
        return TypeSymbol.Int;
    }

    /// <summary>
    /// Ranks numeric conversion targets for overload resolution, lower being better, after C#'s "better conversion
    /// target": a narrower type beats a wider one it converts to, and a signed type beats the unsigned one of its size.
    /// </summary>
    public static int NumericRank(TypeSymbol type) =>
        Array.IndexOf<TypeSymbol>(
        [
            TypeSymbol.SByte, TypeSymbol.Byte, TypeSymbol.Short, TypeSymbol.UShort, TypeSymbol.Int, TypeSymbol.UInt,
            TypeSymbol.Long, TypeSymbol.ULong, TypeSymbol.Float, TypeSymbol.Double, TypeSymbol.Decimal,
        ], type);

    public static string GetText(BoundBinaryOperatorKind kind) => kind switch
    {
        BoundBinaryOperatorKind.Add => "+",
        BoundBinaryOperatorKind.Subtract => "-",
        BoundBinaryOperatorKind.Multiply => "*",
        BoundBinaryOperatorKind.Divide => "/",
        BoundBinaryOperatorKind.Remainder => "%",
        BoundBinaryOperatorKind.Equals => "==",
        BoundBinaryOperatorKind.NotEquals => "!=",
        BoundBinaryOperatorKind.Less => "<",
        BoundBinaryOperatorKind.LessOrEqual => "<=",
        BoundBinaryOperatorKind.Greater => ">",
        BoundBinaryOperatorKind.GreaterOrEqual => ">=",
        BoundBinaryOperatorKind.LogicalAnd => "&&",
        BoundBinaryOperatorKind.LogicalOr => "||",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
