using System.Collections.Immutable;
using Piper.Diagnostics;
using Piper.Text;

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

    [Theory]
    [InlineData("PIP0297", "async fn f() => int = 1;")]
    [InlineData("PIP0297", "using System.Threading.Tasks; fn f() { let g: () => int = async () => { await Task.Yield(); 1 }; }")]
    [InlineData("PIP0298", "using System.Threading.Tasks; async fn f() => Task<int> = 1;")]
    [InlineData("PIP0299", "using System.Threading.Tasks; fn f() => int { await Task.Yield(); 1 }")]
    [InlineData("PIP0299", "using System.Threading.Tasks; async fn f() { let g = () => await Task.Yield(); await Task.Yield(); }")]
    [InlineData("PIP0300", "async fn f() { await 5; }")]
    [InlineData("PIP0303", "fn f() => int { yield return 1; }")]
    [InlineData("PIP0303", "using System.Collections.Generic; async fn f() => IEnumerable<int> { await System.Threading.Tasks.Task.Yield(); yield return 1; }")]
    [InlineData("PIP0304", "fn f() { let g = () => { yield return 1; }; }")]
    [InlineData("PIP0305", "using System.Collections.Generic; fn f() => IEnumerable<int> { yield return 1; return; }")]
    [InlineData("PIP0306", "using System.Collections.Generic; fn f() => IEnumerable<int> { try { yield return 1; } catch (e) { } }")]
    [InlineData("PIP0306", "using System.Collections.Generic; fn f() => IEnumerable<int> { try { } finally { yield break; } }")]
    [InlineData("PIP0202", "using System.Collections.Generic; fn f() => IEnumerable<int> { yield return \"x\"; }")]
    [InlineData("PIP0219", "using System.Collections.Generic; fn main() => IEnumerable<int> { yield return 1; }")]
    [InlineData("PIP0302", "async fn f() { await for x in [1, 2] { } }")]
    [InlineData("PIP0299", "fn f(s: System.Collections.Generic.IAsyncEnumerable<int>) { await for x in s { } }")]
    [InlineData("PIP0294", "async fn f() { await using let s = System.Text.StringBuilder(); }")]
    [InlineData("PIP0301", "using System.Threading.Tasks; trait T { async fn m() => Task; }")]
    [InlineData("PIP0219", "async fn main() => System.Threading.Tasks.Task<int> = await System.Threading.Tasks.Task.FromResult(1);")]
    [InlineData("PIP0247", "trait Show { fn show() => string; } extend Option: Show { fn show() => string = \"\"; }")]
    public void Async_and_runtime_type_errors_are_reported(string code, string program)
    {
        AssertSingleError(code, program);
    }

    [Theory]
    // `async` and `await` are contextual, as in C#: still usable as names.
    [InlineData("fn f() { let async = 1; let await = async + 1; print(await); }")]
    [InlineData("fn f() { let yield = 1; print(yield); }")]
    [InlineData("fn await(x: int) => int = x; fn f() => int = await(2) + 1;")]
    // An async main.
    [InlineData("using System.Threading.Tasks; async fn main() { await Task.Delay(1); }")]
    public void Async_keywords_are_contextual(string program)
    {
        AssertNoErrors(program);
    }

    [Theory]
    // .NET's names for the builtin types are the same types, as in C#.
    [InlineData("using System; fn f(s: String, n: Int32) => string = s + n.ToString();")]
    [InlineData("fn f(o: object) => string = o.ToString() ?? \"\";")]
    [InlineData("fn f(x: int) => System.Int32 = x;")]
    public void Builtin_types_have_their_csharp_aliases(string program)
    {
        AssertNoErrors(program);
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
    [InlineData("PIP0201", "record P(x: int, x: int);")]
    [InlineData("PIP0202", "fn main() { let a: int = \"s\"; }")]
    [InlineData("PIP0202", "fn main() { let a: int = 1.5; }")]
    [InlineData("PIP0202", "fn f(a: long) {} fn main() { f(1.5); }")]
    [InlineData("PIP0203", "fn main() { let a = 1 + true; }")]
    [InlineData("PIP0203", "fn main() { let a = \"a\" < \"b\"; }")]
    [InlineData("PIP0204", "fn main() { let a = -true; }")]
    [InlineData("PIP0205", "fn f(a: int) {} fn main() { f(); }")]
    [InlineData("PIP0205", "record P(x: int); fn main() { let p = P(1, 2); }")]
    [InlineData("PIP0206", "fn main() { let a = 1; a(); }")]
    [InlineData("PIP0207", "fn main() { let a = 1; a = 2; }")]
    [InlineData("PIP0207", "fn f(a: int) { a = 2; }")]
    [InlineData("PIP0207", "fn main() { for i in 0..3 { i = 1; } }")]
    [InlineData("PIP0208", "record P(x: int); fn main() { let p = P(1); p.x = 2; }")]
    [InlineData("PIP0209", "fn f(a: int) => int { if a > 0 { return 1; } }")]
    [InlineData("PIP0209", "fn f() => int { let a = 1; }")]
    [InlineData("PIP0210", "fn main() { if 1 { } }")]
    [InlineData("PIP0210", "fn main() { while \"yes\" { } }")]
    [InlineData("PIP0211", "fn f(a: Foo) {}")]
    [InlineData("PIP0212", "record P(x: int); fn main() { let p = P(1); print(p.y); }")]
    [InlineData("PIP0213", "fn main() { break; }")]
    [InlineData("PIP0214", "fn main() { let a = print(1); }")]
    [InlineData("PIP0214", "fn f(a: unit) {}")]
    [InlineData("PIP0215", "fn main() { return 1; }")]
    [InlineData("PIP0215", "fn f() => int { return; }")]
    [InlineData("PIP0216", "fn main() { let r = 0..3; }")]
    [InlineData("PIP0282", "fn main() { for i in 3 { } }")]
    [InlineData("PIP0007", "fn main() { let x = 2147483648; }")]
    [InlineData("PIP0288", "fn main() { let names = string[3]; }")]
    [InlineData("PIP0107", "fn main() { let t: (int) = 1; }")]
    [InlineData("PIP0292", "fn main() { let b = (byte)300; }")]
    [InlineData("PIP0292", "fn main() { let i = (int)1e20; }")]
    [InlineData("PIP0202", "fn main() { let b: byte = 300; }")]
    [InlineData("PIP0202", "fn main() { let i = 1; let b: byte = i; }")]
    [InlineData("PIP0202", "fn main() { var b: byte = 1; let i = 2; b += i; }")]
    [InlineData("PIP0203", "fn main() { let x = 1UL + 1; }")]
    [InlineData("PIP0203", "fn main() { let x = 1.5m + 1.5; }")]
    [InlineData("PIP0204", "fn main() { let x = -1UL; }")]
    [InlineData("PIP0295", "fn main() { let x = (int)\"1\"; }")]
    [InlineData("PIP0294", "fn main() { using let s = \"text\"; }")]
    [InlineData("PIP0294", "fn main() { using (1) { } }")]
    [InlineData("PIP0207", "fn main() { using var s = System.IO.MemoryStream(); s = System.IO.MemoryStream(); }")]
    [InlineData("PIP0108", "fn main() { using let (a, b) = (1, 2); }")]
    [InlineData("PIP0292", "fn main() { let x = int.MaxValue + 1; }")]
    [InlineData("PIP0292", "fn main() { let x = 2_000_000_000 * 2; }")]
    [InlineData("PIP0292", "fn main() { let x = -(-2147483648); }")]
    [InlineData("PIP0292", "fn main() { let x = long.MinValue / -1L; }")]
    [InlineData("PIP0293", "fn main() { let x = 10 / (5 - 5); }")]
    [InlineData("PIP0293", "fn main() { let x = 7L % 0; }")]
    [InlineData("PIP0291", "fn f(x: int) {} fn main() { f(out let n); }")]
    [InlineData("PIP0207", "fn main() { let n = 0; int.TryParse(\"1\", out n); }")]
    [InlineData("PIP0274", "fn main() { var n = \"\"; int.TryParse(\"1\", out n); }")]
    [InlineData("PIP0274", "fn main() { int.TryParse(out let n, \"1\"); }")]
    [InlineData("PIP0201", "fn main() { let n = 1; int.TryParse(\"1\", out let n); }")]
    [InlineData("PIP0200", "fn main() { while int.TryParse(\"1\", out let n) { break; } let m = n; }")]
    [InlineData("PIP0289", "fn main() { let (a, b) = 1; }")]
    [InlineData("PIP0289", "fn main() { let (a, b) = (1, 2, 3); }")]
    [InlineData("PIP0290", "fn main() { let t = (x: 1, x: 2); }")]
    [InlineData("PIP0290", "fn main() { let t = (Item2: 1, b: 2); }")]
    [InlineData("PIP0207", "fn main() { let a = 1; var b = 2; (a, b) = (b, a); }")]
    [InlineData("PIP0202", "fn main() { var a = 1; var b = 2; (a, b) = (1L, 2); }")]
    [InlineData("PIP0230", "fn f(t: (bool, int)) => int = match t { (true, _) => 1, (false, 0) => 2 };")]
    [InlineData("PIP0236", "fn f(t: (int, int)) => int = match t { (a, b, c) => 1 };")]
    [InlineData("PIP0202", "fn main() { let xs: int[] = [1]; let s: string[] = xs; }")]
    [InlineData("PIP0007", "fn main() { let x = 3 - 2147483648; }")]
    [InlineData("PIP0240", "fn f<T>(x: T) {} fn main() { let a = f; }")]
    [InlineData("PIP0217", "fn main() { let a = print; }")]
    [InlineData("PIP0219", "fn main(a: int) {}")]
    [InlineData("PIP0220", "record P(P: int);")]
    [InlineData("PIP0221", "fn main() { let a = if true { 1 } else { \"s\" }; }")]
    public void Semantic_errors_are_reported(string code, string program)
    {
        AssertSingleError(code, program);
    }

    private const string Shapes = "enum S { A, B(x: int) } record P(a: int, b: int); ";

    [Theory]
    [InlineData("PIP0201", "fn f(p: P) => int = match p { P(x, x) => x };")]
    [InlineData("PIP0207", "fn f(n: int) { match n { x => { x = 1; } } }")]
    [InlineData("PIP0221", "fn f(n: int) { let a = match n { 0 => 1, _ => \"s\" }; }")]
    [InlineData("PIP0222", "enum E { ToString }")]
    [InlineData("PIP0222", "record R(Equals: int);")]
    [InlineData("PIP0230", "fn f(s: S) => int = match s { A => 1 };")]
    [InlineData("PIP0232", "fn f() { let s = S.B; }")]
    [InlineData("PIP0232", "fn f() { let s = S.A(); }")]
    [InlineData("PIP0233", "fn f(n: int) => int = match n { x if { let y = 1; y > x } => 1, _ => 2 };")]
    [InlineData("PIP0234", "fn f(n: int) => int = match n { \"a\" => 1, _ => 2 };")]
    [InlineData("PIP0234", "fn f(s: S) => int = match s { P(_, _) => 1, _ => 2 };")]
    [InlineData("PIP0235", "fn f() { let s = S.C; }")]
    [InlineData("PIP0235", "fn f(s: S) => int = match s { C(x) => 1, _ => 2 };")]
    [InlineData("PIP0236", "fn f(s: S) => int = match s { B(x, y) => 1, _ => 2 };")]
    [InlineData("PIP0236", "fn f(s: S) => int = match s { B => 1, _ => 2 };")]
    [InlineData("PIP0296", "fn f(s: S) => int = match s { B(x) | A => 1, _ => 2 };")]
    [InlineData("PIP0296", "fn f(p: P, t: string) => int = match (p, t) { (P(x, _), _) | (_, x) => 1 };")]
    [InlineData("PIP0237", "fn f(s: S) => int = if s is B(x) | A { 1 } else { 2 };")]
    [InlineData("PIP0238", "enum E {}")]
    [InlineData("PIP0238", "enum E { E }")]
    [InlineData("PIP0239", "fn f(t: string) => int = match t { \"a\"..\"z\" => 1, _ => 2 };")]
    [InlineData("PIP0239", "fn f(n: int) => int = match n { 5..=1 => 1, _ => 2 };")]
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
        var program = Shapes + "record Pair(l: bool, r: bool); " +
            $"fn f(s: S, flag: bool, n: int, p: P, pair: Pair) => int = {match};";
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
        var result = Compilation.Compile(Shapes + $"fn f(s: S, flag: bool, n: int) => int = {match};");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var warning = Assert.Single(result.Diagnostics);
        Assert.Equal(("PIP0231", Diagnostics.DiagnosticSeverity.Warning), (warning.Code, warning.Severity));
    }

    [Fact]
    public void Exhaustive_matches_need_no_wildcard()
    {
        AssertNoErrors(Shapes + """
            record Pair(l: bool, r: bool);
            enum Tree { Leaf, Node(left: Tree, right: Tree) }
            fn a(s: S) => int = match s { A => 0, B(0) => 1, B(n) => n };
            fn b(p: Pair) => int = match p { Pair(true, true) => 1, Pair(false, _) => 2, Pair(_, false) => 3 };
            fn c(t: Tree) => int = match t { Leaf => 0, Node(Leaf, _) => 1, Node(Node(_, _), r) => 2 };
            fn d(flag: bool) => int = match flag { true => 1, false => 0 };
            fn e(s: S) => int = match s { S.A | S.B(_) => 1 };
            """);
    }

    [Fact]
    public void Arms_that_return_have_type_Never()
    {
        AssertNoErrors(Shapes + "fn f(s: S) => int { let n = match s { A => return 0, B(x) => x }; n * 2 }");
    }

    private const string Declarations =
        "trait T { fn m() => int; } record R(x: int); enum O<V> { S(v: V), N } record B<X: T>(x: X); ";

    [Theory]
    [InlineData("PIP0201", "extend R { fn x() => int = 1; }")]
    [InlineData("PIP0201", "enum E { A(name: string) } extend E { fn name() => string = \"\"; }")]
    [InlineData("PIP0202", "fn f() { let t: T = R(1); }")]
    [InlineData("PIP0217", "extend R { fn g() => int = 1; } fn f(r: R) { let v = r.g; }")]
    [InlineData("PIP0240", "fn f() { let x = O.N; }")]
    [InlineData("PIP0241", "fn f(r: R<int>) {}")]
    [InlineData("PIP0241", "fn f(o: O) {}")]
    [InlineData("PIP0242", "fn f(b: B<int>) {}")]
    [InlineData("PIP0242", "fn f() { let b = B(1); }")]
    [InlineData("PIP0243", "fn f(r: R) { r.nope(); }")]
    [InlineData("PIP0244", "fn f() => R = this;")]
    [InlineData("PIP0244", "static fn f() {}")]
    [InlineData("PIP0244", "extend R { static fn make() => R = R(1); } fn f(r: R) { r.make(); }")]
    [InlineData("PIP0244", "extend R { fn get() => int = this.x; } fn f() { R.get(); }")]
    [InlineData("PIP0245", "extend R: T { }")]
    [InlineData("PIP0246", "extend R: T { fn m() => string = \"\"; }")]
    [InlineData("PIP0247", "extend int: T { }")]
    [InlineData("PIP0247", "extend T { }")]
    [InlineData("PIP0247", "extend O<int> { }")]
    [InlineData("PIP0248", "fn f<X: R>(x: X) {}")]
    [InlineData("PIP0250", "trait U { static fn make() => int; }")]
    [InlineData("PIP0251", "fn f();")]
    [InlineData("PIP0251", "extend R { fn g(); }")]
    [InlineData("PIP0252", "extend string { fn Length() => int = 0; }")]
    public void Generics_and_trait_errors_are_reported(string code, string program)
    {
        AssertSingleError(code, Declarations + program);
    }

    [Fact]
    public void Type_arguments_are_inferred_from_arguments_and_expected_types()
    {
        AssertNoErrors(Declarations + """
            record Pair<A, C>(a: A, c: C);
            fn id<Q>(q: Q) => Q = q;
            fn none<Q>() => O<Q> = O.N;
            extend O {
                fn either<U>(whenSome: U, whenNone: U) => U = match this { S(_) => whenSome, N => whenNone };
                static fn empty() => O<V> = O.N;
            }
            extend R: T { fn m() => int = this.x; }
            fn useBound<Y: T>(b: B<Y>) => int = b.x.m();
            fn main() {
                let p: Pair<O<int>, long> = Pair(O.N, 1);
                let d: double = id(1);
                let n: O<string> = none();
                let e: O<bool> = O.empty();
                let s = O.S(R(1)).either("yes", "no");
                let t: T = if d > 0.0 { R(1) } else { R(2) };
                print(useBound(B(R(3))) + t.m());
            }
            """);
    }

    [Fact]
    public void Exhaustiveness_substitutes_type_arguments()
    {
        var program = Declarations + "fn f(o: O<bool>) => int = match o { S(true) => 1, N => 0 };";
        var diagnostic = Assert.Single(Compilation.Compile(program).Diagnostics);
        Assert.Contains("'O.S(false)' is not covered", diagnostic.Message);
    }

    private const string Users = "record U(name: string, nick: string?); ";

    [Theory]
    [InlineData("PIP0202", "fn f(r: Result<int, string>) => Result<int, int> = Ok(r?);")]
    [InlineData("PIP0212", "fn f(u: U?) => string = u.name;")]
    [InlineData("PIP0230", "fn f(x: int?) => int = match x { Some(v) => v };")]
    [InlineData("PIP0234", "fn f(x: int) => int = match x { null => 0, _ => 1 };")]
    [InlineData("PIP0260", "fn f() { let x = null; }")]
    [InlineData("PIP0260", "fn f() { let s: string = null; }")]
    [InlineData("PIP0261", "fn f(n: int) => int = n ?? 0;")]
    [InlineData("PIP0261", "fn f(u: U) => string? = u?.name;")]
    [InlineData("PIP0262", "fn f(x: int?) => bool { let b = x is Some(v); b }")]
    [InlineData("PIP0262", "fn f(x: int?) { if !(x is Some(v)) { } }")]
    [InlineData("PIP0262", "fn f(x: int?) { if x is Some(v) || true { } }")]
    [InlineData("PIP0263", "fn f(x: int?) => int = x?;")]
    [InlineData("PIP0263", "fn f(x: int) => int? = x?;")]
    [InlineData("PIP0263", "fn f(r: Result<int, string>) => int? = r?;")]
    [InlineData("PIP0264", "enum Option { A }")]
    [InlineData("PIP0264", "record Result(x: int);")]
    public void Null_safety_errors_are_reported(string code, string program)
    {
        AssertSingleError(code, Users + program);
    }

    [Fact]
    public void Missing_none_is_reported_by_name()
    {
        var diagnostic = Assert.Single(Compilation.Compile("fn f(x: int?) => int = match x { Some(v) => v };").Diagnostics);
        Assert.Contains("'Option.None' is not covered", diagnostic.Message);
    }

    [Fact]
    public void Optional_values_work_without_ceremony()
    {
        AssertNoErrors(Users + """
            extend U { fn label() => string = this.name; }
            fn a(x: int?) => int = x ?? 0;
            fn b(u: U?) => string? = u?.nick;
            fn c(u: U?) => string? = u?.label();
            fn e() => int? = 5;
            fn g(r: Result<int, string>) => Result<int, string> = Ok(r? + 1);
            fn k(x: int?) => bool = x == null || x == 3;
            fn m<T>(v: T?) => bool = v is Some(_);
            fn n() => bool = m(5);
            fn p(x: int?) => string { if x is Some(v) && v > 1 { "{v}" } else { "none" } }
            fn q(x: int?) => int = match x { Some(v) if v is 1 | 2 => v, _ => 0 };
            fn r(flag: bool) => long? = if flag { 1 } else { null };
            fn s() => Result<int, string> = if true { Ok(1) } else { Err("no") };
            fn t(x: int?) => int { var total = 0; var y = x; while y is Some(v) && v > 0 { total += v; y = v - 1; } total }
            """);
    }

    private static ImmutableArray<Diagnostic> CompileFiles(params string[] files) =>
        Compilation.Compile(files.Select((text, i) => new SourceText(text, $"file{i}.piper"))).Diagnostics;

    private static void AssertSingleErrorInFiles(string code, params string[] files)
    {
        var diagnostics = CompileFiles(files);
        Assert.True(
            diagnostics.Length == 1 && diagnostics[0].Code == code,
            $"Expected exactly one {code} but got:\n{string.Join("\n", diagnostics.Select(d => d.Format()))}");
    }

    [Theory]
    [InlineData("PIP0106", "fn f() {} namespace X;")]
    [InlineData("PIP0106", "fn f() {} using System;")]
    [InlineData("PIP0240", "using System.Collections.Generic; fn f() { let xs = List(); }")]
    [InlineData("PIP0252", "using System.Text; extend StringBuilder { fn Append() {} }")]
    [InlineData("PIP0270", "private record P(x: int); fn f(p: P) {}")]
    [InlineData("PIP0270", "private trait T { fn m(); } record R(x: int); extend R: T { fn m() {} }")]
    [InlineData("PIP0272", "using Nope.Missing;")]
    [InlineData("PIP0272", "fn f() { let x = System.Nope.Thing(); }")]
    [InlineData("PIP0274", "using System; fn f() => double = Math.Sqrt(\"x\");")]
    [InlineData("PIP0277", "using System; fn f(x: System) {}")]
    [InlineData("PIP0278", "using System; fn f() { let m = Math(); }")]
    [InlineData("PIP0212", "using System; fn f() => int = \"abc\".Lenght;")]
    [InlineData("PIP0243", "using System.Text; fn f() { let b = StringBuilder(); b.Apend(\"x\"); }")]
    public void Module_and_interop_errors_are_reported(string code, string program)
    {
        AssertSingleError(code, program);
    }

    [Fact]
    public void Private_declarations_are_invisible_to_other_files()
    {
        AssertSingleErrorInFiles("PIP0271", "namespace N; private fn helper() => int = 1;", "using N; fn f() => int = helper();");
        AssertSingleErrorInFiles("PIP0271", "namespace N; private record R(x: int);", "using N; fn f(r: R) {}");
        AssertSingleErrorInFiles("PIP0271",
            "namespace N; record R(x: int); extend R { private fn secret() => int = 1; }",
            "using N; fn f(r: R) => int = r.secret();");
    }

    [Fact]
    public void Names_imported_from_two_namespaces_are_ambiguous()
    {
        AssertSingleErrorInFiles("PIP0273", "namespace A; record R(x: int);", "namespace B; record R(x: int);", "using A; using B; fn f(r: R) {}");
    }

    [Fact]
    public void A_program_has_one_main()
    {
        AssertSingleErrorInFiles("PIP0276", "fn main() {}", "namespace Other; fn main() {}");
    }

    [Fact]
    public void Namespaces_span_files_and_can_be_qualified()
    {
        var diagnostics = CompileFiles(
            "namespace Geo.Shapes; record Point(x: int, y: int); private fn secret() => int = 1; fn origin() => Point = Point(secret(), 0);",
            "namespace Geo.Shapes; fn far() => Point = Point(100, 100);",
            "namespace App; using Geo.Shapes; fn main() { let a: Geo.Shapes.Point = origin(); let b = Geo.Shapes.far(); print(a == b); }");
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Interop_members_resolve()
    {
        AssertNoErrors("""
            using System;
            using System.Text;
            using System.Collections.Generic;
            extend StringBuilder { fn line(s: string) { this.AppendLine(s); } }
            fn a(s: string?) => bool = string.IsNullOrEmpty(s);
            fn b() => double = Math.Max(1, 2.5) + Math.Sqrt(2);
            fn c() => int = "abc".IndexOf('b') + "x".Length;
            fn d() => string = TimeSpan.FromSeconds(90).ToString();
            fn e() { let sb = StringBuilder(); sb.line("hi"); let xs: List<int> = List(); xs.Add(1); print(xs.Count); }
            fn g() => string? = Environment.GetEnvironmentVariable("HOME");
            fn h() => long = Math.Abs(-3L) + int.MaxValue;
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
            fn isEven(n: int) => bool = if n == 0 { true } else { isOdd(n - 1) };
            fn isOdd(n: int) => bool = if n == 0 { false } else { isEven(n - 1) };
            record Line(start: Point, end: Point);
            record Point(x: int, y: int);
            """);
    }

    [Fact]
    public void Shadowing_in_nested_scopes_is_allowed()
    {
        AssertNoErrors("fn f(x: int) { let x = x + 1; { let x = \"s\"; } }");
    }

    [Fact]
    public void Numeric_widening_is_implicit()
    {
        AssertNoErrors("""
            fn f(a: double) => double = a;
            fn main() { let l: long = 1; let d: double = l; var x = 1L; x += 1; print(f(2) + d); let m = if true { 1 } else { 2.5 }; }
            """);
    }

    [Fact]
    public void Diverging_branches_take_the_type_of_the_other_branch()
    {
        AssertNoErrors("""
            fn f(a: int) => int {
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
