using System.Collections.Immutable;
using Piper.Diagnostics;
using Piper.Syntax;
using Piper.Text;

namespace Piper.Plugins;

/// <summary>
/// What a plugin reads its construct with: the tokens after its keyword, and Piper's own parser for the expressions,
/// blocks and types inside it. Errors are reported the same way Piper's are; parsing never throws, so a plugin can carry
/// on after a missing token just as the parser does.
/// </summary>
public sealed class PluginParser
{
    private readonly Parser _parser;
    private readonly DiagnosticBag _diagnostics;
    private readonly Func<string, Token> _freshName;

    internal PluginParser(Parser parser, DiagnosticBag diagnostics, IPiperPlugin plugin, Token keyword, Func<string, Token> freshName)
    {
        _parser = parser;
        _diagnostics = diagnostics;
        _freshName = freshName;
        Plugin = plugin;
        Keyword = keyword;
        Template = new SyntaxTemplate(() => Span);
    }

    public IPiperPlugin Plugin { get; }

    /// <summary>The construct's keyword, which has already been read.</summary>
    public Token Keyword { get; }

    /// <summary>The source read so far, from the keyword on: the span given to syntax a <see cref="Template"/> makes.</summary>
    public TextSpan Span => TextSpan.FromBounds(Keyword.Span.Start, Math.Max(Keyword.Span.End, _parser.Previous.Span.End));

    /// <summary>Builds Piper syntax from source text, with holes for the user's syntax: see <see cref="SyntaxTemplate"/>.</summary>
    public SyntaxTemplate Template { get; }

    /// <summary>
    /// Whether the code the construct becomes can <c>await</c>: in an <c>async fn</c> or <c>async</c> lambda it can, in
    /// other functions and lambdas it can't, and in a trailing closure it can, but that makes the closure async, which
    /// the function it's passed to may not expect. So there, await only when the user's code in the construct does
    /// (<see cref="SyntaxWalker.Awaits"/>).
    /// </summary>
    public AsyncContext AsyncContext => _parser.AsyncContext;

    public Token Current => _parser.Current;

    public Token Peek(int offset) => _parser.Peek(offset);

    public Token NextToken() => _parser.NextToken();

    /// <summary>Reads a token of <paramref name="kind"/>, or reports it missing and returns a synthesized one.</summary>
    public Token Expect(TokenKind kind) => _parser.Expect(kind);

    /// <summary>Whether the current token is the word <paramref name="word"/>, a name such as `singleton` that is special only here.</summary>
    public bool AtWord(string word) => Current.Kind == TokenKind.Identifier && Current.Text == word;

    /// <summary>Reads the word <paramref name="word"/>, or reports it missing.</summary>
    public Token ExpectWord(string word)
    {
        if (AtWord(word))
            return NextToken();
        Report(Current.Span, $"Expected '{word}'.");
        return new Token(TokenKind.Identifier, new TextSpan(Current.Span.Start, 0), word) { IsMissing = true };
    }

    public ExpressionSyntax ParseExpression() => _parser.ParseExpression();

    /// <summary>Reads an expression that a block follows, so its `{` isn't read as a trailing closure (as in an `if`'s condition).</summary>
    public ExpressionSyntax ParseExpressionBeforeBlock() => _parser.ParseHead(_parser.ParseExpression);

    public BlockExpressionSyntax ParseBlock() => _parser.ParseBlock();

    /// <summary>
    /// Reads a block that the rewritten code runs in an async function or lambda, so it can `await` (and plugins inside
    /// it see <see cref="AsyncContext.Async"/>).
    /// </summary>
    public BlockExpressionSyntax ParseAsyncBlock() => _parser.ParseAsyncBlock();

    /// <summary>`let x = …;`, `mut total = 0;` or `let (a, b) = …;`, at a `let` or `mut`.</summary>
    public StatementSyntax ParseVariableDeclaration() => _parser.ParseVariableDeclaration();

    public TypeSyntax ParseType() => _parser.ParseType();

    public PatternSyntax ParsePattern() => _parser.ParsePattern();

    /// <summary>A lambda's parameters: <c>x</c> or <c>(a: int, b)</c>.</summary>
    public ImmutableArray<LambdaParameterSyntax> ParseLambdaParameters() => _parser.ParseLambdaParameters();

    public DeclarationSyntax? ParseDeclaration() => _parser.ParseDeclaration();

    /// <summary>Reports an error in the plugin's syntax; the message is followed by the plugin's name.</summary>
    public void Report(TextSpan span, string message) => _diagnostics.ReportPluginError(span, Plugin.Name, message);

    /// <summary>
    /// A name for a variable the rewritten code needs, such as <c>__tasks_1</c>: numbered per file so two expansions
    /// can't clash, and starting with two underscores so it doesn't clash with the user's names.
    /// </summary>
    public Token FreshName(string hint) => _freshName(hint);
}
