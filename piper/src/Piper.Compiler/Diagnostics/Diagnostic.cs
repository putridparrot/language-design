using Piper.Text;

namespace Piper.Diagnostics;

public enum DiagnosticSeverity
{
    Error,
    Warning,
}

/// <summary>A diagnostic; <see cref="Source"/> is the file it was reported in (programs can span several).</summary>
public sealed record Diagnostic(string Code, DiagnosticSeverity Severity, TextSpan Span, string Message)
{
    public SourceText? Source { get; init; }

    /// <summary>Formats as <c>file(line,col): error PIP0000: message</c>, the format MSBuild and editors understand.</summary>
    public string Format()
    {
        var severity = Severity == DiagnosticSeverity.Error ? "error" : "warning";
        if (Source is null)
            return $"{severity} {Code}: {Message}";
        var (line, column) = Source.GetLineColumn(Span.Start);
        return $"{Source.FileName}({line},{column}): {severity} {Code}: {Message}";
    }

    public override string ToString() => $"{Code} {Span}: {Message}";
}
