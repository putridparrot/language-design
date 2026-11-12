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
            _plugins.Record(plugin, new PluginExpansion(_plugins.KeywordFor(keyword.Text), keyword, _source, [.. result]) { Words = [.. parser.Words] });
            return result;
        }
        catch (Exception exception)
        {
            _diagnostics.ReportPluginFailed(parser.Span, plugin.Name, keyword.Text, PluginLoader.Explain(exception));
            SkipConstruct(start);
            return [];
        }
    }

    /// <summary>The plugin decorator <paramref name="attribute"/> names, if it names one: an unqualified name.</summary>
    private (IPiperPlugin Plugin, Decorator Decorator)? DecoratorFor(AttributeSyntax attribute) =>
        !_plugins.IsEmpty && attribute.Name.Parts is [var name] ? _plugins.Find<Decorator>(name.Text) : null;

    /// <summary>
    /// Applies the plugin decorators among <paramref name="function"/>'s attributes, nearest the function first, so the
    /// first written wraps the rest. They're removed from its attributes, which keep the others. A decorator that throws
    /// is reported and leaves the function as it was.
    /// </summary>
    private FunctionDeclarationSyntax Decorate(FunctionDeclarationSyntax function, bool isMethod)
    {
        if (_plugins.IsEmpty || function.Attributes.IsEmpty)
            return function;
        var decorators = function.Attributes.Select(attribute => (Attribute: attribute, Found: DecoratorFor(attribute))).ToList();
        if (decorators.TrueForAll(d => d.Found is null))
            return function;

        var result = function with { Attributes = [.. decorators.Where(d => d.Found is null).Select(d => d.Attribute)] };
        for (int i = decorators.Count - 1; i >= 0; i--)
        {
            if (decorators[i].Found is not var (plugin, decorator))
                continue;
            var attribute = decorators[i].Attribute;
            var name = attribute.Name.Parts[0];
            if (decorator.Function is not { } decorate)
            {
                ReportMisplaced(attribute, plugin, decorator, isMethod ? "a method" : "a function");
                continue;
            }
            if (!result.HasBody)
            {
                ReportMisplaced(attribute, plugin, decorator, "one without a body");
                continue;
            }
            var context = new DecoratorContext(plugin, attribute, result, isMethod, _diagnostics, FreshNameAt(attribute));
            try
            {
                result = decorate(context);
                _plugins.Record(plugin, new PluginExpansion(decorator, name, _source, [result]));
            }
            catch (Exception exception)
            {
                _diagnostics.ReportPluginFailed(attribute.Span, plugin.Name, decorator.Text, PluginLoader.Explain(exception));
            }
        }
        return result;
    }

    /// <summary>
    /// Declarations type decorators added beside the type they're on, such as an <c>extend</c> block with a record's new
    /// methods, which <see cref="ParseCompilationUnit"/> adds after it.
    /// </summary>
    private readonly List<DeclarationSyntax> _pendingDeclarations = [];

    /// <summary>
    /// Applies the type decorators among a class's or record's attributes, nearest first, as for functions. Each returns
    /// the type rewritten, which the next one gets, and any declarations to add beside it.
    /// </summary>
    private DeclarationSyntax DecorateType(DeclarationSyntax type)
    {
        var decorators = type.Attributes.Select(attribute => (Attribute: attribute, Found: DecoratorFor(attribute))).ToList();
        if (decorators.TrueForAll(d => d.Found is null))
            return type;

        var result = type with { Attributes = [.. decorators.Where(d => d.Found is null).Select(d => d.Attribute)] };
        for (int i = decorators.Count - 1; i >= 0; i--)
        {
            if (decorators[i].Found is not var (plugin, decorator))
                continue;
            var attribute = decorators[i].Attribute;
            if (decorator.Type is not { } decorate)
            {
                ReportMisplaced(attribute, plugin, decorator, DescribeDeclaration(result));
                continue;
            }
            var context = new TypeDecoratorContext(plugin, attribute, result, _diagnostics, FreshNameAt(attribute));
            try
            {
                ImmutableArray<DeclarationSyntax> declarations = [.. decorate(context)];
                var rewritten = declarations.FirstOrDefault(d => d.GetType() == type.GetType() && d.Name.Text == type.Name.Text)
                    ?? throw new InvalidOperationException($"The decorator returned no {DescribeDeclaration(type)} '{type.Name.Text}'; it must return the type it's on, rewritten or not.");
                _plugins.Record(plugin, new PluginExpansion(decorator, attribute.Name.Parts[0], _source, [.. declarations]));
                result = rewritten;
                _pendingDeclarations.AddRange(declarations.Where(d => !ReferenceEquals(d, rewritten)));
            }
            catch (Exception exception)
            {
                _diagnostics.ReportPluginFailed(attribute.Span, plugin.Name, decorator.Text, PluginLoader.Explain(exception));
            }
        }
        return result;
    }

    /// <summary>Fresh names for code a decorator writes, placed at its attribute.</summary>
    private Func<string, Token> FreshNameAt(AttributeSyntax attribute) =>
        hint => new Token(TokenKind.Identifier, attribute.Span, $"__{hint}_{++_freshNames}") { IsGenerated = true };

    /// <summary>Reports a decorator on <paramref name="what"/>, which it can't rewrite, saying what it can.</summary>
    private void ReportMisplaced(AttributeSyntax attribute, IPiperPlugin plugin, Decorator decorator, string what)
    {
        var accepts = (decorator.Function, decorator.Type) switch
        {
            (not null, not null) => "a function or method with a body, a class or a record",
            (not null, null) => "a function or method with a body",
            (null, not null) => "a class or a record",
            _ => "nothing: it has no handler",
        };
        _diagnostics.ReportDecoratorMisplaced(attribute.Span, plugin.Name, decorator.Text, accepts, what);
    }

    /// <summary>
    /// Reports the plugin decorators among <paramref name="attributes"/>, on <paramref name="what"/>, which can't be
    /// decorated, and returns the others.
    /// </summary>
    private ImmutableArray<AttributeSyntax> NoDecorators(ImmutableArray<AttributeSyntax> attributes, string what)
    {
        if (_plugins.IsEmpty || attributes.IsEmpty)
            return attributes;
        var kept = ImmutableArray.CreateBuilder<AttributeSyntax>();
        foreach (var attribute in attributes)
        {
            if (DecoratorFor(attribute) is var (plugin, decorator))
                ReportMisplaced(attribute, plugin, decorator, what);
            else
                kept.Add(attribute);
        }
        return kept.ToImmutable();
    }

    /// <summary>A declaration's decorators applied, or reported if it can't be decorated.</summary>
    private DeclarationSyntax Decorate(DeclarationSyntax declaration) => declaration switch
    {
        FunctionDeclarationSyntax function => Decorate(function, isMethod: false),
        _ when _plugins.IsEmpty || declaration.Attributes.IsEmpty => declaration,
        ClassDeclarationSyntax or RecordDeclarationSyntax => DecorateType(declaration),
        _ => declaration with { Attributes = NoDecorators(declaration.Attributes, DescribeDeclaration(declaration)) },
    };

    private static string DescribeDeclaration(DeclarationSyntax declaration) => declaration switch
    {
        RecordDeclarationSyntax => "a record",
        ClassDeclarationSyntax => "a class",
        EnumDeclarationSyntax => "an enum",
        TraitDeclarationSyntax => "a trait",
        TestDeclarationSyntax => "a test",
        _ => "this declaration",
    };

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
