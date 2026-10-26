using System.Collections.Immutable;
using Piper.Diagnostics;
using Piper.Plugins;
using Piper.Text;

namespace Piper.Syntax;

/// <summary>Where the parser hands over to plugins (see <see cref="IPiperPlugin"/>).</summary>
public sealed partial class Parser
{
    private PluginRegistry _plugins = PluginRegistry.Empty;

    /// <summary>How many names <see cref="PluginParser.FreshName"/> has made in this file.</summary>
    private int _freshNames;

    /// <summary>Whether the code being parsed can await: see <see cref="PluginParser.AsyncContext"/>.</summary>
    internal AsyncContext AsyncContext => _asyncContext;

    /// <summary>A block that will run in async code, as an `async fn`'s body is parsed.</summary>
    internal BlockExpressionSyntax ParseAsyncBlock()
    {
        var (outerAsync, outerContext) = (_inAsync, _asyncContext);
        (_inAsync, _asyncContext) = (true, AsyncContext.Async);
        var block = ParseBlock();
        (_inAsync, _asyncContext) = (outerAsync, outerContext);
        return block;
    }

    public static CompilationUnitSyntax ParseCompilationUnit(SourceText source, DiagnosticBag diagnostics, PluginRegistry plugins)
    {
        diagnostics.CurrentSource = source;
        var parser = new Parser(source, diagnostics, Lexer.Tokenize(source, diagnostics)) { _plugins = plugins };
        return parser.ParseCompilationUnit();
    }

    /// <summary>
    /// The plugin keyword the current token is, if it's one of kind <typeparamref name="T"/>. Like `yield`, it's a keyword
    /// only when the next token couldn't follow it as a name, so it can still be one: <c>parallel = 4;</c>,
    /// <c>parallel(items)</c> and <c>parallel.Count</c> use a name `parallel`.
    /// </summary>
    private (IPiperPlugin Plugin, T Keyword)? PluginKeywordAt<T>() where T : PluginKeyword =>
        !_plugins.IsEmpty && Current.Kind == TokenKind.Identifier && !CanFollowName(Peek(1).Kind)
            ? _plugins.Find<T>(Current.Text)
            : null;

    /// <summary>Whether a token of <paramref name="kind"/> can follow a name in an expression statement.</summary>
    private static bool CanFollowName(TokenKind kind) =>
        kind is TokenKind.Semicolon or TokenKind.Comma or TokenKind.CloseParen or TokenKind.CloseBracket or TokenKind.CloseBrace
            or TokenKind.EndOfFile or TokenKind.Dot or TokenKind.QuestionDot or TokenKind.Question or TokenKind.OpenParen
            or TokenKind.OpenBracket or TokenKind.FatArrow or TokenKind.IsKeyword ||
        SyntaxFacts.IsAssignmentOperator(kind) || SyntaxFacts.GetBinaryPrecedence(kind) > 0;

    /// <summary>
    /// Reads a plugin's keyword and lets the plugin read the rest of its construct, returning the syntax it means. A
    /// plugin that throws is reported, and its construct skipped.
    /// </summary>
    private ImmutableArray<T> ExpandPlugin<T>(IPiperPlugin plugin, Func<PluginParser, IEnumerable<T>> expand) where T : SyntaxNode
    {
        int start = _position;
        var keyword = NextToken();
        var parser = new PluginParser(this, _diagnostics, plugin, keyword,
            hint => new Token(TokenKind.Identifier, keyword.Span, $"__{hint}_{++_freshNames}") { IsGenerated = true });
        try
        {
            ImmutableArray<T> result = [.. expand(parser)];
            _plugins.Record(plugin, new PluginExpansion(_plugins.KeywordFor(keyword.Text), keyword, _source, [.. result]));
            return result;
        }
        catch (Exception exception)
        {
            _diagnostics.ReportPluginFailed(parser.Span, plugin.Name, keyword.Text, exception.Message);
            SkipConstruct(start);
            return [];
        }
    }

    /// <summary>
    /// Skips the rest of the construct that started at token <paramref name="start"/>: up to a `;` or the `}` closing
    /// its braces, counting any braces already read.
    /// </summary>
    private void SkipConstruct(int start)
    {
        int depth = 0;
        for (int i = start; i < _position; i++)
            depth += _tokens[i].Kind switch { TokenKind.OpenBrace => 1, TokenKind.CloseBrace => -1, _ => 0 };

        while (Current.Kind != TokenKind.EndOfFile)
        {
            switch (Current.Kind)
            {
                case TokenKind.OpenBrace:
                    depth++;
                    break;
                case TokenKind.CloseBrace when depth <= 0:
                    return;
                case TokenKind.CloseBrace when --depth == 0:
                    NextToken();
                    return;
                case TokenKind.Semicolon when depth == 0:
                    NextToken();
                    return;
            }
            NextToken();
        }
    }
}
