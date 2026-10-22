using System.Collections.Immutable;
using Piper.Diagnostics;
using Piper.Syntax;
using Piper.Text;

namespace Piper.Tests;

public class LexerTests
{
    private static (ImmutableArray<Token> Tokens, DiagnosticBag Diagnostics) Lex(string text)
    {
        var diagnostics = new DiagnosticBag();
        return (Lexer.Tokenize(new SourceText(text), diagnostics), diagnostics);
    }

    private static TokenKind[] Kinds(string text)
    {
        var (tokens, diagnostics) = Lex(text);
        Assert.Empty(diagnostics);
        return tokens.Select(t => t.Kind).ToArray();
    }

    private static Token Single(string text)
    {
        var (tokens, diagnostics) = Lex(text);
        Assert.Empty(diagnostics);
        Assert.Equal(2, tokens.Length);
        return tokens[0];
    }

    private static string ErrorCode(string text) => Assert.Single(Lex(text).Diagnostics).Code;

    [Fact]
    public void Operators_use_longest_match()
    {
        Assert.Equal(
            [
                TokenKind.Identifier, TokenKind.PlusEquals, TokenKind.Identifier, TokenKind.EqualsEquals,
                TokenKind.Identifier, TokenKind.Arrow, TokenKind.Identifier, TokenKind.FatArrow,
                TokenKind.Identifier, TokenKind.DotDotEquals, TokenKind.Identifier, TokenKind.DotDot,
                TokenKind.Identifier, TokenKind.BangEquals, TokenKind.Identifier, TokenKind.LessEquals,
                TokenKind.Identifier, TokenKind.GreaterEquals, TokenKind.Identifier, TokenKind.AmpersandAmpersand,
                TokenKind.Identifier, TokenKind.PipePipe, TokenKind.Identifier, TokenKind.EndOfFile,
            ],
            Kinds("a += b == c -> d => e ..= f .. g != h <= i >= j && k || l"));
    }

    [Fact]
    public void Keywords_and_identifiers()
    {
        Assert.Equal(
            [TokenKind.FnKeyword, TokenKind.LetKeyword, TokenKind.VarKeyword, TokenKind.Identifier, TokenKind.Identifier, TokenKind.EndOfFile],
            Kinds("fn let var foo _bar1"));
    }

    [Theory]
    [InlineData("42", 42)]
    [InlineData("1_000_000", 1_000_000)]
    [InlineData("0xFF", 255)]
    [InlineData("0b1010", 10)]
    [InlineData("10L", 10L)]
    [InlineData("1.5", 1.5)]
    [InlineData("2e3", 2000.0)]
    [InlineData("2.5E-1", 0.25)]
    public void Numbers_are_decoded(string text, object expected)
    {
        Assert.Equal(expected, Single(text).Value);
    }

    [Fact]
    public void Range_is_not_mistaken_for_a_decimal_point()
    {
        Assert.Equal(
            [TokenKind.IntegerLiteral, TokenKind.DotDot, TokenKind.IntegerLiteral, TokenKind.EndOfFile],
            Kinds("0..10"));
    }

    [Fact]
    public void String_escapes_are_decoded()
    {
        var token = Single("\"a\\n\\t\\\"\\{\\}\\\\\\u{41}\"");
        Assert.Equal(TokenKind.StringLiteral, token.Kind);
        Assert.Equal("a\n\t\"{}\\A", token.Value);
    }

    [Fact]
    public void Interpolated_string_is_split_into_text_and_holes()
    {
        const string text = "\"x={x + 1}!\"";
        var token = Single(text);
        Assert.Equal(TokenKind.InterpolatedStringLiteral, token.Kind);
        Assert.Collection(
            token.Parts,
            part => Assert.Equal("x=", Assert.IsType<InterpolationText>(part).Value),
            part => Assert.Equal("x + 1", text.Substring(part.Span.Start, part.Span.Length)),
            part => Assert.Equal("!", Assert.IsType<InterpolationText>(part).Value));
    }

    [Fact]
    public void Hole_may_contain_braces_and_strings()
    {
        const string text = "\"<{f(\"}\", { 1 })}>\"";
        var token = Single(text);
        var hole = Assert.Single(token.Parts.OfType<InterpolationHole>());
        Assert.Equal("f(\"}\", { 1 })", text.Substring(hole.Span.Start, hole.Span.Length));
    }

    [Fact]
    public void Comments_are_skipped_and_block_comments_nest()
    {
        Assert.Equal(
            [TokenKind.Identifier, TokenKind.Identifier, TokenKind.EndOfFile],
            Kinds("a /* x /* y */ z */ b // c"));
    }

    [Fact]
    public void Char_literals()
    {
        Assert.Equal('a', Single("'a'").Value);
        Assert.Equal('\n', Single("'\\n'").Value);
        Assert.Equal('\'', Single("'\\''").Value);
    }

    [Theory]
    [InlineData("#", "PIP0001")]
    [InlineData("\"abc", "PIP0002")]
    [InlineData("''", "PIP0003")]
    [InlineData("'ab'", "PIP0003")]
    [InlineData("\"\\q\"", "PIP0004")]
    [InlineData("12abc", "PIP0005")]
    [InlineData("/* never closed", "PIP0006")]
    [InlineData("3000000000", "PIP0007")]
    [InlineData("\"{oops\"", "PIP0008")]
    [InlineData("\"{}\"", "PIP0009")]
    public void Lexical_errors_are_reported(string text, string code)
    {
        Assert.Equal(code, ErrorCode(text));
    }

    [Fact]
    public void Positions_map_to_lines_and_columns()
    {
        var source = new SourceText("a\nbb\r\n  c");
        Assert.Equal((1, 1), source.GetLineColumn(0));
        Assert.Equal((2, 2), source.GetLineColumn(3));
        Assert.Equal((3, 3), source.GetLineColumn(8));
    }
}
