using Piper.Text;

namespace Piper.Diagnostics;

public enum DiagnosticSeverity
{
    Error,
    Warning,
}

public sealed record Diagnostic(string Code, DiagnosticSeverity Severity, TextSpan Span, string Message)
{
    /// <summary>Formats as <c>file(line,col): error PIP0000: message</c>, the format MSBuild and editors understand.</summary>
    public string Format(SourceText source)
    {
        var (line, column) = source.GetLineColumn(Span.Start);
        var severity = Severity == DiagnosticSeverity.Error ? "error" : "warning";
        return $"{source.FileName}({line},{column}): {severity} {Code}: {Message}";
    }

    public override string ToString() => $"{Code} {Span}: {Message}";
}
