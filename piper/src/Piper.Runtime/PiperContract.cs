namespace Piper.Runtime;

/// <summary>The checks behind Piper's <c>requires</c> clauses on functions and records.</summary>
public static class PiperContract
{
    /// <summary>A function's precondition: the caller passed arguments it doesn't accept.</summary>
    /// <param name="values">The values of the parameters the condition mentions, as <c>x = -1, y = 2</c>.</param>
    /// <param name="parameter">The parameter's name when the condition mentions just one, otherwise empty.</param>
    public static void Require(bool condition, string text, string values, string parameter)
    {
        if (!condition)
            throw new ArgumentException(Message("Precondition", text, values), parameter.Length == 0 ? null : parameter);
    }

    /// <summary>
    /// A record's invariant, checked as it's constructed. Returns <paramref name="value"/> (the first field's), so the
    /// check can be its property's initializer.
    /// </summary>
    public static T Invariant<T>(T value, bool condition, string text, string values)
    {
        if (!condition)
            throw new ArgumentException(Message("Invariant", text, values));
        return value;
    }

    /// <summary>
    /// A class's invariant, checked as one is made, before its fields are initialized. Returns true, so the checks can
    /// guard its first field's initializer: <c>Invariant(…) &amp;&amp; Invariant(…) ? value : default!</c>.
    /// </summary>
    public static bool Invariant(bool condition, string text, string values)
    {
        if (!condition)
            throw new ArgumentException(Message("Invariant", text, values));
        return true;
    }

    private static string Message(string kind, string text, string values) =>
        values.Length == 0 ? $"{kind} failed: {text}" : $"{kind} failed: {text} ({values})";
}
