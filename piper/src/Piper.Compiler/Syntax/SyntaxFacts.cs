namespace Piper.Syntax;

public static class SyntaxFacts
{
    /// <summary>
    /// Words that aren't keywords yet but may become ones, so nothing may be named with them: planned features
    /// (<c>const</c>, <c>operator</c>, <c>test</c>, <c>where</c>, <c>type</c>, <c>impl</c>), other languages' module words,
    /// and C#'s modifiers and type kinds.
    /// </summary>
    public static readonly IReadOnlySet<string> ReservedWords = new HashSet<string>
    {
        "base", "const", "operator", "test", "requires", "where", "type", "impl", "module", "import", "self",
        "public", "internal", "protected", "sealed", "abstract", "override", "virtual", "class", "struct", "interface",
    };

    public static TokenKind? GetKeywordKind(string text) => text switch
    {
        "fn" => TokenKind.FnKeyword,
        "let" => TokenKind.LetKeyword,
        "mut" => TokenKind.MutKeyword,
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
        "namespace" => TokenKind.NamespaceKeyword,
        "using" => TokenKind.UsingKeyword,
        "private" => TokenKind.PrivateKeyword,
        "try" => TokenKind.TryKeyword,
        "catch" => TokenKind.CatchKeyword,
        "finally" => TokenKind.FinallyKeyword,
        "defer" => TokenKind.DeferKeyword,
        "throw" => TokenKind.ThrowKeyword,
        "out" => TokenKind.OutKeyword,
        "true" => TokenKind.TrueKeyword,
        "false" => TokenKind.FalseKeyword,
        _ when IsPredefinedType(text) => TokenKind.PredefinedTypeKeyword,
        _ => null,
    };

    /// <summary>
    /// The builtin type names, which are keywords as in C#: `int`, `string`, …, plus Piper's `unit`. They're
    /// names of types (`let x: int`) and, like C#'s, can be used where a type's members are (`int.Parse(s)`).
    /// </summary>
    public static bool IsPredefinedType(string text) => text is
        "int" or "long" or "double" or "bool" or "string" or "char" or "unit" or "object" or
        "byte" or "sbyte" or "short" or "ushort" or "uint" or "ulong" or "float" or "decimal";

    /// <summary>
    /// Binary operator precedence table used by the Pratt parser; higher binds tighter, 0 means "not a binary operator".
    /// Binary operators are left-associative except <c>??</c>, which is right-associative as in C#.
    /// Assignment is handled separately (lowest, right-associative).
    /// </summary>
    public static int GetBinaryPrecedence(TokenKind kind) => kind switch
    {
        TokenKind.Star or TokenKind.Slash or TokenKind.Percent => 9,
        TokenKind.Plus or TokenKind.Minus => 8,
        TokenKind.PipeGreater => 7,
        TokenKind.Less or TokenKind.LessEquals or TokenKind.Greater or TokenKind.GreaterEquals => IsPrecedence,
        TokenKind.EqualsEquals or TokenKind.BangEquals => 5,
        TokenKind.AmpersandAmpersand => 4,
        TokenKind.PipePipe => 3,
        TokenKind.QuestionQuestion => 2,
        TokenKind.DotDot or TokenKind.DotDotLess => 1,
        _ => 0,
    };

    /// <summary><c>x is Pattern</c> binds like the relational operators, as in C#.</summary>
    public const int IsPrecedence = 6;

    public static bool IsRightAssociative(TokenKind kind) => kind == TokenKind.QuestionQuestion;

    /// <summary>Prefix operators bind tighter than any binary operator.</summary>
    public static int GetUnaryPrecedence(TokenKind kind) => kind switch
    {
        TokenKind.Minus or TokenKind.Bang => 10,
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
        TokenKind.MutKeyword => "mut",
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
        TokenKind.NamespaceKeyword => "namespace",
        TokenKind.UsingKeyword => "using",
        TokenKind.PrivateKeyword => "private",
        TokenKind.TryKeyword => "try",
        TokenKind.CatchKeyword => "catch",
        TokenKind.FinallyKeyword => "finally",
        TokenKind.DeferKeyword => "defer",
        TokenKind.ThrowKeyword => "throw",
        TokenKind.OutKeyword => "out",
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
        TokenKind.DotDotLess => "..<",
        TokenKind.Ellipsis => "...",
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
        TokenKind.PipeGreater => "|>",
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
