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
    SemanticModel Semantic);

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
        var units = files.Select(source => Parser.ParseCompilationUnit(source, diagnostics, plugins)).ToImmutableArray();

        // Bind even after syntax errors: missing tokens and error nodes bind silently to the
        // error type, so the user still sees genuine semantic errors elsewhere in the file.
        var program = Binder.BindProgram(units, diagnostics, clr: ClrTypeIndex.For(options.References.Assemblies));
        plugins.Analyze(program, diagnostics);

        var sorted = diagnostics
            .OrderBy(d => d.Source is null ? -1 : files.IndexOf(d.Source))
            .ThenBy(d => d.Span.Start)
            .ToImmutableArray();
        if (diagnostics.HasErrors)
            return new CompilationResult(files, sorted, []);

        var lowered = Lowerer.Lower(program);
        if (options.IncludeTests)
            lowered = lowered.WithTestsAsFunctions();
        return new CompilationResult(files, sorted, CSharpEmitter.Emit(lowered, options.LineDirectives, options.References.Aliases))
        {
            HasMain = program.Main is not null,
            TestCount = program.Tests.Length,
            References = options.References,
        };
    }

    /// <summary>
    /// Parses and binds without lowering or emitting, recording what editor tooling needs: the syntax trees, the bound
    /// program and a <see cref="SemanticModel"/>. Works on incomplete code, as the parser and binder recover from errors.
    /// </summary>
    public static AnalysisResult Analyze(IEnumerable<SourceText> sources, ImmutableArray<IPiperPlugin> plugins = default, ProgramReferences? references = null)
    {
        var files = sources.ToImmutableArray();
        var diagnostics = new DiagnosticBag();
        var registry = PluginRegistry.Create(plugins.IsDefault ? [] : plugins, diagnostics);
        var units = files.Select(source => Parser.ParseCompilationUnit(source, diagnostics, registry)).ToImmutableArray();
        var semantic = new SemanticModel();
        var program = Binder.BindProgram(units, diagnostics, semantic, ClrTypeIndex.For(references?.Assemblies ?? []));
        registry.Analyze(program, diagnostics);
        foreach (var (file, keyword) in registry.KeywordsUsed)
            semantic.AddPluginKeyword(file, keyword.Span);
        return new AnalysisResult(files, [.. diagnostics], units, program, semantic);
    }

    public static CompilationResult Compile(SourceText source, CompilationOptions? options = null) => Compile([source], options);

    public static CompilationResult Compile(string text, string fileName = "<input>", CompilationOptions? options = null) =>
        Compile(new SourceText(text, fileName), options);
}
