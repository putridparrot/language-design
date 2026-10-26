using System.Collections.Immutable;
using Piper.Binding;
using Piper.Diagnostics;
using Piper.Syntax;

namespace Piper.Plugins;

/// <summary>The keywords that a program's plugins add, checked so each belongs to one plugin and isn't one of Piper's.</summary>
public sealed class PluginRegistry
{
    private readonly Dictionary<string, (IPiperPlugin Plugin, PluginKeyword Keyword)> _keywords;

    private readonly ImmutableArray<IPiperPlugin> _plugins;

    private readonly List<(IPiperPlugin Plugin, PluginExpansion Expansion)> _expansions = [];

    private PluginRegistry(Dictionary<string, (IPiperPlugin, PluginKeyword)> keywords, ImmutableArray<IPiperPlugin> plugins) =>
        (_keywords, _plugins) = (keywords, plugins);

    /// <summary>No plugins. Shared, so nothing is recorded in it.</summary>
    public static PluginRegistry Empty { get; } = new([], []);

    public bool IsEmpty => _keywords.Count == 0;

    internal PluginKeyword KeywordFor(string text) => _keywords[text].Keyword;

    /// <summary>Each plugin keyword the source used (<c>services</c>, <c>endpoints</c>), for the editor to colour.</summary>
    public IEnumerable<(Text.SourceText File, Token Keyword)> KeywordsUsed => _expansions.Select(e => (e.Expansion.File, e.Expansion.KeywordToken));

    /// <summary>Remembers an expansion, for the plugin's analysis once the program is bound.</summary>
    internal void Record(IPiperPlugin plugin, PluginExpansion expansion)
    {
        if (!ReferenceEquals(this, Empty))
            _expansions.Add((plugin, expansion));
    }

    /// <summary>
    /// Runs each plugin's <see cref="IPiperPlugin.Analyze"/> over the bound <paramref name="program"/>. A plugin that
    /// throws is reported, and the others still run.
    /// </summary>
    public void Analyze(BoundProgram program, DiagnosticBag diagnostics)
    {
        foreach (var plugin in _plugins)
        {
            var analysis = new PluginAnalysis(plugin, program, [.. _expansions.Where(e => e.Plugin == plugin).Select(e => e.Expansion)], diagnostics);
            try
            {
                plugin.Analyze(analysis);
            }
            catch (Exception exception)
            {
                diagnostics.CurrentSource = null;
                diagnostics.ReportPluginAnalysisFailed(plugin.Name, exception.Message);
            }
        }
    }

    /// <summary>Registers <paramref name="plugins"/>' keywords, reporting (and skipping) any that clash.</summary>
    public static PluginRegistry Create(ImmutableArray<IPiperPlugin> plugins, DiagnosticBag diagnostics)
    {
        var keywords = new Dictionary<string, (IPiperPlugin, PluginKeyword)>();
        foreach (var plugin in plugins)
        {
            foreach (var keyword in plugin.Keywords)
            {
                if (!IsWord(keyword.Text) || SyntaxFacts.GetKeywordKind(keyword.Text) is not null ||
                    SyntaxFacts.ReservedWords.Contains(keyword.Text) || keyword.Text is "async" or "await" or "yield" or "ref")
                    diagnostics.ReportPluginKeywordInvalid(plugin.Name, keyword.Text);
                else if (keywords.TryGetValue(keyword.Text, out var existing))
                    diagnostics.ReportPluginKeywordClash(keyword.Text, existing.Item1.Name, plugin.Name);
                else
                    keywords.Add(keyword.Text, (plugin, keyword));
            }
        }
        return new PluginRegistry(keywords, plugins);
    }

    /// <summary>Whether <paramref name="text"/> lexes as a name, as a keyword must.</summary>
    private static bool IsWord(string text) =>
        text.Length > 0 && (char.IsAsciiLetter(text[0]) || text[0] == '_') && text.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>The plugin keyword <paramref name="text"/> of type <typeparamref name="T"/>, if there is one.</summary>
    public (IPiperPlugin Plugin, T Keyword)? Find<T>(string text) where T : PluginKeyword =>
        _keywords.TryGetValue(text, out var entry) && entry.Keyword is T keyword ? (entry.Plugin, keyword) : null;
}
