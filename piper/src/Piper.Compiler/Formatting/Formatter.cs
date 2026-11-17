using System.Collections.Immutable;
using System.Text;
using Piper.Diagnostics;
using Piper.Syntax;
using Piper.Text;

namespace Piper.Formatting;

/// <summary>
/// <c>piper fmt</c>: lays a file out as Piper is written, changing only whitespace between lines, so it can't change
/// what the code means.
/// <list type="bullet">
/// <item><b>Indentation</b> is four spaces for each bracket (<c>{</c>, <c>(</c>, <c>[</c>) open at the line's start,
/// counting the brackets one line opens once (<c>Foo(Bar(</c> indents its contents one level, not two); a line that
/// starts by closing them is at the level they were opened at.</item>
/// <item><b>A continued line</b> (the line before it didn't end a statement, a block or a list's item: <c>;</c>,
/// <c>{</c>, <c>}</c>, <c>,</c>) is one level further in: <c>.Where(…)</c> after a call, <c>requires …</c> after a
/// record, an expression after <c>=</c>. A line of attributes (<c>[Notify]</c>) isn't continued by the next.</item>
/// <item><b>Within a line</b> nothing moves, and comments stay where they are, indented with their code. A line inside a
/// string or a markup literal is left exactly as it is, as its whitespace is its text.</item>
/// <item><b>Blank lines:</b> trailing whitespace goes, two or more blank lines become one, none open or close a file,
/// and it ends with a newline. Line endings stay the file's own.</item>
/// </list>
/// A file that doesn't lex (an unterminated string) isn't formatted: the errors say why.
/// </summary>
public static class Formatter
{
    private const int IndentSize = 4;

    /// <summary>The file laid out, or null with the lexer's errors when it can't be read as Piper.</summary>
    public static (string? Text, ImmutableArray<Diagnostic> Errors) Format(SourceText source)
    {
        var diagnostics = new DiagnosticBag { CurrentSource = source };
        var tokens = Lexer.Tokenize(source, diagnostics).Where(t => t.Kind != TokenKind.EndOfFile).ToList();
        if (diagnostics.HasErrors)
            return (null, [.. diagnostics]);

        var text = source.Text;
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var starts = new int[lines.Length];
        for (int i = 1; i < lines.Length; i++)
            starts[i] = starts[i - 1] + lines[i - 1].Length + (text.Contains("\r\n") ? 2 : 1);
        int LineOf(int position)
        {
            int index = Array.BinarySearch(starts, position);
            return index >= 0 ? index : ~index - 1;
        }

        // Each line's tokens, and the lines a multi-line token (a string, a markup literal) runs on past its first.
        var lineTokens = new List<Token>[lines.Length];
        for (int i = 0; i < lines.Length; i++)
            lineTokens[i] = [];
        var verbatim = new bool[lines.Length];
        foreach (var token in tokens)
        {
            int first = LineOf(token.Span.Start), last = LineOf(Math.Max(token.Span.Start, token.Span.End - 1));
            lineTokens[first].Add(token);
            for (int line = first + 1; line <= last; line++)
                verbatim[line] = true;
        }

        var output = new List<string>();
        // The brackets open, each with the context of the line that opened it: what's inside is one level further in.
        var open = new Stack<Frame>();
        // The context the next line follows: the last code line's, or, if it closed brackets earlier lines opened, the
        // context of the line that opened them (so after `})`, a chain carries on as from the `.Where(` that began it).
        var previous = new Frame(0, 0, 0, null);
        Token? previousLast = null;
        bool previousOpened = false;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            var code = lineTokens[i];
            var context = previous;
            if (verbatim[i])
            {
                output.Add(lines[i]);
            }
            else if (line.Length == 0)
            {
                // A blank line, unless the last was one, or it'd open the file.
                if (output.Count > 0 && output[^1].Length > 0)
                    output.Add("");
            }
            else
            {
                // Closers that start the line close their brackets first: it's at the level of the line that opened them.
                int leading = Math.Min(code.TakeWhile(t => IsCloser(t.Kind)).Count(), open.Count);
                if (leading > 0)
                {
                    context = open.ElementAt(leading - 1);
                }
                else
                {
                    int @base = open.Count > 0 ? open.Peek().Level + 1 : 0;
                    int extra = previousLast is not null && !previousOpened && @base == previous.Base &&
                                Continues(previousLast, previous.First!, code.FirstOrDefault())
                        ? Continuation(previousLast, previous.First!, previous.Extra, code.FirstOrDefault())
                        : 0;
                    context = new Frame(@base + extra, @base, extra, code.FirstOrDefault());
                }
                output.Add(new string(' ', IndentSize * context.Level) + line.TrimStart());
            }
            if (code.Count == 0)
                continue;

            int before = open.Count;
            int openedHere = 0;
            int leadingClosers = verbatim[i] ? 0 : code.TakeWhile(t => IsCloser(t.Kind)).Count();
            foreach (var (token, index) in code.Select((t, n) => (t, n)))
            {
                if (IsOpener(token.Kind))
                {
                    // A block opened at the statement's own depth (a class's `: Base(…) {`, an `if a &&\n b {`) holds
                    // its body one level in from the statement, not from the continued line it's on.
                    open.Push(token.Kind == TokenKind.OpenBrace && openedHere == 0
                        ? context with { Level = context.Base, Extra = 0 }
                        : context);
                    openedHere++;
                }
                else if (IsCloser(token.Kind) && open.Count > 0)
                {
                    var closed = open.Pop();
                    if (openedHere > 0)
                        openedHere--;
                    else if (index >= leadingClosers)
                        context = closed;   // back in the context of the line that opened it
                }
            }
            previous = context with { First = context.First ?? code[0] };
            previousLast = code[^1];
            previousOpened = open.Count > before;
        }

        // No blank lines at the end; one newline.
        while (output.Count > 0 && output[^1].Length == 0)
            output.RemoveAt(output.Count - 1);
        var result = new StringBuilder();
        foreach (var line in output)
            result.Append(line).Append(newline);
        return (result.ToString(), []);
    }

    /// <summary>
    /// Whether a line starting with <paramref name="next"/> (null for a comment) continues the line before, which
    /// ended with <paramref name="last"/> and began with <paramref name="first"/>: it didn't end a statement, a block
    /// or an item, and wasn't a line of attributes.
    /// </summary>
    private static bool Continues(Token last, Token first, Token? next)
    {
        if (last.Kind is TokenKind.Semicolon or TokenKind.OpenBrace or TokenKind.CloseBrace or TokenKind.Comma
            or TokenKind.OpenParen or TokenKind.OpenBracket)
            return false;
        // `[Notify]`, `[Retry(3)]`: attributes on the declaration below, not an expression it continues.
        if (first.Kind == TokenKind.OpenBracket && last.Kind == TokenKind.CloseBracket)
            return false;
        // A block that follows on its own line (`=> {`) is the statement's, not a continuation of it.
        return next?.Kind != TokenKind.OpenBrace;
    }

    /// <summary>
    /// How many levels in a continued line is, given the line it continues (continued <paramref name="extra"/> levels
    /// itself): one more after <c>=</c> or <c>=&gt;</c> (an expression on its own line); a chain's first <c>.Call()</c>
    /// one more than what it's called on, and the rest of it level with that; anything else level with the
    /// continuation it follows, or one level in if the line before wasn't one.
    /// </summary>
    private static int Continuation(Token last, Token first, int extra, Token? next)
    {
        if (last.Kind is TokenKind.Equals or TokenKind.FatArrow)
            return extra + 1;
        if (next is { Kind: TokenKind.Dot or TokenKind.QuestionDot })
            return first.Kind is TokenKind.Dot or TokenKind.QuestionDot ? extra : extra + 1;
        return Math.Max(extra, 1);
    }

    /// <summary>
    /// A line's layout: its <paramref name="Level"/>; the level its brackets gave it (<paramref name="Base"/>) and how
    /// far it was continued beyond that (<paramref name="Extra"/>); and its statement's line's first token, which says
    /// whether it's a chain's <c>.Call()</c>.
    /// </summary>
    private sealed record Frame(int Level, int Base, int Extra, Token? First);

    private static bool IsOpener(TokenKind kind) => kind is TokenKind.OpenBrace or TokenKind.OpenParen or TokenKind.OpenBracket;

    private static bool IsCloser(TokenKind kind) => kind is TokenKind.CloseBrace or TokenKind.CloseParen or TokenKind.CloseBracket;
}
