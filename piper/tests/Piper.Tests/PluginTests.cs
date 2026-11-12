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
            // As a plugin built against another version of the compiler fails, calling what's no longer there.
            new StatementKeyword("outdated", _ => throw new MissingMethodException("Method not found: 'Piper.Plugins.PluginParser.Gone()'.")),
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
                Print($"{a} {b}");
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
    public void A_plugin_built_against_another_compiler_is_told_to_rebuild()
    {
        var error = Compile("fn Main() { outdated { } }", new TestPlugin()).Diagnostics[0];
        Assert.Equal("PIP0504", error.Code);
        Assert.Contains("'Piper.Plugins.PluginParser.Gone()'", error.Message);
        Assert.Contains("built against another version of piper's compiler: rebuild it", error.Message);
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

    /// <summary>
    /// <c>[Say("text")]</c> prints its text before the function's body; <c>[Broken]</c> throws; analysis warns about
    /// each decorated function, found through the expansion's syntax.
    /// </summary>
    private sealed class DecoratorPlugin : IPiperPlugin
    {
        public string Name => "decorators";

        public IReadOnlyList<PluginKeyword> Keywords { get; } =
        [
            new Decorator("Say", d => d.WithPrologue([d.Template.Statement("Print($text);", ("text", d.Arguments[0]))])),
            new Decorator("Broken", _ => throw new InvalidOperationException("boom")),
        ];

        public void Analyze(PluginAnalysis analysis)
        {
            foreach (var expansion in analysis.Expansions)
            {
                var declaration = expansion.Result.OfType<FunctionDeclarationSyntax>().Single();
                if (analysis.Program.Functions.Any(f => ReferenceEquals(f.Symbol.Declaration, declaration)))
                    analysis.ReportWarning(expansion.File, expansion.KeywordToken.Span, $"'{declaration.Name.Text}' says something.");
            }
        }
    }

    [Fact]
    public void A_decorator_rewrites_the_function_its_on()
    {
        var output = Run("""
            [Say("first")]
            [Say("second")]
            fn Greet(name: string) => string = $"hello {name}";

            fn Main() { Print(Greet("you")); }
            """, new DecoratorPlugin());
        // Nearest first, so the first written is outermost and runs first.
        Assert.Equal("first\nsecond\nhello you\n", output);
    }

    [Fact]
    public void A_decorators_expansion_is_analyzed()
    {
        var text = "[Say(\"hi\")]\nfn Greet() { }\nfn Main() { Greet(); }";
        var warning = Assert.Single(Compile(text, new DecoratorPlugin()).Diagnostics);
        Assert.Equal("PIP0505", warning.Code);
        Assert.Equal("'Greet' says something. (decorators)", warning.Message);
        Assert.Equal(text.IndexOf("Say", StringComparison.Ordinal), warning.Span.Start);
    }

    [Fact]
    public void A_decorator_that_throws_is_reported_and_the_function_left_alone()
    {
        var result = Compile("[Broken]\nfn F() { }\nfn Main() { F(); }", new DecoratorPlugin());
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal("PIP0504", error.Code);
        Assert.Contains("boom", error.Message);
    }

    [Fact]
    public void A_decorator_needs_a_function_with_a_body()
    {
        var result = Compile("""
            trait Named {
                [Say("x")]
                fn Name() => string;
            }

            record P(
                [Say("x")] x: int);

            fn Main() { }
            """, new DecoratorPlugin());
        Assert.Equal(["PIP0507", "PIP0507"], result.Diagnostics.Select(d => d.Code));
        Assert.Contains("one without a body", result.Diagnostics[0].Message);
        Assert.Contains("a parameter", result.Diagnostics[1].Message);
    }

    [Fact]
    public void Without_its_plugin_a_decorator_is_an_ordinary_attribute()
    {
        var result = Compile("[Say(\"hi\")]\nfn F() { }\nfn Main() { }");
        Assert.Equal("PIP0329", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void A_decorator_and_a_keyword_cant_share_a_name()
    {
        var result = Compile("fn Main() { }", new DecoratorPlugin(), new Named("other", "Say"));
        Assert.Equal("PIP0502", Assert.Single(result.Diagnostics).Code);
    }

    /// <summary>
    /// Type decorators. <c>[Order("a")]</c> gives a class a method <c>Order()</c> returning its text, appended to what
    /// a nearer one gave; on a record, it adds the method in an <c>extend</c> block beside it. <c>[Broken]</c> throws.
    /// Analysis warns about each class it decorated, found through the expansion's syntax.
    /// </summary>
    private sealed class TypeDecoratorPlugin : IPiperPlugin
    {
        public string Name => "types";

        public IReadOnlyList<PluginKeyword> Keywords { get; } =
        [
            new Decorator("Order") { Type = Order },
            new Decorator("Shatter") { Type = _ => throw new InvalidOperationException("shattered") },
        ];

        private static IEnumerable<DeclarationSyntax> Order(TypeDecoratorContext d)
        {
            if (d.Record is not null)
            {
                return [d.Declaration, .. d.Template.Declarations("extend $T { fn Order() => string = $text; }",
                    ("T", d.Declaration.Name), ("text", d.Arguments[0]))];
            }
            var existing = d.Class!.Methods.FirstOrDefault(m => m.Name.Text == "Order");
            if (existing is null)
            {
                var (_, methods, _) = d.ClassMembers("fn Order() => string = $text;", ("text", d.Arguments[0]));
                return [d.WithMembers(methods: methods)];
            }
            var appended = existing with { ExpressionBody = d.Template.Expression("$old + $text", ("old", existing.ExpressionBody!), ("text", d.Arguments[0])) };
            return [d.Class with { Methods = d.Class.Methods.Replace(existing, appended) }];
        }

        public void Analyze(PluginAnalysis analysis)
        {
            foreach (var expansion in analysis.Expansions)
            {
                foreach (var declaration in expansion.Result.OfType<ClassDeclarationSyntax>())
                {
                    if (analysis.Program.Types.OfType<Binding.ClassSymbol>().Any(c => ReferenceEquals(c.Declaration, declaration)))
                        analysis.ReportWarning(expansion.File, expansion.KeywordToken.Span, $"'{declaration.Name.Text}' is ordered.");
                }
            }
        }
    }

    [Fact]
    public void A_type_decorator_adds_members_to_a_class()
    {
        var output = Run("""
            [Order("x")]
            class Plain { }

            fn Main() { Print(Plain().Order()); }
            """, new TypeDecoratorPlugin());
        Assert.Equal("x\n", output);
    }

    [Fact]
    public void Type_decorators_apply_nearest_first()
    {
        var output = Run("""
            [Order("a")]
            [Order("b")]
            class Plain { }

            fn Main() { Print(Plain().Order()); }
            """, new TypeDecoratorPlugin());
        Assert.Equal("ba\n", output);
    }

    [Fact]
    public void A_type_decorator_adds_declarations_beside_a_record()
    {
        var output = Run("""
            [Order("r")]
            record Point(x: int);

            fn Main() { Print(Point(1).Order()); }
            """, new TypeDecoratorPlugin());
        Assert.Equal("r\n", output);
    }

    [Fact]
    public void A_type_decorators_class_is_analyzed()
    {
        var text = "[Order(\"x\")]\nclass Plain { }\nfn Main() { }";
        var result = Compile(text, new TypeDecoratorPlugin());
        var warning = Assert.Single(result.Diagnostics);
        Assert.Equal("'Plain' is ordered. (types)", warning.Message);
        Assert.Equal(text.IndexOf("Order", StringComparison.Ordinal), warning.Span.Start);
    }

    [Fact]
    public void A_type_decorator_that_throws_is_reported()
    {
        var error = Assert.Single(Compile("[Shatter]\nclass C { }\nfn Main() { }", new TypeDecoratorPlugin()).Diagnostics);
        Assert.Equal("PIP0504", error.Code);
        Assert.Contains("shattered", error.Message);
    }

    [Fact]
    public void A_decorator_is_only_used_where_it_has_a_handler()
    {
        var result = Compile("""
            [Say("x")]
            class C { }

            [Order("y")]
            fn F() { }

            [Order("z")]
            enum E { A }

            fn Main() { }
            """, new DecoratorPlugin(), new TypeDecoratorPlugin());
        Assert.Equal(["PIP0507", "PIP0507", "PIP0507"], result.Diagnostics.Select(d => d.Code));
        Assert.Contains("only be used on a function or method with a body, not a class", result.Diagnostics[0].Message);
        Assert.Contains("only be used on a class or a record, not a function", result.Diagnostics[1].Message);
        Assert.Contains("not an enum", result.Diagnostics[2].Message);
    }

    [Fact]
    public void Loading_a_missing_plugin_is_reported()
    {
        var (plugins, errors) = PluginLoader.Load(Path.GetTempPath(), ["no-such-plugin.dll"]);
        Assert.Empty(plugins);
        Assert.Equal("PIP0500", Assert.Single(errors).Code);
    }
}
