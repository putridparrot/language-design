namespace Piper.Syntax;

public static class SyntaxFacts
{
    public static TokenKind? GetKeywordKind(string text) => text switch
    {
        "fn" => TokenKind.FnKeyword,
        "let" => TokenKind.LetKeyword,
        "var" => TokenKind.VarKeyword,
        "if" => TokenKind.IfKeyword,
        "else" => TokenKind.ElseKeyword,
        "while" => TokenKind.WhileKeyword,
        "for" => TokenKind.ForKeyword,
        "in" => TokenKind.InKeyword,
        "return" => TokenKind.ReturnKeyword,
        "break" => TokenKind.BreakKeyword,
        "continue" => TokenKind.ContinueKeyword,
        "record" => TokenKind.RecordKeyword,
        "enum" => TokenKind.EnumKeyword,
        "match" => TokenKind.MatchKeyword,
        "trait" => TokenKind.TraitKeyword,
        "extend" => TokenKind.ExtendKeyword,
        "this" => TokenKind.ThisKeyword,
        "static" => TokenKind.StaticKeyword,
        "null" => TokenKind.NullKeyword,
        "is" => TokenKind.IsKeyword,
        "true" => TokenKind.TrueKeyword,
        "false" => TokenKind.FalseKeyword,
        _ => null,
    };

    /// <summary>
    /// Binary operator precedence table used by the Pratt parser; higher binds tighter, 0 means "not a binary operator".
    /// Binary operators are left-associative except <c>??</c>, which is right-associative as in C#.
    /// Assignment is handled separately (lowest, right-associative).
    /// </summary>
    public static int GetBinaryPrecedence(TokenKind kind) => kind switch
    {
        TokenKind.Star or TokenKind.Slash or TokenKind.Percent => 8,
        TokenKind.Plus or TokenKind.Minus => 7,
        TokenKind.Less or TokenKind.LessEquals or TokenKind.Greater or TokenKind.GreaterEquals => IsPrecedence,
        TokenKind.EqualsEquals or TokenKind.BangEquals => 5,
        TokenKind.AmpersandAmpersand => 4,
        TokenKind.PipePipe => 3,
        TokenKind.QuestionQuestion => 2,
        TokenKind.DotDot or TokenKind.DotDotEquals => 1,
        _ => 0,
    };

    /// <summary><c>x is Pattern</c> binds like the relational operators, as in C#.</summary>
    public const int IsPrecedence = 6;

    public static bool IsRightAssociative(TokenKind kind) => kind == TokenKind.QuestionQuestion;

    /// <summary>Prefix operators bind tighter than any binary operator.</summary>
    public static int GetUnaryPrecedence(TokenKind kind) => kind switch
    {
        TokenKind.Minus or TokenKind.Bang => 9,
        _ => 0,
    };

    public static bool IsAssignmentOperator(TokenKind kind) => kind is
        TokenKind.Equals or TokenKind.PlusEquals or TokenKind.MinusEquals or
        TokenKind.StarEquals or TokenKind.SlashEquals or TokenKind.PercentEquals;

    /// <summary>Maps a compound assignment such as <c>+=</c> to its binary operator <c>+</c>.</summary>
    public static TokenKind? GetCompoundAssignmentOperator(TokenKind kind) => kind switch
    {
        TokenKind.PlusEquals => TokenKind.Plus,
        TokenKind.MinusEquals => TokenKind.Minus,
        TokenKind.StarEquals => TokenKind.Star,
        TokenKind.SlashEquals => TokenKind.Slash,
        TokenKind.PercentEquals => TokenKind.Percent,
        _ => null,
    };

    public static string? GetText(TokenKind kind) => kind switch
    {
        TokenKind.FnKeyword => "fn",
        TokenKind.LetKeyword => "let",
        TokenKind.VarKeyword => "var",
        TokenKind.IfKeyword => "if",
        TokenKind.ElseKeyword => "else",
        TokenKind.WhileKeyword => "while",
        TokenKind.ForKeyword => "for",
        TokenKind.InKeyword => "in",
        TokenKind.ReturnKeyword => "return",
        TokenKind.BreakKeyword => "break",
        TokenKind.ContinueKeyword => "continue",
        TokenKind.RecordKeyword => "record",
        TokenKind.EnumKeyword => "enum",
        TokenKind.MatchKeyword => "match",
        TokenKind.TraitKeyword => "trait",
        TokenKind.ExtendKeyword => "extend",
        TokenKind.ThisKeyword => "this",
        TokenKind.StaticKeyword => "static",
        TokenKind.NullKeyword => "null",
        TokenKind.IsKeyword => "is",
        TokenKind.TrueKeyword => "true",
        TokenKind.FalseKeyword => "false",
        TokenKind.OpenParen => "(",
        TokenKind.CloseParen => ")",
        TokenKind.OpenBrace => "{",
        TokenKind.CloseBrace => "}",
        TokenKind.OpenBracket => "[",
        TokenKind.CloseBracket => "]",
        TokenKind.Comma => ",",
        TokenKind.Semicolon => ";",
        TokenKind.Colon => ":",
        TokenKind.Dot => ".",
        TokenKind.DotDot => "..",
        TokenKind.DotDotEquals => "..=",
        TokenKind.Arrow => "->",
        TokenKind.FatArrow => "=>",
        TokenKind.Question => "?",
        TokenKind.QuestionQuestion => "??",
        TokenKind.QuestionDot => "?.",
        TokenKind.Equals => "=",
        TokenKind.EqualsEquals => "==",
        TokenKind.Bang => "!",
        TokenKind.BangEquals => "!=",
        TokenKind.Less => "<",
        TokenKind.LessEquals => "<=",
        TokenKind.Greater => ">",
        TokenKind.GreaterEquals => ">=",
        TokenKind.Plus => "+",
        TokenKind.Minus => "-",
        TokenKind.Star => "*",
        TokenKind.Slash => "/",
        TokenKind.Percent => "%",
        TokenKind.AmpersandAmpersand => "&&",
        TokenKind.PipePipe => "||",
        TokenKind.Pipe => "|",
        TokenKind.PlusEquals => "+=",
        TokenKind.MinusEquals => "-=",
        TokenKind.StarEquals => "*=",
        TokenKind.SlashEquals => "/=",
        TokenKind.PercentEquals => "%=",
        _ => null,
    };

    /// <summary>A human-readable description of a token kind for diagnostics.</summary>
    public static string Describe(TokenKind kind) => GetText(kind) is { } text
        ? $"'{text}'"
        : kind switch
        {
            TokenKind.EndOfFile => "end of file",
            TokenKind.Identifier => "an identifier",
            TokenKind.IntegerLiteral => "an integer literal",
            TokenKind.DoubleLiteral => "a floating-point literal",
            TokenKind.CharLiteral => "a character literal",
            TokenKind.StringLiteral or TokenKind.InterpolatedStringLiteral => "a string literal",
            _ => "an invalid token",
        };
}
