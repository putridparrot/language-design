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

    /// <summary>
    /// A record's invariant checked on its own, without making the record: its message if it fails, or null. What a
    /// record's <see cref="PiperRequirement"/>s call, so a form can say everything that's wrong with what was sent.
    /// </summary>
    public static string? Failure(bool condition, string text, string values) =>
        condition ? null : Message("Invariant", text, values);

    /// <summary>The same, for an invariant with a message of its own.</summary>
    public static string? Failure(bool condition, string text, string values, string field, Func<string> message) =>
        condition ? null : message();

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

/// <summary>
/// One of a record's <c>requires</c>, checkable before the record is made: the compiler gives each record with any a
/// static <c>__PiperRequirements</c> table of them. Reading a form uses it to check every condition whose fields were
/// read, even when others weren't, so the form shows everything that's wrong at once.
/// </summary>
/// <param name="fields">The fields the condition mentions, by name.</param>
/// <param name="check">
/// Given the record's field values in order (boxed; those not read are null), the condition's message if it fails, or
/// null. It's only meaningful when every field in <paramref name="fields"/> was read.
/// </param>
public sealed class PiperRequirement(string[] fields, Func<object?[], string?> check)
{
    /// <summary>The fields the condition mentions.</summary>
    public IReadOnlyList<string> Fields { get; } = fields;

    /// <summary>The field it's about, when it mentions just one, as a form shows its message beside that field.</summary>
    public string? Field => Fields is [var only] ? only : null;

    /// <summary>Its message if it fails for these field values, or null.</summary>
    public string? Check(object?[] values) => check(values);

    /// <summary>A field's value from the boxed values, as the generated checks read them.</summary>
    public static T Value<T>(object?[] values, int index) => values[index] is T value ? value : default!;
}
