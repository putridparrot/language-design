using System.Text;
using System.Text.Json.Nodes;
using Piper.Binding;
using Piper.Syntax;
using Piper.Text;

namespace Piper.LanguageServer;

/// <summary>Quick fixes: adding the arms a match is missing, each throwing until it's written.</summary>
public static class CodeActions
{
    private const string MissingArmBody = "throw System.NotImplementedException()";

    public static JsonArray Actions(AnalysisResult analysis, SourceText source, TextSpan range, JsonArray diagnostics)
    {
        var actions = new JsonArray();
        foreach (var match in analysis.Semantic.IncompleteMatches.Where(m => m.File == source && m.MissingArms.Count > 0))
        {
            var keyword = match.Syntax.Keyword.Span;
            if (keyword.End < range.Start || range.End < keyword.Start || AddArms(source, match) is not { } edit)
                continue;
            var fixes = new JsonArray();
            foreach (var diagnostic in diagnostics.OfType<JsonObject>().Where(d => d["code"]?.GetValue<string>() == "PIP0230"))
                fixes.Add(diagnostic.DeepClone());
            actions.Add(new JsonObject
            {
                ["title"] = match.MissingArms.Count == 1 ? $"Add the missing arm '{match.MissingArms[0]}'" : "Add the missing match arms",
                ["kind"] = "quickfix",
                ["isPreferred"] = true,
                ["diagnostics"] = fixes,
                ["edit"] = new JsonObject
                {
                    ["changes"] = new JsonObject { [PiperLanguageServer.UriOf(source.FileName)] = new JsonArray(edit) },
                },
            });
        }
        return actions;
    }

    /// <summary>
    /// Replaces the space between the last arm and the match's <c>}</c> with the new arms, one per line, indented as the
    /// existing arms are. Null if the match has no <c>}</c> yet.
    /// </summary>
    private static JsonObject? AddArms(SourceText source, IncompleteMatch match)
    {
        var syntax = match.Syntax;
        int close = syntax.Span.End - 1;
        if (close < 0 || source[close] != '}')
            return null;
        int start = close;
        while (start > 0 && char.IsWhiteSpace(source[start - 1]))
            start--;

        var matchIndent = IndentOf(source, syntax.Span.Start);
        var armIndent = syntax.Arms.IsEmpty ? matchIndent + "    " : IndentOf(source, syntax.Arms[0].Span.Start);
        var text = new StringBuilder();
        if (!syntax.Arms.IsEmpty && source[start - 1] != ',' &&
            syntax.Arms[^1].Body is not (BlockExpressionSyntax or IfExpressionSyntax or MatchExpressionSyntax))
        {
            text.Append(',');
        }
        foreach (var arm in match.MissingArms)
            text.Append('\n').Append(armIndent).Append(arm).Append(" => ").Append(MissingArmBody).Append(',');
        // Keep the `}` where it was if it starts its line, otherwise put it on a line of its own under the `match`.
        text.Append('\n').Append(StartsLine(source, close) ? IndentOf(source, close) : matchIndent);

        return new JsonObject { ["range"] = PiperLanguageServer.Range(source, TextSpan.FromBounds(start, close)), ["newText"] = text.ToString() };
    }

    private static int LineStart(SourceText source, int position)
    {
        while (position > 0 && source[position - 1] != '\n')
            position--;
        return position;
    }

    private static string IndentOf(SourceText source, int position)
    {
        int start = LineStart(source, position), end = start;
        while (end < source.Length && source[end] is ' ' or '\t')
            end++;
        return source.ToString(TextSpan.FromBounds(start, end));
    }

    private static bool StartsLine(SourceText source, int position)
    {
        for (int i = LineStart(source, position); i < position; i++)
        {
            if (!char.IsWhiteSpace(source[i]))
                return false;
        }
        return true;
    }
}
