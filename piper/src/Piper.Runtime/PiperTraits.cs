using System.Collections.Concurrent;

namespace Piper.Runtime;

/// <summary>
/// A generated struct that wraps a value of a type C# can't make implement a trait's interface (int, string, a .NET type,
/// Option), and implements the interface by calling the type's extend methods.
/// </summary>
public interface IPiperBox<TValue>
{
    TValue Value { get; }
}

/// <inheritdoc cref="IPiperBox{TValue}"/>
public interface IPiperBox<TValue, TBox> : IPiperBox<TValue> where TBox : IPiperBox<TValue, TBox>
{
    static abstract TBox Wrap(TValue value);
}

/// <summary>
/// Finds the witness through which a type implements a trait. Every trait <c>Shape</c> has a nested interface
/// <c>Shape.PiperWitness&lt;TSelf&gt;</c> with its methods taking the value as a parameter, so generic code can call
/// them on any type: <c>PiperTraits.Witness&lt;Shape.PiperWitness&lt;T&gt;&gt;().area(x)</c>. Piper's records, enums
/// and traits implement the interface itself and are served by <c>Shape.PiperVia&lt;TSelf&gt;</c>; other types by a
/// <c>Shape.PiperMapped&lt;TSelf, TBox&gt;</c> registered when their module loads.
/// </summary>
public static class PiperTraits
{
    private static readonly ConcurrentDictionary<Type, object> Registered = new();
    private static readonly ConcurrentDictionary<(Type Witness, Type Self), (Type Mapped, Type Box)> RegisteredGeneric = new();

    /// <summary>Registers the witness for a non-generic type, e.g. <c>Shape.PiperWitness&lt;int&gt;</c>.</summary>
    public static void Register<TWitness>(TWitness witness) where TWitness : notnull => Registered[typeof(TWitness)] = witness;

    /// <summary>
    /// Registers the witnesses for every instantiation of a generic type: for <c>Option&lt;T&gt;</c> implementing
    /// <c>Shape</c>, <c>Shape.PiperMapped&lt;Option&lt;T&gt;, Box&lt;T&gt;&gt;</c>, whose box takes the type's arguments.
    /// </summary>
    public static void RegisterGeneric(Type witnessDefinition, Type selfDefinition, Type mappedDefinition, Type boxDefinition) =>
        RegisteredGeneric[(witnessDefinition, selfDefinition)] = (mappedDefinition, boxDefinition);

    public static TWitness Witness<TWitness>() => Cache<TWitness>.Value;

    /// <summary>A cast from a trait to a type that may be boxed: <c>(int)shape</c>, or <c>(T)shape</c>.</summary>
    public static T Unbox<T>(object? value) => value switch
    {
        T result => result,
        IPiperBox<T> box => box.Value,
        _ => throw new InvalidCastException($"Unable to cast object of type '{value?.GetType()}' to type '{typeof(T)}'."),
    };

    private static class Cache<TWitness>
    {
        public static readonly TWitness Value = Resolve<TWitness>();
    }

    private static TWitness Resolve<TWitness>()
    {
        var witness = typeof(TWitness);
        if (Registered.TryGetValue(witness, out var registered))
            return (TWitness)registered;

        // A nested type's arguments include its trait's: Convert<A>.PiperWitness<TSelf> has [A, TSelf].
        var definition = witness.GetGenericTypeDefinition();
        var arguments = witness.GetGenericArguments();
        var self = arguments[^1];
        try
        {
            if (self.IsGenericType && RegisteredGeneric.TryGetValue((definition, self.GetGenericTypeDefinition()), out var generic))
            {
                var box = generic.Box.MakeGenericType(self.GetGenericArguments());
                return (TWitness)Activator.CreateInstance(generic.Mapped.MakeGenericType([.. arguments, box]))!;
            }
            var via = definition.DeclaringType!.GetNestedType("PiperVia`1")!;
            return (TWitness)Activator.CreateInstance(via.MakeGenericType(arguments))!;
        }
        catch (ArgumentException)
        {
            // A constraint failed: the type doesn't implement the trait (only possible when called from outside Piper).
            throw new InvalidOperationException($"'{self}' doesn't implement trait '{definition.DeclaringType}'.");
        }
    }
}
