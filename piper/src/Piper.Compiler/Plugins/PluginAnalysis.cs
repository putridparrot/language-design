using System.Collections.Immutable;
using Piper.Binding;
using Piper.Diagnostics;
using Piper.Syntax;
using Piper.Text;

namespace Piper.Plugins;

/// <summary>
/// One use of a plugin's keyword: where it was, and the syntax the plugin rewrote it into. The binder uses those very
/// syntax nodes, so an analyzer can find what they became: <c>program.Types.OfType&lt;EnumSymbol&gt;()</c> whose
/// <c>Declaration</c> is one of <see cref="Result"/>.
/// </summary>
public sealed record PluginExpansion(PluginKeyword Keyword, Token KeywordToken, SourceText File, ImmutableArray<SyntaxNode> Result);

/// <summary>
/// What a plugin's <see cref="IPiperPlugin.Analyze"/> sees once the program is type-checked: the bound program, the
/// plugin's own expansions, and a way to report what it finds. Analysis only reports; it can't change the program.
/// This is where checks that need types belong, such as "an actor's messages should be immutable", which syntax alone
/// can't tell.
/// </summary>
public sealed class PluginAnalysis
{
    private readonly DiagnosticBag _diagnostics;

    internal PluginAnalysis(IPiperPlugin plugin, BoundProgram program, ImmutableArray<PluginExpansion> expansions, DiagnosticBag diagnostics)
    {
        Plugin = plugin;
        Program = program;
        Expansions = expansions;
        _diagnostics = diagnostics;
    }

    public IPiperPlugin Plugin { get; }

    /// <summary>The type-checked program: its types, functions and methods, with their symbols and bound bodies.</summary>
    public BoundProgram Program { get; }

    /// <summary>Each use of this plugin's keywords, in source order.</summary>
    public ImmutableArray<PluginExpansion> Expansions { get; }

    /// <summary>Reports an error at <paramref name="span"/> in <paramref name="file"/>; the message is followed by the plugin's name.</summary>
    public void ReportError(SourceText file, TextSpan span, string message)
    {
        _diagnostics.CurrentSource = file;
        _diagnostics.ReportPluginError(span, Plugin.Name, message);
    }

    /// <summary>Reports a warning, which doesn't stop the program being built.</summary>
    public void ReportWarning(SourceText file, TextSpan span, string message)
    {
        _diagnostics.CurrentSource = file;
        _diagnostics.ReportPluginWarning(span, Plugin.Name, message);
    }
}
