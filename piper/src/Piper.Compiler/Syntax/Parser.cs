using System.Collections.Immutable;
using Piper.Diagnostics;
using Piper.Text;

namespace Piper.Syntax;

/// <summary>
/// Hand-written recursive-descent parser; expressions use Pratt parsing driven by <see cref="SyntaxFacts"/>.
/// The parser never throws on bad input: it reports a diagnostic, synthesizes a missing token or
/// error node, and resynchronizes so later errors are still found. The grammar of each construct is
/// given (informally, EBNF-style) in the doc comment of its Parse method.
/// </summary>
public sealed class Parser
{
    private readonly SourceText _source;
    private readonly DiagnosticBag _diagnostics;
    private readonly ImmutableArray<Token> _tokens;
    private int _position;
    private int _lastErrorPosition = -1;

    private Parser(SourceText source, DiagnosticBag diagnostics, ImmutableArray<Token> tokens)
    {
        _source = source;
        _diagnostics = diagnostics;
        _tokens = tokens;
    }

    public static CompilationUnitSyntax ParseCompilationUnit(SourceText source, DiagnosticBag diagnostics)
    {
        var parser = new Parser(source, diagnostics, Lexer.Tokenize(source, diagnostics));
        return parser.ParseCompilationUnit();
    }

    /// <summary>Parses a single expression that must span all of <paramref name="source"/>. Used by tests and tools.</summary>
    public static ExpressionSyntax ParseExpression(SourceText source, DiagnosticBag diagnostics)
    {
        var parser = new Parser(source, diagnostics, Lexer.Tokenize(source, diagnostics));
        return parser.ParseStandaloneExpression();
    }

    private Token Current => Peek(0);

    private Token Peek(int offset)
    {
        int index = Math.Min(_position + offset, _tokens.Length - 1);
        return _tokens[index];
    }

    private Token Previous => _tokens[Math.Max(_position - 1, 0)];

    private Token NextToken()
    {
        var token = Current;
        if (token.Kind != TokenKind.EndOfFile)
            _position++;
        return token;
    }

    private Token Expect(TokenKind kind)
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

    /// <summary>compilationUnit = { declaration } EOF ;</summary>
    private CompilationUnitSyntax ParseCompilationUnit()
    {
        var declarations = ImmutableArray.CreateBuilder<DeclarationSyntax>();
        while (Current.Kind != TokenKind.EndOfFile)
        {
            int start = _position;
            if (ParseDeclaration() is { } declaration)
                declarations.Add(declaration);
            if (_position == start)
                NextToken();
        }
        return new CompilationUnitSyntax(new TextSpan(0, _source.Length), declarations.ToImmutable());
    }

    /// <summary>declaration = function | record ;</summary>
    private DeclarationSyntax? ParseDeclaration()
    {
        switch (Current.Kind)
        {
            case TokenKind.FnKeyword:
                return ParseFunction();
            case TokenKind.RecordKeyword:
                return ParseRecord();
            default:
                ReportOnce(Current.Span, () => _diagnostics.ReportExpectedDeclaration(Current.Span, Current));
                while (Current.Kind is not (TokenKind.FnKeyword or TokenKind.RecordKeyword or TokenKind.EndOfFile))
                    NextToken();
                return null;
        }
    }

    /// <summary>
    /// function = "fn" IDENT "(" [ parameter { "," parameter } [ "," ] ] ")" [ "->" type ]
    ///            ( block | "=" expression ";" ) ;
    /// </summary>
    private FunctionDeclarationSyntax ParseFunction()
    {
        var keyword = Expect(TokenKind.FnKeyword);
        var name = Expect(TokenKind.Identifier);
        Expect(TokenKind.OpenParen);
        var parameters = ParseSeparatedList(ParseParameter, TokenKind.CloseParen);
        Expect(TokenKind.CloseParen);

        TypeSyntax? returnType = null;
        if (Current.Kind == TokenKind.Arrow)
        {
            NextToken();
            returnType = ParseType();
        }

        if (Current.Kind == TokenKind.Equals)
        {
            NextToken();
            var expression = ParseExpression();
            Expect(TokenKind.Semicolon);
            return new FunctionDeclarationSyntax(SpanFrom(keyword), name, parameters, returnType, null, expression);
        }

        var body = ParseBlock();
        return new FunctionDeclarationSyntax(SpanFrom(keyword), name, parameters, returnType, body, null);
    }

    /// <summary>record = "record" IDENT "(" [ parameter { "," parameter } [ "," ] ] ")" ";" ;</summary>
    private RecordDeclarationSyntax ParseRecord()
    {
        var keyword = Expect(TokenKind.RecordKeyword);
        var name = Expect(TokenKind.Identifier);
        Expect(TokenKind.OpenParen);
        var fields = ParseSeparatedList(ParseParameter, TokenKind.CloseParen);
        Expect(TokenKind.CloseParen);
        Expect(TokenKind.Semicolon);
        return new RecordDeclarationSyntax(SpanFrom(keyword), name, fields);
    }

    /// <summary>parameter = IDENT ":" type ;</summary>
    private ParameterSyntax ParseParameter()
    {
        var name = Expect(TokenKind.Identifier);
        Expect(TokenKind.Colon);
        var type = ParseType();
        return new ParameterSyntax(Span(name, type), name, type);
    }

    /// <summary>type = IDENT ;</summary>
    private TypeSyntax ParseType()
    {
        if (Current.Kind == TokenKind.Identifier)
        {
            var name = NextToken();
            return new NamedTypeSyntax(name.Span, name);
        }

        ReportOnce(Current.Span, () => _diagnostics.ReportExpectedType(Current.Span, Current));
        var missing = Missing(TokenKind.Identifier);
        return new NamedTypeSyntax(missing.Span, missing);
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
        TokenKind.FnKeyword or TokenKind.RecordKeyword or TokenKind.LetKeyword or TokenKind.VarKeyword;

    /// <summary>
    /// block = "{" { statement } [ expression ] "}" ;
    /// The optional final expression (no ';') is the block's value.
    /// </summary>
    private BlockExpressionSyntax ParseBlock()
    {
        var open = Expect(TokenKind.OpenBrace);
        var statements = ImmutableArray.CreateBuilder<StatementSyntax>();
        ExpressionSyntax? trailing = null;

        while (Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile))
        {
            // A declaration keyword inside a block almost always means a missing '}'.
            if (Current.Kind is TokenKind.FnKeyword or TokenKind.RecordKeyword)
                break;

            int start = _position;
            switch (Current.Kind)
            {
                case TokenKind.Semicolon:
                    NextToken();
                    break;
                case TokenKind.LetKeyword or TokenKind.VarKeyword:
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
        return new BlockExpressionSyntax(SpanFrom(open), statements.ToImmutable(), trailing);
    }

    /// <summary>
    /// expressionStatement = blockLike [ ";" ] | expression ";" ;
    /// blockLike = if | block ;
    /// Returns the expression if it is the block's trailing value (immediately followed by '}').
    /// Like Rust, a statement that starts with 'if' or '{' ends at its closing brace.
    /// </summary>
    private ExpressionSyntax? ParseExpressionStatementOrTrailing(ImmutableArray<StatementSyntax>.Builder statements)
    {
        bool isBlockLike = Current.Kind is TokenKind.IfKeyword or TokenKind.OpenBrace;
        var expression = isBlockLike
            ? Current.Kind == TokenKind.IfKeyword ? ParseIf() : ParseBlock()
            : ParseExpression();

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

    /// <summary>variableDeclaration = ( "let" | "var" ) IDENT [ ":" type ] "=" expression ";" ;</summary>
    private VariableDeclarationSyntax ParseVariableDeclaration()
    {
        var keyword = NextToken();
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

    /// <summary>while = "while" expression block ;</summary>
    private WhileStatementSyntax ParseWhile()
    {
        var keyword = NextToken();
        var condition = ParseExpression();
        var body = ParseBlock();
        return new WhileStatementSyntax(Span(keyword, body), condition, body);
    }

    /// <summary>for = "for" IDENT "in" expression block ;</summary>
    private ForStatementSyntax ParseFor()
    {
        var keyword = NextToken();
        var variable = Expect(TokenKind.Identifier);
        Expect(TokenKind.InKeyword);
        var range = ParseExpression();
        var body = ParseBlock();
        return new ForStatementSyntax(Span(keyword, body), variable, range, body);
    }

    /// <summary>return = "return" [ expression ] ";" ;</summary>
    private ReturnStatementSyntax ParseReturn()
    {
        var keyword = NextToken();
        ExpressionSyntax? value = Current.Kind is TokenKind.Semicolon or TokenKind.CloseBrace ? null : ParseExpression();
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
    private ExpressionSyntax ParseExpression() => ParseAssignment();

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
        if (unaryPrecedence != 0 && unaryPrecedence >= parentPrecedence)
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
            int precedence = SyntaxFacts.GetBinaryPrecedence(Current.Kind);
            if (precedence == 0 || precedence <= parentPrecedence)
                return left;

            var op = NextToken();
            var right = ParseBinary(precedence);
            left = op.Kind is TokenKind.DotDot or TokenKind.DotDotEquals
                ? new RangeExpressionSyntax(Span(left, right), left, op, right)
                : new BinaryExpressionSyntax(Span(left, right), left, op, right);
        }
    }

    /// <summary>postfix = primary { "(" [ expression { "," expression } [ "," ] ] ")" | "." IDENT } ;</summary>
    private ExpressionSyntax ParsePostfix(ExpressionSyntax expression)
    {
        while (true)
        {
            switch (Current.Kind)
            {
                case TokenKind.OpenParen:
                    NextToken();
                    var arguments = ParseSeparatedList(ParseExpression, TokenKind.CloseParen);
                    Expect(TokenKind.CloseParen);
                    expression = new CallExpressionSyntax(
                        TextSpan.FromBounds(expression.Span.Start, Previous.Span.End), expression, arguments);
                    break;
                case TokenKind.Dot:
                    NextToken();
                    var member = Expect(TokenKind.Identifier);
                    expression = new MemberAccessExpressionSyntax(
                        TextSpan.FromBounds(expression.Span.Start, Previous.Span.End), expression, member);
                    break;
                default:
                    return expression;
            }
        }
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
                return new LiteralExpressionSyntax(literal.Span, literal, literal.Value);
            }
            case TokenKind.TrueKeyword or TokenKind.FalseKeyword:
            {
                var literal = NextToken();
                return new LiteralExpressionSyntax(literal.Span, literal, literal.Kind == TokenKind.TrueKeyword);
            }
            case TokenKind.InterpolatedStringLiteral:
                return ParseInterpolatedString();
            case TokenKind.Identifier:
            {
                var identifier = NextToken();
                return new NameExpressionSyntax(identifier.Span, identifier);
            }
            case TokenKind.OpenParen:
            {
                var open = NextToken();
                var expression = ParseExpression();
                Expect(TokenKind.CloseParen);
                return new ParenthesizedExpressionSyntax(SpanFrom(open), expression);
            }
            case TokenKind.OpenBrace:
                return ParseBlock();
            case TokenKind.IfKeyword:
                return ParseIf();
            default:
                ReportOnce(Current.Span, () => _diagnostics.ReportExpectedExpression(Current.Span, Current));
                return new ErrorExpressionSyntax(new TextSpan(Current.Span.Start, 0));
        }
    }

    /// <summary>if = "if" expression block [ "else" ( block | if ) ] ;</summary>
    private IfExpressionSyntax ParseIf()
    {
        var keyword = Expect(TokenKind.IfKeyword);
        var condition = ParseExpression();
        var then = ParseBlock();
        ExpressionSyntax? @else = null;
        if (Current.Kind == TokenKind.ElseKeyword)
        {
            NextToken();
            @else = Current.Kind == TokenKind.IfKeyword ? ParseIf() : ParseBlock();
        }
        return new IfExpressionSyntax(SpanFrom(keyword), condition, then, @else);
    }

    /// <summary>interpolatedString = '"' { text | "{" expression "}" } '"' ;  each hole is lexed and parsed separately.</summary>
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
                    var holeParser = new Parser(_source, _diagnostics, Lexer.Tokenize(_source, _diagnostics, hole.Span));
                    var expression = holeParser.ParseStandaloneExpression();
                    parts.Add(new InterpolationSyntax(hole.Span, expression));
                    break;
            }
        }
        return new InterpolatedStringExpressionSyntax(token.Span, parts.ToImmutable());
    }
}
