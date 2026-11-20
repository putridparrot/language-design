using Piper.Binding;

namespace Piper.Plugins.Aspects;

/// <summary>Whether a <c>Result</c>'s error type can hold a <c>[Transient]</c> error, as far as its type says.</summary>
internal static class Transience
{
    /// <summary>
    /// False only when it surely can't: a Piper enum with no <c>[Transient]</c> on it or any variant, a record or class
    /// without one (or a base with one), or a sealed .NET type without one (a string, a number). A trait, a type
    /// parameter, <c>object</c> or an unsealed .NET class could hold one, so they can be.
    /// </summary>
    public static bool CanBeTransient(TypeSymbol error)
    {
        var definition = error.OriginalDefinition;
        switch (definition)
        {
            case EnumSymbol @enum:
                return IsMarked(@enum) || @enum.Variants.Any(IsMarked);
            case RecordSymbol record:
                return IsMarked(record);
            case ClassSymbol @class:
                for (TypeSymbol? type = @class; type is not null; type = (type as ClassSymbol)?.Base)
                {
                    if (type is ClassSymbol piper ? IsMarked(piper) : ClrCanBeTransient(type))
                        return true;
                }
                return false;
            default:
                // A builtin (string, int) or a tuple can't be; an error type not known (an error already reported) is let be.
                return definition == TypeSymbol.Error || definition.IsTrait || definition is TypeParameterSymbol || ClrCanBeTransient(definition);
        }
    }

    // By name, as the plugin may see Piper.Runtime loaded apart from the program's.
    private const string Attribute = "Piper.Runtime.TransientAttribute";

    private static bool IsMarked(Symbol symbol) =>
        symbol.Attributes.Any(a => a.Type is ClrTypeSymbol { Type.FullName: Attribute });

    private static bool ClrCanBeTransient(TypeSymbol type) =>
        type is ClrTypeSymbol { Type: var clr } &&
        (!clr.IsSealed && !clr.IsValueType ||
         clr.GetCustomAttributes(inherit: true).Any(a => a.GetType().FullName == Attribute) ||
         typeof(TimeoutException).IsAssignableFrom(clr));
}
