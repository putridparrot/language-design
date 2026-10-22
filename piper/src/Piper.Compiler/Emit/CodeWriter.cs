using System.Text;

namespace Piper.Emit;

/// <summary>Accumulates indented lines of generated code. Always uses '\n' line endings.</summary>
public sealed class CodeWriter
{
    private const int IndentSize = 4;

    private readonly StringBuilder _builder = new();
    private int _indent;

    public void WriteLine(string line = "")
    {
        if (line.Length > 0)
            _builder.Append(' ', _indent * IndentSize).Append(line);
        _builder.Append('\n');
    }

    public void OpenBrace()
    {
        WriteLine("{");
        _indent++;
    }

    public void CloseBrace()
    {
        _indent--;
        WriteLine("}");
    }

    public override string ToString() => _builder.ToString();
}
