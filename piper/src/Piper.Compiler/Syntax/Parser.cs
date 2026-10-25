using System.Collections.Immutable;
using Piper.Diagnostics;
using Piper.Plugins;
using Piper.Text;

namespace Piper.Syntax;

/// <summary>
/// Hand-written recursive-descent parser; expressions use Pratt parsing driven by <see cref="SyntaxFacts"/>.
/// The parser never throws on bad input: it reports a diagnostic, synthesizes a missing token or
/// error node, and resynchronizes so later errors are still found. The grammar of each construct is
/// given (informally, EBNF-style) in the doc comment of its Parse method.
/// </summary>
public sealed partial class Parser
{
    private readonly SourceText _source;
    private readonly DiagnosticBag _diagnostics;
    private readonly ImmutableArray<Token> _tokens;
    private int _position;
    private int _lastErrorPosition = -1;
    private bool _inGuard;

    /// <summary>Inside an async function or lambda, where (as in C#) `await (x)` is the operator rather than a call.</summary>
    private bool _inAsync;

    /// <summary>Set when a `yield` is parsed, so the enclosing function can be marked an iterator.</summary>
    private bool _sawYield;

    /// <summary>
    /// Whether the innermost function, lambda or closure being parsed is async: an `async fn` or `async` lambda is, other
    /// functions and lambdas aren't, and a trailing closure is if it turns out to await (for plugins: see
    /// <see cref="Plugins.PluginParser.AsyncContext"/>).
    /// </summary>
    private AsyncContext _asyncContext;

    /// <summary>
    /// In the head of an `if`, `while`, `for`, `match` or `requires`, where a `{` starts the body rather than a trailing
    /// closure: `if ready(x) { … }`. Cleared again inside brackets and blocks.
    /// </summary>
    private bool _noTrailingClosure;

    /// <summary>Where the most recent trailing closure ended, so a statement ending with one needs no `;`.</summary>
    private int _trailingClosureEnd = -1;

    private Parser(SourceText source, DiagnosticBag diagnostics, ImmutableArray<Token> tokens)
    {
        _source = source;
        _diagnostics = diagnostics;
        _tokens = tokens;
    }

    public static CompilationUnitSyntax ParseCompilationUnit(SourceText source, DiagnosticBag diagnostics)
    {
        diagnostics.CurrentSource = source;
        var parser = new Parser(source, diagnostics, Lexer.Tokenize(source, diagnostics));
        return parser.ParseCompilationUnit();
    }

    /// <summary>Parses a single expression that must span all of <paramref name="source"/>. Used by tests and tools.</summary>
    public static ExpressionSyntax ParseExpression(SourceText source, DiagnosticBag diagnostics)
    {
        var parser = new Parser(source, diagnostics, Lexer.Tokenize(source, diagnostics));
        return parser.ParseStandaloneExpression();
    }

    internal Token Current => Peek(0);

    internal Token Peek(int offset)
    {
        int index = Math.Min(_position + offset, _tokens.Length - 1);
        return _tokens[index];
    }

    internal Token Previous => _tokens[Math.Max(_position - 1, 0)];

    internal Token NextToken()
    {
        var token = Current;
        if (token.Kind != TokenKind.EndOfFile)
            _position++;
        return token;
    }

    internal Token Expect(TokenKind kind)
    {
        if (Current.Kind == kind)
            return NextToken();

        // A missing ';' or ')' is best reported right after the previous token, not at the start of the next line.
        var span = kind is TokenKind.Semicolon or TokenKind.CloseParen && _position > 0
            ? new TextSpan(Previous.Span.End, 0)
            : Current.Span;
        ReportOnce(span, () => _diagnostics.ReportExpectedToken(span, kind, Current));
        return Missing(kind);
    }

    private Token Missing(TokenKind kind) =>
        new(kind, new TextSpan(Current.Span.Start, 0), "") { IsMissing = true };

    /// <summary>Only one error per position, so a single mistake doesn't cascade into a wall of errors.</summary>
    private void ReportOnce(TextSpan span, Action report)
    {
        if (span.Start == _lastErrorPosition)
            return;
        _lastErrorPosition = span.Start;
        report();
    }

    private static TextSpan Span(SyntaxNode first, SyntaxNode last) => TextSpan.FromBounds(first.Span.Start, last.Span.End);

    private static TextSpan Span(Token first, SyntaxNode last) => TextSpan.FromBounds(first.Span.Start, last.Span.End);

    private TextSpan SpanFrom(Token first) => TextSpan.FromBounds(first.Span.Start, Math.Max(first.Span.End, Previous.Span.End));

    /// <summary>A literal's value; reports <c>2147483648</c> (or the Long equivalent) used without a minus sign.</summary>
    private object? LiteralValue(Token literal)
    {
        if (literal.Value is not NegatableLiteral min)
            return literal.Value;
        _diagnostics.ReportIntegerTooLarge(literal.Span, literal.Text, min.TypeName);
        return min.TypeName == "int" ? 0 : 0L;
    }

    /// <summary>Tokens that continue a postfix expression, which binds tighter than unary minus.</summary>
    private static bool IsPostfixStart(TokenKind kind) =>
        kind is TokenKind.Dot or TokenKind.QuestionDot or TokenKind.OpenParen or TokenKind.OpenBracket or TokenKind.Question;

    /// <summary>
    /// compilationUnit = { using } [ "namespace" qualifiedName ";" ] { using } { attributes [ "private" ] declaration } EOF ;
    /// using           = "using" qualifiedName ";" ;
    /// </summary>
    private CompilationUnitSyntax ParseCompilationUnit()
    {
        var usings = ImmutableArray.CreateBuilder<UsingDirectiveSyntax>();
        NamespaceDeclarationSyntax? @namespace = null;
        var declarations = ImmutableArray.CreateBuilder<DeclarationSyntax>();

        while (Current.Kind != TokenKind.EndOfFile)
        {
            int start = _position;
            switch (Current.Kind)
            {
                case TokenKind.UsingKeyword:
                {
                    var keyword = NextToken();
                    var name = ParseQualifiedName();
                    Expect(TokenKind.Semicolon);
                    var directive = new UsingDirectiveSyntax(SpanFrom(keyword), name);
                    if (declarations.Count > 0)
                        _diagnostics.ReportMisplacedDirective(directive.Span, "using");
                    usings.Add(directive);
                    break;
                }
                case TokenKind.NamespaceKeyword:
                {
                    var keyword = NextToken();
                    var name = ParseQualifiedName();
                    Expect(TokenKind.Semicolon);
                    var declaration = new NamespaceDeclarationSyntax(SpanFrom(keyword), name);
                    if (@namespace is not null || declarations.Count > 0)
                        _diagnostics.ReportMisplacedDirective(declaration.Span, "namespace");
                    else
                        @namespace = declaration;
                    break;
                }
                // `endpoints "/api" { … }`: a plugin's declaration.
                case TokenKind.Identifier when PluginKeywordAt<DeclarationKeyword>() is var (plugin, keyword):
                    declarations.AddRange(ExpandPlugin(plugin, keyword.Expand));
                    break;
                default:
                {
                    var documentation = DocumentationBefore(Current.Span.Start);
                    if (ParseDeclaration() is { } parsed)
                        declarations.Add(parsed with { Documentation = documentation });
                    break;
                }
            }
            if (_position == start)
                NextToken();
        }
        return new CompilationUnitSyntax(
            new TextSpan(0, _source.Length), _source, @namespace, usings.ToImmutable(), declarations.ToImmutable());
    }

    /// <summary>declaration = function | record | enum | trait | extend ;  optionally preceded by attributes and "private".</summary>
    internal DeclarationSyntax? ParseDeclaration()
    {
        if (Current.Kind == TokenKind.OpenBracket)
        {
            var attributes = ParseAttributes();
            return ParseDeclaration() is { } declaration ? declaration with { Attributes = attributes } : null;
        }

        if (Current.Kind == TokenKind.PrivateKeyword)
        {
            var @private = NextToken();
            return ParseDeclaration() is { } declaration ? declaration with { Private = @private } : null;
        }

        switch (Current.Kind)
        {
            case TokenKind.FnKeyword or TokenKind.StaticKeyword:
                return ParseFunction();
            case TokenKind.Identifier when IsAsyncModifier(0):
                return ParseFunction();
            case TokenKind.Identifier when IsTestStart(0) || (Current.Text == "async" && IsTestStart(1)):
                return ParseTest();
            case TokenKind.RecordKeyword:
                return ParseRecord();
            case TokenKind.EnumKeyword:
                return ParseEnum();
            case TokenKind.TraitKeyword:
                return ParseTrait();
            case TokenKind.ExtendKeyword:
                return ParseExtend();
            default:
                ReportOnce(Current.Span, () => _diagnostics.ReportExpectedDeclaration(Current.Span, Current));
                while (!IsDeclarationStart(Current.Kind) && Current.Kind != TokenKind.EndOfFile)
                    NextToken();
                return null;
        }
    }

    /// <summary>
    /// The text of the `///` lines directly above the line <paramref name="position"/> starts, when it's the first thing
    /// on its line (a field after another on the same line has none). One space after `///` is dropped; `////` isn't a
    /// doc comment.
    /// </summary>
    private string? DocumentationBefore(int position)
    {
        int lineStart = position;
        while (lineStart > 0 && _source[lineStart - 1] != '\n')
            lineStart--;
        for (int i = lineStart; i < position; i++)
        {
            if (!char.IsWhiteSpace(_source[i]))
                return null;
        }

        var lines = new List<string>();
        for (int end = lineStart; end > 0;)
        {
            int start = end - 1;
            while (start > 0 && _source[start - 1] != '\n')
                start--;
            var line = _source.ToString(TextSpan.FromBounds(start, end - 1)).Trim();
            if (!line.StartsWith("///", StringComparison.Ordinal) || line.StartsWith("////", StringComparison.Ordinal))
                break;
            lines.Insert(0, line.Length > 3 && line[3] == ' ' ? line[4..] : line[3..]);
            end = start;
        }
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    /// <summary>
    /// attributes = { "[" attribute { "," attribute } [ "," ] "]" } ;
    /// attribute  = qualifiedName [ "(" [ expression { "," expression } [ "," ] ] ")" ] ;
    /// </summary>
    private ImmutableArray<AttributeSyntax> ParseAttributes()
    {
        var attributes = ImmutableArray.CreateBuilder<AttributeSyntax>();
        while (Current.Kind == TokenKind.OpenBracket)
        {
            NextToken();
            attributes.AddRange(ParseSeparatedList(() =>
            {
                var name = ParseQualifiedName();
                var arguments = ImmutableArray<ExpressionSyntax>.Empty;
                if (Current.Kind == TokenKind.OpenParen)
                {
                    NextToken();
                    arguments = ParseSeparatedList(ParseExpression, TokenKind.CloseParen);
                    Expect(TokenKind.CloseParen);
                }
                return new AttributeSyntax(TextSpan.FromBounds(name.Span.Start, Previous.Span.End), name, arguments);
            }, TokenKind.CloseBracket));
            Expect(TokenKind.CloseBracket);
        }
        return attributes.ToImmutable();
    }

    private static bool IsDeclarationStart(TokenKind kind) => kind is
        TokenKind.FnKeyword or TokenKind.StaticKeyword or TokenKind.RecordKeyword or TokenKind.EnumKeyword or
        TokenKind.TraitKeyword or TokenKind.ExtendKeyword or TokenKind.PrivateKeyword or
        TokenKind.NamespaceKeyword or TokenKind.UsingKeyword;

    /// <summary>
    /// function = [ "static" ] "fn" IDENT [ typeParameters ] "(" [ parameter { "," parameter } [ "," ] ] ")"
    ///            [ "=>" type ] ( block | "=" expression ";" | ";" ) ;
    /// The bodiless form declares a required trait method, and 'static' only makes sense on methods;
    /// the binder reports either one elsewhere.
    /// </summary>
    private FunctionDeclarationSyntax ParseFunction()
    {
        // Modifiers, in either order as in C#: `static`, and the contextual `async`.
        Token? @static = null;
        Token? @async = null;
        var first = Current;
        while (true)
        {
            if (Current.Kind == TokenKind.StaticKeyword && @static is null)
                @static = NextToken();
            else if (IsAsyncModifier(0) && @async is null)
                @async = NextToken();
            else
                break;
        }
        // `operator +(other: T) => T`: an operator, which is declared like a method named by its symbol.
        Token? @operator = null;
        Token name;
        if (IsOperatorStart(0))
        {
            @operator = NextToken();
            if (@static is null && @async is null)
                first = @operator;
            name = NextToken();
        }
        else
        {
            var keyword = Expect(TokenKind.FnKeyword);
            if (@static is null && @async is null)
                first = keyword;
            name = Expect(TokenKind.Identifier);
        }
        var typeParameters = ParseTypeParameters();
        Expect(TokenKind.OpenParen);

        var parameters = ParseSeparatedList(ParseParameter, TokenKind.CloseParen);
        Expect(TokenKind.CloseParen);

        TypeSyntax? returnType = null;
        if (Current.Kind is TokenKind.FatArrow or TokenKind.Arrow)
        {
            // `->` (from older Piper) is reported but read as `=>`.
            Expect(TokenKind.FatArrow);
            if (Previous.Kind != TokenKind.FatArrow)
                NextToken();
            returnType = ParseType();
        }
        var requires = ParseRequires();

        var (outerAsync, outerYield, outerContext) = (_inAsync, _sawYield, _asyncContext);
        _inAsync = @async is not null;
        _asyncContext = _inAsync ? AsyncContext.Async : AsyncContext.Sync;
        _sawYield = false;
        BlockExpressionSyntax? block = null;
        ExpressionSyntax? expression = null;
        if (Current.Kind == TokenKind.Equals)
        {
            NextToken();
            expression = ParseExpression();
            Expect(TokenKind.Semicolon);
        }
        else if (Current.Kind == TokenKind.Semicolon)
        {
            NextToken();
        }
        else
        {
            block = ParseBlock();
        }

        bool isIterator = _sawYield;
        (_inAsync, _sawYield, _asyncContext) = (outerAsync, outerYield, outerContext);
        return new FunctionDeclarationSyntax(SpanFrom(first), name, typeParameters, @static, parameters, returnType, block, expression)
        {
            Async = @async,
            Operator = @operator,
            Requires = requires,
            IsIterator = isIterator,
        };
    }

    /// <summary>`async` is a keyword only where C# treats it as one: before `fn` (or `static fn`) and before a lambda.</summary>
    /// <summary>
    /// requires = { "requires" condition { "," condition } } ;  conditions are expressions without assignment, so a
    /// function's `= body` that follows isn't read as one. `requires` is reserved, so it only means this.
    /// </summary>
    private ImmutableArray<ExpressionSyntax> ParseRequires()
    {
        var conditions = ImmutableArray.CreateBuilder<ExpressionSyntax>();
        while (Current is { Kind: TokenKind.Identifier, Text: "requires" })
        {
            NextToken();
            conditions.Add(ParseHead(() => ParseBinary(0)));
            while (Current.Kind == TokenKind.Comma)
            {
                NextToken();
                conditions.Add(ParseHead(() => ParseBinary(0)));
            }
        }
        return conditions.ToImmutable();
    }

    /// <summary>`test "name"`: `test` is reserved, so it only means this.</summary>
    private bool IsTestStart(int offset) =>
        Peek(offset) is { Kind: TokenKind.Identifier, Text: "test" } &&
        Peek(offset + 1).Kind is TokenKind.StringLiteral or TokenKind.InterpolatedStringLiteral;

    /// <summary>test = [ "async" ] "test" STRING block ;</summary>
    private TestDeclarationSyntax ParseTest()
    {
        var first = Current;
        var @async = Current.Text == "async" ? NextToken() : null;
        NextToken();
        var name = NextToken();
        if (name.Kind == TokenKind.InterpolatedStringLiteral)
            _diagnostics.ReportInterpolatedTestName(name.Span);
        var (outerAsync, outerContext) = (_inAsync, _asyncContext);
        _inAsync = @async is not null;
        _asyncContext = _inAsync ? AsyncContext.Async : AsyncContext.Sync;
        var body = ParseBlock();
        (_inAsync, _asyncContext) = (outerAsync, outerContext);
        var testName = name.Value as string ?? name.Text;
        // The body becomes a function that nothing can call, since its name isn't declared in any scope.
        var functionName = new Token(TokenKind.Identifier, name.Span, $"PiperTest_{name.Span.Start}");
        var function = new FunctionDeclarationSyntax(SpanFrom(first), functionName, [], null, [], null, body, null) { Async = @async };
        return new TestDeclarationSyntax(SpanFrom(first), name, testName, function);
    }

    private bool IsAsyncModifier(int offset) =>
        Peek(offset) is { Kind: TokenKind.Identifier, Text: "async" } &&
        (Peek(offset + 1).Kind is TokenKind.FnKeyword || (Peek(offset + 1).Kind == TokenKind.StaticKeyword && Peek(offset + 2).Kind == TokenKind.FnKeyword));

    /// <summary>A function declaration starts here: `fn`, `static fn`, `async fn`, `static async fn`, `operator +`, ….</summary>
    private bool AtFunctionStart(int offset = 0) =>
        Peek(offset).Kind is TokenKind.FnKeyword || IsAsyncModifier(offset) || IsOperatorStart(offset) ||
        (Peek(offset).Kind == TokenKind.StaticKeyword &&
         (Peek(offset + 1).Kind == TokenKind.FnKeyword || IsAsyncModifier(offset + 1) || IsOperatorStart(offset + 1)));

    /// <summary>`operator` (reserved, so only meaning this) followed by an operator that can be declared.</summary>
    private bool IsOperatorStart(int offset) =>
        Peek(offset) is { Kind: TokenKind.Identifier, Text: "operator" } &&
        Peek(offset + 1).Kind is TokenKind.Plus or TokenKind.Minus or TokenKind.Star or TokenKind.Slash or TokenKind.Percent;

    /// <summary>
    /// typeParameters = "&lt;" typeParameter { "," typeParameter } [ "," ] "&gt;" ;
    /// typeParameter  = IDENT [ ":" type { "+" type } ] ;
    /// </summary>
    private ImmutableArray<TypeParameterSyntax> ParseTypeParameters()
    {
        if (Current.Kind != TokenKind.Less)
            return [];

        NextToken();
        var parameters = ParseSeparatedList(() =>
        {
            var name = Expect(TokenKind.Identifier);
            var constraints = ImmutableArray.CreateBuilder<TypeSyntax>();
            if (Current.Kind == TokenKind.Colon)
            {
                NextToken();
                constraints.Add(ParseType());
                while (Current.Kind == TokenKind.Plus)
                {
                    NextToken();
                    constraints.Add(ParseType());
                }
            }
            return new TypeParameterSyntax(SpanFrom(name), name, constraints.ToImmutable());
        }, TokenKind.Greater);
        Expect(TokenKind.Greater);
        return parameters;
    }

    /// <summary>record = "record" IDENT [ typeParameters ] "(" [ parameter { "," parameter } [ "," ] ] ")" ";" ;</summary>
    private RecordDeclarationSyntax ParseRecord()
    {
        var keyword = Expect(TokenKind.RecordKeyword);
        var name = Expect(TokenKind.Identifier);
        var typeParameters = ParseTypeParameters();
        Expect(TokenKind.OpenParen);
        var fields = ParseSeparatedList(ParseParameter, TokenKind.CloseParen);
        Expect(TokenKind.CloseParen);
        var requires = ParseRequires();
        Expect(TokenKind.Semicolon);
        return new RecordDeclarationSyntax(SpanFrom(keyword), name, typeParameters, fields) { Requires = requires };
    }

    /// <summary>
    /// enum    = "enum" IDENT [ typeParameters ] "{" [ variant { "," variant } [ "," ] ] "}" ;
    /// variant = IDENT [ "(" [ parameter { "," parameter } [ "," ] ] ")" ] ;
    /// </summary>
    private EnumDeclarationSyntax ParseEnum()
    {
        var keyword = Expect(TokenKind.EnumKeyword);
        var name = Expect(TokenKind.Identifier);
        var typeParameters = ParseTypeParameters();
        Expect(TokenKind.OpenBrace);
        var variants = ParseSeparatedList(ParseVariant, TokenKind.CloseBrace);
        Expect(TokenKind.CloseBrace);
        return new EnumDeclarationSyntax(SpanFrom(keyword), name, typeParameters, variants);
    }

    /// <summary>trait = "trait" IDENT [ typeParameters ] [ ":" type { "," type } ] "{" { function } "}" ;</summary>
    private TraitDeclarationSyntax ParseTrait()
    {
        var keyword = Expect(TokenKind.TraitKeyword);
        var name = Expect(TokenKind.Identifier);
        var typeParameters = ParseTypeParameters();
        var parents = ImmutableArray.CreateBuilder<TypeSyntax>();
        if (Current.Kind == TokenKind.Colon)
        {
            NextToken();
            parents.Add(ParseType());
            while (Current.Kind == TokenKind.Comma)
            {
                NextToken();
                parents.Add(ParseType());
            }
        }
        var methods = ParseMethodBlock();
        return new TraitDeclarationSyntax(SpanFrom(keyword), name, typeParameters, parents.ToImmutable(), methods);
    }

    /// <summary>extend = "extend" type [ ":" type { "," type } ] "{" { function } "}" ;</summary>
    private ExtendDeclarationSyntax ParseExtend()
    {
        var keyword = Expect(TokenKind.ExtendKeyword);
        var target = ParseNamedType();
        var traits = ImmutableArray.CreateBuilder<TypeSyntax>();
        if (Current.Kind == TokenKind.Colon)
        {
            NextToken();
            traits.Add(ParseType());
            while (Current.Kind == TokenKind.Comma)
            {
                NextToken();
                traits.Add(ParseType());
            }
        }
        var methods = ParseMethodBlock();
        return new ExtendDeclarationSyntax(SpanFrom(keyword), target.Name, target, traits.ToImmutable(), methods);
    }

    private ImmutableArray<FunctionDeclarationSyntax> ParseMethodBlock()
    {
        Expect(TokenKind.OpenBrace);
        var methods = ImmutableArray.CreateBuilder<FunctionDeclarationSyntax>();
        while (Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile))
        {
            var documentation = DocumentationBefore(Current.Span.Start);
            if (Current.Kind == TokenKind.OpenBracket)
            {
                var attributes = ParseAttributes();
                var @private = Current.Kind == TokenKind.PrivateKeyword && AtFunctionStart(1) ? NextToken() : (Token?)null;
                if (AtFunctionStart())
                    methods.Add(ParseFunction() with { Attributes = attributes, Private = @private, Documentation = documentation });
                else
                    ReportOnce(Current.Span, () => _diagnostics.ReportExpectedToken(Current.Span, TokenKind.FnKeyword, Current));
                continue;
            }

            if (AtFunctionStart())
            {
                methods.Add(ParseFunction() with { Documentation = documentation });
                continue;
            }

            if (Current.Kind == TokenKind.PrivateKeyword && AtFunctionStart(1))
            {
                var @private = NextToken();
                methods.Add(ParseFunction() with { Private = @private, Documentation = documentation });
                continue;
            }

            // Another declaration keyword probably means a missing '}'.
            if (IsDeclarationStart(Current.Kind))
                break;
            ReportOnce(Current.Span, () => _diagnostics.ReportExpectedToken(Current.Span, TokenKind.FnKeyword, Current));
            NextToken();
        }
        Expect(TokenKind.CloseBrace);
        return methods.ToImmutable();
    }

    private VariantSyntax ParseVariant()
    {
        var documentation = DocumentationBefore(Current.Span.Start);
        var name = Expect(TokenKind.Identifier);
        var fields = ImmutableArray<ParameterSyntax>.Empty;
        if (Current.Kind == TokenKind.OpenParen)
        {
            NextToken();
            fields = ParseSeparatedList(ParseParameter, TokenKind.CloseParen);
            Expect(TokenKind.CloseParen);
        }
        return new VariantSyntax(SpanFrom(name), name, fields) { Documentation = documentation };
    }

    /// <summary>parameter = attributes IDENT ":" type [ "=" expression ] ;</summary>
    private ParameterSyntax ParseParameter()
    {
        var documentation = DocumentationBefore(Current.Span.Start);
        var attributes = ParseAttributes();
        var name = Expect(TokenKind.Identifier);
        Expect(TokenKind.Colon);
        var type = ParseType();
        ExpressionSyntax? @default = null;
        if (Current.Kind == TokenKind.Equals)
        {
            NextToken();
            @default = ParseBinary(0);
        }
        return new ParameterSyntax(Span(name, type), name, type) { Attributes = attributes, Documentation = documentation, Default = @default };
    }

    /// <summary>
    /// type = ( namedType | functionType ) { "?" | "[" { "," } "]" } ;
    /// (* T? is Option&lt;T&gt;; the lexer's "??" counts as two. Int[] is an array, Int[,] a two-dimensional one *)
    /// </summary>
    internal TypeSyntax ParseType()
    {
        TypeSyntax type = Current.Kind == TokenKind.OpenParen ? ParseParenthesizedType() : ParseNamedType();
        while (true)
        {
            if (Current.Kind is TokenKind.Question or TokenKind.QuestionQuestion)
            {
                int levels = Current.Kind == TokenKind.QuestionQuestion ? 2 : 1;
                var question = NextToken();
                for (int i = 0; i < levels; i++)
                    type = new OptionalTypeSyntax(TextSpan.FromBounds(type.Span.Start, question.Span.End), type);
            }
            else if (IsRankSpecifier())
            {
                NextToken();
                int rank = 1;
                while (Current.Kind == TokenKind.Comma)
                {
                    NextToken();
                    rank++;
                }
                Expect(TokenKind.CloseBracket);
                type = new ArrayTypeSyntax(TextSpan.FromBounds(type.Span.Start, Previous.Span.End), type, rank);
            }
            else
            {
                return type;
            }
        }
    }

    /// <summary><c>[]</c> or <c>[,]</c> after a type: an array rank specifier (rather than, say, an index).</summary>
    private bool IsRankSpecifier() =>
        Current.Kind == TokenKind.OpenBracket && Peek(1).Kind is TokenKind.CloseBracket or TokenKind.Comma;

    /// <summary>namedType = IDENT [ "&lt;" type { "," type } [ "," ] "&gt;" ] ;</summary>
    private NamedTypeSyntax ParseNamedType()
    {
        // `int`, `string`, …: a builtin type's keyword.
        if (Current.Kind == TokenKind.PredefinedTypeKeyword)
        {
            var keyword = NextToken();
            return new NamedTypeSyntax(keyword.Span, [], keyword, []);
        }

        if (Current.Kind == TokenKind.Identifier)
        {
            var first = Current;
            var parts = ImmutableArray.CreateBuilder<Token>();
            parts.Add(NextToken());
            while (Current.Kind == TokenKind.Dot)
            {
                NextToken();
                parts.Add(Expect(TokenKind.Identifier));
            }
            var name = parts[^1];
            parts.RemoveAt(parts.Count - 1);
            var qualifier = parts.ToImmutable();

            if (Current.Kind != TokenKind.Less)
                return new NamedTypeSyntax(SpanFrom(first), qualifier, name, []);

            NextToken();
            var arguments = ParseSeparatedList(ParseType, TokenKind.Greater);
            Expect(TokenKind.Greater);
            return new NamedTypeSyntax(SpanFrom(first), qualifier, name, arguments);
        }

        ReportOnce(Current.Span, () => _diagnostics.ReportExpectedType(Current.Span, Current));
        var missing = Missing(TokenKind.Identifier);
        return new NamedTypeSyntax(missing.Span, [], missing, []);
    }

    /// <summary>
    /// functionType = "(" [ type { "," type } [ "," ] ] ")" "=>" type ;
    /// tupleType    = "(" tupleElement "," tupleElement { "," tupleElement } ")" ;
    /// tupleElement = [ IDENT ":" ] type ;
    /// </summary>
    private TypeSyntax ParseParenthesizedType()
    {
        var open = NextToken();
        var elements = ParseSeparatedList(() =>
        {
            var start = Current;
            Token? name = null;
            if (Current.Kind == TokenKind.Identifier && Peek(1).Kind == TokenKind.Colon)
            {
                name = NextToken();
                NextToken();
            }
            var type = ParseType();
            return new TupleElementTypeSyntax(Span(start, type), name, type);
        }, TokenKind.CloseParen);
        Expect(TokenKind.CloseParen);

        if (Current.Kind is TokenKind.FatArrow or TokenKind.Arrow)
        {
            Expect(TokenKind.FatArrow);
            if (Previous.Kind != TokenKind.FatArrow)
                NextToken();
            var returnType = ParseType();
            return new FunctionTypeSyntax(Span(open, returnType), [.. elements.Select(e => e.Type)], returnType);
        }

        var tuple = new TupleTypeSyntax(SpanFrom(open), elements);
        if (elements.Length < 2)
            ReportOnce(tuple.Span, () => _diagnostics.ReportTupleTooShort(tuple.Span));
        return tuple;
    }

    /// <summary>qualifiedName = IDENT { "." IDENT } ;</summary>
    private QualifiedNameSyntax ParseQualifiedName()
    {
        var parts = ImmutableArray.CreateBuilder<Token>();
        var first = Expect(TokenKind.Identifier);
        parts.Add(first);
        while (Current.Kind == TokenKind.Dot)
        {
            NextToken();
            parts.Add(Expect(TokenKind.Identifier));
        }
        return new QualifiedNameSyntax(TextSpan.FromBounds(first.Span.Start, Previous.Span.End), parts.ToImmutable());
    }

    private ImmutableArray<T> ParseSeparatedList<T>(Func<T> parseItem, TokenKind close)
    {
        var items = ImmutableArray.CreateBuilder<T>();
        while (Current.Kind != close && Current.Kind != TokenKind.EndOfFile)
        {
            int start = _position;
            items.Add(parseItem());

            if (Current.Kind == TokenKind.Comma)
            {
                NextToken();
                continue;
            }

            if (Current.Kind == close || _position == start || IsListTerminator(Current.Kind))
                break;

            // `f(1 2)`: report the missing ',' and keep parsing items.
            Expect(TokenKind.Comma);
        }
        return items.ToImmutable();
    }

    /// <summary>Tokens that can't continue a list; the caller's Expect(close) reports the error.</summary>
    private static bool IsListTerminator(TokenKind kind) => kind is
        TokenKind.Semicolon or TokenKind.OpenBrace or TokenKind.CloseBrace or TokenKind.EndOfFile or
        TokenKind.FnKeyword or TokenKind.RecordKeyword or TokenKind.EnumKeyword or TokenKind.TraitKeyword or
        TokenKind.ExtendKeyword or TokenKind.LetKeyword or TokenKind.MutKeyword or TokenKind.FatArrow;

    /// <summary>
    /// block = "{" { statement } [ expression ] "}" ;
    /// The optional final expression (no ';') is the block's value.
    /// </summary>
    internal BlockExpressionSyntax ParseBlock() => ParseBlockBody(Expect(TokenKind.OpenBrace));

    /// <summary>The rest of a block after its `{` (<paramref name="open"/>), which has already been read.</summary>
    private BlockExpressionSyntax ParseBlockBody(Token open)
    {
        var noTrailingClosure = _noTrailingClosure;
        _noTrailingClosure = false;
        var statements = ImmutableArray.CreateBuilder<StatementSyntax>();
        ExpressionSyntax? trailing = null;

        while (Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile))
        {
            // A declaration keyword inside a block almost always means a missing '}' (but `using let`/`using (` is a statement).
            if (IsDeclarationStart(Current.Kind) &&
                !(Current.Kind == TokenKind.UsingKeyword && Peek(1).Kind is TokenKind.LetKeyword or TokenKind.MutKeyword or TokenKind.OpenParen))
                break;

            int start = _position;
            switch (Current.Kind)
            {
                case TokenKind.Semicolon:
                    NextToken();
                    break;
                case TokenKind.LetKeyword or TokenKind.MutKeyword:
                    statements.Add(ParseVariableDeclaration());
                    break;
                case TokenKind.WhileKeyword:
                    statements.Add(ParseWhile());
                    break;
                case TokenKind.ForKeyword:
                    statements.Add(ParseFor());
                    break;
                case TokenKind.ReturnKeyword:
                    statements.Add(ParseReturn());
                    break;
                case TokenKind.TryKeyword:
                    statements.Add(ParseTry());
                    break;
                case TokenKind.UsingKeyword:
                    statements.Add(ParseUsing());
                    break;
                case TokenKind.DeferKeyword:
                    statements.Add(ParseDefer());
                    break;
                // `yield return x;`, `yield break;`: `yield` is a keyword only there, as in C#.
                case TokenKind.Identifier when Current.Text == "yield" && Peek(1).Kind is TokenKind.ReturnKeyword or TokenKind.BreakKeyword:
                {
                    var yieldKeyword = NextToken();
                    ExpressionSyntax? value = null;
                    if (NextToken().Kind == TokenKind.ReturnKeyword)
                        value = ParseExpression();
                    Expect(TokenKind.Semicolon);
                    _sawYield = true;
                    statements.Add(new YieldStatementSyntax(SpanFrom(yieldKeyword), yieldKeyword, value));
                    break;
                }
                // `await for x in stream { … }`, `await using …`: the asynchronous forms, as C#'s `await foreach`/`await using`.
                case TokenKind.Identifier when Current.Text == "await" && Peek(1).Kind is TokenKind.ForKeyword or TokenKind.UsingKeyword:
                {
                    var awaitKeyword = NextToken();
                    statements.Add(Current.Kind == TokenKind.ForKeyword
                        ? ParseFor() with { Await = awaitKeyword }
                        : ParseUsing() switch
                        {
                            VariableDeclarationSyntax declaration => declaration with { Await = awaitKeyword },
                            UsingStatementSyntax statement => statement with { Await = awaitKeyword },
                            var other => other,
                        });
                    break;
                }
                case TokenKind.BreakKeyword:
                {
                    var keyword = NextToken();
                    Expect(TokenKind.Semicolon);
                    statements.Add(new BreakStatementSyntax(SpanFrom(keyword), keyword));
                    break;
                }
                case TokenKind.ContinueKeyword:
                {
                    var keyword = NextToken();
                    Expect(TokenKind.Semicolon);
                    statements.Add(new ContinueStatementSyntax(SpanFrom(keyword), keyword));
                    break;
                }
                // `parallel for x in items { … }`: a plugin's statement.
                case TokenKind.Identifier when PluginKeywordAt<StatementKeyword>() is var (plugin, keyword):
                    statements.AddRange(ExpandPlugin(plugin, keyword.Expand));
                    break;
                default:
                    trailing = ParseExpressionStatementOrTrailing(statements);
                    break;
            }

            // The statement parsers report an error without consuming when they see a token that
            // cannot start anything; skip it so we always make progress.
            if (_position == start)
                NextToken();
        }

        Expect(TokenKind.CloseBrace);
        _noTrailingClosure = noTrailingClosure;
        return new BlockExpressionSyntax(SpanFrom(open), statements.ToImmutable(), trailing);
    }

    /// <summary>
    /// expressionStatement = blockLike [ ";" ] | expression ";" ;
    /// blockLike = if | match | block ;
    /// Returns the expression if it is the block's trailing value (immediately followed by '}').
    /// Like Rust, a statement that starts with 'if', 'match' or '{' ends at its closing brace, and so (like Kotlin) does
    /// one that ends with a trailing closure: `parallel(items) { x => … }`.
    /// </summary>
    private ExpressionSyntax? ParseExpressionStatementOrTrailing(ImmutableArray<StatementSyntax>.Builder statements)
    {
        bool isBlockLike = Current.Kind is TokenKind.IfKeyword or TokenKind.MatchKeyword || (Current.Kind == TokenKind.OpenBrace && !AtRecordSpread());
        var expression = Current.Kind switch
        {
            TokenKind.IfKeyword => ParseIf(),
            TokenKind.MatchKeyword => ParseMatch(),
            TokenKind.OpenBrace when !AtRecordSpread() => ParseBlock(),
            _ => ParseExpression(),
        };
        isBlockLike |= expression.Span.End == _trailingClosureEnd;

        if (Current.Kind == TokenKind.Semicolon)
        {
            NextToken();
            statements.Add(new ExpressionStatementSyntax(
                TextSpan.FromBounds(expression.Span.Start, Previous.Span.End), expression));
            return null;
        }

        if (Current.Kind == TokenKind.CloseBrace)
            return expression;

        if (!isBlockLike && expression is not ErrorExpressionSyntax)
            Expect(TokenKind.Semicolon);

        statements.Add(new ExpressionStatementSyntax(expression.Span, expression));
        return null;
    }

    /// <summary>
    /// variableDeclaration = ( "let" | "mut" ) ( IDENT [ ":" type ] | parenthesizedDesignation ) "=" expression ";" ;
    /// </summary>
    private StatementSyntax ParseVariableDeclaration()
    {
        var keyword = NextToken();

        // `let Some(x) = value else { … };` or `let Point(x, y) = p;`: a pattern where a name would be (a list, or a name
        // followed by `(`, `.` or `{`).
        if (Current.Kind == TokenKind.OpenBracket ||
            (Current.Kind == TokenKind.Identifier && Peek(1).Kind is TokenKind.OpenParen or TokenKind.Dot or TokenKind.OpenBrace))
        {
            var pattern = ParsePattern();
            Expect(TokenKind.Equals);
            var value = ParseExpression();
            if (Current.Kind != TokenKind.ElseKeyword)
            {
                Expect(TokenKind.Semicolon);
                return new LetElseStatementSyntax(SpanFrom(keyword), keyword, pattern, value, null);
            }
            NextToken();
            var @else = ParseBlock();
            if (Current.Kind == TokenKind.Semicolon)
                NextToken();
            return new LetElseStatementSyntax(SpanFrom(keyword), keyword, pattern, value, @else);
        }

        if (Current.Kind == TokenKind.OpenParen)
        {
            // `let (a, (b, _)) = value;`
            var designation = ParseParenthesizedDesignation();
            Expect(TokenKind.Equals);
            var value = ParseExpression();
            Expect(TokenKind.Semicolon);
            return new DeconstructionDeclarationSyntax(SpanFrom(keyword), keyword, designation, value);
        }

        var name = Expect(TokenKind.Identifier);
        TypeSyntax? type = null;
        if (Current.Kind == TokenKind.Colon)
        {
            NextToken();
            type = ParseType();
        }

        Expect(TokenKind.Equals);
        var initializer = ParseExpression();
        Expect(TokenKind.Semicolon);
        return new VariableDeclarationSyntax(SpanFrom(keyword), keyword, name, type, initializer);
    }

    /// <summary>
    /// using = "using" ( "let" | "mut" ) IDENT [ ":" type ] "=" expression ";"
    ///       | "using" "(" [ ( "let" | "mut" ) IDENT [ ":" type ] "=" ] expression ")" block ;
    /// </summary>
    private StatementSyntax ParseUsing()
    {
        var keyword = NextToken();
        if (Current.Kind is TokenKind.LetKeyword or TokenKind.MutKeyword)
        {
            var statement = ParseVariableDeclaration();
            if (statement is VariableDeclarationSyntax declaration)
                return declaration with { Span = SpanFrom(keyword), Using = keyword };
            ReportOnce(statement.Span, () => _diagnostics.ReportUsingDeconstruction(statement.Span));
            return statement;
        }

        Expect(TokenKind.OpenParen);
        Token? name = null;
        TypeSyntax? type = null;
        if (Current.Kind is TokenKind.LetKeyword or TokenKind.MutKeyword)
        {
            NextToken();
            name = Expect(TokenKind.Identifier);
            if (Current.Kind == TokenKind.Colon)
            {
                NextToken();
                type = ParseType();
            }
            Expect(TokenKind.Equals);
        }
        var resource = ParseExpression();
        Expect(TokenKind.CloseParen);
        var body = ParseBlock();
        return new UsingStatementSyntax(Span(keyword, body), keyword, name, type, resource, body);
    }

    /// <summary>while = "while" expression block ;</summary>
    private WhileStatementSyntax ParseWhile()
    {
        var keyword = NextToken();
        var condition = ParseHead(ParseExpression);
        var body = ParseBlock();
        return new WhileStatementSyntax(Span(keyword, body), condition, body);
    }

    /// <summary>for = "for" ( IDENT | parenthesizedDesignation ) "in" expression block ;</summary>
    private ForStatementSyntax ParseFor()
    {
        var keyword = NextToken();
        ParenthesizedDesignationSyntax? deconstruction = null;
        Token variable;
        if (Current.Kind == TokenKind.OpenParen)
        {
            deconstruction = ParseParenthesizedDesignation();
            variable = Missing(TokenKind.Identifier);
        }
        else
        {
            variable = Expect(TokenKind.Identifier);
        }
        Expect(TokenKind.InKeyword);
        var range = ParseHead(ParseExpression);
        var body = ParseBlock();
        return new ForStatementSyntax(Span(keyword, body), variable, range, body) { Deconstruction = deconstruction };
    }

    /// <summary>
    /// parenthesizedDesignation = "(" designation "," designation { "," designation } ")" ;
    /// designation              = IDENT | parenthesizedDesignation ;   (* "_" discards *)
    /// </summary>
    private ParenthesizedDesignationSyntax ParseParenthesizedDesignation()
    {
        var open = Expect(TokenKind.OpenParen);
        var elements = ParseSeparatedList(
            () => Current.Kind == TokenKind.OpenParen
                ? ParseParenthesizedDesignation()
                : (DesignationSyntax)new SingleDesignationSyntax(Current.Span, Expect(TokenKind.Identifier)),
            TokenKind.CloseParen);
        Expect(TokenKind.CloseParen);
        var designation = new ParenthesizedDesignationSyntax(SpanFrom(open), elements);
        if (elements.Length < 2)
            ReportOnce(designation.Span, () => _diagnostics.ReportTupleTooShort(designation.Span));
        return designation;
    }

    /// <summary>return = "return" [ expression ] ";" ;</summary>
    private ReturnStatementSyntax ParseReturn(bool requireSemicolon = true)
    {
        var keyword = NextToken();
        ExpressionSyntax? value = Current.Kind is TokenKind.Semicolon or TokenKind.CloseBrace or TokenKind.Comma
            ? null
            : ParseExpression();
        if (requireSemicolon)
            Expect(TokenKind.Semicolon);
        return new ReturnStatementSyntax(SpanFrom(keyword), keyword, value);
    }

    private ExpressionSyntax ParseStandaloneExpression()
    {
        var expression = ParseExpression();
        if (Current.Kind != TokenKind.EndOfFile)
            ReportOnce(Current.Span, () => _diagnostics.ReportUnexpectedTokenInInterpolation(Current.Span, Current));
        return expression;
    }

    /// <summary>expression = assignment ;</summary>
    internal ExpressionSyntax ParseExpression() => ParseAssignment();

    /// <summary>assignment = binary [ assignOp assignment ] ;   (right-associative)</summary>
    private ExpressionSyntax ParseAssignment()
    {
        var target = ParseBinary(0);
        if (!SyntaxFacts.IsAssignmentOperator(Current.Kind))
            return target;

        var op = NextToken();
        var value = ParseAssignment();
        return new AssignmentExpressionSyntax(Span(target, value), target, op, value);
    }

    /// <summary>
    /// binary = unary { binaryOp binary } ;   precedence climbing, see <see cref="SyntaxFacts.GetBinaryPrecedence"/>
    /// unary  = ( "-" | "!" ) unary | postfix ;
    /// </summary>
    private ExpressionSyntax ParseBinary(int parentPrecedence)
    {
        ExpressionSyntax left;
        int unaryPrecedence = SyntaxFacts.GetUnaryPrecedence(Current.Kind);
        if (Current.Kind == TokenKind.Minus && Peek(1) is { Kind: TokenKind.IntegerLiteral, Value: NegatableLiteral min } &&
            !IsPostfixStart(Peek(2).Kind) && unaryPrecedence >= parentPrecedence)
        {
            // `-2147483648`: the one literal too large for Int on its own, as in C#.
            var minus = NextToken();
            var number = NextToken();
            left = new LiteralExpressionSyntax(SpanFrom(minus), number, min.Negated);
        }
        else if (Current is { Kind: TokenKind.Identifier, Text: "await" } && StartsOperand(Peek(1).Kind) &&
                 (_inAsync || Peek(1).Kind != TokenKind.OpenParen) && CastPrecedence >= parentPrecedence)
        {
            // `await task`: like C#'s, a keyword only when an operand follows, and it binds like a unary operator.
            var keyword = NextToken();
            var operand = ParseBinary(CastPrecedence);
            left = new AwaitExpressionSyntax(Span(keyword, operand), keyword, operand);
        }
        else if (Current.Kind == TokenKind.OpenParen && CastPrecedence >= parentPrecedence && IsCast())
        {
            // `(Int)x`: binds like a unary operator, so `(Int)x.y` casts `x.y`.
            var open = NextToken();
            var type = ParseType();
            Expect(TokenKind.CloseParen);
            var operand = ParseBinary(CastPrecedence);
            left = new CastExpressionSyntax(Span(open, operand), type, operand);
        }
        else if (unaryPrecedence != 0 && unaryPrecedence >= parentPrecedence)
        {
            var op = NextToken();
            var operand = ParseBinary(unaryPrecedence);
            left = new UnaryExpressionSyntax(Span(op, operand), op, operand);
        }
        else
        {
            left = ParsePostfix(ParsePrimary());
        }

        while (true)
        {
            // isExpression = binary "is" pattern ;
            if (Current.Kind == TokenKind.IsKeyword && SyntaxFacts.IsPrecedence > parentPrecedence)
            {
                NextToken();
                var pattern = ParsePattern();
                left = new IsExpressionSyntax(TextSpan.FromBounds(left.Span.Start, pattern.Span.End), left, pattern);
                continue;
            }

            int precedence = SyntaxFacts.GetBinaryPrecedence(Current.Kind);
            if (precedence == 0 || precedence <= parentPrecedence)
                return left;

            var op = NextToken();
            var right = ParseBinary(SyntaxFacts.IsRightAssociative(op.Kind) ? precedence - 1 : precedence);
            left = op.Kind switch
            {
                TokenKind.PipeGreater => new PipeExpressionSyntax(Span(left, right), left, op, right),
                TokenKind.DotDot or TokenKind.DotDotLess => new RangeExpressionSyntax(Span(left, right), left, op, right),
                _ => new BinaryExpressionSyntax(Span(left, right), left, op, right),
            };
        }
    }

    /// <summary>
    /// postfix = primary { "(" [ expression { "," expression } [ "," ] ] ")" [ trailingClosure ] | "." IDENT | "?" | "?." IDENT postfixChain }
    ///         | ( IDENT | postfix "." IDENT ) trailingClosure ;
    /// After "?." the rest of the chain is parsed on an implicit receiver, so `a?.b.c()` skips `.c()` too when `a` is None.
    /// </summary>
    private ExpressionSyntax ParsePostfix(ExpressionSyntax expression)
    {
        while (true)
        {
            switch (Current.Kind)
            {
                case TokenKind.QuestionDot:
                {
                    var receiver = new ImplicitReceiverSyntax(NextToken().Span);
                    var name = Expect(TokenKind.Identifier);
                    var first = new MemberAccessExpressionSyntax(TextSpan.FromBounds(receiver.Span.Start, Previous.Span.End), receiver, name);
                    var chain = ParsePostfix(first);
                    return new ConditionalAccessExpressionSyntax(Span(expression, chain), expression, chain);
                }
                case TokenKind.Question:
                    NextToken();
                    expression = new TryExpressionSyntax(TextSpan.FromBounds(expression.Span.Start, Previous.Span.End), expression);
                    break;
                case TokenKind.OpenParen:
                {
                    NextToken();
                    // Lambdas (and trailing closures) are fine inside an argument list, even within a match guard or an `if`'s head.
                    var (inGuard, noTrailingClosure) = (_inGuard, _noTrailingClosure);
                    (_inGuard, _noTrailingClosure) = (false, false);
                    var arguments = ParseSeparatedList(ParseArgument, TokenKind.CloseParen);
                    (_inGuard, _noTrailingClosure) = (inGuard, noTrailingClosure);
                    Expect(TokenKind.CloseParen);
                    expression = new CallExpressionSyntax(
                        TextSpan.FromBounds(expression.Span.Start, Previous.Span.End), expression, arguments);
                    if (AtTrailingClosure())
                        return ParseTrailingClosure(expression);
                    break;
                }
                // `concurrency { … }`, `items.each { x => … }`: a call whose only argument is a trailing closure.
                case TokenKind.OpenBrace when expression is NameExpressionSyntax or MemberAccessExpressionSyntax && AtTrailingClosure():
                    return ParseTrailingClosure(expression);
                case TokenKind.OpenBracket:
                {
                    NextToken();
                    var noTrailingClosure = _noTrailingClosure;
                    _noTrailingClosure = false;
                    var indices = ParseSeparatedList(ParseExpression, TokenKind.CloseBracket);
                    _noTrailingClosure = noTrailingClosure;
                    Expect(TokenKind.CloseBracket);
                    expression = new IndexExpressionSyntax(
                        TextSpan.FromBounds(expression.Span.Start, Previous.Span.End), expression, indices);
                    break;
                }
                case TokenKind.Dot:
                {
                    NextToken();
                    var member = Expect(TokenKind.Identifier);
                    var typeArguments = ParseTypeArgumentsIfGeneric();
                    expression = new MemberAccessExpressionSyntax(
                        TextSpan.FromBounds(expression.Span.Start, Previous.Span.End), expression, member) { TypeArguments = typeArguments };
                    break;
                }
                default:
                    return expression;
            }
        }
    }

    /// <summary>
    /// A `{` that starts a trailing closure: not in an `if`/`while`/`for`/`match` head (where it's the body), and not a
    /// record spread.
    /// </summary>
    private bool AtTrailingClosure() => Current.Kind == TokenKind.OpenBrace && !_noTrailingClosure && !AtRecordSpread();

    /// <summary>
    /// trailingClosure = "{" [ lambdaParameters "=>" ] { statement } [ expression ] "}" ;
    /// Like Kotlin's and Swift's: `f(a) { x => … }` is `f(a, x => { … })`, and `f { … }` is `f(() => { … })`. A closure
    /// that awaits is async: `Retry(3) { await Fetch() }` is `Retry(3, async () => { await Fetch() })`.
    /// <paramref name="callee"/> is the call it's added to, or (when there are no parentheses) the function.
    /// </summary>
    private ExpressionSyntax ParseTrailingClosure(ExpressionSyntax callee)
    {
        var open = NextToken();
        ImmutableArray<LambdaParameterSyntax> parameters = [];
        if ((Current.Kind == TokenKind.Identifier && Peek(1).Kind == TokenKind.FatArrow) ||
            (Current.Kind == TokenKind.OpenParen && IsParenthesizedLambda()))
        {
            parameters = ParseLambdaParameters();
            Expect(TokenKind.FatArrow);
        }

        // Whether it's async isn't known until its end, so `await` is read as an operator throughout. It's async if its
        // body, as plugins have rewritten it, awaits other than in a lambda of its own.
        var (outerYield, outerAsync, outerContext) = (_sawYield, _inAsync, _asyncContext);
        (_inAsync, _asyncContext) = (true, AsyncContext.IfAwaited);
        var body = ParseBlockBody(open);
        (_sawYield, _inAsync, _asyncContext) = (outerYield, outerAsync, outerContext);
        var closure = new LambdaExpressionSyntax(body.Span, parameters, body)
        {
            Async = SyntaxWalker.Awaits(body) ? new Token(TokenKind.Identifier, new TextSpan(open.Span.Start, 0), "async") : null,
        };

        var call = callee is CallExpressionSyntax withArguments
            ? withArguments with { Span = Span(callee, closure), Arguments = withArguments.Arguments.Add(closure) }
            : new CallExpressionSyntax(Span(callee, closure), callee, [closure]);
        _trailingClosureEnd = call.Span.End;

        // `f { … }.Wait()` carries on, but a `(`, `[` or `{` on the next line starts a new statement.
        return Current.Kind is TokenKind.Dot or TokenKind.QuestionDot or TokenKind.Question ? ParsePostfix(call) : call;
    }

    /// <summary>
    /// primary = INT | DOUBLE | CHAR | STRING | interpolatedString | "true" | "false"
    ///         | IDENT | "(" expression ")" | block | if ;
    /// </summary>
    private ExpressionSyntax ParsePrimary()
    {
        switch (Current.Kind)
        {
            case TokenKind.IntegerLiteral or TokenKind.DoubleLiteral or TokenKind.CharLiteral or TokenKind.StringLiteral:
            {
                var literal = NextToken();
                return new LiteralExpressionSyntax(literal.Span, literal, LiteralValue(literal));
            }
            case TokenKind.TrueKeyword or TokenKind.FalseKeyword:
            {
                var literal = NextToken();
                return new LiteralExpressionSyntax(literal.Span, literal, literal.Kind == TokenKind.TrueKeyword);
            }
            case TokenKind.InterpolatedStringLiteral:
                return ParseInterpolatedString();
            case TokenKind.NullKeyword:
            {
                var keyword = NextToken();
                return new NullLiteralExpressionSyntax(keyword.Span, keyword);
            }
            case TokenKind.Identifier when Peek(1).Kind == TokenKind.FatArrow && !_inGuard:
                return ParseLambda();
            // `async x => …`, `async (x) => …`
            case TokenKind.Identifier when Current.Text == "async" && !_inGuard &&
                                           ((Peek(1).Kind == TokenKind.Identifier && Peek(2).Kind == TokenKind.FatArrow) ||
                                            (Peek(1).Kind == TokenKind.OpenParen && IsParenthesizedLambda(start: 1))):
            {
                var asyncKeyword = NextToken();
                var outerAsync = _inAsync;
                _inAsync = true;
                var lambda = ParseLambda(isAsync: true);
                _inAsync = outerAsync;
                return lambda with { Span = TextSpan.FromBounds(asyncKeyword.Span.Start, lambda.Span.End), Async = asyncKeyword };
            }
            // A builtin type's keyword is used like its name: `int.Parse(s)`, `string.Join(…)`, `int[5]`.
            case TokenKind.Identifier or TokenKind.ThisKeyword or TokenKind.PredefinedTypeKeyword:
            {
                var identifier = NextToken();
                var typeArguments = identifier.Kind == TokenKind.Identifier ? ParseTypeArgumentsIfGeneric() : [];
                return new NameExpressionSyntax(SpanFrom(identifier), identifier) { TypeArguments = typeArguments };
            }
            case TokenKind.OpenParen when IsParenthesizedLambda():
                return ParseLambda();
            case TokenKind.OpenParen:
            {
                // `(e)`, or a tuple: `(1, "a")`, `(x: 1, y: 2)`.
                var open = NextToken();
                var first = ParseTupleElement();
                if (first.Name is null && Current.Kind != TokenKind.Comma)
                {
                    Expect(TokenKind.CloseParen);
                    return new ParenthesizedExpressionSyntax(SpanFrom(open), first.Expression);
                }

                var elements = ImmutableArray.CreateBuilder<TupleElementSyntax>();
                elements.Add(first);
                while (Current.Kind == TokenKind.Comma && Peek(1).Kind != TokenKind.CloseParen)
                {
                    NextToken();
                    elements.Add(ParseTupleElement());
                }
                if (Current.Kind == TokenKind.Comma)
                    NextToken();
                Expect(TokenKind.CloseParen);
                var tuple = new TupleExpressionSyntax(SpanFrom(open), elements.ToImmutable());
                if (elements.Count < 2)
                    ReportOnce(tuple.Span, () => _diagnostics.ReportTupleTooShort(tuple.Span));
                return tuple;
            }
            case TokenKind.OpenBracket:
            {
                var open = NextToken();
                var elements = ParseSeparatedList(ParseCollectionElement, TokenKind.CloseBracket);
                Expect(TokenKind.CloseBracket);
                return new CollectionExpressionSyntax(SpanFrom(open), elements);
            }
            case TokenKind.ThrowKeyword:
            {
                var keyword = NextToken();
                var exception = ParseExpression();
                return new ThrowExpressionSyntax(Span(keyword, exception), keyword, exception);
            }
            case TokenKind.OpenBrace when AtRecordSpread():
                return ParseRecordSpread();
            case TokenKind.OpenBrace:
                return ParseBlock();
            case TokenKind.IfKeyword:
                return ParseIf();
            case TokenKind.MatchKeyword:
                return ParseMatch();
            default:
                ReportOnce(Current.Span, () => _diagnostics.ReportExpectedExpression(Current.Span, Current));
                return new ErrorExpressionSyntax(new TextSpan(Current.Span.Start, 0));
        }
    }

    /// <summary>typeArguments = "&lt;" type { "," type } "&gt;" ;   only where <see cref="LooksLikeTypeArguments"/> says so.</summary>
    private ImmutableArray<TypeSyntax> ParseTypeArgumentsIfGeneric()
    {
        if (Current.Kind != TokenKind.Less || !LooksLikeTypeArguments())
            return [];
        NextToken();
        var arguments = ParseSeparatedList(ParseType, TokenKind.Greater);
        Expect(TokenKind.Greater);
        return arguments;
    }

    /// <summary>
    /// C#'s rule for <c>f&lt;A, B&gt;(x)</c> versus <c>f &lt; A</c>: at a '&lt;' after a name, the tokens up to the
    /// matching '&gt;' must be able to form type arguments, and the token after it must be one of
    /// <c>( ) ] } : ; , . ?. ? == != | &amp;&amp; || [</c>. So <c>f(a &lt; b, c &gt; (d))</c> is a generic call, as in C#.
    /// </summary>
    private bool LooksLikeTypeArguments()
    {
        int depth = 0;
        for (int offset = 0; ; offset++)
        {
            switch (Peek(offset).Kind)
            {
                case TokenKind.Less:
                    depth++;
                    break;
                case TokenKind.Greater when --depth == 0:
                    return Peek(offset + 1).Kind is TokenKind.OpenParen or TokenKind.CloseParen or TokenKind.CloseBracket
                        or TokenKind.CloseBrace or TokenKind.Colon or TokenKind.Semicolon or TokenKind.Comma or TokenKind.Dot
                        or TokenKind.QuestionDot or TokenKind.Question or TokenKind.EqualsEquals or TokenKind.BangEquals
                        or TokenKind.Pipe or TokenKind.AmpersandAmpersand or TokenKind.PipePipe or TokenKind.PipeGreater or TokenKind.OpenBracket
                        or TokenKind.EndOfFile;
                case TokenKind.Greater or TokenKind.Identifier or TokenKind.PredefinedTypeKeyword or TokenKind.Dot or TokenKind.Comma or TokenKind.Question
                    or TokenKind.QuestionQuestion or TokenKind.OpenBracket or TokenKind.CloseBracket or TokenKind.OpenParen
                    or TokenKind.CloseParen or TokenKind.Colon:
                    break;
                // A function type's `=>` follows its parameter list's `)`, unlike a lambda's or a match arm's.
                case TokenKind.FatArrow when Peek(offset - 1).Kind == TokenKind.CloseParen:
                    break;
                default:
                    return false;
            }
        }
    }

    /// <summary>argument = [ IDENT ":" ] ( expression | "out" [ "let" | "mut" ] IDENT | "ref" expression ) ;</summary>
    private ExpressionSyntax ParseArgument()
    {
        // `retries: 5`: a named argument.
        if (Current.Kind == TokenKind.Identifier && Peek(1).Kind == TokenKind.Colon)
        {
            var parameterName = NextToken();
            NextToken();
            var value = ParseArgument();
            return new NamedArgumentSyntax(Span(parameterName, value), parameterName, value);
        }

        // `ref x`: `ref` is a keyword only before a name, so it can still be one itself.
        if (Current is { Kind: TokenKind.Identifier, Text: "ref" } && Peek(1).Kind == TokenKind.Identifier)
        {
            var refKeyword = NextToken();
            var target = ParseExpression();
            return new RefArgumentSyntax(Span(refKeyword, target), refKeyword, target);
        }
        if (Current.Kind != TokenKind.OutKeyword)
            return ParseExpression();
        var keyword = NextToken();
        Token? declaration = Current.Kind is TokenKind.LetKeyword or TokenKind.MutKeyword ? NextToken() : null;
        var name = Expect(TokenKind.Identifier);
        return new OutArgumentSyntax(SpanFrom(keyword), keyword, declaration, name);
    }

    /// <summary>A `{` that starts a record spread rather than a block: no block starts with `...`.</summary>
    private bool AtRecordSpread() => Current.Kind == TokenKind.OpenBrace && Peek(1).Kind == TokenKind.Ellipsis;

    /// <summary>
    /// recordSpread = "{" "..." expression { "," fieldUpdate } [ "," ] "}" ;
    /// fieldUpdate  = IDENT [ ":" expression ] ;   (* `height` alone is `height: height` *)
    /// </summary>
    private RecordSpreadExpressionSyntax ParseRecordSpread()
    {
        var open = NextToken();
        NextToken();
        var source = ParseExpression();
        var updates = ImmutableArray.CreateBuilder<FieldUpdateSyntax>();
        while (Current.Kind == TokenKind.Comma)
        {
            NextToken();
            if (Current.Kind == TokenKind.CloseBrace)
                break;
            var name = Expect(TokenKind.Identifier);
            ExpressionSyntax? value = null;
            if (Current.Kind == TokenKind.Colon)
            {
                NextToken();
                value = ParseExpression();
            }
            updates.Add(new FieldUpdateSyntax(SpanFrom(name), name, value));
        }
        Expect(TokenKind.CloseBrace);
        return new RecordSpreadExpressionSyntax(SpanFrom(open), source, updates.ToImmutable());
    }

    /// <summary>tupleElement = [ IDENT ":" ] expression ;</summary>
    private TupleElementSyntax ParseTupleElement()
    {
        var start = Current;
        Token? name = null;
        if (Current.Kind == TokenKind.Identifier && Peek(1).Kind == TokenKind.Colon)
        {
            name = NextToken();
            NextToken();
        }
        var expression = ParseExpression();
        return new TupleElementSyntax(TextSpan.FromBounds(start.Span.Start, expression.Span.End), name, expression);
    }

    /// <summary>collectionElement = [ "..." ] expression ;</summary>
    private ExpressionSyntax ParseCollectionElement()
    {
        if (Current.Kind != TokenKind.Ellipsis)
            return ParseExpression();
        var ellipsis = NextToken();
        var source = ParseExpression();
        return new SpreadElementSyntax(TextSpan.FromBounds(ellipsis.Span.Start, source.Span.End), source);
    }

    private static readonly int CastPrecedence = SyntaxFacts.GetUnaryPrecedence(TokenKind.Minus);

    /// <summary>Tokens that can start an operand, so that `await` before one is the operator rather than a name.</summary>
    private static bool StartsOperand(TokenKind kind) => kind is
        TokenKind.Identifier or TokenKind.PredefinedTypeKeyword or TokenKind.IntegerLiteral or TokenKind.DoubleLiteral or
        TokenKind.CharLiteral or TokenKind.StringLiteral or TokenKind.InterpolatedStringLiteral or TokenKind.OpenParen or
        TokenKind.OpenBracket or TokenKind.ThisKeyword or TokenKind.TrueKeyword or TokenKind.FalseKeyword or
        TokenKind.NullKeyword or TokenKind.Bang or TokenKind.IfKeyword or TokenKind.MatchKeyword;

    /// <summary>
    /// C#'s rule for <c>(T)x</c> versus a parenthesized expression: the tokens inside the parentheses must be able to
    /// form a type, and the token after them must be one that can start an operand: an identifier, a literal,
    /// <c>(</c>, <c>!</c>, <c>this</c>, <c>true</c>, <c>false</c> or <c>null</c>. So <c>(Int)x</c> and
    /// <c>(Byte)(a + b)</c> are casts, but <c>(a) - b</c> is a subtraction.
    /// </summary>
    private bool IsCast()
    {
        // As in C#, a builtin type keyword alone in parentheses is always a cast: `(int)-x`.
        if (Peek(1).Kind == TokenKind.PredefinedTypeKeyword && Peek(2).Kind == TokenKind.CloseParen)
            return true;
        if (Peek(1).Kind is not (TokenKind.Identifier or TokenKind.OpenParen or TokenKind.PredefinedTypeKeyword))
            return false;
        int depth = 0;
        for (int offset = 0; ; offset++)
        {
            switch (Peek(offset).Kind)
            {
                case TokenKind.OpenParen:
                    depth++;
                    break;
                case TokenKind.CloseParen when --depth == 0:
                    return Peek(offset + 1).Kind is TokenKind.Identifier or TokenKind.PredefinedTypeKeyword or TokenKind.IntegerLiteral or TokenKind.DoubleLiteral
                        or TokenKind.CharLiteral or TokenKind.StringLiteral or TokenKind.InterpolatedStringLiteral or TokenKind.OpenParen
                        or TokenKind.Bang or TokenKind.ThisKeyword or TokenKind.TrueKeyword or TokenKind.FalseKeyword or TokenKind.NullKeyword;
                case TokenKind.CloseParen or TokenKind.Identifier or TokenKind.PredefinedTypeKeyword or TokenKind.Dot or TokenKind.Less or TokenKind.Greater
                    or TokenKind.Comma or TokenKind.Question or TokenKind.QuestionQuestion or TokenKind.OpenBracket
                    or TokenKind.CloseBracket or TokenKind.Colon:
                    break;
                case TokenKind.FatArrow when Peek(offset - 1).Kind == TokenKind.CloseParen:
                    break;
                default:
                    return false;
            }
        }
    }

    /// <summary>True at a '(' that starts a lambda: the matching ')' is followed by '=>'.</summary>
    private bool IsParenthesizedLambda(int start = 0)
    {
        if (_inGuard)
            return false;
        int depth = 0;
        for (int offset = start; ; offset++)
        {
            var kind = Peek(offset).Kind;
            if (kind == TokenKind.EndOfFile)
                return false;
            if (kind == TokenKind.OpenParen)
                depth++;
            else if (kind == TokenKind.CloseParen && --depth == 0)
                return Peek(offset + 1).Kind == TokenKind.FatArrow;
        }
    }

    /// <summary>
    /// lambda          = ( IDENT | "(" [ lambdaParameter { "," lambdaParameter } [ "," ] ] ")" ) "=>" ( block | expression ) ;
    /// lambdaParameter = IDENT [ ":" type ] ;
    /// </summary>
    private LambdaExpressionSyntax ParseLambda(bool isAsync = false)
    {
        var first = Current;
        var parameters = ParseLambdaParameters();
        Expect(TokenKind.FatArrow);
        // A lambda's `yield` doesn't make the enclosing function an iterator (it's reported: lambdas can't be iterators),
        // and its `await` doesn't make an enclosing trailing closure async.
        var (outerYield, outerContext) = (_sawYield, _asyncContext);
        _asyncContext = isAsync ? AsyncContext.Async : AsyncContext.Sync;
        ExpressionSyntax body = Current.Kind == TokenKind.OpenBrace && !AtRecordSpread() ? ParseBlock() : ParseExpression();
        (_sawYield, _asyncContext) = (outerYield, outerContext);
        return new LambdaExpressionSyntax(TextSpan.FromBounds(first.Span.Start, body.Span.End), parameters, body);
    }

    /// <summary>lambdaParameters = IDENT | "(" [ lambdaParameter { "," lambdaParameter } [ "," ] ] ")" ;</summary>
    internal ImmutableArray<LambdaParameterSyntax> ParseLambdaParameters()
    {
        if (Current.Kind == TokenKind.Identifier)
        {
            var name = NextToken();
            return [new LambdaParameterSyntax(name.Span, name, null)];
        }

        Expect(TokenKind.OpenParen);
        var parameters = ParseSeparatedList(() =>
        {
            var name = Expect(TokenKind.Identifier);
            TypeSyntax? type = null;
            if (Current.Kind == TokenKind.Colon)
            {
                NextToken();
                type = ParseType();
            }
            return new LambdaParameterSyntax(SpanFrom(name), name, type);
        }, TokenKind.CloseParen);
        Expect(TokenKind.CloseParen);
        return parameters;
    }

    /// <summary>
    /// Parses the head of an `if`, `while`, `for`, `match` or `requires`, where a `{` ends the head rather than starting
    /// a trailing closure.
    /// </summary>
    internal T ParseHead<T>(Func<T> parse)
    {
        var noTrailingClosure = _noTrailingClosure;
        _noTrailingClosure = true;
        var head = parse();
        _noTrailingClosure = noTrailingClosure;
        return head;
    }

    /// <summary>defer = "defer" ( block [ ";" ] | expression ";" ) ;</summary>
    private DeferStatementSyntax ParseDefer()
    {
        var keyword = NextToken();
        ExpressionSyntax body;
        if (Current.Kind == TokenKind.OpenBrace && !AtRecordSpread())
        {
            body = ParseBlock();
            if (Current.Kind == TokenKind.Semicolon)
                NextToken();
        }
        else
        {
            body = ParseExpression();
            Expect(TokenKind.Semicolon);
        }
        return new DeferStatementSyntax(SpanFrom(keyword), keyword, body);
    }

    /// <summary>
    /// try   = "try" block { catch } [ "finally" block ] ;
    /// catch = "catch" [ "(" IDENT [ ":" type ] ")" ] block ;
    /// </summary>
    private TryStatementSyntax ParseTry()
    {
        var keyword = NextToken();
        var body = ParseBlock();
        var catches = ImmutableArray.CreateBuilder<CatchClauseSyntax>();
        while (Current.Kind == TokenKind.CatchKeyword)
        {
            var catchKeyword = NextToken();
            Token? name = null;
            TypeSyntax? type = null;
            if (Current.Kind == TokenKind.OpenParen)
            {
                NextToken();
                name = Expect(TokenKind.Identifier);
                if (Current.Kind == TokenKind.Colon)
                {
                    NextToken();
                    type = ParseType();
                }
                Expect(TokenKind.CloseParen);
            }
            var catchBody = ParseBlock();
            catches.Add(new CatchClauseSyntax(SpanFrom(catchKeyword), catchKeyword, name, type, catchBody));
        }

        BlockExpressionSyntax? @finally = null;
        if (Current.Kind == TokenKind.FinallyKeyword)
        {
            NextToken();
            @finally = ParseBlock();
        }

        if (catches.Count == 0 && @finally is null)
            Expect(TokenKind.CatchKeyword);
        return new TryStatementSyntax(SpanFrom(keyword), body, catches.ToImmutable(), @finally);
    }

    /// <summary>if = "if" expression block [ "else" ( block | if ) ] ;</summary>
    private IfExpressionSyntax ParseIf()
    {
        var keyword = Expect(TokenKind.IfKeyword);
        var condition = ParseHead(ParseExpression);
        var then = ParseBlock();
        ExpressionSyntax? @else = null;
        if (Current.Kind == TokenKind.ElseKeyword)
        {
            NextToken();
            @else = Current.Kind == TokenKind.IfKeyword ? ParseIf() : ParseBlock();
        }
        return new IfExpressionSyntax(SpanFrom(keyword), condition, then, @else);
    }

    /// <summary>
    /// match   = "match" expression "{" { arm ( "," | (* nothing after a block-like body *) ) } "}" ;
    /// arm     = pattern [ "if" expression ] "=>" armBody ;
    /// armBody = expression | "return" [ expression ] | "break" | "continue" ;
    /// </summary>
    private MatchExpressionSyntax ParseMatch()
    {
        var keyword = Expect(TokenKind.MatchKeyword);
        var scrutinee = ParseHead(ParseExpression);
        Expect(TokenKind.OpenBrace);

        var arms = ImmutableArray.CreateBuilder<MatchArmSyntax>();
        while (Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile) && !IsDeclarationStart(Current.Kind))
        {
            int start = _position;
            var pattern = ParsePattern();
            ExpressionSyntax? guard = null;
            if (Current.Kind == TokenKind.IfKeyword)
            {
                NextToken();
                // `x if ok => …`: the guard's `ok =>` must not be read as the start of a lambda.
                _inGuard = true;
                guard = ParseExpression();
                _inGuard = false;
            }
            Expect(TokenKind.FatArrow);
            var body = ParseArmBody();
            arms.Add(new MatchArmSyntax(TextSpan.FromBounds(pattern.Span.Start, body.Span.End), pattern, guard, body));

            if (Current.Kind == TokenKind.Comma)
                NextToken();
            else if (Current.Kind != TokenKind.CloseBrace && body is not (BlockExpressionSyntax or IfExpressionSyntax or MatchExpressionSyntax))
                Expect(TokenKind.Comma);

            if (_position == start)
                NextToken();
        }

        Expect(TokenKind.CloseBrace);
        return new MatchExpressionSyntax(SpanFrom(keyword), keyword, scrutinee, arms.ToImmutable());
    }

    /// <summary>`return`, `break` and `continue` arms are wrapped in a block so the rest of the compiler sees an ordinary block.</summary>
    private ExpressionSyntax ParseArmBody()
    {
        StatementSyntax? jump = Current.Kind switch
        {
            TokenKind.ReturnKeyword => ParseReturn(requireSemicolon: false),
            TokenKind.BreakKeyword => new BreakStatementSyntax(Current.Span, NextToken()),
            TokenKind.ContinueKeyword => new ContinueStatementSyntax(Current.Span, NextToken()),
            _ => null,
        };
        return jump is null ? ParseExpression() : new BlockExpressionSyntax(jump.Span, [jump], null);
    }

    /// <summary>pattern = primaryPattern { "|" primaryPattern } ;</summary>
    internal PatternSyntax ParsePattern()
    {
        var first = ParsePrimaryPattern();
        if (Current.Kind != TokenKind.Pipe)
            return first;

        var alternatives = ImmutableArray.CreateBuilder<PatternSyntax>();
        alternatives.Add(first);
        while (Current.Kind == TokenKind.Pipe)
        {
            NextToken();
            alternatives.Add(ParsePrimaryPattern());
        }
        return new OrPatternSyntax(TextSpan.FromBounds(first.Span.Start, Previous.Span.End), alternatives.ToImmutable());
    }

    /// <summary>
    /// primaryPattern = "_" | IDENT | [ IDENT "." ] IDENT [ "(" [ pattern { "," pattern } [ "," ] ] ")" | properties ]
    ///                | properties | "[" [ listElement { "," listElement } [ "," ] ] "]"
    ///                | ( "<" | "<=" | ">" | ">=" ) literalPattern
    ///                | literalPattern [ ( ".." | "..<" ) literalPattern ] ;
    /// properties     = "{" [ IDENT ":" pattern { "," IDENT ":" pattern } [ "," ] ] "}" ;
    /// listElement    = pattern | "..." [ IDENT ] ;
    /// literalPattern = [ "-" ] ( INT | DOUBLE ) | CHAR | STRING | "true" | "false" ;
    /// </summary>
    private PatternSyntax ParsePrimaryPattern()
    {
        if (Current.Kind == TokenKind.NullKeyword)
            return new NullPatternSyntax(NextToken().Span);

        if (Current.Kind == TokenKind.OpenBracket)
        {
            var open = NextToken();
            var elements = ParseSeparatedList(ParseListPatternElement, TokenKind.CloseBracket);
            Expect(TokenKind.CloseBracket);
            return new ListPatternSyntax(SpanFrom(open), elements);
        }

        if (IsPropertyPatternStart())
            return ParsePropertyPattern(Current, null, null);

        if (Current.Kind is TokenKind.Less or TokenKind.LessEquals or TokenKind.Greater or TokenKind.GreaterEquals)
        {
            var comparison = NextToken();
            if (ParseLiteralPattern() is { } value)
                return new RelationalPatternSyntax(SpanFrom(comparison), comparison, value);
            ReportOnce(Current.Span, () => _diagnostics.ReportExpectedPattern(Current.Span, Current));
            return new ErrorPatternSyntax(comparison.Span);
        }

        if (Current.Kind == TokenKind.OpenParen)
        {
            // `(0, _)`: a tuple pattern.
            var open = NextToken();
            var elements = ParseSeparatedList(ParsePattern, TokenKind.CloseParen);
            Expect(TokenKind.CloseParen);
            var tuple = new TuplePatternSyntax(SpanFrom(open), elements);
            if (elements.Length < 2)
                ReportOnce(tuple.Span, () => _diagnostics.ReportTupleTooShort(tuple.Span));
            return tuple;
        }

        if (Current.Kind == TokenKind.Identifier)
        {
            var name = NextToken();
            if (name.Text == "_")
                return new WildcardPatternSyntax(name.Span);

            Token? qualifier = null;
            if (Current.Kind == TokenKind.Dot)
            {
                NextToken();
                qualifier = name;
                name = Expect(TokenKind.Identifier);
            }

            if (Current.Kind == TokenKind.OpenParen)
            {
                NextToken();
                var arguments = ParseSeparatedList(ParsePattern, TokenKind.CloseParen);
                Expect(TokenKind.CloseParen);
                return new ConstructorPatternSyntax(SpanFrom(qualifier ?? name), qualifier, name, arguments, HasArgumentList: true);
            }

            if (IsPropertyPatternStart())
                return ParsePropertyPattern(qualifier ?? name, qualifier, name);

            return qualifier is null
                ? new IdentifierPatternSyntax(name.Span, name)
                : new ConstructorPatternSyntax(SpanFrom(qualifier), qualifier, name, [], HasArgumentList: false);
        }

        if (ParseLiteralPattern() is not { } literal)
        {
            ReportOnce(Current.Span, () => _diagnostics.ReportExpectedPattern(Current.Span, Current));
            return new ErrorPatternSyntax(new TextSpan(Current.Span.Start, 0));
        }

        if (Current.Kind is not (TokenKind.DotDot or TokenKind.DotDotLess))
            return literal;

        var op = NextToken();
        if (ParseLiteralPattern() is not { } end)
        {
            ReportOnce(Current.Span, () => _diagnostics.ReportExpectedPattern(Current.Span, Current));
            return new ErrorPatternSyntax(TextSpan.FromBounds(literal.Span.Start, op.Span.End));
        }
        return new RangePatternSyntax(Span(literal, end), literal, op, end);
    }

    /// <summary>
    /// A `{` that starts a property pattern rather than a block (`if p is Point { … }` has both): one followed by
    /// `name:`, or an empty `{}` that isn't followed by a block.
    /// </summary>
    private bool IsPropertyPatternStart() =>
        Current.Kind == TokenKind.OpenBrace &&
        ((Peek(1).Kind == TokenKind.Identifier && Peek(2).Kind == TokenKind.Colon) ||
         (Peek(1).Kind == TokenKind.CloseBrace && Peek(2).Kind != TokenKind.OpenBrace));

    private PropertyPatternSyntax ParsePropertyPattern(Token first, Token? qualifier, Token? name)
    {
        Expect(TokenKind.OpenBrace);
        var properties = ParseSeparatedList(() =>
        {
            var property = Expect(TokenKind.Identifier);
            Expect(TokenKind.Colon);
            var pattern = ParsePattern();
            return new PropertySubpatternSyntax(TextSpan.FromBounds(property.Span.Start, pattern.Span.End), property, pattern);
        }, TokenKind.CloseBrace);
        Expect(TokenKind.CloseBrace);
        return new PropertyPatternSyntax(SpanFrom(first), qualifier, name, properties);
    }

    private PatternSyntax ParseListPatternElement()
    {
        if (Current.Kind != TokenKind.Ellipsis)
            return ParsePattern();
        var ellipsis = NextToken();
        Token? name = Current.Kind == TokenKind.Identifier ? NextToken() : null;
        return new SlicePatternSyntax(SpanFrom(ellipsis), name);
    }

    private LiteralPatternSyntax? ParseLiteralPattern()
    {
        if (Current.Kind == TokenKind.Minus && Peek(1).Kind is TokenKind.IntegerLiteral or TokenKind.DoubleLiteral)
        {
            var minus = NextToken();
            var number = NextToken();
            object? negated = number.Value switch
            {
                int i => -i,
                long l => -l,
                double d => -d,
                NegatableLiteral min => min.Negated,
                _ => null,
            };
            return new LiteralPatternSyntax(TextSpan.FromBounds(minus.Span.Start, number.Span.End), number, negated);
        }

        switch (Current.Kind)
        {
            case TokenKind.IntegerLiteral or TokenKind.DoubleLiteral or TokenKind.CharLiteral or TokenKind.StringLiteral:
            {
                var literal = NextToken();
                return new LiteralPatternSyntax(literal.Span, literal, LiteralValue(literal));
            }
            case TokenKind.TrueKeyword or TokenKind.FalseKeyword:
            {
                var literal = NextToken();
                return new LiteralPatternSyntax(literal.Span, literal, literal.Kind == TokenKind.TrueKeyword);
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// interpolatedString = '"' { text | "{" expression [ "," alignment ] [ ":" format ] "}" } '"' ;  each hole is lexed
    /// and parsed separately. As in C#, a `,` or `:` outside brackets ends the expression: `{price,10:C}`.
    /// </summary>
    private InterpolatedStringExpressionSyntax ParseInterpolatedString()
    {
        var token = NextToken();
        var parts = ImmutableArray.CreateBuilder<InterpolatedStringPartSyntax>();
        foreach (var part in token.Parts)
        {
            switch (part)
            {
                case InterpolationText text:
                    parts.Add(new InterpolatedTextSyntax(text.Span, text.Value));
                    break;
                case InterpolationHole hole:
                {
                    var (expressionSpan, alignment, format) = SplitHole(hole.Span);
                    var holeParser = new Parser(_source, _diagnostics, Lexer.Tokenize(_source, _diagnostics, expressionSpan));
                    var expression = holeParser.ParseStandaloneExpression();
                    parts.Add(new InterpolationSyntax(hole.Span, expression) { Alignment = alignment, Format = format });
                    break;
                }
            }
        }
        return new InterpolatedStringExpressionSyntax(token.Span, parts.ToImmutable());
    }

    /// <summary>
    /// Splits a hole into its expression and, after a `,` or `:` at its top level (outside brackets and literals), its
    /// alignment and format: `price,10:C` is `price`, 10 and "C".
    /// </summary>
    private (TextSpan Expression, int? Alignment, string? Format) SplitHole(TextSpan hole)
    {
        int depth = 0, comma = -1, colon = -1;
        for (int i = hole.Start; i < hole.End && colon < 0; i++)
        {
            switch (_source[i])
            {
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    depth--;
                    break;
                case '"' or '\'':
                    char quote = _source[i];
                    for (i++; i < hole.End && _source[i] != quote; i++)
                    {
                        if (_source[i] == '\\')
                            i++;
                    }
                    break;
                case ',' when depth == 0 && comma < 0:
                    comma = i;
                    break;
                case ':' when depth == 0:
                    colon = i;
                    break;
            }
        }
        int expressionEnd = comma >= 0 ? comma : colon >= 0 ? colon : hole.End;
        int? alignment = null;
        if (comma >= 0)
        {
            var span = TextSpan.FromBounds(comma + 1, colon >= 0 ? colon : hole.End);
            if (int.TryParse(_source.ToString(span).Trim(), out var value))
                alignment = value;
            else
                _diagnostics.ReportInvalidAlignment(span);
        }
        string? format = colon >= 0 ? _source.ToString(TextSpan.FromBounds(colon + 1, hole.End)) : null;
        return (TextSpan.FromBounds(hole.Start, expressionEnd), alignment, format);
    }
}
