using System.Collections.Immutable;
using Piper.Binding;
using Piper.Diagnostics;
using Piper.Emit;
using Piper.Lowering;
using Piper.Plugins;
using Piper.Syntax;
using Piper.Text;

namespace Piper;

/// <summary>A generated C# file; <see cref="Name"/> is the Piper file's name with a .cs extension.</summary>
public sealed record GeneratedFile(string Name, string Text);

public sealed record CompilationResult(ImmutableArray<SourceText> Sources, ImmutableArray<Diagnostic> Diagnostics, ImmutableArray<GeneratedFile> Files)
{
    public bool Success => !Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    public IEnumerable<string> FormattedDiagnostics => Diagnostics.Select(d => d.Format());

    /// <summary>Whether the program has a `Main` function (an entry point), so it can be built as an executable.</summary>
    public bool HasMain { get; init; }

    /// <summary>How many `test` blocks the program has.</summary>
    public int TestCount { get; init; }

    /// <summary>What the program references beyond the platform, which running or writing it needs (from <see cref="CompilationOptions.References"/>).</summary>
    public ProgramReferences References { get; init; } = ProgramReferences.None;

    /// <summary>
    /// With <see cref="WireShapeMode.Compare"/>, each recorded shape the program describes (an api's, a database's),
    /// compared with the one recorded earlier, in the order the plugins described them.
    /// </summary>
    public ImmutableArray<ShapeComparison> Shapes { get; init; } = [];

    /// <summary>All generated C#; with several files each is preceded by a <c>// ==== name ====</c> line.</summary>
    public string? CSharp => !Success
        ? null
        : Files.Length == 1 ? Files[0].Text : string.Concat(Files.Select(f => $"// ==== {f.Name} ====\n{f.Text}"));
}

/// <summary>What <see cref="Compilation.Analyze"/> found: diagnostics, syntax, the bound program and its semantic model.</summary>
public sealed record AnalysisResult(
    ImmutableArray<SourceText> Sources,
    ImmutableArray<Diagnostic> Diagnostics,
    ImmutableArray<CompilationUnitSyntax> Units,
    BoundProgram Program,
    SemanticModel Semantic)
{
    /// <summary>The plugins the program was analyzed with, whose keywords the editor offers.</summary>
    public ImmutableArray<IPiperPlugin> Plugins { get; init; } = [];
}

/// <param name="LineDirectives">
/// Write `#line` directives into the C#, so C# compiler errors, stack traces and debuggers point at the `.piper`
/// source. Off by default to keep the generated code easy to read.
/// </param>
/// <param name="IncludeTests">Compile `test` blocks in, for `piper test`; other builds leave them out (but still check them).</param>
public sealed record CompilationOptions(bool LineDirectives = false, bool IncludeTests = false)
{
    /// <summary>The plugins whose keywords the program can use (piper.json's "plugins", loaded by <see cref="PluginLoader"/>).</summary>
    public ImmutableArray<IPiperPlugin> Plugins { get; init; } = [];

    /// <summary>The packages, frameworks and assemblies the program can use beyond the platform (piper.json's, resolved).</summary>
    public ProgramReferences References { get; init; } = ProgramReferences.None;

    /// <summary>
    /// The sources (by <see cref="SourceText.FileName"/>) that come from a shared project (piper.json's "projects"):
    /// compiled in, but without their `test` blocks, which are that project's own.
    /// </summary>
    public ImmutableHashSet<string> SharedFiles { get; init; } = [];

    /// <summary>
    /// Whether the shapes of what crosses the wire (an api's calls and types, in <c>Name.shape.json</c> beside it) are
    /// only checked, as by default, or recorded too, as <c>piper build</c>, <c>run</c> and <c>test</c> do.
    /// </summary>
    public WireShapeMode WireShapes { get; init; } = WireShapeMode.Check;

    /// <summary>
    /// With <see cref="WireShapeMode.Compare"/>, reads the shape recorded earlier by its path (<c>piper check --since</c>
    /// reads it from git at a tag), or null for none; without it, the shapes on disk are compared with.
    /// </summary>
    public Func<string, string?>? ShapeBaseline { get; init; }
}

/// <summary>Runs the whole pipeline over a program's files: lex → parse → bind → lower → emit C#.</summary>
public static class Compilation
{
    public static CompilationResult Compile(IEnumerable<SourceText> sources, CompilationOptions? options = null)
    {
        options ??= new CompilationOptions();
        var files = sources.ToImmutableArray();
        var diagnostics = new DiagnosticBag();
        var plugins = PluginRegistry.Create(options.Plugins, diagnostics);
        var units = WithoutSharedTests(files.Select(source => Parser.ParseCompilationUnit(source, diagnostics, plugins)), options.SharedFiles);

        // Bind even after syntax errors: missing tokens and error nodes bind silently to the
        // error type, so the user still sees genuine semantic errors elsewhere in the file.
        var program = Binder.BindProgram(units, diagnostics, clr: ClrTypeIndex.For(options.References.Assemblies));
        var shapes = new List<ShapeComparison>();
        plugins.Analyze(program, diagnostics, options.WireShapes, options.ShapeBaseline, shapes);

        var sorted = diagnostics
            .OrderBy(d => d.Source is null ? -1 : files.IndexOf(d.Source))
            .ThenBy(d => d.Span.Start)
            .ToImmutableArray();
        if (diagnostics.HasErrors)
            return new CompilationResult(files, sorted, []) { Shapes = [.. shapes] };

        var lowered = Lowerer.Lower(program);
        if (options.IncludeTests)
            lowered = lowered.WithTestsAsFunctions();
        return new CompilationResult(files, sorted, CSharpEmitter.Emit(lowered, options.LineDirectives, options.References.Aliases, [.. options.Plugins.SelectMany(p => p.Providers).Distinct()]))
        {
            HasMain = program.Main is not null,
            TestCount = program.Tests.Length,
            References = options.References,
            Shapes = [.. shapes],
        };
    }

    /// <summary>
    /// Parses and binds without lowering or emitting, recording what editor tooling needs: the syntax trees, the bound
    /// program and a <see cref="SemanticModel"/>. Works on incomplete code, as the parser and binder recover from errors.
    /// </summary>
    public static AnalysisResult Analyze(
        IEnumerable<SourceText> sources, ImmutableArray<IPiperPlugin> plugins = default, ProgramReferences? references = null,
        ImmutableHashSet<string>? sharedFiles = null)
    {
        var files = sources.ToImmutableArray();
        var diagnostics = new DiagnosticBag();
        var registry = PluginRegistry.Create(plugins.IsDefault ? [] : plugins, diagnostics);
        var units = WithoutSharedTests(files.Select(source => Parser.ParseCompilationUnit(source, diagnostics, registry)), sharedFiles ?? []);
        var semantic = new SemanticModel();
        var program = Binder.BindProgram(units, diagnostics, semantic, ClrTypeIndex.For(references?.Assemblies ?? []));
        registry.Analyze(program, diagnostics);
        foreach (var use in registry.KeywordsUsed)
            semantic.AddPluginKeyword(use);
        return new AnalysisResult(files, [.. diagnostics], units, program, semantic) { Plugins = plugins.IsDefault ? [] : plugins };
    }

    /// <summary>The units, with the `test` blocks of shared projects' files left out: those are their own projects' tests.</summary>
    private static ImmutableArray<CompilationUnitSyntax> WithoutSharedTests(IEnumerable<CompilationUnitSyntax> units, ImmutableHashSet<string> shared) =>
        [.. units.Select(unit => shared.Contains(unit.Source.FileName)
            ? unit with { Declarations = [.. unit.Declarations.Where(d => d is not TestDeclarationSyntax)] }
            : unit)];

    public static CompilationResult Compile(SourceText source, CompilationOptions? options = null) => Compile([source], options);

    public static CompilationResult Compile(string text, string fileName = "<input>", CompilationOptions? options = null) =>
        Compile(new SourceText(text, fileName), options);
}
