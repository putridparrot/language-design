namespace Piper.Text;

/// <summary>The text of a Piper source file plus a line map for turning positions into line/column.</summary>
public sealed class SourceText
{
    private readonly int[] _lineStarts;

    public SourceText(string text, string fileName = "<input>")
    {
        Text = text;
        FileName = fileName;
        _lineStarts = ComputeLineStarts(text);
    }

    public string Text { get; }

    public string FileName { get; }

    public int Length => Text.Length;

    public char this[int index] => Text[index];

    public string ToString(TextSpan span) => Text.Substring(span.Start, span.Length);

    /// <summary>Returns the 1-based line and column of <paramref name="position"/>.</summary>
    public (int Line, int Column) GetLineColumn(int position)
    {
        int index = Array.BinarySearch(_lineStarts, position);
        int line = index >= 0 ? index : ~index - 1;
        return (line + 1, position - _lineStarts[line] + 1);
    }

    public int LineCount => _lineStarts.Length;

    /// <summary>The position of a 0-based line and column (clamped to the text), as editors count them.</summary>
    public int GetPosition(int line, int column)
    {
        if (line >= _lineStarts.Length)
            return Text.Length;
        var start = _lineStarts[Math.Max(line, 0)];
        var end = line + 1 < _lineStarts.Length ? _lineStarts[line + 1] : Text.Length;
        return Math.Min(start + Math.Max(column, 0), end);
    }

    private static int[] ComputeLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                starts.Add(i + 1);
            }
            else if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }
        return starts.ToArray();
    }
}
