using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Piper.Diagnostics;
using Piper.Text;

namespace Piper.Syntax;

/// <summary>
/// Hand-written scanner. Whitespace and comments are skipped; positions are always absolute
/// within the <see cref="SourceText"/>, so a lexer over a sub-range (an interpolation hole)
/// reports diagnostics at the right place.
/// </summary>
public sealed class Lexer
{
    private readonly SourceText _source;
    private readonly DiagnosticBag _diagnostics;
    private readonly int _end;
    private int _position;

    public Lexer(SourceText source, DiagnosticBag diagnostics)
        : this(source, diagnostics, new TextSpan(0, source.Length))
    {
    }

    public Lexer(SourceText source, DiagnosticBag diagnostics, TextSpan range)
    {
        _source = source;
        _diagnostics = diagnostics;
        _position = range.Start;
        _end = range.End;
    }

    public static ImmutableArray<Token> Tokenize(SourceText source, DiagnosticBag diagnostics) =>
        new Lexer(source, diagnostics).TokenizeAll();

    public static ImmutableArray<Token> Tokenize(SourceText source, DiagnosticBag diagnostics, TextSpan range) =>
        new Lexer(source, diagnostics, range).TokenizeAll();

    private ImmutableArray<Token> TokenizeAll()
    {
        var tokens = ImmutableArray.CreateBuilder<Token>();
        Token token;
        do
        {
            token = NextToken();
            tokens.Add(token);
        }
        while (token.Kind != TokenKind.EndOfFile);
        return tokens.ToImmutable();
    }

    private bool IsAtEnd => _position >= _end;

    private char Current => Peek(0);

    private char Peek(int offset)
    {
        int index = _position + offset;
        return index < _end ? _source[index] : '\0';
    }

    public Token NextToken()
    {
        SkipTrivia();

        if (IsAtEnd)
            return new Token(TokenKind.EndOfFile, new TextSpan(_position, 0), "");

        char c = Current;
        if (IsIdentifierStart(c))
            return ScanIdentifierOrKeyword();
        if (char.IsAsciiDigit(c))
            return ScanNumber();

        switch (c)
        {
            case '"': return ScanString();
            case '\'': return ScanChar();
            case '(': return Punctuation(TokenKind.OpenParen, 1);
            case ')': return Punctuation(TokenKind.CloseParen, 1);
            case '{': return Punctuation(TokenKind.OpenBrace, 1);
            case '}': return Punctuation(TokenKind.CloseBrace, 1);
            case '[': return Punctuation(TokenKind.OpenBracket, 1);
            case ']': return Punctuation(TokenKind.CloseBracket, 1);
            case ',': return Punctuation(TokenKind.Comma, 1);
            case ';': return Punctuation(TokenKind.Semicolon, 1);
            case ':': return Punctuation(TokenKind.Colon, 1);
            case '?': return Punctuation(TokenKind.Question, 1);
            case '.':
                if (Peek(1) != '.')
                    return Punctuation(TokenKind.Dot, 1);
                return Peek(2) == '=' ? Punctuation(TokenKind.DotDotEquals, 3) : Punctuation(TokenKind.DotDot, 2);
            case '-':
                return Peek(1) switch
                {
                    '>' => Punctuation(TokenKind.Arrow, 2),
                    '=' => Punctuation(TokenKind.MinusEquals, 2),
                    _ => Punctuation(TokenKind.Minus, 1),
                };
            case '=':
                return Peek(1) switch
                {
                    '=' => Punctuation(TokenKind.EqualsEquals, 2),
                    '>' => Punctuation(TokenKind.FatArrow, 2),
                    _ => Punctuation(TokenKind.Equals, 1),
                };
            case '!': return Peek(1) == '=' ? Punctuation(TokenKind.BangEquals, 2) : Punctuation(TokenKind.Bang, 1);
            case '<': return Peek(1) == '=' ? Punctuation(TokenKind.LessEquals, 2) : Punctuation(TokenKind.Less, 1);
            case '>': return Peek(1) == '=' ? Punctuation(TokenKind.GreaterEquals, 2) : Punctuation(TokenKind.Greater, 1);
            case '+': return Peek(1) == '=' ? Punctuation(TokenKind.PlusEquals, 2) : Punctuation(TokenKind.Plus, 1);
            case '*': return Peek(1) == '=' ? Punctuation(TokenKind.StarEquals, 2) : Punctuation(TokenKind.Star, 1);
            case '/': return Peek(1) == '=' ? Punctuation(TokenKind.SlashEquals, 2) : Punctuation(TokenKind.Slash, 1);
            case '%': return Peek(1) == '=' ? Punctuation(TokenKind.PercentEquals, 2) : Punctuation(TokenKind.Percent, 1);
            case '&' when Peek(1) == '&': return Punctuation(TokenKind.AmpersandAmpersand, 2);
            case '|': return Peek(1) == '|' ? Punctuation(TokenKind.PipePipe, 2) : Punctuation(TokenKind.Pipe, 1);
        }

        int length = char.IsHighSurrogate(c) && char.IsLowSurrogate(Peek(1)) ? 2 : 1;
        var span = new TextSpan(_position, length);
        _position += length;
        var text = _source.ToString(span);
        _diagnostics.ReportUnexpectedCharacter(span, text);
        return new Token(TokenKind.BadToken, span, text);
    }

    private Token Punctuation(TokenKind kind, int length)
    {
        var span = new TextSpan(_position, length);
        _position += length;
        return new Token(kind, span, _source.ToString(span));
    }

    private void SkipTrivia()
    {
        while (!IsAtEnd)
        {
            char c = Current;
            if (char.IsWhiteSpace(c))
            {
                _position++;
            }
            else if (c == '/' && Peek(1) == '/')
            {
                while (!IsAtEnd && Current is not ('\n' or '\r'))
                    _position++;
            }
            else if (c == '/' && Peek(1) == '*')
            {
                SkipBlockComment();
            }
            else
            {
                break;
            }
        }
    }

    /// <summary>Block comments nest, as in Rust and Swift, so code containing comments can be commented out.</summary>
    private void SkipBlockComment()
    {
        int start = _position;
        _position += 2;
        int depth = 1;
        while (depth > 0)
        {
            if (IsAtEnd)
            {
                _diagnostics.ReportUnterminatedComment(new TextSpan(start, 2));
                return;
            }

            if (Current == '/' && Peek(1) == '*')
            {
                depth++;
                _position += 2;
            }
            else if (Current == '*' && Peek(1) == '/')
            {
                depth--;
                _position += 2;
            }
            else
            {
                _position++;
            }
        }
    }

    private static bool IsIdentifierStart(char c) => char.IsAsciiLetter(c) || c == '_';

    private static bool IsIdentifierPart(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    private Token ScanIdentifierOrKeyword()
    {
        int start = _position;
        while (IsIdentifierPart(Current))
            _position++;

        var span = TextSpan.FromBounds(start, _position);
        var text = _source.ToString(span);
        return new Token(SyntaxFacts.GetKeywordKind(text) ?? TokenKind.Identifier, span, text);
    }

    /// <summary>
    /// number = digits [ "." digits ] [ exponent ] [ "L" ] | "0x" hexDigits [ "L" ] | "0b" binaryDigits [ "L" ] ;
    /// Underscores may separate digits. Integers are Int unless suffixed with L (Long).
    /// </summary>
    private Token ScanNumber()
    {
        int start = _position;
        bool isDouble = false;
        bool valid = true;
        int radix = 10;
        string digits;

        if (Current == '0' && Peek(1) is 'x' or 'X' or 'b' or 'B')
        {
            radix = Peek(1) is 'x' or 'X' ? 16 : 2;
            _position += 2;
            digits = ScanDigits(radix);
            valid = digits.Length > 0;
        }
        else
        {
            var builder = new StringBuilder(ScanDigits(10));
            // Only treat '.' as a decimal point when a digit follows, so `0..10` lexes as a range.
            if (Current == '.' && char.IsAsciiDigit(Peek(1)))
            {
                isDouble = true;
                _position++;
                builder.Append('.').Append(ScanDigits(10));
            }

            if (Current is 'e' or 'E' &&
                (char.IsAsciiDigit(Peek(1)) || (Peek(1) is '+' or '-' && char.IsAsciiDigit(Peek(2)))))
            {
                isDouble = true;
                builder.Append('e');
                _position++;
                if (Current is '+' or '-')
                {
                    builder.Append(Current);
                    _position++;
                }
                builder.Append(ScanDigits(10));
            }

            digits = builder.ToString();
        }

        bool isLong = false;
        if (!isDouble && Current == 'L')
        {
            isLong = true;
            _position++;
        }

        while (IsIdentifierPart(Current))
        {
            _position++;
            valid = false;
        }

        var span = TextSpan.FromBounds(start, _position);
        var text = _source.ToString(span);

        if (!valid)
        {
            _diagnostics.ReportInvalidNumber(span, text);
            return new Token(TokenKind.IntegerLiteral, span, text, 0);
        }

        if (isDouble)
        {
            if (!double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d))
            {
                _diagnostics.ReportInvalidNumber(span, text);
                d = 0;
            }
            return new Token(TokenKind.DoubleLiteral, span, text, d);
        }

        bool parsed = TryParseInteger(digits, radix, out ulong value);
        if (isLong)
        {
            if (!parsed || value > long.MaxValue)
            {
                _diagnostics.ReportIntegerTooLarge(span, text, "Long");
                value = 0;
            }
            return new Token(TokenKind.IntegerLiteral, span, text, (long)value);
        }

        if (!parsed || value > int.MaxValue)
        {
            _diagnostics.ReportIntegerTooLarge(span, text, "Int");
            value = 0;
        }
        return new Token(TokenKind.IntegerLiteral, span, text, (int)value);
    }

    private string ScanDigits(int radix)
    {
        var builder = new StringBuilder();
        while (true)
        {
            char c = Current;
            if (c == '_' && builder.Length > 0)
            {
                _position++;
            }
            else if (radix == 16 ? char.IsAsciiHexDigit(c) : radix == 2 ? c is '0' or '1' : char.IsAsciiDigit(c))
            {
                builder.Append(c);
                _position++;
            }
            else
            {
                return builder.ToString();
            }
        }
    }

    private static bool TryParseInteger(string digits, int radix, out ulong value)
    {
        value = 0;
        foreach (char c in digits)
        {
            ulong digit = (ulong)Convert.ToInt32(c.ToString(), 16);
            if (value > (ulong.MaxValue - digit) / (ulong)radix)
                return false;
            value = value * (ulong)radix + digit;
        }
        return true;
    }

    /// <summary>
    /// string = '"' { char | escape | "{" expression "}" } '"' ;
    /// A string containing holes becomes an InterpolatedStringLiteral; the hole spans are re-lexed by the parser.
    /// </summary>
    private Token ScanString()
    {
        int start = _position;
        _position++;

        var parts = ImmutableArray.CreateBuilder<InterpolationPart>();
        var text = new StringBuilder();
        int textStart = _position;
        int contentEnd;
        bool hasHoles = false;

        while (true)
        {
            if (IsAtEnd || Current is '\n' or '\r')
            {
                _diagnostics.ReportUnterminatedString(new TextSpan(start, 1));
                contentEnd = _position;
                break;
            }

            char c = Current;
            if (c == '"')
            {
                contentEnd = _position;
                _position++;
                break;
            }

            if (c == '\\')
            {
                ScanEscape(text);
                continue;
            }

            if (c == '{')
            {
                hasHoles = true;
                FlushText(parts, text, textStart, _position);

                int holeEnd = FindHoleEnd(_position + 1);
                if (holeEnd < 0)
                {
                    _diagnostics.ReportUnterminatedInterpolation(new TextSpan(_position, 1));
                    while (!IsAtEnd && Current is not ('\n' or '\r'))
                        _position++;
                    textStart = contentEnd = _position;
                    break;
                }

                var holeSpan = TextSpan.FromBounds(_position + 1, holeEnd);
                if (string.IsNullOrWhiteSpace(_source.ToString(holeSpan)))
                    _diagnostics.ReportEmptyInterpolation(TextSpan.FromBounds(_position, holeEnd + 1));
                else
                    parts.Add(new InterpolationHole(holeSpan));

                _position = holeEnd + 1;
                textStart = _position;
                continue;
            }

            text.Append(c);
            _position++;
        }

        FlushText(parts, text, textStart, contentEnd);

        var span = TextSpan.FromBounds(start, _position);
        var tokenText = _source.ToString(span);
        if (!hasHoles)
        {
            var value = string.Concat(parts.OfType<InterpolationText>().Select(p => p.Value));
            return new Token(TokenKind.StringLiteral, span, tokenText, value);
        }

        return new Token(TokenKind.InterpolatedStringLiteral, span, tokenText) { Parts = parts.ToImmutable() };
    }

    private static void FlushText(ImmutableArray<InterpolationPart>.Builder parts, StringBuilder text, int start, int end)
    {
        if (text.Length == 0)
            return;
        parts.Add(new InterpolationText(TextSpan.FromBounds(start, end), text.ToString()));
        text.Clear();
    }

    /// <summary>Finds the '}' closing a hole that starts at <paramref name="index"/>, skipping nested braces and strings.</summary>
    private int FindHoleEnd(int index)
    {
        int depth = 0;
        while (index < _end)
        {
            switch (_source[index])
            {
                case '\n' or '\r':
                    return -1;
                case '{':
                    depth++;
                    index++;
                    break;
                case '}':
                    if (depth == 0)
                        return index;
                    depth--;
                    index++;
                    break;
                case '"':
                    index = SkipNestedQuoted(index, '"');
                    if (index < 0)
                        return -1;
                    break;
                case '\'':
                    index = SkipNestedQuoted(index, '\'');
                    if (index < 0)
                        return -1;
                    break;
                default:
                    index++;
                    break;
            }
        }
        return -1;
    }

    private int SkipNestedQuoted(int index, char quote)
    {
        index++;
        while (index < _end)
        {
            char c = _source[index];
            if (c is '\n' or '\r')
                return -1;
            if (c == '\\')
            {
                index += 2;
                continue;
            }
            if (c == quote)
                return index + 1;
            if (c == '{' && quote == '"')
            {
                int end = FindHoleEnd(index + 1);
                if (end < 0)
                    return -1;
                index = end + 1;
                continue;
            }
            index++;
        }
        return -1;
    }

    /// <summary>escape = "\" ( "n" | "t" | "r" | "0" | "\" | '"' | "'" | "{" | "}" | "u{" hexDigits "}" ) ;</summary>
    private void ScanEscape(StringBuilder builder)
    {
        int start = _position;
        _position++;

        char c = Current;
        char? simple = c switch
        {
            'n' => '\n',
            't' => '\t',
            'r' => '\r',
            '0' => '\0',
            '\\' => '\\',
            '"' => '"',
            '\'' => '\'',
            '{' => '{',
            '}' => '}',
            _ => null,
        };

        if (simple is { } value && !IsAtEnd)
        {
            builder.Append(value);
            _position++;
            return;
        }

        if (c == 'u' && Peek(1) == '{')
        {
            int digitsStart = _position + 2;
            int index = digitsStart;
            while (index < _end && char.IsAsciiHexDigit(_source[index]) && index - digitsStart < 6)
                index++;

            if (index > digitsStart && index < _end && _source[index] == '}')
            {
                int codePoint = int.Parse(_source.ToString(TextSpan.FromBounds(digitsStart, index)), NumberStyles.HexNumber);
                _position = index + 1;
                if (codePoint <= 0x10FFFF && codePoint is < 0xD800 or > 0xDFFF)
                {
                    builder.Append(char.ConvertFromUtf32(codePoint));
                    return;
                }
                var span = TextSpan.FromBounds(start, _position);
                _diagnostics.ReportInvalidEscapeSequence(span, _source.ToString(span));
                return;
            }
        }

        if (IsAtEnd || c is '\n' or '\r')
        {
            _diagnostics.ReportInvalidEscapeSequence(new TextSpan(start, 1), "\\");
            return;
        }

        _position++;
        var badSpan = TextSpan.FromBounds(start, _position);
        _diagnostics.ReportInvalidEscapeSequence(badSpan, _source.ToString(badSpan));
    }

    private Token ScanChar()
    {
        int start = _position;
        _position++;

        var builder = new StringBuilder();
        if (IsAtEnd || Current is '\n' or '\r' or '\'')
        {
            if (Current == '\'')
                _position++;
            return BadCharLiteral(start, report: true);
        }

        int errorsBefore = _diagnostics.Count;
        if (Current == '\\')
        {
            ScanEscape(builder);
        }
        else
        {
            builder.Append(Current);
            _position++;
            if (char.IsHighSurrogate(builder[0]) && char.IsLowSurrogate(Current))
            {
                builder.Append(Current);
                _position++;
            }
        }

        if (Current != '\'')
        {
            // `'ab'`: skip to the closing quote on this line so the stray quote doesn't start another literal.
            int index = _position;
            while (index < _end && _source[index] is not ('\'' or '\n' or '\r'))
                index++;
            if (index < _end && _source[index] == '\'')
                _position = index + 1;
            return BadCharLiteral(start, report: true);
        }

        _position++;
        bool escapeFailed = _diagnostics.Count != errorsBefore;
        if (builder.Length != 1)
            return BadCharLiteral(start, report: !escapeFailed);

        var span = TextSpan.FromBounds(start, _position);
        return new Token(TokenKind.CharLiteral, span, _source.ToString(span), builder[0]);
    }

    private Token BadCharLiteral(int start, bool report)
    {
        var span = TextSpan.FromBounds(start, _position);
        if (report)
            _diagnostics.ReportInvalidCharacterLiteral(span);
        return new Token(TokenKind.CharLiteral, span, _source.ToString(span), '\0');
    }
}
