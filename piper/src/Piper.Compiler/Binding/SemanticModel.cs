using Piper.Text;

namespace Piper.Binding;

/// <summary>A name in the source that declares or refers to <paramref name="Symbol"/> (a definition, never an instantiation).</summary>
public sealed record SymbolOccurrence(SourceText File, TextSpan Span, Symbol Symbol, bool IsDeclaration);

/// <summary>An expression's type, and the scope it was bound in (what names were visible there).</summary>
public sealed record ExpressionInfo(SourceText File, TextSpan Span, TypeSymbol Type, Scope Scope);

/// <summary>
/// What the binder learned about the source, for editor tooling: which symbol each name means, where symbols are declared,
/// and each expression's type and scope. Filled only when <see cref="Compilation.Analyze"/> asks for it.
/// </summary>
public sealed class SemanticModel
{
    private readonly List<SymbolOccurrence> _occurrences = [];
    private readonly List<ExpressionInfo> _expressions = [];

    public IReadOnlyList<SymbolOccurrence> Occurrences => _occurrences;

    public IReadOnlyList<ExpressionInfo> Expressions => _expressions;

    internal void AddOccurrence(SourceText file, TextSpan span, Symbol symbol, bool isDeclaration)
    {
        if (span.Length == 0)
            return;
        // A reference to `Box<int>` is to `Box`.
        if (symbol is TypeSymbol type && type.OriginalDefinition != type)
            symbol = type.OriginalDefinition;
        _occurrences.Add(new SymbolOccurrence(file, span, symbol, isDeclaration));
    }

    internal void AddExpression(SourceText file, TextSpan span, TypeSymbol type, Scope scope) =>
        _expressions.Add(new ExpressionInfo(file, span, type, scope));

    /// <summary>The name at <paramref name="position"/>, including just after its last character.</summary>
    public SymbolOccurrence? OccurrenceAt(SourceText file, int position) =>
        _occurrences.Where(o => o.File == file && o.Span.Start <= position && position <= o.Span.End)
            .OrderBy(o => o.Span.Length)
            .FirstOrDefault();

    public SymbolOccurrence? DeclarationOf(Symbol symbol) =>
        _occurrences.FirstOrDefault(o => o.IsDeclaration && o.Symbol == symbol);

    public IEnumerable<SymbolOccurrence> ReferencesTo(Symbol symbol) =>
        _occurrences.Where(o => o.Symbol == symbol).DistinctBy(o => (o.File, o.Span));

    /// <summary>The innermost expression containing <paramref name="position"/>.</summary>
    public ExpressionInfo? ExpressionAt(SourceText file, int position) =>
        _expressions.Where(e => e.File == file && e.Span.Start <= position && position <= e.Span.End)
            .OrderBy(e => e.Span.Length)
            .FirstOrDefault();

    /// <summary>The smallest expression ending at <paramref name="position"/>: the receiver in <c>point.|</c>.</summary>
    public ExpressionInfo? ExpressionEndingAt(SourceText file, int position) =>
        _expressions.Where(e => e.File == file && e.Span.End == position && e.Span.Length > 0)
            .OrderBy(e => e.Span.Length)
            .FirstOrDefault();

    /// <summary>
    /// The scope at <paramref name="position"/>: that of the innermost expression around it, or failing that the nearest
    /// one before it in the file (so a blank line in a block sees the block's locals).
    /// </summary>
    public Scope? ScopeAt(SourceText file, int position) =>
        ExpressionAt(file, position)?.Scope ??
        _expressions.Where(e => e.File == file && e.Span.End <= position)
            .OrderByDescending(e => e.Span.End)
            .ThenBy(e => e.Span.Length)
            .FirstOrDefault()?.Scope;
}
