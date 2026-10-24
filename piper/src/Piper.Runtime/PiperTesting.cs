using System.Collections;
using System.Globalization;

namespace Piper.Runtime;

/// <summary>Marks the method a Piper <c>test "name" { … }</c> block compiles to, for <c>piper test</c> to find.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PiperTestAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>Thrown when a Piper <c>Assert</c> or <c>AssertEqual</c> fails.</summary>
public sealed class PiperAssertionException(string message) : Exception(message);

/// <summary>Piper's <c>Assert</c> and <c>AssertEqual</c>.</summary>
public static class PiperAssert
{
    /// <param name="description">The user's message, or the condition's source text.</param>
    public static void True(bool condition, string description)
    {
        if (!condition)
            throw new PiperAssertionException($"Assertion failed: {description}");
    }

    /// <summary>Compares with <see cref="object.Equals(object, object)"/>, and sequences (other than strings) element by element.</summary>
    public static void Equal<T>(T expected, T actual)
    {
        if (!AreEqual(expected, actual))
            throw new PiperAssertionException($"Expected {Format(expected)} but got {Format(actual)}");
    }

    private static bool AreEqual(object? expected, object? actual)
    {
        if (expected is IEnumerable expectedItems and not string && actual is IEnumerable actualItems and not string)
        {
            var left = expectedItems.GetEnumerator();
            var right = actualItems.GetEnumerator();
            while (true)
            {
                bool hasLeft = left.MoveNext(), hasRight = right.MoveNext();
                if (hasLeft != hasRight)
                    return false;
                if (!hasLeft)
                    return true;
                if (!AreEqual(left.Current, right.Current))
                    return false;
            }
        }
        return Equals(expected, actual);
    }

    /// <summary>A value as Piper prints it, with strings and chars quoted and sequences listed: <c>["a", "b"]</c>.</summary>
    private static string Format(object? value) => value switch
    {
        null => "null",
        string text => $"\"{text}\"",
        char c => $"'{c}'",
        IEnumerable items => $"[{string.Join(", ", items.Cast<object?>().Select(Format))}]",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
