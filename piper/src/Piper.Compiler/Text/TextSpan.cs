namespace Piper.Text;

/// <summary>A half-open range of characters [Start, End) within a <see cref="SourceText"/>.</summary>
public readonly record struct TextSpan(int Start, int Length)
{
    public int End => Start + Length;

    public static TextSpan FromBounds(int start, int end) => new(start, end - start);

    public override string ToString() => $"[{Start}..{End})";
}
