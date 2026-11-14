namespace Piper.Runtime;

/// <summary>
/// Marks a .NET parameter whose string names a field of a record: of the type argument <see cref="TypeArgument"/> of
/// the type the method belongs to. Where Piper passes it a constant, the compiler checks it's one of that record's
/// fields, as for <c>errors.For("title")</c> on a <c>FormErrors&lt;NewLot&gt;</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class PiperFieldNameAttribute(int typeArgument = 0) : Attribute
{
    /// <summary>Which of the declaring type's type arguments is the record, counting from 0.</summary>
    public int TypeArgument { get; } = typeArgument;
}
