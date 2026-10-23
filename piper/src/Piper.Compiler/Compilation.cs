using System.Collections.Immutable;
using Piper.Binding;
using Piper.Diagnostics;
using Piper.Emit;
using Piper.Lowering;
using Piper.Syntax;
using Piper.Text;

namespace Piper;

/// <summary>A generated C# file; <see cref="Name"/> is the Piper file's name with a .cs extension.</summary>
public sealed record GeneratedFile(string Name, string Text);

public sealed record CompilationResult(ImmutableArray<SourceText> Sources, ImmutableArray<Diagnostic> Diagnostics, ImmutableArray<GeneratedFile> Files)
{
    public bool Success => !Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    public IEnumerable<string> FormattedDiagnostics => Diagnostics.Select(d => d.Format());

    /// <summary>Whether the program has a `main` function (an entry point), so it can be built as an executable.</summary>
    public bool HasMain { get; init; }

    /// <summary>All generated C#; with several files each is preceded by a <c>// ==== name ====</c> line.</summary>
    public string? CSharp => !Success
        ? null
        : Files.Length == 1 ? Files[0].Text : string.Concat(Files.Select(f => $"// ==== {f.Name} ====\n{f.Text}"));
}

/// <param name="LineDirectives">
/// Write `#line` directives into the C#, so C# compiler errors, stack traces and debuggers point at the `.piper`
/// source. Off by default to keep the generated code easy to read.
/// </param>
public sealed record CompilationOptions(bool LineDirectives = false);

/// <summary>Runs the whole pipeline over a program's files: lex → parse → bind → lower → emit C#.</summary>
public static class Compilation
{
    public static CompilationResult Compile(IEnumerable<SourceText> sources, CompilationOptions? options = null)
    {
        options ??= new CompilationOptions();
        var files = sources.ToImmutableArray();
        var diagnostics = new DiagnosticBag();
        var units = files.Select(source => Parser.ParseCompilationUnit(source, diagnostics)).ToImmutableArray();

        // Bind even after syntax errors: missing tokens and error nodes bind silently to the
        // error type, so the user still sees genuine semantic errors elsewhere in the file.
        var program = Binder.BindProgram(units, diagnostics);

        var sorted = diagnostics
            .OrderBy(d => d.Source is null ? -1 : files.IndexOf(d.Source))
            .ThenBy(d => d.Span.Start)
            .ToImmutableArray();
        if (diagnostics.HasErrors)
            return new CompilationResult(files, sorted, []);

        var lowered = Lowerer.Lower(program);
        return new CompilationResult(files, sorted, CSharpEmitter.Emit(lowered, options.LineDirectives)) { HasMain = program.Main is not null };
    }

    public static CompilationResult Compile(SourceText source, CompilationOptions? options = null) => Compile([source], options);

    public static CompilationResult Compile(string text, string fileName = "<input>", CompilationOptions? options = null) =>
        Compile(new SourceText(text, fileName), options);
}
