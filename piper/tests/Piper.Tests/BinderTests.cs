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

    private const string Shapes = "enum S { A, B(x: Int) } record P(a: Int, b: Int); ";

    [Theory]
    [InlineData("PIP0201", "fn f(p: P) -> Int = match p { P(x, x) => x };")]
    [InlineData("PIP0207", "fn f(n: Int) { match n { x => { x = 1; } } }")]
    [InlineData("PIP0221", "fn f(n: Int) { let a = match n { 0 => 1, _ => \"s\" }; }")]
    [InlineData("PIP0222", "enum E { ToString }")]
    [InlineData("PIP0222", "record R(Equals: Int);")]
    [InlineData("PIP0230", "fn f(s: S) -> Int = match s { A => 1 };")]
    [InlineData("PIP0232", "fn f() { let s = S.B; }")]
    [InlineData("PIP0232", "fn f() { let s = S.A(); }")]
    [InlineData("PIP0233", "fn f(n: Int) -> Int = match n { x if { let y = 1; y > x } => 1, _ => 2 };")]
    [InlineData("PIP0234", "fn f(n: Int) -> Int = match n { \"a\" => 1, _ => 2 };")]
    [InlineData("PIP0234", "fn f(s: S) -> Int = match s { P(_, _) => 1, _ => 2 };")]
    [InlineData("PIP0235", "fn f() { let s = S.C; }")]
    [InlineData("PIP0235", "fn f(s: S) -> Int = match s { C(x) => 1, _ => 2 };")]
    [InlineData("PIP0236", "fn f(s: S) -> Int = match s { B(x, y) => 1, _ => 2 };")]
    [InlineData("PIP0236", "fn f(s: S) -> Int = match s { B => 1, _ => 2 };")]
    [InlineData("PIP0237", "fn f(s: S) -> Int = match s { B(x) | A => 1, _ => 2 };")]
    [InlineData("PIP0238", "enum E {}")]
    [InlineData("PIP0238", "enum E { E }")]
    [InlineData("PIP0239", "fn f(t: String) -> Int = match t { \"a\"..\"z\" => 1, _ => 2 };")]
    [InlineData("PIP0239", "fn f(n: Int) -> Int = match n { 5..=1 => 1, _ => 2 };")]
    public void Pattern_matching_errors_are_reported(string code, string program)
    {
        AssertSingleError(code, Shapes + program);
    }

    [Theory]
    [InlineData("match s { A => 1 }", "S.B(_)")]
    [InlineData("match s { B(_) => 1 }", "S.A")]
    [InlineData("match s { A => 1, B(0) => 2 }", "S.B(_)")]
    [InlineData("match s { A => 1, B(x) if x > 0 => 2 }", "S.B(_)")]
    [InlineData("match flag { true => 1 }", "false")]
    [InlineData("match n { 0 => 1, 1..=9 => 2 }", "_")]
    [InlineData("match p { P(0, _) => 1, P(_, 0) => 2 }", "P(_, _)")]
    [InlineData("match pair { Pair(true, true) => 1, Pair(false, _) => 2 }", "Pair(true, false)")]
    public void Non_exhaustive_matches_name_a_missing_case(string match, string missing)
    {
        var program = Shapes + "record Pair(l: Bool, r: Bool); " +
            $"fn f(s: S, flag: Bool, n: Int, p: P, pair: Pair) -> Int = {match};";
        var diagnostic = Assert.Single(Compilation.Compile(program).Diagnostics);
        Assert.Equal("PIP0230", diagnostic.Code);
        Assert.Contains($"'{missing}' is not covered", diagnostic.Message);
    }

    [Theory]
    [InlineData("match n { _ => 1, 0 => 2 }")]
    [InlineData("match s { A => 1, A => 2, B(_) => 3 }")]
    [InlineData("match s { B(_) => 1, B(5) => 2, A => 3 }")]
    [InlineData("match flag { true | false => 1, true => 2 }")]
    [InlineData("match s { Typo => 1, A => 2 }")]
    public void Unreachable_arms_are_warnings(string match)
    {
        var result = Compilation.Compile(Shapes + $"fn f(s: S, flag: Bool, n: Int) -> Int = {match};");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var warning = Assert.Single(result.Diagnostics);
        Assert.Equal(("PIP0231", Diagnostics.DiagnosticSeverity.Warning), (warning.Code, warning.Severity));
    }

    [Fact]
    public void Exhaustive_matches_need_no_wildcard()
    {
        AssertNoErrors(Shapes + """
            record Pair(l: Bool, r: Bool);
            enum Tree { Leaf, Node(left: Tree, right: Tree) }
            fn a(s: S) -> Int = match s { A => 0, B(0) => 1, B(n) => n };
            fn b(p: Pair) -> Int = match p { Pair(true, true) => 1, Pair(false, _) => 2, Pair(_, false) => 3 };
            fn c(t: Tree) -> Int = match t { Leaf => 0, Node(Leaf, _) => 1, Node(Node(_, _), r) => 2 };
            fn d(flag: Bool) -> Int = match flag { true => 1, false => 0 };
            fn e(s: S) -> Int = match s { S.A | S.B(_) => 1 };
            """);
    }

    [Fact]
    public void Arms_that_return_have_type_Never()
    {
        AssertNoErrors(Shapes + "fn f(s: S) -> Int { let n = match s { A => return 0, B(x) => x }; n * 2 }");
    }

    private const string Declarations =
        "trait T { fn m() -> Int; } record R(x: Int); enum O<V> { S(v: V), N } record B<X: T>(x: X); ";

    [Theory]
    [InlineData("PIP0201", "extend R { fn x() -> Int = 1; }")]
    [InlineData("PIP0201", "enum E { A(name: String) } extend E { fn name() -> String = \"\"; }")]
    [InlineData("PIP0202", "fn f() { let t: T = R(1); }")]
    [InlineData("PIP0203", "fn eq<Q>(a: Q, b: Q) -> Bool = a == b;")]
    [InlineData("PIP0217", "extend R { fn g() -> Int = 1; } fn f(r: R) { let v = r.g; }")]
    [InlineData("PIP0240", "fn f() { let x = O.N; }")]
    [InlineData("PIP0241", "fn f(r: R<Int>) {}")]
    [InlineData("PIP0241", "fn f(o: O) {}")]
    [InlineData("PIP0242", "fn f(b: B<Int>) {}")]
    [InlineData("PIP0242", "fn f() { let b = B(1); }")]
    [InlineData("PIP0243", "fn f(r: R) { r.nope(); }")]
    [InlineData("PIP0244", "fn f() -> R = this;")]
    [InlineData("PIP0244", "static fn f() {}")]
    [InlineData("PIP0244", "extend R { static fn make() -> R = R(1); } fn f(r: R) { r.make(); }")]
    [InlineData("PIP0244", "extend R { fn get() -> Int = this.x; } fn f() { R.get(); }")]
    [InlineData("PIP0245", "extend R: T { }")]
    [InlineData("PIP0246", "extend R: T { fn m() -> String = \"\"; }")]
    [InlineData("PIP0247", "extend Int: T { }")]
    [InlineData("PIP0247", "extend T { }")]
    [InlineData("PIP0247", "extend O<Int> { }")]
    [InlineData("PIP0248", "fn f<X: R>(x: X) {}")]
    [InlineData("PIP0250", "trait U { static fn make() -> Int; }")]
    [InlineData("PIP0251", "fn f();")]
    [InlineData("PIP0251", "extend R { fn g(); }")]
    [InlineData("PIP0252", "extend String { fn Length() -> Int = 0; }")]
    public void Generics_and_trait_errors_are_reported(string code, string program)
    {
        AssertSingleError(code, Declarations + program);
    }

    [Fact]
    public void Type_arguments_are_inferred_from_arguments_and_expected_types()
    {
        AssertNoErrors(Declarations + """
            record Pair<A, C>(a: A, c: C);
            fn id<Q>(q: Q) -> Q = q;
            fn none<Q>() -> O<Q> = O.N;
            extend O {
                fn either<U>(whenSome: U, whenNone: U) -> U = match this { S(_) => whenSome, N => whenNone };
                static fn empty() -> O<V> = O.N;
            }
            extend R: T { fn m() -> Int = this.x; }
            fn useBound<Y: T>(b: B<Y>) -> Int = b.x.m();
            fn main() {
                let p: Pair<O<Int>, Long> = Pair(O.N, 1);
                let d: Double = id(1);
                let n: O<String> = none();
                let e: O<Bool> = O.empty();
                let s = O.S(R(1)).either("yes", "no");
                let t: T = if d > 0.0 { R(1) } else { R(2) };
                print(useBound(B(R(3))) + t.m());
            }
            """);
    }

    [Fact]
    public void Exhaustiveness_substitutes_type_arguments()
    {
        var program = Declarations + "fn f(o: O<Bool>) -> Int = match o { S(true) => 1, N => 0 };";
        var diagnostic = Assert.Single(Compilation.Compile(program).Diagnostics);
        Assert.Contains("'O.S(false)' is not covered", diagnostic.Message);
    }

    private const string Users = "record U(name: String, nick: String?); ";

    [Theory]
    [InlineData("PIP0202", "fn f(r: Result<Int, String>) -> Result<Int, Int> = Ok(r?);")]
    [InlineData("PIP0212", "fn f(u: U?) -> String = u.name;")]
    [InlineData("PIP0230", "fn f(x: Int?) -> Int = match x { Some(v) => v };")]
    [InlineData("PIP0234", "fn f(x: Int) -> Int = match x { null => 0, _ => 1 };")]
    [InlineData("PIP0260", "fn f() { let x = null; }")]
    [InlineData("PIP0260", "fn f() { let s: String = null; }")]
    [InlineData("PIP0261", "fn f(n: Int) -> Int = n ?? 0;")]
    [InlineData("PIP0261", "fn f(u: U) -> String? = u?.name;")]
    [InlineData("PIP0262", "fn f(x: Int?) -> Bool { let b = x is Some(v); b }")]
    [InlineData("PIP0262", "fn f(x: Int?) { if !(x is Some(v)) { } }")]
    [InlineData("PIP0262", "fn f(x: Int?) { if x is Some(v) || true { } }")]
    [InlineData("PIP0263", "fn f(x: Int?) -> Int = x?;")]
    [InlineData("PIP0263", "fn f(x: Int) -> Int? = x?;")]
    [InlineData("PIP0263", "fn f(r: Result<Int, String>) -> Int? = r?;")]
    [InlineData("PIP0264", "enum Option { A }")]
    [InlineData("PIP0264", "record Result(x: Int);")]
    public void Null_safety_errors_are_reported(string code, string program)
    {
        AssertSingleError(code, Users + program);
    }

    [Fact]
    public void Missing_none_is_reported_by_name()
    {
        var diagnostic = Assert.Single(Compilation.Compile("fn f(x: Int?) -> Int = match x { Some(v) => v };").Diagnostics);
        Assert.Contains("'Option.None' is not covered", diagnostic.Message);
    }

    [Fact]
    public void Optional_values_work_without_ceremony()
    {
        AssertNoErrors(Users + """
            extend U { fn label() -> String = this.name; }
            fn a(x: Int?) -> Int = x ?? 0;
            fn b(u: U?) -> String? = u?.nick;
            fn c(u: U?) -> String? = u?.label();
            fn e() -> Int? = 5;
            fn g(r: Result<Int, String>) -> Result<Int, String> = Ok(r? + 1);
            fn k(x: Int?) -> Bool = x == null || x == 3;
            fn m<T>(v: T?) -> Bool = v is Some(_);
            fn n() -> Bool = m(5);
            fn p(x: Int?) -> String { if x is Some(v) && v > 1 { "{v}" } else { "none" } }
            fn q(x: Int?) -> Int = match x { Some(v) if v is 1 | 2 => v, _ => 0 };
            fn r(flag: Bool) -> Long? = if flag { 1 } else { null };
            fn s() -> Result<Int, String> = if true { Ok(1) } else { Err("no") };
            fn t(x: Int?) -> Int { var total = 0; var y = x; while y is Some(v) && v > 0 { total += v; y = v - 1; } total }
            """);
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
