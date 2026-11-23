using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Piper.Binding;

/// <summary>What a build may assume calls a program's declarations, besides its own code.</summary>
public enum ProgramRoots
{
    /// <summary>
    /// Anything not private: C# compiled into the same assembly (a Piper.Build project) can call it. Only private
    /// declarations the tests alone use are left out. The default, as a compilation doesn't know what it's for.
    /// </summary>
    Visible,

    /// <summary>A library's public declarations (<c>public fn</c>), which other assemblies call.</summary>
    Exported,

    /// <summary>An executable's <c>Main</c>, and nothing else: <c>piper run</c>, <c>piper build --exe</c>.</summary>
    Main,
}

/// <summary>
/// Leaves out of a build the declarations only its <c>test</c> blocks use: functions, records, enums, classes, traits
/// and methods that the program itself (from its roots: <see cref="ProgramRoots"/>) never reaches. A test's helper
/// then costs the binary nothing.
///
/// <para>
/// What's reached is found by following everything a declaration holds: its body's calls and constructions, the types
/// of its signature and fields, the traits a type implements, the functions used as values. A declaration the tests
/// don't reach at all is kept as it is, used or not (only test-only code goes, so dead code stays as it did), and so is
/// everything it reaches. Declarations .NET may find by reflection are roots: any with attributes
/// (<c>[ModuleInitializer]</c>, a framework's), and types implementing traits for builtin or .NET types. Of a type
/// kept, only its plain methods can go: not operators, overrides, accessors, constructors, trait implementations, or
/// those C# calls by name (<c>ToString</c>, <c>Parse</c>, <c>GetEnumerator</c>, …).
/// </para>
/// </summary>
public static class TestOnlyCode
{
    /// <summary>
    /// <paramref name="program"/> (lowered) without what only its tests use. A program without tests is returned as it is.
    /// </summary>
    public static BoundProgram Remove(BoundProgram program, ProgramRoots roots)
    {
        if (program.Tests.IsEmpty)
            return program;
        var graph = new Graph(program);
        var fromTests = graph.Reach(program.Tests.SelectMany(t => t.Generators.Prepend(t.Function)));
        if (fromTests.Count == 0)
            return program;

        // Everything the tests don't reach is kept, and so is what a root reaches.
        var starts = graph.Declarations.Where(d => !fromTests.Contains(d) || IsRoot(d, program, roots))
            .Concat(program.ExternalConformances.SelectMany(c => new object[] { c.Target, c.Trait }));
        var kept = graph.Reach(starts, includeStarts: true);
        var removed = fromTests.Where(d => !kept.Contains(d)).ToHashSet(ReferenceEqualityComparer.Instance);
        if (removed.Count == 0)
            return program;

        bool Gone(Symbol symbol) => removed.Contains(symbol) ||
            symbol is MethodSymbol method && removed.Contains(method.ContainingType.OriginalDefinition);
        return new BoundProgram(
            program.Files, program.FileNamespaces,
            [.. program.Types.Where(t => !removed.Contains(t))],
            program.BuiltinTypes,
            [.. program.Functions.Where(f => !removed.Contains(f.Symbol))],
            [.. program.Methods.Where(m => !Gone(m.Symbol))],
            program.Main)
        {
            ExternalConformances = program.ExternalConformances,
            Constants = program.Constants,
            Tests = program.Tests,
            OmittedMethods = [.. program.Methods.Select(m => (MethodSymbol)m.Symbol).Where(Gone)],
        };
    }

    private static bool IsRoot(object declaration, BoundProgram program, ProgramRoots roots)
    {
        if (declaration is not Symbol symbol)
            return false;
        if (ReferenceEquals(symbol, program.Main) || !symbol.Attributes.IsEmpty)
            return true;
        return roots switch
        {
            ProgramRoots.Visible => !symbol.IsPrivate,
            ProgramRoots.Exported => symbol.IsExported,
            _ => false,
        };
    }

    /// <summary>Names C# (or .NET) calls by name rather than through a call Piper binds: such methods are always kept.</summary>
    private static readonly HashSet<string> CalledByName =
    [
        "ToString", "Equals", "GetHashCode", "CompareTo", "Parse", "TryParse", "GetEnumerator", "GetAsyncEnumerator",
        "MoveNext", "MoveNextAsync", "Current", "Dispose", "DisposeAsync", "GetAwaiter", "Deconstruct", "Add",
        "Invoke", "PrintMembers",
    ];

    /// <summary>A method that can be left out on its own: one only code calls, and Piper binds every call to.</summary>
    private static bool IsPlain(MethodSymbol method, BoundProgram program) =>
        method.ContainingType is not TraitSymbol &&
        !method.IsOperator && !method.IsOverride && !method.IsVirtual && !method.IsAbstract && !method.IsHidden && !method.IsProtected &&
        method.Attributes.IsEmpty &&
        !CalledByName.Contains(method.Name) &&
        !ImplementsTrait(method, program);

    /// <summary>Whether a method has the name of one of its type's traits' methods (its own, or one a builtin or .NET type implements).</summary>
    private static bool ImplementsTrait(MethodSymbol method, BoundProgram program)
    {
        var traits = method.ContainingType.OriginalDefinition is NamedTypeDefinition named ? named.Traits.ToList() : [];
        traits.AddRange(program.ExternalConformances.Where(c => ReferenceEquals(c.Target.OriginalDefinition, method.ContainingType.OriginalDefinition)).Select(c => c.Trait));
        var seen = new HashSet<TraitSymbol>();
        var pending = new Stack<TypeSymbol>(traits);
        while (pending.TryPop(out var trait))
        {
            if (trait.TraitDefinition is not { } definition || !seen.Add(definition))
                continue;
            if (definition.Methods.Any(m => m.Name == method.Name))
                return true;
            foreach (var parent in definition.Parents)
                pending.Push(parent);
        }
        return false;
    }

    /// <summary>The program's declarations, and what each reaches.</summary>
    private sealed class Graph
    {
        private readonly Dictionary<FunctionSymbol, BoundFunction> _functions;
        private readonly Dictionary<MethodSymbol, BoundFunction> _methods;
        private readonly HashSet<TypeSymbol> _types;
        private readonly BoundProgram _program;

        public Graph(BoundProgram program)
        {
            _program = program;
            _functions = program.Functions.ToDictionary(f => f.Symbol);
            _methods = program.Methods.ToDictionary(m => (MethodSymbol)m.Symbol);
            _types = [.. program.Types];
            Declarations =
            [
                .. program.Types,
                .. program.Functions.Select(f => f.Symbol),
                .. _methods.Keys.Where(m => IsPlain(m, program)),
            ];
        }

        /// <summary>What can be left out: the program's types and functions, and its types' plain methods.</summary>
        public List<Symbol> Declarations { get; }

        /// <summary>
        /// The declarations reached from <paramref name="starts"/>: the program's types, functions and plain methods
        /// that they use, directly or through what those use. With <paramref name="includeStarts"/>, the starts too.
        /// </summary>
        public HashSet<object> Reach(IEnumerable<object> starts, bool includeStarts = false)
        {
            var reached = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var pending = new Stack<object>();
            foreach (var start in starts)
            {
                if (includeStarts)
                    Mark(start, reached);
                pending.Push(start);
            }
            while (pending.TryPop(out var current))
            {
                if (!current.GetType().IsValueType && !visited.Add(current))
                    continue;
                switch (current)
                {
                    case string or NamespaceSymbol:
                        continue;
                    case MethodSymbol method:
                        Mark(method, reached);
                        if (_methods.TryGetValue(method, out var methodBody))
                            pending.Push(methodBody.Body);
                        break;
                    case FunctionSymbol function:
                        Mark(function, reached);
                        if (_functions.TryGetValue(function, out var body))
                            pending.Push(body.Body);
                        break;
                    case TypeSymbol type:
                        var definition = type.OriginalDefinition;
                        if (!ReferenceEquals(definition, type))
                            pending.Push(definition);
                        Mark(definition, reached);
                        // A type keeps the methods that can't go on their own; its plain ones are reached by their callers.
                        if (definition is NamedTypeDefinition named && ReferenceEquals(definition, type))
                        {
                            foreach (var method in named.Methods.Where(m => !IsPlain(m, _program)))
                                pending.Push(method);
                            if (named.CompareTo is { } compare)
                                pending.Push(compare);
                        }
                        break;
                    case BoundFunction function:
                        pending.Push(function.Symbol);
                        pending.Push(function.Body);
                        continue;
                    case ITuple tuple:
                        for (int i = 0; i < tuple.Length; i++)
                        {
                            if (tuple[i] is { } element)
                                pending.Push(element);
                        }
                        continue;
                    case IEnumerable when current.GetType().GetProperty("IsDefault")?.GetValue(current) is true:
                        continue;
                    case IEnumerable items:
                        foreach (var item in items)
                        {
                            if (item is not null)
                                pending.Push(item);
                        }
                        continue;
                }
                if (current is Scope || current.GetType().Namespace != typeof(Symbol).Namespace)
                    continue;
                foreach (var field in Stored.GetOrAdd(current.GetType(), StoredFields))
                {
                    if (field.GetValue(current) is { } value)
                        pending.Push(value);
                }
            }
            return reached;
        }

        private void Mark(object declaration, HashSet<object> reached)
        {
            if (_declarations.Contains(declaration))
                reached.Add(declaration);
        }

        private HashSet<object> _declarations => field ??= new HashSet<object>(Declarations, ReferenceEqualityComparer.Instance);
    }

    private static readonly ConcurrentDictionary<Type, FieldInfo[]> Stored = new();

    /// <summary>
    /// What a binder object stores (its fields, and its base types': auto-properties' backing fields, captured
    /// constructor parameters), rather than what its properties compute, as following a computed one could make new
    /// symbols for ever (a tuple's <c>Unnamed</c>). Not its file, namespace, syntax or scopes, which lead to everything
    /// declared near it; and not a type's methods, which are reached by their callers (<see cref="Graph.Reach"/>).
    /// </summary>
    private static FieldInfo[] StoredFields(Type type)
    {
        var fields = new List<FieldInfo>();
        for (var current = type; current is not null && current != typeof(object); current = current.BaseType)
        {
            fields.AddRange(current.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(f => !Skipped.Contains(MemberName(f.Name))));
        }
        return [.. fields];
    }

    /// <summary>The property or parameter a field stores: <c>Name</c> for <c>&lt;Name&gt;k__BackingField</c>, <c>name</c> for a captured <c>&lt;name&gt;P</c>.</summary>
    private static string MemberName(string field) =>
        field.StartsWith('<') && field.IndexOf('>') is > 1 and var end ? field[1..end] : field;

    private static readonly HashSet<string> Skipped =
    [
        "DeclaringFile", "Namespace", "Declaration", "declaration", "Documentation", "SourceSpan",
        "MemberScope", "StaticScope", "MemberScopes", "_methods",
        // A generic definition's constructions (List<> has List<Order>, List<Seed>, …), which use it, not it them.
        "_instantiations",
    ];
}
