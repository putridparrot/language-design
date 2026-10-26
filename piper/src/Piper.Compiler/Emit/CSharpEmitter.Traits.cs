using System.Collections.Immutable;
using System.Text;
using Piper.Binding;
using Piper.Text;

namespace Piper.Emit;

/// <summary>
/// Trait witnesses. C# can't make int, string, a .NET type or Option implement an interface, so a trait bound isn't a C#
/// constraint. Each trait <c>Shape</c> instead has a nested <c>Shape.PiperWitness&lt;TPiperSelf&gt;</c> interface, whose
/// methods take the value as a parameter, and generic code calls through the witness for its type parameter:
/// <c>PiperTraits.Witness&lt;Shape.PiperWitness&lt;T&gt;&gt;().area(x)</c>. Types that implement the interface (records,
/// enums, traits) are served by <c>Shape.PiperVia&lt;TPiperSelf&gt;</c>, which calls the interface; other types by
/// <c>Shape.PiperMapped&lt;TPiperSelf, TPiperBox&gt;</c>, which wraps the value in a generated box struct that implements
/// the interface by calling the type's extend methods. A file's boxes register their witnesses when the module loads.
/// </summary>
public sealed partial class CSharpEmitter
{
    private const string TraitsClassName = "global::Piper.Runtime.PiperTraits";
    private const string Self = "TPiperSelf";
    private const string SelfParameter = "piperSelf";

    /// <summary>
    /// True for a type that reaches its traits through a witness rather than C# interface dispatch: a type parameter
    /// (which may be instantiated with anything), or a builtin, .NET or runtime type.
    /// </summary>
    private static bool UsesWitness(TypeSymbol type) =>
        !type.IsTrait && type != TypeSymbol.Error && type != TypeSymbol.Never &&
        type.OriginalDefinition is not NamedTypeDefinition { IsRuntimeType: false };

    /// <summary><c>PiperTraits.Witness&lt;Shape.PiperWitness&lt;T&gt;&gt;()</c>: how <paramref name="self"/> implements <paramref name="trait"/>.</summary>
    private string WitnessOf(TypeSymbol trait, TypeSymbol self) =>
        $"{TraitsClassName}.Witness<{TypeName(trait)}.PiperWitness<{TypeName(self)}>>()";

    /// <summary>A trait followed by every trait it inherits, directly or not, instantiated to match.</summary>
    private static List<TypeSymbol> TraitAncestors(TypeSymbol trait)
    {
        var result = new List<TypeSymbol>();
        var pending = new Queue<TypeSymbol>([trait]);
        while (pending.TryDequeue(out var next))
        {
            if (result.Contains(next) || next.TraitDefinition is not { } definition)
                continue;
            result.Add(next);
            var map = TypeSubstitution.MapFor(next);
            foreach (var parent in definition.Parents)
                pending.Enqueue(TypeSubstitution.Substitute(parent, map));
        }
        return result;
    }

    /// <summary>Every method a value of <paramref name="trait"/> has, with the map from its declaring trait's type parameters.</summary>
    private static IEnumerable<(MethodSymbol Method, Dictionary<TypeParameterSymbol, TypeSymbol> Map)> TraitMethods(TypeSymbol trait) =>
        TraitAncestors(trait).SelectMany(a => a.TraitDefinition!.Methods.Select(m => (m, TypeSubstitution.MapFor(a))));

    /// <summary>A trait with static methods can't be the type of a value, so its witness has no <c>PiperBox</c>.</summary>
    private static bool CanBox(TypeSymbol trait) => !TraitMethods(trait).Any(m => m.Method.IsStatic);

    /// <summary><c>U mapAll&lt;U&gt;(TPiperSelf piperSelf, Func&lt;T, U&gt; f) where …</c>, after the given modifiers.</summary>
    private string WitnessSignature(MethodSymbol method, IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol> map, string modifiers, bool withSelf)
    {
        // Defaults too, so generic code calling the trait method through its witness can leave arguments out.
        var parameters = method.Parameters.Select(p => $"{TypeName(TypeSubstitution.Substitute(p.Type, map))} {Escape(p.Name)}{DefaultValue(p.Default)}");
        if (withSelf && !method.IsStatic)
            parameters = parameters.Prepend($"{Self} {SelfParameter}");
        return $"{modifiers}{TypeName(TypeSubstitution.Substitute(method.ReturnType, map))} {Escape(method.Name)}" +
               $"{TypeParameterList(method.TypeParameters)}({string.Join(", ", parameters)}){Constraints(method.TypeParameters)}";
    }

    private static string ForwardedCall(MethodSymbol method, string receiver) =>
        PropertyOf(method) is { } property
            ? $"{receiver}.{Escape(property)}"
            : $"{receiver}.{Escape(method.Name)}{TypeParameterList(method.TypeParameters)}({string.Join(", ", method.Parameters.Select(p => Escape(p.Name)))})";

    /// <summary>The witness interface and its two implementations, nested in the trait's interface.</summary>
    private void EmitWitnessTypes(TraitSymbol trait)
    {
        var traitType = TypeName(trait.SelfType);
        var ancestors = TraitAncestors(trait.SelfType);
        var methods = TraitMethods(trait.SelfType).ToList();
        bool canBox = CanBox(trait.SelfType);
        var noMap = new Dictionary<TypeParameterSymbol, TypeSymbol>();

        var parents = trait.Parents.IsEmpty ? "" : " : " + string.Join(", ", trait.Parents.Select(p => $"{TypeName(p)}.PiperWitness<{Self}>"));
        _writer.WriteLine($"public interface PiperWitness<{Self}>{parents}");
        _writer.OpenBrace();
        if (canBox)
            _writer.WriteLine($"{(trait.Parents.IsEmpty ? "" : "new ")}{traitType} PiperBox({Self} {SelfParameter});");
        foreach (var method in trait.Methods)
            _writer.WriteLine($"{WitnessSignature(method, noMap, "", withSelf: true)};");
        _writer.CloseBrace();

        void Implementation(string header, string box, Func<MethodSymbol, string> receiver)
        {
            _writer.WriteLine();
            _writer.WriteLine(header);
            _writer.OpenBrace();
            if (canBox)
            {
                foreach (var ancestor in ancestors)
                    _writer.WriteLine($"{TypeName(ancestor)} {TypeName(ancestor)}.PiperWitness<{Self}>.PiperBox({Self} {SelfParameter}) => {box};");
            }
            foreach (var (method, map) in methods)
                _writer.WriteLine($"{WitnessSignature(method, map, "public ", withSelf: true)} => {ForwardedCall(method, receiver(method))};");
            _writer.CloseBrace();
        }

        Implementation($"public readonly struct PiperVia<{Self}> : PiperWitness<{Self}> where {Self} : {traitType}",
            SelfParameter, method => method.IsStatic ? Self : SelfParameter);
        Implementation($"public readonly struct PiperMapped<{Self}, TPiperBox> : PiperWitness<{Self}> " +
                       $"where TPiperBox : struct, {traitType}, global::Piper.Runtime.IPiperBox<{Self}, TPiperBox>",
            $"TPiperBox.Wrap({SelfParameter})", method => method.IsStatic ? "TPiperBox" : $"TPiperBox.Wrap({SelfParameter})");
    }

    // Boxes for builtin, .NET and runtime types

    /// <summary>The file that declares a type's box: the first with an `extend` making it implement a trait.</summary>
    private SourceText BoxHome(TypeSymbol target) => _program.ExternalConformances.First(c => c.Target == target).File;

    private List<TypeSymbol> BoxedTypesIn(SourceText file) =>
        [.. _program.ExternalConformances.Select(c => c.Target).Distinct().Where(t => BoxHome(t) == file)];

    /// <summary>The traits a type implements, with those they inherit.</summary>
    private List<TypeSymbol> ExternalTraits(TypeSymbol target) =>
        [.. _program.ExternalConformances.Where(c => c.Target == target).SelectMany(c => TraitAncestors(c.Trait)).Distinct()];

    /// <summary>
    /// <c>file readonly record struct PiperBox_int(int Value) : Shape, IPiperBox&lt;int, PiperBox_int&gt;</c>, implementing
    /// each trait method the type has itself by calling it, and a module initializer registering the witnesses.
    /// </summary>
    private void EmitBoxes(SourceText file)
    {
        var targets = BoxedTypesIn(file);
        var names = new Dictionary<TypeSymbol, string>();
        var used = new HashSet<string>();
        foreach (var target in targets)
        {
            var name = "PiperBox_" + new string([.. target.Name.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_')]);
            while (!used.Add(name))
                name += "_";
            names[target] = name;
        }

        for (int i = 0; i < targets.Count; i++)
        {
            if (i > 0)
                _writer.WriteLine();
            EmitBox(targets[i], names[targets[i]]);
        }

        _writer.WriteLine();
        _writer.WriteLine("file static class PiperWitnesses");
        _writer.OpenBrace();
        _writer.WriteLine("[global::System.Runtime.CompilerServices.ModuleInitializer]");
        _writer.WriteLine("internal static void Register()");
        _writer.OpenBrace();
        foreach (var target in targets)
        {
            if (target is GenericTypeDefinition { IsGeneric: true } generic)
            {
                // Option<T> is registered for every T: the runtime makes the box and witness for the instantiation it needs.
                var open = $"typeof({TypeName(generic)}<{new string(',', generic.TypeParameters.Length - 1)}>)";
                var box = $"typeof({names[target]}<{new string(',', generic.TypeParameters.Length - 1)}>)";
                foreach (var definition in ExternalTraits(target).Select(t => t.TraitDefinition!).Distinct())
                {
                    var trait = definition.IsGeneric ? $"{TypeName(definition)}<{new string(',', definition.TypeParameters.Length - 1)}>" : TypeName(definition);
                    _writer.WriteLine($"{TraitsClassName}.RegisterGeneric(typeof({trait}.PiperWitness<>), {open}, typeof({trait}.PiperMapped<,>), {box});");
                }
            }
            else
            {
                var self = TypeName(target);
                foreach (var trait in ExternalTraits(target))
                {
                    var traitName = TypeName(trait);
                    _writer.WriteLine($"{TraitsClassName}.Register<{traitName}.PiperWitness<{self}>>(new {traitName}.PiperMapped<{self}, {names[target]}>());");
                }
            }
        }
        _writer.CloseBrace();
        _writer.CloseBrace();
    }

    private void EmitBox(TypeSymbol target, string name)
    {
        var (typeParameters, valueType) = target is GenericTypeDefinition { IsGeneric: true } generic
            ? (TypeParameterList(generic.TypeParameters), TypeName(generic.SelfType))
            : ("", TypeName(target));
        var boxType = name + typeParameters;
        var declared = _program.ExternalConformances.Where(c => c.Target == target).Select(c => TypeName(c.Trait));
        _writer.WriteLine($"file readonly record struct {boxType}({valueType} Value) : {string.Join(", ", declared)}, " +
                          $"global::Piper.Runtime.IPiperBox<{valueType}, {boxType}>");
        _writer.OpenBrace();
        _writer.WriteLine($"public static {boxType} Wrap({valueType} value) => new(value);");
        _writer.WriteLine();
        _writer.WriteLine("public override string ToString() => $\"{Value}\";");

        // Trait methods the type has itself; defaults it doesn't override are the interface's own.
        var own = _program.Methods.Select(m => (MethodSymbol)m.Symbol)
            .Where(m => m.ContainingType == target && !m.IsPrivate)
            .ToDictionary(m => m.Name);
        foreach (var (method, map) in ExternalTraits(target).SelectMany(t => t.TraitDefinition!.Methods.Select(m => (m, TypeSubstitution.MapFor(t)))))
        {
            if (!own.TryGetValue(method.Name, out var implementation))
                continue;
            var arguments = string.Join(", ", method.Parameters.Select(p => Escape(p.Name)));
            var call = ExtensionCall(implementation, target, method.IsStatic ? null : "Value", TypeParameterList(method.TypeParameters), arguments);
            // A trait property, implemented by the type's extension property: `public double Area => Value.Area;`.
            var signature = PropertyOf(method) is { } property
                ? $"public {TypeName(TypeSubstitution.Substitute(method.ReturnType, map))} {Escape(property)}"
                : WitnessSignature(method, map, method.IsStatic ? "public static " : "public ", withSelf: false);
            _writer.WriteLine($"{signature} => {call};");
        }
        _writer.CloseBrace();
    }

    /// <summary>
    /// A call to an extend method of a builtin, .NET or runtime type, which is a C# 14 extension member: <c>x.squared()</c>
    /// where its namespace is in scope, and otherwise its static form, <c>global::Geo.ShapesExtensions.squared(x)</c>.
    /// </summary>
    private string ExtensionCall(MethodSymbol method, TypeSymbol receiverType, string? receiver, string typeArguments, string arguments)
    {
        var @namespace = NamespaceOf(method);
        bool sameNamespace = @namespace == _namespace || (@namespace.IsGlobal && _namespace is { IsGlobal: true });
        var name = Escape(method.Name);
        if (sameNamespace)
        {
            // An extension property is read by name: `n.IsEven`, `int.Zero`.
            var member = PropertyOf(method) is { } property ? Escape(property) : $"{name}{typeArguments}({arguments})";
            return receiver is null ? $"{TypeName(receiverType)}.{member}" : $"{receiver}.{member}";
        }

        // The static form's type parameters are the extension block's (Option's T), then the method's.
        if (method.ContainingType is GenericTypeDefinition { IsGeneric: true } && (receiver is null || typeArguments != ""))
        {
            var blockArguments = receiverType.TypeArguments.Select(a => TypeName(a));
            var methodArguments = typeArguments == "" ? [] : typeArguments[1..^1].Split(", ");
            typeArguments = $"<{string.Join(", ", blockArguments.Concat(methodArguments))}>";
        }
        var container = $"global::{Qualified(@namespace, Escape(ExtensionClassName(method)))}";
        var allArguments = receiver is null ? arguments : arguments == "" ? receiver : $"{receiver}, {arguments}";
        return $"{container}.{name}{typeArguments}({allArguments})";
    }
}
