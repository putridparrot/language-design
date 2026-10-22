namespace Piper.Tests;

public class BinderTests
{
    private static void AssertSingleError(string code, string program)
    {
        var diagnostics = Compilation.Compile(program).Diagnostics;
        Assert.True(
            diagnostics.Length == 1 && diagnostics[0].Code == code,
            $"Expected exactly one {code} but got:\n{string.Join("\n", diagnostics)}");
    }

    private static void AssertNoErrors(string program)
    {
        var result = Compilation.Compile(program);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
    }

    [Theory]
    [InlineData("PIP0200", "fn main() { print(y); }")]
    [InlineData("PIP0201", "fn main() { let a = 1; let a = 2; }")]
    [InlineData("PIP0201", "fn f() {} fn f() {}")]
    [InlineData("PIP0201", "record P(x: Int, x: Int);")]
    [InlineData("PIP0202", "fn main() { let a: Int = \"s\"; }")]
    [InlineData("PIP0202", "fn main() { let a: Int = 1.5; }")]
    [InlineData("PIP0202", "fn f(a: Long) {} fn main() { f(1.5); }")]
    [InlineData("PIP0203", "fn main() { let a = 1 + true; }")]
    [InlineData("PIP0203", "fn main() { let a = \"a\" < \"b\"; }")]
    [InlineData("PIP0204", "fn main() { let a = -true; }")]
    [InlineData("PIP0205", "fn f(a: Int) {} fn main() { f(); }")]
    [InlineData("PIP0205", "record P(x: Int); fn main() { let p = P(1, 2); }")]
    [InlineData("PIP0206", "fn main() { let a = 1; a(); }")]
    [InlineData("PIP0207", "fn main() { let a = 1; a = 2; }")]
    [InlineData("PIP0207", "fn f(a: Int) { a = 2; }")]
    [InlineData("PIP0207", "fn main() { for i in 0..3 { i = 1; } }")]
    [InlineData("PIP0208", "record P(x: Int); fn main() { let p = P(1); p.x = 2; }")]
    [InlineData("PIP0209", "fn f(a: Int) -> Int { if a > 0 { return 1; } }")]
    [InlineData("PIP0209", "fn f() -> Int { let a = 1; }")]
    [InlineData("PIP0210", "fn main() { if 1 { } }")]
    [InlineData("PIP0210", "fn main() { while \"yes\" { } }")]
    [InlineData("PIP0211", "fn f(a: Foo) {}")]
    [InlineData("PIP0212", "record P(x: Int); fn main() { let p = P(1); print(p.y); }")]
    [InlineData("PIP0213", "fn main() { break; }")]
    [InlineData("PIP0214", "fn main() { let a = print(1); }")]
    [InlineData("PIP0214", "fn f(a: Unit) {}")]
    [InlineData("PIP0215", "fn main() { return 1; }")]
    [InlineData("PIP0215", "fn f() -> Int { return; }")]
    [InlineData("PIP0216", "fn main() { let r = 0..3; }")]
    [InlineData("PIP0216", "fn main() { for i in 3 { } }")]
    [InlineData("PIP0217", "fn f() {} fn main() { let a = f; }")]
    [InlineData("PIP0219", "fn main(a: Int) {}")]
    [InlineData("PIP0220", "record P(P: Int);")]
    [InlineData("PIP0221", "fn main() { let a = if true { 1 } else { \"s\" }; }")]
    public void Semantic_errors_are_reported(string code, string program)
    {
        AssertSingleError(code, program);
    }

    [Fact]
    public void Errors_do_not_cascade()
    {
        AssertSingleError("PIP0200", "fn main() { let a = missing + 1; let b = a * 2; print(b); }");
    }

    [Fact]
    public void Declarations_can_appear_in_any_order()
    {
        AssertNoErrors("""
            fn main() { print(isEven(10)); let l = Line(Point(0, 0), Point(1, 1)); }
            fn isEven(n: Int) -> Bool = if n == 0 { true } else { isOdd(n - 1) };
            fn isOdd(n: Int) -> Bool = if n == 0 { false } else { isEven(n - 1) };
            record Line(start: Point, end: Point);
            record Point(x: Int, y: Int);
            """);
    }

    [Fact]
    public void Shadowing_in_nested_scopes_is_allowed()
    {
        AssertNoErrors("fn f(x: Int) { let x = x + 1; { let x = \"s\"; } }");
    }

    [Fact]
    public void Numeric_widening_is_implicit()
    {
        AssertNoErrors("""
            fn f(a: Double) -> Double = a;
            fn main() { let l: Long = 1; let d: Double = l; var x = 1L; x += 1; print(f(2) + d); let m = if true { 1 } else { 2.5 }; }
            """);
    }

    [Fact]
    public void Diverging_branches_take_the_type_of_the_other_branch()
    {
        AssertNoErrors("""
            fn f(a: Int) -> Int {
                let b = if a > 0 { a } else { return 0; };
                if b > 10 { return 10; } else { return b; }
            }
            """);
    }

    [Fact]
    public void Diagnostics_are_formatted_with_file_line_and_column()
    {
        var result = Compilation.Compile("fn main() {\n    print(y);\n}", "test.piper");
        Assert.Equal(
            "test.piper(2,11): error PIP0200: The name 'y' does not exist in the current scope.",
            Assert.Single(result.FormattedDiagnostics));
    }
}
