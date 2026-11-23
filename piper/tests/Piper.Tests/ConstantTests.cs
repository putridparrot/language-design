using System.Text.RegularExpressions;
using Piper.Binding;
using Piper.Text;

namespace Piper.Tests;

/// <summary><c>const</c> declarations: their values, where they can be used, what's an error, and the C# they leave.</summary>
public class ConstantTests
{
    private static CompilationResult Compile(string program, ProgramRoots roots = ProgramRoots.Visible) =>
        Compilation.Compile([new SourceText(program, "main.piper")], new CompilationOptions { Roots = roots });

    private static string CSharp(string program, ProgramRoots roots = ProgramRoots.Visible)
    {
        var result = Compile(program, roots);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        return result.CSharp!;
    }

    private static List<string> Codes(string program) => [.. Compile(program).Diagnostics.Select(d => d.Code)];

    [Theory]
    [InlineData("const X = 2 * 3 + 1;", "const int X = 7;")]
    [InlineData("const X = Y + 1; const Y = 4;", "const int X = 5;")]
    [InlineData("const X: byte = 7;", "const byte X = (byte)(7);")]
    [InlineData("const X: double = 1;", "const double X = 1.0;")]
    [InlineData("const X = -5;", "const int X = -5;")]
    [InlineData("const X = int.MaxValue;", "const int X = 2147483647;")]
    [InlineData("const X = 1.0 / 0.0;", "const double X = double.PositiveInfinity;")]
    [InlineData("const X = (char)65;", "const char X = 'A';")]
    [InlineData("const X = $\"{Y}!\"; const Y = \"a\" + \"b\";", "const string X = \"ab!\";")]
    [InlineData("const X = 1 < 2 && !false;", "const bool X = true;")]
    [InlineData("const X = 1.5m;", "const decimal X = 1.5m;")]
    [InlineData("const X = System.DayOfWeek.Monday;", "const global::System.DayOfWeek X = global::System.DayOfWeek.Monday;")]
    [InlineData("const X = 30s;", "static readonly global::System.TimeSpan X = global::System.TimeSpan.FromSeconds(30);")]
    [InlineData("const X = nameof(Main);", "const string X = \"Main\";")]
    public void Values_are_worked_out_when_compiling(string declaration, string expected) =>
        Assert.Contains("internal " + expected, CSharp(declaration + "\nfn Main() {}"));

    [Theory]
    [InlineData("const X = F(); fn F() => int = 1;", "PIP0411")]
    [InlineData("const X = [1];", "PIP0411")]
    [InlineData("const X: string? = \"a\";", "PIP0411")]
    [InlineData("const X = $\"{1}\";", "PIP0411")]
    [InlineData("const X = 1 / 0;", "PIP0293")]
    [InlineData("const X = int.MaxValue + 1;", "PIP0292")]
    [InlineData("const X: byte = 300;", "PIP0202")]
    [InlineData("const X = Y; const Y = X;", "PIP0412")]
    [InlineData("const X = nameof(Y); const Y: string = Y;", "PIP0412")]
    [InlineData("const X = 1; const X = 2;", "PIP0201")]
    [InlineData("const X = 1; fn F() { X = 2; }", "PIP0208")]
    [InlineData("const X = 1; fn F(s: string) => bool = s is X;", "PIP0234")]
    [InlineData("fn F(n: int, m: int) => bool = n is < m;", "PIP0411")]
    [InlineData("fn F(n: int) { const X = n; }", "PIP0411")]
    [InlineData("class C { const X = 1; } fn F() => int = C.X;", "PIP0351")]
    [InlineData("class C { const X = 1; } fn F(n: int) => bool = n is C.X;", "PIP0351")]
    [InlineData("class C { public const X = 1; } fn F(c: C) => int = c.X;", "PIP0354")]
    [InlineData("class C { const X = 1; fn X() {} }", "PIP0201")]
    [InlineData("[System.Obsolete(\"use Y\", true)] const X = 1; fn F() => int = X;", "PIP0334")]
    public void What_is_wrong_is_one_error(string program, string code) => Assert.Equal([code], Codes(program));

    [Fact]
    public void Patterns_match_a_constants_value()
    {
        var csharp = CSharp("""
            namespace App;
            const Low = 1;
            const High = 9;
            class Limits { public const Max = 20; }
            fn F(n: int) => string = match n {
                Low => "low", Low..High => "in", Limits.Max | App.High => "edge", >= Limits.Max => "over", other => $"{other}",
            };
            """);
        Assert.Contains("is 1", csharp);
        Assert.Contains(">= 1 and <= 9", csharp);
        Assert.Contains("20 or 9", csharp);
        Assert.Contains(">= 20", csharp);
    }

    [Fact]
    public void Explicit_bindings_and_nearer_variables_hide_a_constant() =>
        CSharp("""
            const n = 1;
            fn F(o: object) => string = match o { n: string => n, _ => "" };
            fn G(xs: int[]) => int = match xs { [_, ...n] => n.Length, _ => 0 };
            fn H(n: string) => string = match n { n => n };
            """);

    [Fact]
    public void Attributes_defaults_fields_and_base_arguments_take_constants()
    {
        var csharp = CSharp("""
            const Message = "old";
            const Size: int = 10;
            [System.Obsolete(Message)]
            fn Old() {}
            record Page(size: int = Size);
            class Counter(n: int = Limit) {
                public const Limit = 3;
                protected const Step = 2;
                let total: int = n + Limit;
                static let copy = Limit;
                fn Total() => int = total + Limit + copy;
            }
            class Derived: Counter(Size) { fn More() => int = Step + Limit; }
            """);
        Assert.Contains("ObsoleteAttribute(\"old\")", csharp);
        Assert.Contains("int size = 10", csharp);
        Assert.Contains("public const int Limit = 3;", csharp);
        Assert.Contains("protected const int Step = 2;", csharp);
        Assert.Contains("2 + 3", csharp);
    }

    [Fact]
    public void A_negative_constant_is_parenthesized_where_C_sharp_needs_it()
    {
        var csharp = CSharp("const X = -5; fn F() => string = X.ToString(); fn G() => int = -X;");
        Assert.Contains("(-5).ToString()", csharp);
        Assert.Contains("-(-5)", csharp);
    }

    [Fact]
    public void A_local_constant_leaves_nothing_in_the_C_sharp()
    {
        var csharp = CSharp("fn F() => int { const X = 2; const Y = X * 2; X + Y }");
        Assert.Contains("2 + 4", csharp);
        Assert.DoesNotContain("const", csharp);
        Assert.DoesNotMatch(new Regex(@"\{\s*\}"), csharp);
    }

    [Fact]
    public void Constants_are_written_for_whoever_may_name_them()
    {
        const string program = "public const A = 1; const B = 2; private const C = 3; fn Main() { Print(A + B + C); }";
        Assert.Matches(new Regex("public const int A = 1;\\s+internal const int B = 2;"), CSharp(program));
        Assert.DoesNotContain(" C = ", CSharp(program));
        Assert.DoesNotContain(" B = ", CSharp(program, ProgramRoots.Exported));
        Assert.DoesNotContain("const", CSharp(program, ProgramRoots.Main));
    }

    [Fact]
    public void A_constant_in_an_sql_string_is_still_a_parameter() =>
        Assert.DoesNotContain("name = Ada", CSharp("const Name = \"Ada\"; fn F() => Sql = sql$\"select 1 from t where name = {Name}\";"));

    [Fact]
    public void Editor_tooling_knows_constants()
    {
        var analysis = Compilation.Analyze([new SourceText("const Max = 5; fn F(n: int) => bool = n is Max || n > Max;", "main.piper")], []);
        var constant = Assert.Single(analysis.Program.Constants);
        Assert.Equal(3, analysis.Semantic.ReferencesTo(constant).Count());
        Assert.Equal("const Max: int = 5", LanguageServer.SymbolDisplay.Describe(constant));
    }
}
