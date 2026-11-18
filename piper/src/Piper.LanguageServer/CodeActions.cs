using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Piper.Binding;
using Piper.Syntax;
using Piper.Text;

namespace Piper.LanguageServer;

/// <summary>Quick fixes: adding the arms a match is missing, each throwing until it's written, and the usings a name needs.</summary>
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
        foreach (var action in AddUsings(analysis, source, range, diagnostics))
            actions.Add(action);
        return actions;
    }

    /// <summary>The names a missing <c>using</c> leaves unknown: a name, a type, or (an extension) method.</summary>
    private static readonly Dictionary<string, Regex> Unresolved = new()
    {
        ["PIP0200"] = new(@"^The name '([A-Za-z_]\w*)' does not exist"),
        ["PIP0211"] = new(@"^The type '([A-Za-z_]\w*)(?:<[^']*)?' does not exist"),
        ["PIP0243"] = new(@"has no method '([A-Za-z_]\w*)'"),
    };

    /// <summary>At most this many namespaces are offered for one name; past that, the name is too common to guess.</summary>
    private const int MostUsings = 6;

    /// <summary>
    /// "Add 'using System.Text;'" for each name in the range that doesn't resolve but would with a using: a .NET type
    /// (or, for a method, an extension method) the program references, or a public Piper declaration in another of its
    /// namespaces.
    /// </summary>
    private static IEnumerable<JsonObject> AddUsings(AnalysisResult analysis, SourceText source, TextSpan range, JsonArray diagnostics)
    {
        if (analysis.Units.FirstOrDefault(u => u.Source == source) is not { } unit || analysis.Program.FileNamespaces.IsDefaultOrEmpty)
            return [];
        var imported = unit.Usings.Select(u => u.Name.Text).ToHashSet();
        if (unit.Namespace is { } own)
            imported.Add(own.Name.Text);
        var piperNamespaces = analysis.Program.FileNamespaces.Where(ns => !ns.IsGlobal).Distinct().ToList();
        var clr = analysis.Program.FileNamespaces[0].Clr;

        var actions = new List<JsonObject>();
        var offered = new HashSet<string>();
        foreach (var diagnostic in analysis.Diagnostics.Where(d => d.Source == source && d.Span.End >= range.Start && range.End >= d.Span.Start))
        {
            if (!Unresolved.TryGetValue(diagnostic.Code, out var pattern) || pattern.Match(diagnostic.Message) is not { Success: true } match)
                continue;
            var name = match.Groups.Values.Skip(1).First(g => g.Success).Value;
            var isMethod = diagnostic.Code == "PIP0243";
            var candidates = (isMethod ? clr.NamespacesWithExtension(name) : clr.NamespacesWithType(name))
                .Concat(piperNamespaces.Where(ns => ns.Members.LookupLocal(name) is not null).Select(ns => ns.FullName))
                .Where(ns => !imported.Contains(ns))
                .Distinct()
                .OrderBy(ns => ns.StartsWith("System", StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(ns => ns.Count(c => c == '.'))
                .ThenBy(ns => ns, StringComparer.Ordinal)
                .ToList();
            if (candidates.Count is 0 or > MostUsings)
                continue;

            var fixes = new JsonArray();
            foreach (var lsp in diagnostics.OfType<JsonObject>().Where(d => d["code"]?.GetValue<string>() == diagnostic.Code))
                fixes.Add(lsp.DeepClone());
            foreach (var @namespace in candidates.Where(ns => offered.Add(ns)))
            {
                actions.Add(new JsonObject
                {
                    ["title"] = $"Add 'using {@namespace};'",
                    ["kind"] = "quickfix",
                    ["isPreferred"] = candidates.Count == 1,
                    ["diagnostics"] = fixes.DeepClone(),
                    ["edit"] = new JsonObject
                    {
                        ["changes"] = new JsonObject { [PiperLanguageServer.UriOf(source.FileName)] = new JsonArray(AddUsing(source, unit, @namespace)) },
                    },
                });
            }
        }
        return actions;
    }

    /// <summary>
    /// Inserts <c>using <paramref name="namespace"/>;</c> among the file's usings, in order if they're in order (after
    /// them if not); after the namespace declaration if there are none; otherwise at the top of the file.
    /// </summary>
    private static JsonObject AddUsing(SourceText source, CompilationUnitSyntax unit, string @namespace)
    {
        var line = $"using {@namespace};";
        if (!unit.Usings.IsEmpty)
        {
            var names = unit.Usings.Select(u => u.Name.Text).ToList();
            var sorted = names.SequenceEqual(names.Order(StringComparer.Ordinal));
            var before = sorted ? unit.Usings.FirstOrDefault(u => string.CompareOrdinal(u.Name.Text, @namespace) > 0) : null;
            return before is not null
                ? Insert(source, LineStart(source, before.Span.Start), IndentOf(source, before.Span.Start) + line + "\n")
                : Insert(source, unit.Usings[^1].Span.End, "\n" + IndentOf(source, unit.Usings[^1].Span.Start) + line);
        }
        if (unit.Namespace is { } declared)
            return Insert(source, declared.Span.End, "\n\n" + line);
        // Past any comments heading the file, which usually say what it is.
        int start = 0;
        while (start < source.Length && source.ToString(TextSpan.FromBounds(start, Math.Min(source.Length, start + 2))) == "//")
        {
            while (start < source.Length && source[start] != '\n')
                start++;
            if (start < source.Length)
                start++;
        }
        return Insert(source, start, line + "\n\n");
    }

    private static JsonObject Insert(SourceText source, int position, string text) =>
        new() { ["range"] = PiperLanguageServer.Range(source, new TextSpan(position, 0)), ["newText"] = text };

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
