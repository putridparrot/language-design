namespace Piper.Binding;

/// <summary>A lexical scope. Names may shadow names from enclosing scopes but not be redeclared in the same scope.</summary>
public sealed class Scope(Scope? parent)
{
    private readonly Dictionary<string, Symbol> _symbols = [];

    public Scope? Parent { get; } = parent;

    public bool TryDeclare(Symbol symbol) => _symbols.TryAdd(symbol.Name, symbol);

    public Symbol? Lookup(string name)
    {
        for (var scope = this; scope is not null; scope = scope.Parent)
        {
            if (scope._symbols.TryGetValue(name, out var symbol))
                return symbol;
        }
        return null;
    }

    public IEnumerable<Symbol> Symbols => _symbols.Values;
}
