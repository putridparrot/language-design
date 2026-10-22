using Piper.Diagnostics;
using Piper.Syntax;
using Piper.Text;

namespace Piper.Tests;

public class ParserTests
{
    private static CompilationUnitSyntax Parse(string text, out DiagnosticBag diagnostics)
    {
        diagnostics = new DiagnosticBag();
        return Parser.ParseCompilationUnit(new SourceText(text), diagnostics);
    }

    private static CompilationUnitSyntax ParseClean(string text)
    {
        var unit = Parse(text, out var diagnostics);
        Assert.Empty(diagnostics);
        return unit;
    }

    private static BlockExpressionSyntax Body(CompilationUnitSyntax unit, int index = 0) =>
        ((FunctionDeclarationSyntax)unit.Declarations[index]).BlockBody!;

    /// <summary>Renders an expression fully parenthesized so tests can assert precedence and associativity.</summary>
    private static string Show(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax e => e.Literal.Text,
        NameExpressionSyntax e => e.Identifier.Text,
        UnaryExpressionSyntax e => $"({e.Operator.Text}{Show(e.Operand)})",
        BinaryExpressionSyntax e => $"({Show(e.Left)} {e.Operator.Text} {Show(e.Right)})",
        RangeExpressionSyntax e => $"({Show(e.Start)} {e.Operator.Text} {Show(e.End)})",
        AssignmentExpressionSyntax e => $"({Show(e.Target)} {e.Operator.Text} {Show(e.Value)})",
        CallExpressionSyntax e => $"{Show(e.Callee)}({string.Join(", ", e.Arguments.Select(Show))})",
        MemberAccessExpressionSyntax e => $"{Show(e.Target)}.{e.Member.Text}",
        ParenthesizedExpressionSyntax e => Show(e.Expression),
        IfExpressionSyntax => "if",
        BlockExpressionSyntax => "{}",
        _ => expression.GetType().Name,
    };

    [Theory]
    [InlineData("1 + 2 * 3", "(1 + (2 * 3))")]
    [InlineData("1 * 2 + 3", "((1 * 2) + 3)")]
    [InlineData("a - b - c", "((a - b) - c)")]
    [InlineData("-a * b", "((-a) * b)")]
    [InlineData("- -a", "(-(-a))")]
    [InlineData("!a && b || c", "(((!a) && b) || c)")]
    [InlineData("a || b && c", "(a || (b && c))")]
    [InlineData("a < b == c > d", "((a < b) == (c > d))")]
    [InlineData("a = b = c", "(a = (b = c))")]
    [InlineData("a += 1 + 2", "(a += (1 + 2))")]
    [InlineData("f(1, g(2)).x.y", "f(1, g(2)).x.y")]
    [InlineData("-p.x", "(-p.x)")]
    [InlineData("0..n + 1", "(0 .. (n + 1))")]
    [InlineData("0..=n", "(0 ..= n)")]
    [InlineData("(1 + 2) * 3", "((1 + 2) * 3)")]
    public void Expressions_respect_precedence_and_associativity(string text, string expected)
    {
        var diagnostics = new DiagnosticBag();
        var expression = Parser.ParseExpression(new SourceText(text), diagnostics);
        Assert.Empty(diagnostics);
        Assert.Equal(expected, Show(expression));
    }

    [Fact]
    public void Function_with_parameters_return_type_and_trailing_expression()
    {
        var unit = ParseClean("fn add(a: Int, b: Int) -> Int { let c = a; c + b }");
        var function = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(unit.Declarations));
        Assert.Equal("add", function.Name.Text);
        Assert.Equal(["a", "b"], function.Parameters.Select(p => p.Name.Text));
        Assert.Equal("Int", ((NamedTypeSyntax)function.ReturnType!).Name.Text);
        Assert.IsType<VariableDeclarationSyntax>(Assert.Single(function.BlockBody!.Statements));
        Assert.Equal("(c + b)", Show(function.BlockBody.Trailing!));
    }

    [Fact]
    public void Expression_bodied_function()
    {
        var function = (FunctionDeclarationSyntax)ParseClean("fn twice(x: Int) -> Int = x * 2;").Declarations[0];
        Assert.Null(function.BlockBody);
        Assert.Equal("(x * 2)", Show(function.ExpressionBody!));
    }

    [Fact]
    public void Record_declaration()
    {
        var record = Assert.IsType<RecordDeclarationSyntax>(Assert.Single(ParseClean("record Point(x: Int, y: Int,);").Declarations));
        Assert.Equal(["x", "y"], record.Fields.Select(f => f.Name.Text));
    }

    [Fact]
    public void Block_like_statements_need_no_semicolon()
    {
        var body = Body(ParseClean("fn main() { if a { b(); } { c(); } while x { } d(); }"));
        Assert.Equal(4, body.Statements.Length);
        Assert.Null(body.Trailing);
    }

    [Fact]
    public void Final_if_is_the_trailing_expression()
    {
        var body = Body(ParseClean("fn f() -> Int { if a { 1 } else if b { 2 } else { 3 } }"));
        var @if = Assert.IsType<IfExpressionSyntax>(body.Trailing);
        Assert.IsType<IfExpressionSyntax>(@if.Else);
    }

    [Fact]
    public void Interpolation_holes_are_parsed_as_expressions()
    {
        var diagnostics = new DiagnosticBag();
        var expression = Parser.ParseExpression(new SourceText("\"sum={a + b}\""), diagnostics);
        Assert.Empty(diagnostics);
        var interpolated = Assert.IsType<InterpolatedStringExpressionSyntax>(expression);
        var hole = Assert.Single(interpolated.Parts.OfType<InterpolationSyntax>());
        Assert.Equal("(a + b)", Show(hole.Expression));
    }

    [Fact]
    public void Enum_declaration()
    {
        var @enum = Assert.IsType<EnumDeclarationSyntax>(Assert.Single(
            ParseClean("enum Shape { Circle(radius: Double), Rect(w: Double, h: Double), Empty, }").Declarations));
        Assert.Equal(["Circle", "Rect", "Empty"], @enum.Variants.Select(v => v.Name.Text));
        Assert.Equal([1, 2, 0], @enum.Variants.Select(v => v.Fields.Length));
    }

    [Fact]
    public void Match_arms_with_guards_and_block_bodies()
    {
        var body = Body(ParseClean("""
            fn f(s: Shape) -> Int {
                match s {
                    Circle(r) if r > 1.0 => 1,
                    Rect(_, _) => { 2 }
                    Empty => return 3,
                }
            }
            """));
        var match = Assert.IsType<MatchExpressionSyntax>(body.Trailing);
        Assert.Equal(3, match.Arms.Length);
        Assert.NotNull(match.Arms[0].Guard);
        Assert.IsType<BlockExpressionSyntax>(match.Arms[1].Body);
        var jump = Assert.IsType<BlockExpressionSyntax>(match.Arms[2].Body);
        Assert.IsType<ReturnStatementSyntax>(Assert.Single(jump.Statements));
    }

    [Fact]
    public void Pattern_forms()
    {
        var body = Body(ParseClean("fn f() { match x { 1 | 2 => 0, -9..=-1 => 0, Shape.Rect(Num(0), _) => 0, Shape.Empty => 0, y => 0, _ => 0 } }"));
        var arms = Assert.IsType<MatchExpressionSyntax>(body.Trailing).Arms;
        Assert.Equal(2, Assert.IsType<OrPatternSyntax>(arms[0].Pattern).Alternatives.Length);
        var range = Assert.IsType<RangePatternSyntax>(arms[1].Pattern);
        Assert.Equal((-9, -1, true), (range.Start.Value, range.End.Value, range.IsInclusive));
        var rect = Assert.IsType<ConstructorPatternSyntax>(arms[2].Pattern);
        Assert.Equal("Shape", rect.Qualifier!.Text);
        Assert.IsType<ConstructorPatternSyntax>(rect.Arguments[0]);
        Assert.False(Assert.IsType<ConstructorPatternSyntax>(arms[3].Pattern).HasArgumentList);
        Assert.IsType<IdentifierPatternSyntax>(arms[4].Pattern);
        Assert.IsType<WildcardPatternSyntax>(arms[5].Pattern);
    }

    [Fact]
    public void Missing_comma_between_arms_is_reported_once()
    {
        var unit = Parse("fn f() { match x { 1 => a 2 => b } }", out var diagnostics);
        Assert.Equal("PIP0100", Assert.Single(diagnostics).Code);
        Assert.Equal(2, Assert.IsType<MatchExpressionSyntax>(Body(unit).Trailing).Arms.Length);
    }

    [Fact]
    public void Missing_pattern_is_reported()
    {
        Parse("fn f() { match x { => 1 } }", out var diagnostics);
        Assert.Equal("PIP0105", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void Missing_semicolon_reports_one_error_and_recovers()
    {
        var unit = Parse("fn main() { let a = 1 let b = 2; }\nfn other() {}", out var diagnostics);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("PIP0100", diagnostic.Code);
        Assert.Contains("';'", diagnostic.Message);
        Assert.Equal(2, unit.Declarations.Length);
        Assert.Equal(2, Body(unit).Statements.Length);
    }

    [Fact]
    public void Missing_close_brace_does_not_swallow_the_next_function()
    {
        var unit = Parse("fn a() { let x = 1;\nfn b() {}", out var diagnostics);
        Assert.Equal("PIP0100", Assert.Single(diagnostics).Code);
        Assert.Equal(["a", "b"], unit.Declarations.Select(d => d.Name.Text));
    }

    [Fact]
    public void Garbage_at_top_level_is_reported_once_and_skipped()
    {
        var unit = Parse("123 + ; ) fn main() {}", out var diagnostics);
        Assert.Equal("PIP0102", Assert.Single(diagnostics).Code);
        Assert.Single(unit.Declarations);
    }

    [Fact]
    public void Missing_expression_is_reported()
    {
        Parse("fn main() { let a = ; }", out var diagnostics);
        Assert.Equal("PIP0101", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void Several_independent_errors_are_all_reported()
    {
        Parse("fn a() { let x = ; }\nfn b() { let y = 1 }\nfn c() { foo(1 2); }", out var diagnostics);
        Assert.Equal(3, diagnostics.Count);
    }
}
