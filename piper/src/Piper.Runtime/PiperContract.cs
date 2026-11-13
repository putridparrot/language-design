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

    /// <summary>A precondition with a message of its own: <c>requires amount &gt; 0 else "Bid something."</c>.</summary>
    /// <param name="parameter">The parameter it's about (the one it mentions), or empty.</param>
    public static void Require(bool condition, string text, string values, string parameter, Func<string> message)
    {
        if (!condition)
            throw new PiperContractException(message(), parameter.Length == 0 ? null : parameter, Message("Precondition", text, values));
    }

    /// <summary>A record's invariant with a message of its own, and the field it's about (the one it mentions), or empty.</summary>
    public static T Invariant<T>(T value, bool condition, string text, string values, string field, Func<string> message)
    {
        if (!condition)
            throw new PiperContractException(message(), field.Length == 0 ? null : field, Message("Invariant", text, values));
        return value;
    }

    /// <summary>A class's invariant with a message of its own, and the parameter it's about, or empty.</summary>
    public static bool Invariant(bool condition, string text, string values, string field, Func<string> message)
    {
        if (!condition)
            throw new PiperContractException(message(), field.Length == 0 ? null : field, Message("Invariant", text, values));
        return true;
    }

    private static string Message(string kind, string text, string values) =>
        values.Length == 0 ? $"{kind} failed: {text}" : $"{kind} failed: {text} ({values})";
}

/// <summary>
/// A <c>requires … else "message"</c> that failed: an <see cref="ArgumentException"/> whose <see cref="Message"/> is the
/// message as written (for a person: shown beside a form's field, or as a 400's detail), with the field or parameter
/// it's about, if it mentions just one, and the condition that failed, for a developer.
/// </summary>
public sealed class PiperContractException(string message, string? field, string condition) : ArgumentException(message, field)
{
    private readonly string _message = message;

    /// <summary>The message as written, without ArgumentException's "(Parameter '…')".</summary>
    public override string Message => _message;

    /// <summary>The field or parameter the condition is about, or null if it mentions several (or none).</summary>
    public string? Field => ParamName;

    /// <summary>What failed: <c>Invariant failed: price &gt; 0 (price = -1)</c>.</summary>
    public string Condition { get; } = condition;
}
