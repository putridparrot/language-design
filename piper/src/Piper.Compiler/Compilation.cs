using System.Collections.Immutable;
using System.Text;
using Piper.Binding;
using Piper.Diagnostics;
using Piper.Emit;
using Piper.Lowering;
using Piper.Syntax;
using Piper.Text;

namespace Piper;

public sealed record CompilationResult(SourceText Source, ImmutableArray<Diagnostic> Diagnostics, string? CSharp)
{
    public bool Success => CSharp is not null;

    public IEnumerable<string> FormattedDiagnostics => Diagnostics.Select(d => d.Format(Source));
}

/// <summary>Runs the whole pipeline: lex → parse → bind → lower → emit C#.</summary>
public static class Compilation
{
    public static CompilationResult Compile(SourceText source, string? moduleName = null)
    {
        var diagnostics = new DiagnosticBag();
        var unit = Parser.ParseCompilationUnit(source, diagnostics);

        // Bind even after syntax errors: missing tokens and error nodes bind silently to the
        // error type, so the user still sees genuine semantic errors elsewhere in the file.
        var program = Binder.BindProgram(unit, diagnostics);

        var sorted = diagnostics.OrderBy(d => d.Span.Start).ToImmutableArray();
        if (diagnostics.HasErrors)
            return new CompilationResult(source, sorted, null);

        var lowered = Lowerer.Lower(program);
        var csharp = CSharpEmitter.Emit(lowered, moduleName ?? ModuleNameFromFile(source.FileName));
        return new CompilationResult(source, sorted, csharp);
    }

    public static CompilationResult Compile(string text, string fileName = "<input>", string? moduleName = null) =>
        Compile(new SourceText(text, fileName), moduleName);

    /// <summary>`hello_world.piper` → `Hello_world`; falls back to `Program`.</summary>
    public static string ModuleNameFromFile(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var builder = new StringBuilder();
        foreach (char c in stem)
            builder.Append(char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_');

        if (builder.Length == 0 || builder.ToString().All(c => c == '_'))
            return "Program";
        if (char.IsAsciiDigit(builder[0]))
            builder.Insert(0, '_');
        builder[0] = char.ToUpperInvariant(builder[0]);
        return builder.ToString();
    }
}
