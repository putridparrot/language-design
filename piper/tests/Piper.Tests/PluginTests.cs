using System.Collections.Immutable;
using Piper.Plugins;
using Piper.Syntax;
using Piper.Text;

namespace Piper.Tests;

/// <summary>Compiler plugins: keywords that rewrite their construct into ordinary Piper as it's parsed.</summary>
public class PluginTests
{
    /// <summary>
    /// <c>unless cond { … }</c> is <c>if !cond { … }</c>; <c>greeting Name "text";</c> declares
    /// <c>fn Name() { Print("text"); }</c>; <c>fails</c> throws; <c>picky x;</c> reports an error.
    /// </summary>
    private sealed class TestPlugin : IPiperPlugin
    {
        public string Name => "test";

        public IReadOnlyList<PluginKeyword> Keywords { get; } =
        [
            new StatementKeyword("unless", p =>
            {
                var condition = p.ParseExpressionBeforeBlock();
                var body = p.ParseBlock();
                return [p.Template.Statement("if !($condition) { $body }", ("condition", condition), ("body", body))];
            }),
            new DeclarationKeyword("greeting", p =>
            {
                var name = p.Expect(TokenKind.Identifier);
                var text = p.Expect(TokenKind.StringLiteral);
                p.Expect(TokenKind.Semicolon);
                return p.Template.Declarations("fn $name() { Print($text); }",
                    ("name", name), ("text", new LiteralExpressionSyntax(text.Span, text, text.Value)));
            }),
            new StatementKeyword("fails", _ => throw new InvalidOperationException("boom")),
            new StatementKeyword("picky", p =>
            {
                var value = p.ParseExpression();
                p.Expect(TokenKind.Semicolon);
                p.Report(value.Span, "Too picky.");
                return [];
            }),
        ];
    }

    /// <summary><c>swap a, b;</c> exchanges two variables through a fresh one.</summary>
    private sealed class SwapPlugin : IPiperPlugin
    {
        public string Name => "swap";

        public IReadOnlyList<PluginKeyword> Keywords { get; } =
        [
            new StatementKeyword("swap", p =>
            {
                var a = p.ParseExpression();
                p.Expect(TokenKind.Comma);
                var b = p.ParseExpression();
                p.Expect(TokenKind.Semicolon);
                var temporary = p.FreshName("swap");
                return
                [
                    p.Template.Statement("let $t = $a;", ("t", temporary), ("a", a)),
                    p.Template.Statement("$a = $b;", ("a", a), ("b", b)),
                    p.Template.Statement("$b = $t;", ("b", b), ("t", temporary)),
                ];
            }),
        ];
    }

    private sealed class Named(string name, params string[] keywords) : IPiperPlugin
    {
        public string Name => name;

        public IReadOnlyList<PluginKeyword> Keywords { get; } = [.. keywords.Select(k => new StatementKeyword(k, _ => []))];
    }

    private static CompilationResult Compile(string text, params IPiperPlugin[] plugins) =>
        Compilation.Compile([new SourceText(text, "main.piper")], new CompilationOptions { Plugins = [.. plugins] });

    private static string Run(string text, params IPiperPlugin[] plugins)
    {
        var result = Compile(text, plugins);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var run = Runner.Run(result.Files);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        return run.Output.Replace("\r\n", "\n");
    }

    [Fact]
    public void A_statement_keyword_is_rewritten_into_piper()
    {
        var output = Run("""
            fn Main() {
                let ready = false;
                unless ready {
                    Print("waiting");
                }
                unless !ready { Print("never"); }
            }
            """, new TestPlugin());
        Assert.Equal("waiting\n", output);
    }

    [Fact]
    public void A_declaration_keyword_is_rewritten_into_declarations()
    {
        var output = Run("""
            greeting Hello "hello from a plugin";
            fn Main() { Hello(); }
            """, new TestPlugin());
        Assert.Equal("hello from a plugin\n", output);
    }

    [Fact]
    public void Fresh_names_dont_clash_when_a_construct_is_used_twice()
    {
        var output = Run("""
            fn Main() {
                mut a = 1;
                mut b = 2;
                swap a, b;
                swap a, b;
                swap a, b;
                Print("{a} {b}");
            }
            """, new SwapPlugin());
        Assert.Equal("2 1\n", output);
    }

    [Fact]
    public void A_plugin_keyword_is_still_a_name_where_its_construct_cant_start()
    {
        var output = Run("""
            fn Main() {
                let unless = 3;
                Print(unless + 1);
            }
            """, new TestPlugin());
        Assert.Equal("4\n", output);
    }

    [Fact]
    public void Without_its_plugin_a_keyword_is_just_a_name()
    {
        var result = Compile("fn Main() { unless ready { } }");
        Assert.False(result.Success);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code.StartsWith("PIP05", StringComparison.Ordinal));
    }

    [Fact]
    public void An_error_in_the_users_code_is_reported_where_it_is()
    {
        var text = """
            fn Main() {
                unless false {
                    let s: string = 42;
                }
            }
            """;
        var result = Compile(text, new TestPlugin());
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal(text.IndexOf("42", StringComparison.Ordinal), error.Span.Start);
    }

    [Fact]
    public void An_error_in_the_rewritten_code_is_reported_at_the_construct()
    {
        var text = "fn Main() {\n    unless 42 { }\n}";
        var result = Compile(text, new TestPlugin());
        var error = Assert.Single(result.Diagnostics);
        Assert.True(error.Span.Start >= text.IndexOf("unless", StringComparison.Ordinal) && error.Span.End <= text.LastIndexOf('}'),
            error.ToString());
    }

    [Fact]
    public void A_plugin_reports_errors_with_its_name()
    {
        var text = "fn Main() { picky 1 + 2; }";
        var error = Assert.Single(Compile(text, new TestPlugin()).Diagnostics);
        Assert.Equal("PIP0503", error.Code);
        Assert.Equal("Too picky. (test)", error.Message);
        Assert.Equal(text.IndexOf("1 + 2", StringComparison.Ordinal), error.Span.Start);
    }

    [Fact]
    public void A_plugin_that_throws_is_reported_and_its_construct_skipped()
    {
        var text = "fn Main() { fails { let x = 1; } let s: string = 1; }";
        var result = Compile(text, new TestPlugin());
        Assert.Equal(2, result.Diagnostics.Length);
        Assert.Equal("PIP0504", result.Diagnostics[0].Code);
        Assert.Equal(text.LastIndexOf('1'), result.Diagnostics[1].Span.Start);
        Assert.Contains("boom", result.Diagnostics[0].Message);
    }

    [Fact]
    public void Two_plugins_cant_add_the_same_keyword()
    {
        var result = Compile("fn Main() {}", new Named("one", "go"), new Named("two", "go"));
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal("PIP0502", error.Code);
        Assert.Contains("'one' and 'two'", error.Message);
    }

    [Theory]
    [InlineData("while")]
    [InlineData("int")]
    [InlineData("requires")]
    [InlineData("await")]
    [InlineData("two words")]
    public void A_plugin_cant_add_a_piper_keyword(string keyword)
    {
        var error = Assert.Single(Compile("fn Main() {}", new Named("bad", keyword)).Diagnostics);
        Assert.Equal("PIP0501", error.Code);
    }

    [Fact]
    public void Templates_splice_statement_and_argument_lists()
    {
        var template = new SyntaxTemplate(default);
        var call = (CallExpressionSyntax)template.Expression("f(1, $rest)",
            ("rest", new ExpressionSyntax[] { template.Expression("2"), template.Expression("3") }));
        Assert.Equal(3, call.Arguments.Length);

        var block = (BlockExpressionSyntax)template.Expression("{ a(); $body }",
            ("body", template.Statements("b(); c();")));
        Assert.Equal(3, block.Statements.Length);
        Assert.Null(block.Trailing);
    }

    [Fact]
    public void Template_syntax_gets_the_construct_span_and_holes_keep_theirs()
    {
        var construct = new TextSpan(100, 20);
        var user = template().Expression("x");
        var filled = (BinaryExpressionSyntax)new SyntaxTemplate(construct).Expression("$value + 1", ("value", user with { Span = new TextSpan(5, 1) }));
        Assert.Equal(construct, filled.Span);
        Assert.Equal(construct, filled.Right.Span);
        Assert.Equal(new TextSpan(5, 1), filled.Left.Span);

        static SyntaxTemplate template() => new(default);
    }

    [Fact]
    public void A_template_hole_without_a_value_is_a_plugin_bug()
    {
        var template = new SyntaxTemplate(default);
        Assert.Throws<InvalidOperationException>(() => template.Expression("f($missing)"));
        Assert.Throws<InvalidOperationException>(() => template.Expression("f(1)", ("unused", "x")));
        Assert.Throws<InvalidOperationException>(() => template.Expression("f(1"));
    }

    /// <summary><c>whereami {}</c> becomes <c>Print("async")</c> or <c>Print("sync")</c>, as <see cref="PluginParser.AsyncContext"/> says.</summary>
    private sealed class WherePlugin : IPiperPlugin
    {
        public string Name => "where";

        public IReadOnlyList<PluginKeyword> Keywords { get; } =
        [
            new StatementKeyword("whereami", p =>
            {
                p.Expect(TokenKind.OpenBrace);
                p.Expect(TokenKind.CloseBrace);
                return [p.Template.Statement(p.AsyncContext switch { AsyncContext.Async => "Print(\"async\");", AsyncContext.Sync => "Print(\"sync\");", _ => "Print(\"if awaited\");" })];
            }),
        ];
    }

    [Fact]
    public void A_plugin_knows_whether_it_can_await()
    {
        var output = Run("""
            using System.Threading.Tasks;
            fn Twice(body: () => unit) { body(); }
            async fn Main() {
                whereami {}
                let sync = () => { whereami {} };
                sync();
                let later = async () => { whereami {} await Task.Yield(); };
                await later();
                Twice { whereami {} }
            }
            """, new WherePlugin());
        Assert.Equal("async\nsync\nasync\nif awaited\n", output);
    }

    /// <summary>
    /// <c>tagged Name;</c> declares <c>record Name(value: int);</c>, and analysis warns about each such record whose
    /// name is short, finding its symbol through the expansion's syntax. With <paramref name="fail"/>, analysis throws.
    /// </summary>
    private sealed class AnalyzingPlugin(bool fail = false) : IPiperPlugin
    {
        public string Name => "tagged";

        public IReadOnlyList<PluginKeyword> Keywords { get; } =
        [
            new DeclarationKeyword("tagged", p =>
            {
                var name = p.Expect(TokenKind.Identifier);
                p.Expect(TokenKind.Semicolon);
                return p.Template.Declarations("record $name(value: int);", ("name", name));
            }),
        ];

        public void Analyze(PluginAnalysis analysis)
        {
            if (fail)
                throw new InvalidOperationException("analysis broke");
            foreach (var expansion in analysis.Expansions)
            {
                var declaration = expansion.Result.OfType<RecordDeclarationSyntax>().Single();
                var record = analysis.Program.Types.OfType<Binding.RecordSymbol>().Single(r => ReferenceEquals(r.Declaration, declaration));
                if (record.Name.Length < 3)
                    analysis.ReportWarning(expansion.File, declaration.Name.Span, $"'{record.Name}' with {record.Fields.Length} field is too short a name.");
            }
        }
    }

    [Fact]
    public void A_plugin_analyzes_the_bound_program_through_its_expansions()
    {
        var text = "tagged Ok2;\ntagged Id;\nfn Main() { Print(Id(1).value); }";
        var result = Compile(text, new AnalyzingPlugin());
        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics);
        Assert.Equal(("PIP0505", Diagnostics.DiagnosticSeverity.Warning), (warning.Code, warning.Severity));
        Assert.Equal("'Id' with 1 field is too short a name. (tagged)", warning.Message);
        Assert.Equal(text.IndexOf("Id;", StringComparison.Ordinal), warning.Span.Start);
    }

    [Fact]
    public void A_plugin_whose_analysis_throws_is_reported()
    {
        var result = Compile("tagged Name;\nfn Main() {}", new AnalyzingPlugin(fail: true));
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal("PIP0506", error.Code);
        Assert.Contains("analysis broke", error.Message);
    }

    [Fact]
    public void Loading_a_missing_plugin_is_reported()
    {
        var (plugins, errors) = PluginLoader.Load(Path.GetTempPath(), ["no-such-plugin.dll"]);
        Assert.Empty(plugins);
        Assert.Equal("PIP0500", Assert.Single(errors).Code);
    }
}
