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

    public static TypeSymbol? ResolveUnary(BoundUnaryOperatorKind kind, TypeSymbol operand) => kind switch
    {
        BoundUnaryOperatorKind.Negate when operand.IsNumeric => operand,
        BoundUnaryOperatorKind.LogicalNot when operand == TypeSymbol.Bool => TypeSymbol.Bool,
        _ => null,
    };

    public static BinaryOperatorResolution? ResolveBinary(BoundBinaryOperatorKind kind, TypeSymbol left, TypeSymbol right)
    {
        var numeric = left.IsNumeric && right.IsNumeric ? Widest(left, right) : null;
        switch (kind)
        {
            case BoundBinaryOperatorKind.Add when left == TypeSymbol.String && right == TypeSymbol.String:
                return new(kind, TypeSymbol.String, TypeSymbol.String);

            case BoundBinaryOperatorKind.Add or BoundBinaryOperatorKind.Subtract or BoundBinaryOperatorKind.Multiply
                or BoundBinaryOperatorKind.Divide or BoundBinaryOperatorKind.Remainder when numeric is not null:
                return new(kind, numeric, numeric);

            case BoundBinaryOperatorKind.Less or BoundBinaryOperatorKind.LessOrEqual
                or BoundBinaryOperatorKind.Greater or BoundBinaryOperatorKind.GreaterOrEqual:
                if (numeric is not null)
                    return new(kind, numeric, TypeSymbol.Bool);
                if (left == TypeSymbol.Char && right == TypeSymbol.Char)
                    return new(kind, TypeSymbol.Char, TypeSymbol.Bool);
                return null;

            case BoundBinaryOperatorKind.Equals or BoundBinaryOperatorKind.NotEquals:
                if (numeric is not null)
                    return new(kind, numeric, TypeSymbol.Bool);
                // C# can't compare an unconstrained type parameter with ==, and == on an interface would
                // compare references, so neither type parameters nor traits support equality (yet).
                if (left == right && left.IsValueType && left is not (TypeParameterSymbol or TraitSymbol))
                    return new(kind, left, TypeSymbol.Bool);
                return null;

            case BoundBinaryOperatorKind.LogicalAnd or BoundBinaryOperatorKind.LogicalOr
                when left == TypeSymbol.Bool && right == TypeSymbol.Bool:
                return new(kind, TypeSymbol.Bool, TypeSymbol.Bool);

            default:
                return null;
        }
    }

    /// <summary>The implicit (widening) conversions: Int → Long → Double.</summary>
    public static bool IsImplicitlyConvertible(TypeSymbol from, TypeSymbol to)
    {
        if (from == to || from == TypeSymbol.Error || to == TypeSymbol.Error || from == TypeSymbol.Never)
            return true;
        if (from == TypeSymbol.Int)
            return to == TypeSymbol.Long || to == TypeSymbol.Double;
        if (from == TypeSymbol.Long)
            return to == TypeSymbol.Double;
        return false;
    }

    private static TypeSymbol Widest(TypeSymbol a, TypeSymbol b) =>
        a == TypeSymbol.Double || b == TypeSymbol.Double ? TypeSymbol.Double
        : a == TypeSymbol.Long || b == TypeSymbol.Long ? TypeSymbol.Long
        : TypeSymbol.Int;

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
