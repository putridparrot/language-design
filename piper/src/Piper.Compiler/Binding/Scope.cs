namespace Piper.Binding;

/// <summary>
/// A lexical scope. Names may shadow names from enclosing scopes but not be redeclared in the same scope.
/// A scope may also have a fallback, consulted when a name isn't declared directly (used for imports).
/// </summary>
public sealed class Scope
{
    private readonly Dictionary<string, Symbol> _symbols;
    private readonly Func<string, Symbol?>? _fallback;

    public Scope(Scope? parent)
        : this(parent, [], null)
    {
    }

    private Scope(Scope? parent, Dictionary<string, Symbol> symbols, Func<string, Symbol?>? fallback)
    {
        Parent = parent;
        _symbols = symbols;
        _fallback = fallback;
    }

    /// <summary>
    /// A scope that shares <paramref name="source"/>'s symbols but has its own parent, so each file can see
    /// a namespace's members through a chain that continues with that file's own imports.
    /// </summary>
    public static Scope View(Scope source, Scope? parent) => new(parent, source._symbols, source._fallback);

    public static Scope WithFallback(Scope? parent, Func<string, Symbol?> fallback) => new(parent, [], fallback);

    public Scope? Parent { get; }

    public bool TryDeclare(Symbol symbol) => _symbols.TryAdd(symbol.Name, symbol);

    /// <summary>Looks only in this scope (and its fallback), not in enclosing scopes.</summary>
    public Symbol? LookupLocal(string name) =>
        _symbols.TryGetValue(name, out var symbol) ? symbol : _fallback?.Invoke(name);

    public Symbol? Lookup(string name)
    {
        for (var scope = this; scope is not null; scope = scope.Parent)
        {
            if (scope.LookupLocal(name) is { } symbol)
                return symbol;
        }
        return null;
    }

    public IEnumerable<Symbol> Symbols => _symbols.Values;
}
