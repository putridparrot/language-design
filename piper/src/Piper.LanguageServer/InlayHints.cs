using System.Text.Json.Nodes;
using Piper.Binding;
using Piper.Syntax;
using Piper.Text;

namespace Piper.LanguageServer;

/// <summary>Inlay hints: the inferred type after each variable declared without one (<c>let total</c><i>: double</i>).</summary>
public static class InlayHints
{
    public static JsonArray Hints(AnalysisResult analysis, SourceText source, TextSpan range)
    {
        var hints = new JsonArray();
        if (analysis.Units.FirstOrDefault(u => u.Source == source) is not { } unit)
            return hints;

        var names = SyntaxWalker.DescendantsAndSelf(unit).SelectMany(UntypedNames).Where(n => !n.IsMissing && range.Start <= n.Span.Start && n.Span.End <= range.End);
        foreach (var name in names.OrderBy(n => n.Span.Start))
        {
            if (analysis.Semantic.OccurrenceAt(source, name.Span.Start) is not { IsDeclaration: true, Symbol: VariableSymbol variable } occurrence ||
                occurrence.Span != name.Span || variable.Type == TypeSymbol.Error)
            {
                continue;
            }
            hints.Add(new JsonObject
            {
                ["position"] = PiperLanguageServer.Range(source, name.Span)["end"]!.DeepClone(),
                ["label"] = $": {variable.Type}",
                ["kind"] = 1,
            });
        }
        return hints;
    }

    /// <summary>The names a node declares without writing their type.</summary>
    private static IEnumerable<Token> UntypedNames(SyntaxNode node) => node switch
    {
        VariableDeclarationSyntax { Type: null } declaration => [declaration.Name],
        UsingStatementSyntax { Name: { } name, Type: null } => [name],
        ForStatementSyntax { Deconstruction: null } loop => [loop.Variable],
        LambdaParameterSyntax { Type: null } parameter => [parameter.Name],
        SingleDesignationSyntax { IsDiscard: false } designation => [designation.Name],
        OutArgumentSyntax { Declaration: not null } argument => [argument.Name],
        CatchClauseSyntax { Name: { } name, Type: null } => [name],
        _ => [],
    };
}
