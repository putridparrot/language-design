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
public sealed record PluginExpansion(PluginKeyword Keyword, Token KeywordToken, SourceText File, ImmutableArray<SyntaxNode> Result)
{
    /// <summary>The words special to the construct that it read (<see cref="PluginParser.ReadWord"/>), such as `on`.</summary>
    public ImmutableArray<Token> Words { get; init; } = [];

    /// <summary>
    /// What the plugin noted while reading the construct (<see cref="PluginParser.Data"/>), for its analysis: the
    /// policy names an endpoint uses, say, rather than finding them again in the syntax it wrote.
    /// </summary>
    public object? Data { get; init; }
}

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

    /// <summary>Whether <see cref="CheckWireShape"/> only checks, or records the shape too.</summary>
    public WireShapeMode ShapeMode { get; init; } = WireShapeMode.Check;

    /// <summary>
    /// Checks what <paramref name="name"/> answers over the wire against the shape recorded beside
    /// <paramref name="file"/>, <c>name.shape.json</c>, so a change that breaks callers built against it (a client
    /// already installed, a pod not yet updated) is an error at <paramref name="span"/>, not a failure in production.
    /// As <see cref="ShapeMode"/> says, it then records the shape: when there's none yet, or the change is compatible
    /// (a call or optional field added), or when breaking changes are accepted. A file not on disk has no shape, and a
    /// program with errors is neither compared nor recorded until they're fixed.
    /// </summary>
    public void CheckWireShape(SourceText file, TextSpan span, string name, IEnumerable<WireOperation> operations) =>
        CheckWireShape(file, span, name, operations, replayed: false);

    /// <summary>
    /// The same for a contract whose "callers" are instances already running, replaying what they recorded (a workflow's
    /// steps), so its breaking changes say so.
    /// </summary>
    public void CheckWireShape(SourceText file, TextSpan span, string name, IEnumerable<WireOperation> operations, bool replayed)
    {
        // A program with errors has types that couldn't be bound: compared, they'd look like changes; recorded, wrong.
        if (!File.Exists(file.FileName) || _diagnostics.HasErrors)
            return;
        var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file.FileName))!, $"{name}.shape.json");
        var now = WireShapes.Describe(name, operations);
        var text = WireShapes.Write(now);

        string? recorded = File.Exists(path) ? File.ReadAllText(path) : null;
        if (recorded is not null && ShapeMode != WireShapeMode.AcceptBreaking)
        {
            System.Text.Json.Nodes.JsonObject? before;
            try
            {
                before = System.Text.Json.Nodes.JsonNode.Parse(recorded) as System.Text.Json.Nodes.JsonObject;
            }
            catch (System.Text.Json.JsonException)
            {
                before = null;
            }
            if (before is null)
            {
                ReportError(file, span, $"{Path.GetFileName(path)} isn't a shape Piper can read; delete it to record {name}'s shape afresh.");
                return;
            }
            var breaking = WireShapes.Breaking(before, now, replayed);
            foreach (var change in breaking)
            {
                ReportError(file, span,
                    $"A breaking change to {name}: {change}. If it's meant, build with --accept-breaking, which records the new shape in {Path.GetFileName(path)}.");
            }
            if (!breaking.IsEmpty)
                return;
        }
        if (ShapeMode != WireShapeMode.Check && recorded?.ReplaceLineEndings("\n") != text)
            File.WriteAllText(path, text);
    }

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
