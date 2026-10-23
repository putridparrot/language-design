using System.Globalization;
using System.Reflection;

namespace Piper.Binding;

/// <summary>
/// C# evaluates constant expressions (literals, <c>const</c> fields such as <c>Int.MaxValue</c>, and arithmetic, casts and
/// conversions on them) when it compiles, in a checked context: <c>Int.MaxValue + 1</c>, <c>1 / 0</c> and
/// <c>(Byte)300</c> are compile errors there. The binder uses this to report those itself, rather than emit C# that
/// wouldn't compile, and to allow C#'s constant conversions such as <c>7</c> to a Byte.
/// </summary>
public static class ConstantFolding
{
    public enum Failure
    {
        None,
        Overflow,
        DivideByZero,
    }

    /// <summary>The numeric value of a constant expression, or null if it isn't one (or its own evaluation fails).</summary>
    public static object? Value(BoundExpression expression) => expression switch
    {
        BoundLiteralExpression literal when IsNumber(literal.Value) => literal.Value,
        BoundClrMemberExpression { Receiver: null, Member: FieldInfo { IsLiteral: true } field } =>
            field.GetRawConstantValue() is var value && IsNumber(value) ? value : null,
        BoundConversionExpression conversion when Value(conversion.Expression) is { } value => Cast(value, conversion.Type, out var result) == Failure.None ? result : null,
        BoundCastExpression cast when Value(cast.Operand) is { } value => Cast(value, cast.Type, out var result) == Failure.None ? result : null,
        BoundUnaryExpression { Operator: BoundUnaryOperatorKind.Negate } unary when Value(unary.Operand) is { } operand =>
            Negate(operand, out var result) == Failure.None ? result : null,
        BoundBinaryExpression binary when Value(binary.Left) is { } left && Value(binary.Right) is { } right =>
            Binary(binary.Operator, left, right, out var result) == Failure.None ? result : null,
        _ => null,
    };

    private static bool IsNumber(object? value) =>
        value is int or long or uint or ulong or double or float or decimal or byte or sbyte or short or ushort;

    /// <summary>
    /// C#'s implicit constant conversion: an Int constant to a smaller or unsigned integer type when its value fits
    /// (<c>let b: Byte = 7;</c>), and a Long constant to ULong when it isn't negative.
    /// </summary>
    public static bool FitsImplicitly(BoundExpression expression, TypeSymbol target)
    {
        if (Value(expression) is not { } value)
            return false;
        bool integralTarget = target == TypeSymbol.SByte || target == TypeSymbol.Byte || target == TypeSymbol.Short ||
                              target == TypeSymbol.UShort || target == TypeSymbol.UInt || target == TypeSymbol.ULong;
        return value switch
        {
            int when integralTarget => Cast(value, target, out _) == Failure.None,
            long l when target == TypeSymbol.ULong => l >= 0,
            _ => false,
        };
    }

    public static Failure Negate(object operand, out object? result)
    {
        try
        {
            result = operand switch
            {
                int i => checked(-i),
                long l => checked(-l),
                double d => -d,
                float f => -f,
                decimal m => -m,
                _ => null,
            };
            return result is null ? Failure.Overflow : Failure.None;
        }
        catch (OverflowException)
        {
            result = null;
            return Failure.Overflow;
        }
    }

    /// <summary>Arithmetic on two constants of the same (promoted) type; comparisons aren't folded.</summary>
    public static Failure Binary(BoundBinaryOperatorKind kind, object left, object right, out object? result)
    {
        result = null;
        if (kind is not (BoundBinaryOperatorKind.Add or BoundBinaryOperatorKind.Subtract or BoundBinaryOperatorKind.Multiply
            or BoundBinaryOperatorKind.Divide or BoundBinaryOperatorKind.Remainder))
        {
            return Failure.None;
        }

        try
        {
            result = (left, right) switch
            {
                (int a, int b) => Arithmetic(kind, a, b, (x, y) => checked(x + y), (x, y) => checked(x - y), (x, y) => checked(x * y), (x, y) => checked(x / y), (x, y) => checked(x % y)),
                (long a, long b) => Arithmetic(kind, a, b, (x, y) => checked(x + y), (x, y) => checked(x - y), (x, y) => checked(x * y), (x, y) => checked(x / y), (x, y) => checked(x % y)),
                (uint a, uint b) => Arithmetic(kind, a, b, (x, y) => checked(x + y), (x, y) => checked(x - y), (x, y) => checked(x * y), (x, y) => x / y, (x, y) => x % y),
                (ulong a, ulong b) => Arithmetic(kind, a, b, (x, y) => checked(x + y), (x, y) => checked(x - y), (x, y) => checked(x * y), (x, y) => x / y, (x, y) => x % y),
                (double a, double b) => Arithmetic(kind, a, b, (x, y) => x + y, (x, y) => x - y, (x, y) => x * y, (x, y) => x / y, (x, y) => x % y),
                (float a, float b) => Arithmetic(kind, a, b, (x, y) => x + y, (x, y) => x - y, (x, y) => x * y, (x, y) => x / y, (x, y) => x % y),
                (decimal a, decimal b) => Arithmetic(kind, a, b, (x, y) => x + y, (x, y) => x - y, (x, y) => x * y, (x, y) => x / y, (x, y) => x % y),
                _ => null,
            };
            return Failure.None;
        }
        catch (OverflowException)
        {
            return Failure.Overflow;
        }
        catch (DivideByZeroException)
        {
            return Failure.DivideByZero;
        }
    }

    private static object Arithmetic<T>(
        BoundBinaryOperatorKind kind,
        T a,
        T b,
        Func<T, T, T> add,
        Func<T, T, T> subtract,
        Func<T, T, T> multiply,
        Func<T, T, T> divide,
        Func<T, T, T> remainder) where T : notnull =>
        kind switch
        {
            BoundBinaryOperatorKind.Add => add(a, b),
            BoundBinaryOperatorKind.Subtract => subtract(a, b),
            BoundBinaryOperatorKind.Multiply => multiply(a, b),
            BoundBinaryOperatorKind.Divide => divide(a, b),
            _ => remainder(a, b),
        };

    /// <summary>
    /// Converts a constant to a numeric type as a checked C# cast would: integers must fit, and a Float, Double or
    /// Decimal is truncated towards zero first (<c>(Int)2.7</c> is 2, <c>(Byte)300</c> overflows).
    /// </summary>
    public static Failure Cast(object value, TypeSymbol type, out object? result)
    {
        result = null;
        if (ClrTypes.ToClr(type) is not { } target || !IsNumber(value) && value is not char)
            return Failure.None;
        try
        {
            if (target == typeof(double) || target == typeof(float) || target == typeof(decimal))
            {
                result = System.Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
            }
            else
            {
                // To an integer: truncate a fraction first (ChangeType would round), then require the value to fit.
                var whole = value is double or float or decimal ? Math.Truncate(System.Convert.ToDecimal(value, CultureInfo.InvariantCulture)) : value;
                result = System.Convert.ChangeType(whole, target, CultureInfo.InvariantCulture);
            }
            return Failure.None;
        }
        catch (OverflowException)
        {
            return Failure.Overflow;
        }
        catch (InvalidCastException)
        {
            return Failure.None;
        }
    }
}
