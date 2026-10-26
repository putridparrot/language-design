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
    [InlineData("PIP0307", "fn f() { let x = { ...5, y: 1 }; }")]
    [InlineData("PIP0308", "record P(x: int); fn f(p: P) => P = { ...p, x: 1, x: 2 };")]
    [InlineData("PIP0284", "fn f() => int { defer { return 1; } 2 }")]
    [InlineData("PIP0284", "fn f() { while true { defer { break; } } }")]
    [InlineData("PIP0284", "fn f(x: int?) => int? { defer { let y = x?; } x }")]
    [InlineData("PIP0284", "fn f(x: int?) => int? { try { } finally { let y = x?; } x }")]
    [InlineData("PIP0306", "using System.Collections.Generic; fn f() => IEnumerable<int> { defer { yield return 1; } }")]
    [InlineData("PIP0200", "fn f() { defer Print(x); let x = 1; }")]
    [InlineData("PIP0263", "fn f(r: Result<int, string>) => int = r?.CompareTo(0);")]
    [InlineData("PIP0200", "using System.Collections.Generic; fn f(d: Dictionary<string, string>) { if !d.TryGetValue(\"k\", out let v) { Print(v); return; } }")]
    [InlineData("PIP0261", "using System.Collections.Generic; fn f(d: Dictionary<string, string>) { if d.TryGetValue(\"k\", out let v) { Print(v ?? \"\"); } }")]
    [InlineData("PIP0212", "using System.Collections.Generic; fn f(d: Dictionary<string, string>) { if !d.TryGetValue(\"k\", out let v) { Print(1); } Print(v.Length); }")]
    [InlineData("PIP0312", "fn f() { let n = 0; System.Threading.Interlocked.Increment(ref n); }")]
    [InlineData("PIP0311", "fn g(x: int) { } fn f() { mut n = 0; g(ref n); }")]
    [InlineData("PIP0313", "fn f(c: System.Collections.ObjectModel.ObservableCollection<int>) { let h = c.CollectionChanged; }")]
    [InlineData("PIP0313", "fn f(c: System.Collections.ObjectModel.ObservableCollection<int>) { c.CollectionChanged = (s, e) => Print(1); }")]
    [InlineData("PIP0309", "fn f() { let xs = [1, ...5]; }")]
    [InlineData("PIP0310", "fn f(xs: string[]) { let ys: int[] = [...xs]; }")]
    [InlineData("PIP0310", "fn f(xs: int[]) { let ys: int?[] = [...xs]; }")]
    [InlineData("PIP0221", "fn f(xs: string[]) { let ys = [1, ...xs]; }")]
    [InlineData("PIP0202", "record P(x: int); fn f(p: P) => P = { ...p, x: \"one\" };")]
    [InlineData("PIP0200", "record P(x: int); fn f(p: P) => P = { ...p, x };")]
    [InlineData("PIP0212", "record P(x: int); fn f(p: P) => P = { ...p, z: 1 };")]
    [InlineData("PIP0303", "fn f() => int { yield return 1; }")]
    [InlineData("PIP0303", "using System.Collections.Generic; async fn f() => IEnumerable<int> { await System.Threading.Tasks.Task.Yield(); yield return 1; }")]
    [InlineData("PIP0304", "fn f() { let g = () => { yield return 1; }; }")]
    [InlineData("PIP0305", "using System.Collections.Generic; fn f() => IEnumerable<int> { yield return 1; return; }")]
    [InlineData("PIP0306", "using System.Collections.Generic; fn f() => IEnumerable<int> { try { yield return 1; } catch (e) { } }")]
    [InlineData("PIP0306", "using System.Collections.Generic; fn f() => IEnumerable<int> { try { } finally { yield break; } }")]
    [InlineData("PIP0202", "using System.Collections.Generic; fn f() => IEnumerable<int> { yield return \"x\"; }")]
    [InlineData("PIP0219", "using System.Collections.Generic; fn Main() => IEnumerable<int> { yield return 1; }")]
    [InlineData("PIP0302", "async fn f() { await for x in [1, 2] { } }")]
    [InlineData("PIP0299", "fn f(s: System.Collections.Generic.IAsyncEnumerable<int>) { await for x in s { } }")]
    [InlineData("PIP0294", "async fn f() { await using let s = System.Text.StringBuilder(); }")]
    [InlineData("PIP0301", "using System.Threading.Tasks; trait T { async fn m() => Task; }")]
    [InlineData("PIP0219", "async fn Main() => System.Threading.Tasks.Task<int> = await System.Threading.Tasks.Task.FromResult(1);")]
    [InlineData("PIP0247", "extend Option: System.IComparable<int> { fn CompareTo(other: int) => int = 0; }")]
    public void Async_and_runtime_type_errors_are_reported(string code, string program)
    {
        AssertSingleError(code, program);
    }

    [Theory]
    // `async` and `await` are contextual, as in C#: still usable as names.
    [InlineData("fn f() { let async = 1; let await = async + 1; Print(await); }")]
    [InlineData("fn f() { let yield = 1; Print(yield); }")]
    [InlineData("fn await(x: int) => int = x; fn f() => int = await(2) + 1;")]
    // An async main.
    [InlineData("using System.Threading.Tasks; async fn Main() { await Task.Delay(1); }")]
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
    [InlineData("PIP0200", "fn Main() { Print(y); }")]
    [InlineData("PIP0201", "fn Main() { let a = 1; let a = 2; }")]
    [InlineData("PIP0201", "fn f() {} fn f() {}")]
    [InlineData("PIP0201", "record P(x: int, x: int);")]
    [InlineData("PIP0202", "fn Main() { let a: int = \"s\"; }")]
    [InlineData("PIP0202", "fn Main() { let a: int = 1.5; }")]
    [InlineData("PIP0202", "fn f(a: long) {} fn Main() { f(1.5); }")]
    [InlineData("PIP0203", "fn Main() { let a = 1 + true; }")]
    [InlineData("PIP0203", "fn Main() { let a = \"a\" < \"b\"; }")]
    [InlineData("PIP0204", "fn Main() { let a = -true; }")]
    [InlineData("PIP0205", "fn f(a: int) {} fn Main() { f(); }")]
    [InlineData("PIP0205", "record P(x: int); fn Main() { let p = P(1, 2); }")]
    [InlineData("PIP0206", "fn Main() { let a = 1; a(); }")]
    [InlineData("PIP0207", "fn Main() { let a = 1; a = 2; }")]
    [InlineData("PIP0207", "fn f(a: int) { a = 2; }")]
    [InlineData("PIP0207", "fn Main() { for i in 0..<3 { i = 1; } }")]
    [InlineData("PIP0208", "record P(x: int); fn Main() { let p = P(1); p.x = 2; }")]
    [InlineData("PIP0209", "fn f(a: int) => int { if a > 0 { return 1; } }")]
    [InlineData("PIP0209", "fn f() => int { let a = 1; }")]
    [InlineData("PIP0210", "fn Main() { if 1 { } }")]
    [InlineData("PIP0210", "fn Main() { while \"yes\" { } }")]
    [InlineData("PIP0211", "fn f(a: Foo) {}")]
    [InlineData("PIP0212", "record P(x: int); fn Main() { let p = P(1); Print(p.y); }")]
    [InlineData("PIP0213", "fn Main() { break; }")]
    [InlineData("PIP0214", "fn Main() { let a = Print(1); }")]
    [InlineData("PIP0214", "fn f(a: unit) {}")]
    [InlineData("PIP0215", "fn Main() { return 1; }")]
    [InlineData("PIP0215", "fn f() => int { return; }")]
    [InlineData("PIP0216", "fn Main() { let r = 0..<3; }")]
    [InlineData("PIP0282", "fn Main() { for i in 3 { } }")]
    [InlineData("PIP0007", "fn Main() { let x = 2147483648; }")]
    [InlineData("PIP0288", "fn Main() { let names = string[3]; }")]
    [InlineData("PIP0107", "fn Main() { let t: (int) = 1; }")]
    [InlineData("PIP0292", "fn Main() { let b = (byte)300; }")]
    [InlineData("PIP0292", "fn Main() { let i = (int)1e20; }")]
    [InlineData("PIP0202", "fn Main() { let b: byte = 300; }")]
    [InlineData("PIP0202", "fn Main() { let i = 1; let b: byte = i; }")]
    [InlineData("PIP0202", "fn Main() { mut b: byte = 1; let i = 2; b += i; }")]
    [InlineData("PIP0203", "fn Main() { let x = 1UL + 1; }")]
    [InlineData("PIP0203", "fn Main() { let x = 1.5m + 1.5; }")]
    [InlineData("PIP0204", "fn Main() { let x = -1UL; }")]
    [InlineData("PIP0295", "fn Main() { let x = (int)\"1\"; }")]
    [InlineData("PIP0294", "fn Main() { using let s = \"text\"; }")]
    [InlineData("PIP0294", "fn Main() { using (1) { } }")]
    [InlineData("PIP0207", "fn Main() { using mut s = System.IO.MemoryStream(); s = System.IO.MemoryStream(); }")]
    [InlineData("PIP0108", "fn Main() { using let (a, b) = (1, 2); }")]
    [InlineData("PIP0292", "fn Main() { let x = int.MaxValue + 1; }")]
    [InlineData("PIP0292", "fn Main() { let x = 2_000_000_000 * 2; }")]
    [InlineData("PIP0292", "fn Main() { let x = -(-2147483648); }")]
    [InlineData("PIP0292", "fn Main() { let x = long.MinValue / -1L; }")]
    [InlineData("PIP0293", "fn Main() { let x = 10 / (5 - 5); }")]
    [InlineData("PIP0293", "fn Main() { let x = 7L % 0; }")]
    [InlineData("PIP0291", "fn f(x: int) {} fn Main() { f(out let n); }")]
    [InlineData("PIP0207", "fn Main() { let n = 0; int.TryParse(\"1\", out n); }")]
    [InlineData("PIP0274", "fn Main() { mut n = \"\"; int.TryParse(\"1\", out n); }")]
    [InlineData("PIP0274", "fn Main() { int.TryParse(out let n, \"1\"); }")]
    [InlineData("PIP0201", "fn Main() { let n = 1; int.TryParse(\"1\", out let n); }")]
    [InlineData("PIP0200", "fn Main() { while int.TryParse(\"1\", out let n) { break; } let m = n; }")]
    [InlineData("PIP0289", "fn Main() { let (a, b) = 1; }")]
    [InlineData("PIP0289", "fn Main() { let (a, b) = (1, 2, 3); }")]
    [InlineData("PIP0290", "fn Main() { let t = (x: 1, x: 2); }")]
    [InlineData("PIP0290", "fn Main() { let t = (Item2: 1, b: 2); }")]
    [InlineData("PIP0207", "fn Main() { let a = 1; mut b = 2; (a, b) = (b, a); }")]
    [InlineData("PIP0202", "fn Main() { mut a = 1; mut b = 2; (a, b) = (1L, 2); }")]
    [InlineData("PIP0230", "fn f(t: (bool, int)) => int = match t { (true, _) => 1, (false, 0) => 2 };")]
    [InlineData("PIP0236", "fn f(t: (int, int)) => int = match t { (a, b, c) => 1 };")]
    [InlineData("PIP0202", "fn Main() { let xs: int[] = [1]; let s: string[] = xs; }")]
    [InlineData("PIP0007", "fn Main() { let x = 3 - 2147483648; }")]
    [InlineData("PIP0240", "fn f<T>(x: T) {} fn Main() { let a = f; }")]
    [InlineData("PIP0217", "fn Main() { let a = Print; }")]
    [InlineData("PIP0219", "fn Main(a: int) {}")]
    [InlineData("PIP0219", "fn Main(args: string[], more: int) {}")]
    [InlineData("PIP0219", "fn Main(args: int[]) {}")]
    [InlineData("PIP0220", "record P(P: int);")]
    [InlineData("PIP0221", "fn Main() { let a = if true { 1 } else { \"s\" }; }")]
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
    [InlineData("PIP0234", "fn f(n: int) => int = match n { \"a\" => 1, _ => 2 };")]
    [InlineData("PIP0234", "fn f(s: S) => int = match s { P(_, _) => 1, _ => 2 };")]
    [InlineData("PIP0235", "fn f() { let s = S.C; }")]
    [InlineData("PIP0235", "fn f(s: S) => int = match s { C(x) => 1, _ => 2 };")]
    [InlineData("PIP0236", "fn f(s: S) => int = match s { B(x, y) => 1, _ => 2 };")]
    [InlineData("PIP0236", "fn f(s: S) => int = match s { B => 1, _ => 2 };")]
    [InlineData("PIP0296", "fn f(s: S) => int = match s { B(x) | A => 1, _ => 2 };")]
    [InlineData("PIP0296", "fn f(p: P, t: string) => int = match (p, t) { (P(x, _), _) | (_, x) => 1 };")]
    [InlineData("PIP0296", "fn f(s: S) => int = if s is B(x) | A { 1 } else { 2 };")]
    [InlineData("PIP0262", "fn f(s: S) => bool = s is B(x) | B(x);")]
    [InlineData("PIP0238", "enum E {}")]
    [InlineData("PIP0238", "enum E { E }")]
    [InlineData("PIP0239", "fn f(t: string) => int = match t { \"a\"..\"z\" => 1, _ => 2 };")]
    [InlineData("PIP0239", "fn f(n: int) => int = match n { 5..1 => 1, _ => 2 };")]
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
    [InlineData("match n { 0 => 1, 1..9 => 2 }", "_")]
    [InlineData("match n { -2147483648..0 => 1, 2..2147483647 => 2 }", "1")]
    [InlineData("match n { -2147483648..0 => 1, 5..2147483647 => 2 }", "1..4")]
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
    [InlineData("match n { 1..5 => 1, 3 => 2, _ => 3 }")]
    [InlineData("match n { 0..<10 => 1, 5..9 => 2, _ => 3 }")]
    [InlineData("match n { 1 | 2 | 3 => 1, 2..3 => 2, _ => 3 }")]
    [InlineData("match n { 0..4 => 1, 5..9 => 2, 3..7 => 3, _ => 4 }")]
    [InlineData("match s { Typo => 1, A => 2 }")]
    public void Unreachable_arms_are_warnings(string match)
    {
        var result = Compilation.Compile(Shapes + $"fn f(s: S, flag: bool, n: int) => int = {match};");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var warning = Assert.Single(result.Diagnostics);
        Assert.Equal(("PIP0231", Diagnostics.DiagnosticSeverity.Warning), (warning.Code, warning.Severity));
    }

    private const string ListPatterns = "using System.Collections.Generic; ";

    [Theory]
    [InlineData("match xs { [] => 0, [x] => x }", "[_, _, ...]")]
    [InlineData("match xs { [x, ...rest] => x }", "[]")]
    [InlineData("match xs { [] => 0, [x, y] => x, [x, y, z, ...] => z }", "[_]")]
    [InlineData("match xs { [] => 0, [0, ...] => 1 }", "[_]")]
    [InlineData("match n { < 0 => 1, > 0 => 2 }", "0")]
    [InlineData("match n { <= 10 => 1, 12..20 => 2, > 20 => 3 }", "11")]
    [InlineData("match p { { a: 0 } => 1, P { b: 0 } => 2 }", "P(_, _)")]
    [InlineData("match s { B { x: < 0 } => 1, A => 2 }", "S.B(_)")]
    [InlineData("match text { { Length: 0 } => 1 }", "_")]
    public void Non_exhaustive_new_patterns_name_a_missing_case(string match, string missing)
    {
        var program = ListPatterns + Shapes + $"fn f(xs: List<int>, n: int, p: P, s: S, text: string) => int = {match};";
        var diagnostic = Assert.Single(Compilation.Compile(program).Diagnostics);
        Assert.Equal("PIP0230", diagnostic.Code);
        Assert.Contains($"'{missing}' is not covered", diagnostic.Message);
    }

    [Theory]
    [InlineData("match xs { [] => 0, [x, ...] => x }")]
    [InlineData("match xs { [x, y, ...] => 1, [x] => 2, [] => 3 }")]
    [InlineData("match xs { [..., last] => last, [] => 0 }")]
    [InlineData("match n { < 0 => 1, 0 => 2, > 0 => 3 }")]
    [InlineData("match n { < 5 => 1, >= 5 => 2 }")]
    [InlineData("match p { { a: 0 } => 1, P { a: _, b: _ } => 2 }")]
    [InlineData("match text { { Length: 0 } => 1, { } => 2 }")]
    public void New_patterns_can_be_exhaustive(string match)
    {
        var program = ListPatterns + Shapes + $"fn f(xs: List<int>, n: int, p: P, s: S, text: string) => int = {match};";
        AssertNoErrors(program);
        Assert.Empty(Compilation.Compile(program).Diagnostics);
    }

    [Theory]
    [InlineData("match xs { [...] => 1, [x] => 2 }")]
    [InlineData("match xs { [x, ...] => 1, [y, z] => 2, _ => 3 }")]
    [InlineData("match n { < 10 => 1, 5 => 2, _ => 3 }")]
    [InlineData("match p { P(_, _) => 1, { a: 1 } => 2 }")]
    public void New_patterns_make_later_arms_unreachable(string match)
    {
        var result = Compilation.Compile(ListPatterns + Shapes + $"fn f(xs: List<int>, n: int, p: P) => int = {match};");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Equal("PIP0231", Assert.Single(result.Diagnostics).Code);
    }

    [Theory]
    [InlineData("PIP0322", "fn f(n: int) => bool = n is [x];")]
    [InlineData("PIP0323", "fn f(xs: int[]) => bool = xs is [..., ...];")]
    [InlineData("PIP0323", "using System.Collections.Generic; fn f(xs: IReadOnlyList<int>) => int = match xs { [x, ...rest] => x, _ => 0 };")]
    [InlineData("PIP0324", "enum E { A } fn f(e: E) => bool = e is { x: 1 };")]
    [InlineData("PIP0324", "fn f(o: int?) => bool = o is { Value: 1 };")]
    [InlineData("PIP0325", "record P(a: int); fn f(p: P) => bool = p is { a: 1, a: 2 };")]
    [InlineData("PIP0212", "record P(a: int); fn f(p: P) => bool = p is P { b: 1 };")]
    [InlineData("PIP0212", "fn f(s: string) => bool = s is { Size: 1 };")]
    [InlineData("PIP0327", "fn f(s: string) => bool = s is < \"m\";")]
    [InlineData("PIP0328", "fn f(n: uint) => bool = n is < 0;")]
    [InlineData("PIP0234", "fn f(n: int) => bool = n is < 'a';")]
    public void Invalid_new_patterns_are_errors(string code, string program) => AssertSingleError(code, program);

    [Fact]
    public void A_property_pattern_on_a_member_that_can_be_null_is_an_error()
    {
        // Exception.InnerException is Exception?, which Piper reads as an optional.
        AssertSingleError("PIP0326", "fn f(e: System.Exception) => bool = e is { InnerException: _ };");
    }

    [Theory]
    [InlineData("PIP0329", "[Nonexistent] fn f() { }")]
    [InlineData("PIP0329", "[System.String] fn f() { }")]
    [InlineData("PIP0330", "fn g() => string = \"x\"; [System.Obsolete(g())] fn f() { }")]
    [InlineData("PIP0330", "[System.Obsolete(DiagnosticId = \"A\", \"x\")] fn f() { }")]
    [InlineData("PIP0274", "[System.Obsolete(1, 2, 3)] fn f() { }")]
    [InlineData("PIP0212", "[System.Obsolete(Nope = 1)] fn f() { }")]
    [InlineData("PIP0331", "[System.Serializable] fn f() { }")]
    [InlineData("PIP0331", "[System.Flags] record R(x: int);")]
    [InlineData("PIP0332", "[System.Obsolete] [System.Obsolete] fn f() { }")]
    [InlineData("PIP0334", "[System.Obsolete(\"gone\", true)] fn old() { } fn f() { old(); }")]
    public void Invalid_attributes_are_errors(string code, string program) => AssertSingleError(code, program);

    [Fact]
    public void Using_an_obsolete_declaration_is_a_warning()
    {
        var result = Compilation.Compile("""
            [System.Obsolete("Use area.")]
            fn size() => int = 1;

            [System.Obsolete]
            record Old(x: int);

            fn Main() {
                Print(size());
                Print(Old(1).x);
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Equal(
            ["'size' is obsolete: Use area.", "'Old' is obsolete."],
            result.Diagnostics.Select(d => d.Message));
        Assert.All(result.Diagnostics, d => Assert.Equal(("PIP0333", DiagnosticSeverity.Warning), (d.Code, d.Severity)));
    }

    [Fact]
    public void Attributes_are_emitted_on_their_declarations()
    {
        var result = Compilation.Compile("""
            using System.Text.Json.Serialization;
            [System.Obsolete("old")]
            record P([JsonPropertyName("id")] x: int);
            fn f([System.Diagnostics.CodeAnalysis.StringSyntax("Regex")] p: string) { }
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("[global::System.ObsoleteAttribute(\"old\")]", result.CSharp);
        Assert.Contains("[property: global::System.Text.Json.Serialization.JsonPropertyNameAttribute(\"id\")] int x", result.CSharp);
        Assert.Contains("[global::System.Diagnostics.CodeAnalysis.StringSyntaxAttribute(\"Regex\")] string p", result.CSharp);
    }

    [Theory]
    [InlineData("fn Main() { let type = 1; }")]
    [InlineData("fn Main() { mut class = 1; }")]
    [InlineData("fn test() { }")]
    [InlineData("fn F(self: int) { }")]
    [InlineData("record Event(type: string);")]
    [InlineData("record impl(x: int);")]
    [InlineData("enum E { A(const: int) }")]
    [InlineData("trait T { fn F<where>(); }")]
    [InlineData("fn Main() { let f = (module: int) => module; }")]
    [InlineData("fn F(x: int?) => int = match x { Some(public) => 1, None => 0 };")]
    public void Reserved_words_cant_be_names(string program) => AssertSingleError("PIP0335", program);

    [Fact]
    public void Using_an_obsolete_dotnet_member_is_a_warning()
    {
        var result = Compilation.Compile("fn F() { let client = System.Net.WebClient(); }");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var warning = Assert.Single(result.Diagnostics);
        Assert.Equal(("PIP0333", DiagnosticSeverity.Warning), (warning.Code, warning.Severity));
        Assert.StartsWith("'WebClient' is obsolete: ", warning.Message);
    }

    [Fact]
    public void Using_a_dotnet_member_obsolete_as_an_error_is_an_error_not_an_internal_one()
    {
        AssertSingleError("PIP0334", "fn F() { let v = System.Runtime.CompilerServices.RuntimeOps.CreateRuntimeVariables(); }");
    }

    private const string Contracts = """
        fn Sqrt(x: double) => double requires x >= 0.0 = x;
        fn Between(low: int, high: int) => int requires low <= high, low >= 0 = high - low;
        record Percentage(value: int) requires value >= 0 && value <= 100;
        record P(x: int);
        extend P { fn Scale(k: int) => P requires k != 0 = P(this.x * k); }

        """;

    [Theory]
    [InlineData("PIP0340", "fn F() => double = Sqrt(-1.0);")]
    [InlineData("PIP0340", "fn F() => double = Sqrt(-1);")]
    [InlineData("PIP0340", "fn F() => int = Between(5, 2);")]
    [InlineData("PIP0340", "fn F() => int = Between(-1, 2);")]
    [InlineData("PIP0340", "fn F() => Percentage = Percentage(150);")]
    [InlineData("PIP0340", "fn F() => P = P(1).Scale(0);")]
    [InlineData("PIP0341", "trait T { fn F(n: int) => int requires n > 0; }")]
    [InlineData("PIP0210", "fn F(s: string) => int requires s.Length = 1;")]
    [InlineData("PIP0200", "fn F(n: int) => int requires m > 0 = n;")]
    [InlineData("PIP0335", "fn requires() { }")]
    public void Broken_and_invalid_contracts_are_errors(string code, string program) => AssertSingleError(code, Contracts + program);

    [Theory]
    [InlineData("fn F(n: double) => double = Sqrt(n);")]
    [InlineData("fn F() => int = Between(1, 2) + Between(0, 0);")]
    [InlineData("fn F(n: int) => Percentage = Percentage(n);")]
    [InlineData("fn F(p: Percentage) => Percentage = { ...p, value: 5 };")]
    public void Calls_that_meet_or_cant_be_checked_are_fine(string program) => AssertNoErrors(Contracts + program);

    [Theory]
    [InlineData("PIP0343", "fn F(x: int?) => int { let Some(v) = x else { Print(0); }; v }")]
    [InlineData("PIP0343", "fn F(x: int?) => int { mut Some(v) = x else { return 0; }; v }")]
    [InlineData("PIP0200", "fn F(x: int?) => int { let Some(v) = x else { return v; }; v }")]
    [InlineData("PIP0347", "fn F(x: int?) => int { let Some(v) = x; v }")]
    [InlineData("PIP0347", "enum S { A(n: int), B } fn F(s: S) => int { let A(n) = s; n }")]
    [InlineData("PIP0347", "fn F(xs: int[]) => int { let [a, b] = xs; a + b }")]
    [InlineData("PIP0342", "fn F(n: int) => string = \"{n,wide}\";")]
    [InlineData("PIP0345", "fn F(a: int = 1, b: int) => int = a;")]
    [InlineData("PIP0345", "record R(x: int); fn F(r: R = R(1)) => int = 0;")]
    [InlineData("PIP0345", "record V(x: int); extend V { operator +(o: V = V(0)) => V = o; }")]
    [InlineData("PIP0346", "fn F(a: int, b: int = 2) => int = a; fn G() => int = F(b: 1);")]
    [InlineData("PIP0346", "fn F(a: int, b: int = 2) => int = a; fn G() => int = F(1, c: 2);")]
    [InlineData("PIP0346", "fn F(a: int, b: int = 2) => int = a; fn G() => int = F(a: 1, 2);")]
    [InlineData("PIP0346", "fn F(a: int, b: int = 2) => int = a; fn G() => int = F(1, a: 2);")]
    [InlineData("PIP0346", "fn G(f: (int) => int) => int = f(x: 1);")]
    [InlineData("PIP0205", "fn F(a: int, b: int = 2) => int = a; fn G() => int = F();")]
    [InlineData("PIP0340", "fn F(n: int = 0) => int requires n > 0 = n; fn G() => int = F();")]
    public void Invalid_let_else_formats_defaults_and_named_arguments_are_errors(string code, string program) => AssertSingleError(code, program);

    [Fact]
    public void A_let_pattern_that_always_matches_needs_no_else()
    {
        AssertNoErrors("record P(x: int, y: int); record L(a: P, b: P); fn F(l: L) => int { let L(P(x1, _), P(x2, _)) = l; let P { y: h } = l.a; x2 - x1 + h }");
        Assert.Empty(Compilation.Compile("record P(x: int, y: int); fn F(p: P) => int { let P(x, y) = p; x + y }").Diagnostics);
    }

    [Fact]
    public void A_let_else_that_always_matches_is_a_warning()
    {
        var result = Compilation.Compile("record P(x: int); fn F(p: P) => int { let P(x) = p else { return 0; }; x }");
        Assert.True(result.Success);
        Assert.Equal(("PIP0344", DiagnosticSeverity.Warning), (Assert.Single(result.Diagnostics).Code, result.Diagnostics[0].Severity));
    }

    private const string Vectors = "record V(x: int); ";

    [Theory]
    [InlineData("PIP0339", "extend int { operator +(other: int) => int = 0; }")]
    [InlineData("PIP0339", "trait T { operator +(other: T) => T; }")]
    [InlineData("PIP0339", Vectors + "extend V { operator +() => V = this; }")]
    [InlineData("PIP0339", Vectors + "extend V { operator *(a: V, b: V) => V = this; }")]
    [InlineData("PIP0339", Vectors + "extend V { static operator +(a: int, b: int) => int = 0; }")]
    [InlineData("PIP0339", Vectors + "extend V { operator +(other: V) { } }")]
    [InlineData("PIP0339", Vectors + "extend V { operator +<U>(other: U) => V = this; }")]
    [InlineData("PIP0201", Vectors + "extend V { operator +(o: V) => V = this; operator +(p: V) => V = p; }")]
    [InlineData("PIP0203", Vectors + "extend V { operator +(o: V) => V = o; } fn F(v: V) => V = v - v;")]
    [InlineData("PIP0203", Vectors + "extend V { operator +(o: V) => V = o; } fn F(v: V) => V = v + 1;")]
    [InlineData("PIP0202", Vectors + "extend V { operator +(o: V) => int = 1; } fn F(v: V) { mut w = v; w += v; }")]
    public void Invalid_operators_are_errors(string code, string program) => AssertSingleError(code, program);

    [Fact]
    public void Operator_overloads_are_chosen_by_operand_types()
    {
        AssertNoErrors(Vectors + """
            extend V {
                operator -() => V = V(-this.x);
                operator -(other: V) => V = V(this.x - other.x);
                operator *(k: int) => V = V(this.x * k);
                static operator *(k: int, v: V) => V = v * k;
            }
            fn F(v: V) => V { mut w = -v - v * 2; w -= 3 * v; w }
            """);
    }

    private const string GenericTraits = "trait Convert<T> { fn convert() => T; } trait Ord<T: Ord<T>> { fn cmp(other: T) => int; } record M(x: int); ";

    [Theory]
    [InlineData("PIP0246", "extend M: Convert<string> { fn convert() => int = 1; }")]
    [InlineData("PIP0245", "extend M: Convert<string> { }")]
    [InlineData("PIP0314", "extend M: Convert<string>, Convert<int> { fn convert() => string = \"\"; }")]
    [InlineData("PIP0242", "fn f<T: Ord<T>>(x: T) => int = 0; fn g() => int = f(M(1));")]
    [InlineData("PIP0242", "record N(x: int); extend M: Ord<N> { fn cmp(other: N) => int = 0; }")]
    [InlineData("PIP0241", "fn f(c: Convert) => int = 0;")]
    [InlineData("PIP0315", "trait A: B { } trait B: A { }")]
    [InlineData("PIP0316", "trait A { fn m() => int; } trait B: A { fn m() => int; }")]
    [InlineData("PIP0246", "trait A { fn m() => int; } trait B: A { fn m() => string = \"\"; }")]
    [InlineData("PIP0250", "trait A { static fn m() => int = 0; } trait B: A { static fn m() => int = 1; }")]
    [InlineData("PIP0321", "trait A { fn m() => int; } trait B: A { fn m() => int = 1; } trait C: A { fn m() => int = 2; } trait D: B, C { }")]
    [InlineData("PIP0321", "trait A { fn m() => int; } trait B: A { fn m() => int = 1; } trait C: A { fn m() => int = 2; } extend M: B, C { }")]
    [InlineData("PIP0246", "trait P { fn pick<A: Convert<int>>(a: A) => A; } extend M: P { fn pick<A>(a: A) => A = a; }")]
    [InlineData("PIP0246", "trait P { fn pick<A>(a: A) => A; } extend M: P { fn pick<A, B>(a: A) => A = a; }")]
    [InlineData("PIP0250", "trait A { fn m() => int; } extend A { static fn n() => int = 1; }")]
    [InlineData("PIP0201", "trait A { fn m() => int; } extend A { fn m() => int = 1; }")]
    [InlineData("PIP0317", "trait A { fn m() => int; } trait B { fn m() => int; } trait C: A, B { }")]
    [InlineData("PIP0245", "trait A { fn m() => int; } trait B: A { fn n() => int; } extend M: B { fn n() => int = 1; }")]
    [InlineData("PIP0314", "trait E<T> { } trait O<T>: E<T> { } extend M: O<int>, E<string> { }")]
    [InlineData("PIP0248", "trait A: M { }")]
    [InlineData("PIP0318", "extend M: System.Collections.Generic.IList<int> { }")]
    [InlineData("PIP0319", "trait Z<T: Z<T>> { static fn zero() => T; } extend M: Z<M> { static fn zero() => M = M(0); } fn f(z: Z<M>) { }")]
    [InlineData("PIP0319", "trait Z<T: Z<T>> { static fn zero() => T; } extend M: Z<M> { static fn zero() => M = M(0); } fn f() { let zs: System.Collections.Generic.List<Z<M>> = []; }")]
    [InlineData("PIP0320", "trait Z<T: Z<T>> { static fn zero() => T; static fn d() => int = 1; } extend M: Z<M> { static fn zero() => M = M(0); } trait N { static fn d() => int = 1; } fn f() => int = N.d();")]
    [InlineData("PIP0246", "trait Z<T: Z<T>> { static fn zero() => T; } extend M: Z<M> { fn zero() => M = M(0); }")]
    [InlineData("PIP0245", "trait Z<T: Z<T>> { static fn zero() => T; } extend M: Z<M> { }")]
    [InlineData("PIP0246", "extend M: System.IComparable<M> { fn CompareTo(other: M) => int = 0; }")]
    [InlineData("PIP0246", "extend M: System.IComparable<M> { private fn CompareTo(other: M?) => int = 0; }")]
    [InlineData("PIP0245", "extend M: System.IDisposable { }")]
    [InlineData("PIP0242", "fn f<T: System.IComparable<T>>(x: T) => int = 0; fn g() => int = f(M(1));")]
    [InlineData("PIP0202", "extend M: Convert<string> { fn convert() => string = \"\"; } fn f(m: M) => int = m.convert();")]
    public void Generic_trait_errors_are_reported(string code, string program)
    {
        AssertSingleError(code, GenericTraits + program);
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
            fn g(b: byte) => int = match b { 0..127 => 1, 128..255 => 2 };
            fn h(c: char, n: sbyte) => int = match (c, n) { (_, -128..<0) => 1, (_, 0) => 2, (_, 1..127) => 3 };
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
    [InlineData("PIP0247", "extend int: System.IDisposable { fn Dispose() { } }")]
    [InlineData("PIP0245", "extend int: T { }")]
    [InlineData("PIP0247", "extend T: T { }")]
    [InlineData("PIP0247", "extend O<int> { }")]
    [InlineData("PIP0248", "fn f<X: R>(x: X) {}")]
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
            fn Main() {
                let p: Pair<O<int>, long> = Pair(O.N, 1);
                let d: double = id(1);
                let n: O<string> = none();
                let e: O<bool> = O.empty();
                let s = O.S(R(1)).either("yes", "no");
                let t: T = if d > 0.0 { R(1) } else { R(2) };
                Print(useBound(B(R(3))) + t.m());
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
            fn t(x: int?) => int { mut total = 0; mut y = x; while y is Some(v) && v > 0 { total += v; y = v - 1; } total }
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
        AssertSingleErrorInFiles("PIP0276", "fn Main() {}", "namespace Other; fn Main() {}");
    }

    [Fact]
    public void Namespaces_span_files_and_can_be_qualified()
    {
        var diagnostics = CompileFiles(
            "namespace Geo.Shapes; record Point(x: int, y: int); private fn secret() => int = 1; fn origin() => Point = Point(secret(), 0);",
            "namespace Geo.Shapes; fn far() => Point = Point(100, 100);",
            "namespace App; using Geo.Shapes; fn Main() { let a: Geo.Shapes.Point = origin(); let b = Geo.Shapes.far(); Print(a == b); }");
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
            fn e() { let sb = StringBuilder(); sb.line("hi"); let xs: List<int> = List(); xs.Add(1); Print(xs.Count); }
            fn g() => string? = Environment.GetEnvironmentVariable("HOME");
            fn h() => long = Math.Abs(-3L) + int.MaxValue;
            """);
    }

    [Fact]
    public void Errors_do_not_cascade()
    {
        AssertSingleError("PIP0200", "fn Main() { let a = missing + 1; let b = a * 2; Print(b); }");
    }

    [Fact]
    public void Declarations_can_appear_in_any_order()
    {
        AssertNoErrors("""
            fn Main() { Print(isEven(10)); let l = Line(Point(0, 0), Point(1, 1)); }
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
            fn Main() { let l: long = 1; let d: double = l; mut x = 1L; x += 1; Print(f(2) + d); let m = if true { 1 } else { 2.5 }; }
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

    [Theory]
    // Parallel.ForEach has many overloads, but only one takes a one-parameter lambda with a List.
    [InlineData("fn Main() { System.Threading.Tasks.Parallel.ForEach([1, 2], n => {\n    let s: string = n;\n}); }")]
    [InlineData("fn Main() { let xs = [1, 2]; xs.ForEach(n => {\n    let s: string = n;\n}); }")]
    [InlineData("fn Main() { System.Threading.Tasks.Task.Run(() => {\n    let s: string = 1;\n}); }")]
    public void An_error_in_a_lambda_passed_to_dotnet_is_reported_where_it_is(string program)
    {
        var error = Assert.Single(Compilation.Compile(program).Diagnostics);
        Assert.Equal("PIP0202", error.Code);
        Assert.Equal(program.IndexOf("let s", StringComparison.Ordinal) + "let s: string = ".Length, error.Span.Start);
    }

    [Fact]
    public void A_lambda_fitting_no_overload_is_still_reported_as_no_overload()
    {
        AssertSingleError("PIP0274", "fn Main() { let xs = [1, 2]; xs.ForEach((a, b) => { }); }");
    }

    [Theory]
    [InlineData("PIP0348", "fn Main() { private fn f() { } }")]
    [InlineData("PIP0244", "fn Main() { static fn f() { } }")]
    [InlineData("PIP0201", "fn Main() { fn f() { } fn f() { } }")]
    [InlineData("PIP0200", "fn Main() { if true { fn f() { } } f(); }")]
    [InlineData("PIP0209", "fn Main() { fn f() => int { } }")]
    public void Local_function_errors(string code, string program) => AssertSingleError(code, program);

    [Theory]
    [InlineData("PIP0349", "sealed class A { } class B: A { } fn Main() { }")]
    [InlineData("PIP0349", "class A: int { } fn Main() { }")]
    [InlineData("PIP0349", "class A: System.IComparable<int>, System.Exception { fn CompareTo(other: int) => int = 0; } fn Main() { }")]
    [InlineData("PIP0349", "class A: B { } class B: A { } fn Main() { }")]
    [InlineData("PIP0350", "abstract class A { } fn Main() { let a = A(); }")]
    [InlineData("PIP0351", "class A { mut x = 1; } fn Main() { Print(A().x); }")]
    [InlineData("PIP0351", "class A { private fn F() { } } fn Main() { A().F(); }")]
    [InlineData("PIP0352", "class A { fn F() { } } class B: A { override fn F() { } } fn Main() { }")]
    [InlineData("PIP0352", "class A { virtual fn F() { } } class B: A { override fn F(x: int) { } } fn Main() { }")]
    [InlineData("PIP0352", "class A { virtual fn F() { } } class B: A { fn F() { } } fn Main() { }")]
    [InlineData("PIP0352", "class A { override fn F() { } } fn Main() { }")]
    [InlineData("PIP0352", "class A { abstract fn F(); } fn Main() { }")]
    [InlineData("PIP0352", "class A { fn F(); } fn Main() { }")]
    [InlineData("PIP0352", "abstract class A { abstract fn F() { } } fn Main() { }")]
    [InlineData("PIP0352", "class A { static virtual fn F() { } } fn Main() { }")]
    [InlineData("PIP0352", "class A: System.Collections.ObjectModel.Collection<string> { override fn InsertItem(index: int, item: int) { } } fn Main() { }")]
    [InlineData("PIP0353", "abstract class A { abstract fn F(); } class B: A { } fn Main() { }")]
    [InlineData("PIP0354", "class A { mut x = 1; static fn F() => int = x; } fn Main() { }")]
    [InlineData("PIP0354", "class A { let x = 1; let y = x; } fn Main() { }")]
    [InlineData("PIP0354", "class A { fn F() { } static fn G() { F(); } } fn Main() { }")]
    [InlineData("PIP0207", "class A { let x = 1; fn F() { x = 2; } } fn Main() { }")]
    [InlineData("PIP0201", "class A { let x = 1; fn x() { } } fn Main() { }")]
    [InlineData("PIP0205", "class A(x: int) { } fn Main() { let a = A(); }")]
    [InlineData("PIP0212", "class A: System.Collections.ObjectModel.Collection<int> { } fn Main() { Print(A().Items); }")]
    [InlineData("PIP0200", "class A: System.Exception { static fn F() => string = Message; } fn Main() { }")]
    public void Class_errors(string code, string program) => AssertSingleError(code, program);

    [Theory]
    [InlineData("PIP0354", "class A { static let x = y; static let y = 1; } fn Main() { }")]
    [InlineData("PIP0354", "class A { static let x = x; } fn Main() { }")]
    [InlineData("PIP0354", "class A(p: int) { static let x = p; } fn Main() { }")]
    [InlineData("PIP0354", "class A { let y = 1; static let x = y; } fn Main() { }")]
    [InlineData("PIP0354", "class A { public static let x = 1; } fn Main() { Print(A().x); }")]
    [InlineData("PIP0354", "class A { public static mut x = 1; } fn Main() { A().x = 2; }")]
    [InlineData("PIP0354", "class A<T> { public static let x = 1; } fn Main() { Print(A.x); }")]
    [InlineData("PIP0351", "class A { static let x = 1; } fn Main() { Print(A.x); }")]
    [InlineData("PIP0351", "class A { static mut x = 1; } fn Main() { A.x = 2; }")]
    [InlineData("PIP0351", "class A { static let x = 1; } class B: A { fn F() => int = A.x; } fn Main() { }")]
    [InlineData("PIP0207", "class A { public static let x = 1; } fn Main() { A.x = 2; }")]
    [InlineData("PIP0207", "class A { static let x = 1; static fn F() { x = 2; } } fn Main() { }")]
    [InlineData("PIP0201", "class A { static let x = 1; let x = 2; } fn Main() { }")]
    [InlineData("PIP0201", "class A { static let x = 1; static fn x() { } } fn Main() { }")]
    [InlineData("PIP0206", "class A { public static let x = 1; } fn Main() { A.x(); }")]
    public void Static_field_errors(string code, string program) => AssertSingleError(code, program);

    [Theory]
    [InlineData("PIP0340", "class A(x: int) requires x > 0 { } fn Main() { let a = A(-1); }")]
    [InlineData("PIP0340", "class A(x: int, y: int) requires x < y { } fn Main() { let a = A(y: 1, x: 2); }")]
    [InlineData("PIP0354", "class A(x: int) requires y > 0 { let y = 1; } fn Main() { }")]
    [InlineData("PIP0354", "class A(x: int) requires F() { fn F() => bool = true; } fn Main() { }")]
    [InlineData("PIP0200", "class A(x: int) requires z > 0 { } fn Main() { }")]
    public void Class_invariant_errors(string code, string program) => AssertSingleError(code, program);

    [Theory]
    [InlineData("PIP0356", "class A { prop X: int { get; private set; } = 0; } fn Main() { A().X = 1; }")]
    [InlineData("PIP0356", "class A { prop X: int { get; private set; } = 0; } class B: A { fn F() { X = 1; } } fn Main() { }")]
    [InlineData("PIP0356", "class A { static prop X: int { get; private set; } = 0; } fn Main() { A.X += 1; }")]
    [InlineData("PIP0207", "class A { prop X => int = 1; } fn Main() { A().X = 2; }")]
    [InlineData("PIP0207", "class A { prop X => int = 1; fn F() { X = 2; } } fn Main() { }")]
    [InlineData("PIP0351", "class A { private prop X => int = 1; } fn Main() { Print(A().X); }")]
    [InlineData("PIP0354", "class A { prop X => int = 1; let y = X; } fn Main() { }")]
    [InlineData("PIP0354", "class A { prop X => int = 1; static fn F() => int = X; } fn Main() { }")]
    [InlineData("PIP0355", "class A { mut x = 0; prop X => int { set { x = value; } } } fn Main() { }")]
    [InlineData("PIP0355", "class A { prop X => int { get = 1; get = 2; } } fn Main() { }")]
    [InlineData("PIP0355", "class A { mut x = 0; prop X => int { private get = x; set { x = value; } } } fn Main() { }")]
    [InlineData("PIP0355", "class A { mut x = 0; private prop X => int { get = x; private set { x = value; } } } fn Main() { }")]
    [InlineData("PIP0355", "class A { prop X: int { get; } } fn Main() { }")]
    [InlineData("PIP0355", "class A { prop X => int { get = 1; } = 2; } fn Main() { }")]
    [InlineData("PIP0355", "class A { prop X => int { get = 1; set; } } fn Main() { }")]
    [InlineData("PIP0355", "class A { prop X => 1; } fn Main() { }")]
    [InlineData("PIP0355", "class A { virtual prop X: int { get; } = 1; } fn Main() { }")]
    [InlineData("PIP0355", "abstract class A { abstract prop X: int { get; } = 1; } fn Main() { }")]
    [InlineData("PIP0352", "class A { override prop X => int = 1; } fn Main() { }")]
    [InlineData("PIP0352", "class A { virtual prop X => int = 1; } class B: A { override prop X => string = \"\"; } fn Main() { }")]
    [InlineData("PIP0352", "class A { virtual prop X => int = 1; } class B: A { prop X => int = 2; } fn Main() { }")]
    [InlineData("PIP0352", "class A { virtual prop X => int = 1; } class B: A { mut x = 0; override prop X => int { get = x; set { x = value; } } } fn Main() { }")]
    [InlineData("PIP0352", "class A { abstract prop X => int { get; } } fn Main() { }")]
    [InlineData("PIP0352", "abstract class A { abstract prop X => int { get = 1; } } fn Main() { }")]
    [InlineData("PIP0352", "class A { static mut x = 0; static virtual prop X => int { get = x; set { x = value; } } } fn Main() { }")]
    [InlineData("PIP0352", "class E: System.Exception { override prop Message => int = 1; } fn Main() { }")]
    [InlineData("PIP0353", "abstract class A { abstract prop X => int { get; set; } } class B: A { } fn Main() { }")]
    [InlineData("PIP0109", "class A { public prop X => int = 1; } fn Main() { }")]
    [InlineData("PIP0201", "class A { mut x = 0; prop x => int = 1; } fn Main() { }")]
    [InlineData("PIP0243", "class A { prop X => int = 1; } fn Main() { A().get_X(); }")]
    public void Property_errors(string code, string program) => AssertSingleError(code, program);

    [Theory]
    [InlineData("PIP0356", "class A { mut x = 0; prop this[i: int] => int { get = x; private set { x = value; } } } fn Main() { A()[0] = 1; }")]
    [InlineData("PIP0285", "class A { prop this[i: int] => int = i; } fn Main() { A()[0] = 1; }")]
    [InlineData("PIP0351", "class A { private prop this[i: int] => int = i; } fn Main() { Print(A()[0]); }")]
    [InlineData("PIP0274", "class A { prop this[i: int] => int = i; } fn Main() { Print(A()[1, 2]); }")]
    [InlineData("PIP0202", "class A { prop this[i: int] => int = i; } fn Main() { Print(A()[\"x\"]); }")]
    [InlineData("PIP0275", "class A { prop this[i: long] => int = 1; prop this[d: double] => int = 2; } fn Main() { Print(A()[1]); }")]
    [InlineData("PIP0355", "class A { static prop this[i: int] => int = i; } fn Main() { }")]
    [InlineData("PIP0355", "class A { prop this[value: int] => int = value; } fn Main() { }")]
    [InlineData("PIP0355", "class A { prop this[i: int] => int { get; set; } } fn Main() { }")]
    [InlineData("PIP0355", "class A { prop this[i: int] => i; } fn Main() { }")]
    [InlineData("PIP0355", "class A { prop this[i: int] => int = i; prop this[j: int] => string = \"\"; } fn Main() { }")]
    [InlineData("PIP0352", "abstract class A { abstract prop this[i: int] => int { get; } } class B: A { prop this[i: int] => int = i; } fn Main() { }")]
    [InlineData("PIP0353", "abstract class A { abstract prop this[i: int] => int { get; } } class B: A { } fn Main() { }")]
    public void Indexer_errors(string code, string program) => AssertSingleError(code, program);

    private const string ProtectedBase = "class A { protected mut x = 1; protected fn F() => int = 1; protected prop P => int = 2; protected static fn S() => int = 3; prop Q: int { get; protected set; } = 0; protected prop this[i: int] => int = i; }\n";

    [Theory]
    [InlineData("PIP0357", "fn Main() { Print(A().F()); }")]
    [InlineData("PIP0357", "fn Main() { Print(A().P); }")]
    [InlineData("PIP0357", "fn Main() { Print(A().x); }")]
    [InlineData("PIP0357", "fn Main() { Print(A.S()); }")]
    [InlineData("PIP0357", "fn Main() { Print(A()[0]); }")]
    [InlineData("PIP0357", "class B: A { fn G(other: A) => int = other.F(); } fn Main() { }")]
    [InlineData("PIP0357", "class B: A { fn G(other: A) => int = other.x; } fn Main() { }")]
    [InlineData("PIP0357", "class C { fn G(a: A) => int = a.P; } fn Main() { }")]
    [InlineData("PIP0356", "fn Main() { A().Q = 1; }")]
    [InlineData("PIP0356", "class C { fn G(a: A) { a.Q = 1; } } fn Main() { }")]
    [InlineData("PIP0352", "class B { private virtual fn F() { } } fn Main() { }")]
    [InlineData("PIP0352", "class B { virtual fn F() { } } class C: B { protected override fn F() { } } fn Main() { }")]
    [InlineData("PIP0355", "class B { prop P => int { protected get = 1; } } fn Main() { }")]
    [InlineData("PIP0355", "class B { mut v = 0; protected prop P => int { get = v; protected set { v = value; } } } fn Main() { }")]
    public void Protected_errors(string code, string program) => AssertSingleError(code, ProtectedBase + program);

    [Theory]
    [InlineData("PIP0331", "class A { [System.Serializable] let x = 1; } fn Main() { }")]
    [InlineData("PIP0331", "class A { [System.NonSerialized] prop P => int = 1; } fn Main() { }")]
    [InlineData("PIP0331", "class A { [System.NonSerialized] prop this[i: int] => int = i; } fn Main() { }")]
    [InlineData("PIP0331", "[System.NonSerialized] class A { } fn Main() { }")]
    [InlineData("PIP0332", "class A { [System.Obsolete, System.Obsolete] let x = 1; } fn Main() { }")]
    public void Class_attribute_errors(string code, string program) => AssertSingleError(code, program);

    [Theory]
    [InlineData("PIP0354", "trait T { fn Name() => string; static fn Make() => string = Name(); } fn Main() { }")]
    [InlineData("PIP0200", "trait T { static fn Zero() => int; fn F() => int = Zero(); } fn Main() { }")]
    public void Bare_trait_method_errors(string code, string program) => AssertSingleError(code, program);

    [Theory]
    [InlineData("PIP0358", "class A(x: int) { A(y: int): this(y) { } } fn Main() { }")]
    [InlineData("PIP0358", "class A { A() { } } fn Main() { }")]
    [InlineData("PIP0358", "class A(x: int) { A() { } } fn Main() { }")]
    [InlineData("PIP0275", "class A(x: long) { A(d: double): this(1L) { } } fn Main() { let a = A(1); }")]
    [InlineData("PIP0274", "class A(x: int) { A(s: string): this(1) { } } fn Main() { let a = A(true); }")]
    [InlineData("PIP0351", "class A(x: int) { private A(): this(1) { } } fn Main() { let a = A(); }")]
    [InlineData("PIP0357", "class A(x: int) { protected A(): this(1) { } } class B: A(1) { fn F() { let a = A(); } } fn Main() { }")]
    [InlineData("PIP0354", "class A(x: int) { let y = 1; A(): this(y) { } } fn Main() { }")]
    [InlineData("PIP0207", "class A(x: int) { let y = 1; A(): this(1) { y = 2; } } fn Main() { }")]
    [InlineData("PIP0359", "class A { prop P: int { get; init; } = 0; } fn Main() { A().P = 1; }")]
    [InlineData("PIP0359", "class A { prop P: int { get; init; } = 0; fn F() { P = 1; } } fn Main() { }")]
    [InlineData("PIP0359", "class A(x: int) { prop P: int { get; init; } = 0; A(other: A): this(1) { other.P = 2; } } fn Main() { }")]
    [InlineData("PIP0359", "class A(x: int) { prop P: int { get; init; } = 0; A(): this(1) { let f = () => { P = 2; }; } } fn Main() { }")]
    [InlineData("PIP0355", "class A { mut v = 0; prop this[i: int] => int { get = v; init { v = value; } } } fn Main() { }")]
    [InlineData("PIP0352", "class A { mut v = 0; virtual prop P => int { get = v; init { v = value; } } } class B: A { override prop P => int { get = 1; set { } } } fn Main() { }")]
    public void Constructor_and_init_errors(string code, string program) => AssertSingleError(code, program);

    [Theory]
    [InlineData("PIP0109", "trait T { prop X => int { get; set; } } fn Main() { }")]
    [InlineData("PIP0109", "extend int { prop X: int { get; } = 1; } fn Main() { }")]
    [InlineData("PIP0109", "extend int { prop X => 1; } fn Main() { }")]
    [InlineData("PIP0109", "extend int { prop this[i: int] => int = i; } fn Main() { }")]
    [InlineData("PIP0245", "trait T { prop X => int; } record R(a: int); extend R: T { } fn Main() { }")]
    [InlineData("PIP0245", "trait T { prop X => int; } class C: T { public let X = \"\"; } fn Main() { }")]
    [InlineData("PIP0245", "trait T { prop X => int; } class C: T { let X = 1; } fn Main() { }")]
    [InlineData("PIP0246", "trait T { prop X => int; } record R(a: int); extend R: T { prop X => string = \"\"; } fn Main() { }")]
    [InlineData("PIP0243", "extend int { prop X => int = 1; } fn Main() { Print(1.get_X()); }")]
    [InlineData("PIP0285", "extend int { static prop X => int = 1; } fn Main() { int.X = 2; }")]
    [InlineData("PIP0285", "extend int { prop X => int = 1; } fn Main() { let n = 1; n.X = 2; }")]
    public void Trait_and_extend_property_errors(string code, string program) => AssertSingleError(code, program);

    [Theory]
    [InlineData("PIP0352", "abstract class A<T> { abstract fn F(x: T) => T; } class B: A<int> { override fn F(x: string) => string = x; } fn Main() { }")]
    [InlineData("PIP0352", "class A<T>(v: T) { virtual prop P => T = v; } class B: A<int>(1) { override prop P => string = \"\"; } fn Main() { }")]
    [InlineData("PIP0353", "abstract class A<T> { abstract fn F(x: T); } class B: A<int> { } fn Main() { }")]
    [InlineData("PIP0357", "class A<T> { protected let x = 1; } class B: A<int> { } fn Main() { Print(B().x); }")]
    [InlineData("PIP0202", "class A<T>(v: T) { public mut x: T = v; } class B: A<int>(1) { fn F() { x = \"s\"; } } fn Main() { }")]
    [InlineData("PIP0202", "class A<T>(v: T) { public mut x: T = v; } class B: A<int>(1) { } fn Main() { let b = B(); b.x = \"s\"; }")]
    [InlineData("PIP0202", "class A<T>(v: T) { fn Get() => T = v; } class B: A<int>(1) { fn F() => string = Get(); } fn Main() { }")]
    public void Generic_base_errors(string code, string program) => AssertSingleError(code, program);

    private const string VarianceShapes = "trait Shape { fn Area() => double; } record Circle(r: double); extend Circle: Shape { fn Area() => double = this.r; } extend int: Shape { fn Area() => double = 1.0; }\n";

    [Theory]
    [InlineData("PIP0202", "fn Main() { let ints: System.Collections.Generic.List<int> = [1]; let s: System.Collections.Generic.IEnumerable<Shape> = ints; }")]
    [InlineData("PIP0202", "fn Main() { let circles: System.Collections.Generic.List<Circle> = []; let s: System.Collections.Generic.List<Shape> = circles; }")]
    [InlineData("PIP0274", "fn Main() { let s = System.Collections.Generic.List<Shape>(); s.Add(\"x\"); }")]
    [InlineData("PIP0202", "record ByRadius(); extend ByRadius: System.Collections.Generic.IComparer<Circle> { fn Compare(a: Circle?, b: Circle?) => int = 0; } fn Main() { let c: System.Collections.Generic.IComparer<Shape> = ByRadius(); }")]
    public void Variance_errors(string code, string program) => AssertSingleError(code, VarianceShapes + program);

    [Theory]
    // The function's signature must be the delegate's.
    [InlineData("PIP0274", "fn Both(a: int, b: int) => int = a + b; fn Main() { let n: System.Collections.Generic.List<int> = [1]; n.ConvertAll(Both); }")]
    [InlineData("PIP0274", "fn Wrong(s: string) => bool = true; fn Main() { let n: System.Collections.Generic.List<int> = [1]; n.FindAll(Wrong); }")]
    // A variable holding a Func is a delegate already, which C# doesn't convert to another delegate type.
    [InlineData("PIP0274", "fn IsEven(n: int) => bool = n % 2 == 0; fn Main() { let f: System.Func<int, bool> = IsEven; let n: System.Collections.Generic.List<int> = [1]; n.FindAll(f); }")]
    public void Function_as_delegate_errors(string code, string program) => AssertSingleError(code, program);

    [Theory]
    [InlineData("PIP0295", "fn Main() { let s = (string)System.DayOfWeek.Monday; }")]
    [InlineData("PIP0295", "fn Main() { let d = (System.DayOfWeek)\"Monday\"; }")]
    // None goes only where .NET takes null.
    [InlineData("PIP0274", "fn Main() { Print(System.Math.Abs(None)); }")]
    // Several overloads take it equally well, as in C#.
    [InlineData("PIP0275", "fn Main() { let sb = System.Text.StringBuilder(); sb.Append(None); }")]
    public void Enum_cast_and_none_argument_errors(string code, string program) => AssertSingleError(code, program);

    [Fact]
    public void An_override_of_a_generic_base_s_method_is_described_with_its_type_arguments()
    {
        var result = Compilation.Compile("abstract class A<T> { abstract fn F(x: T) => T; } class B: A<int> { override fn F(x: string) => string = x; } fn Main() { }");
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("(x: int) => int"));
    }

    [Fact]
    public void A_derived_class_s_constructor_sets_its_base_s_init_properties()
    {
        var result = Compilation.Compile(
            "class A { prop P: int { get; init; } = 0; }\nclass B: A { B(x: int): this() { P = x; } }\n" +
            "fn Main() { Print(B(4).P); }");
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var run = Runner.Run(result.Files);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        Assert.Equal("4", run.Output.Trim());
    }

    [Theory]
    [InlineData("class B: A { fn G() => int = F() + P + x + S() + this[1]; fn H(other: B) => int = other.F() + other.x; fn K() { Q = 5; } } fn Main() { }")]
    [InlineData("class B: A { override fn ToString() => string = \"{base.ToString()}\"; } fn Main() { }")]
    public void Protected_members_are_usable_in_derived_classes(string program)
    {
        var result = Compilation.Compile(ProtectedBase + program);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        // C# agrees: its access rules for protected members are the ones Piper checks.
        var run = Runner.Run(result.Files);
        Assert.True(run.Success, string.Join("\n", run.Errors));
    }

    [Fact]
    public void A_class_can_override_a_dotnet_property()
    {
        var result = Compilation.Compile(
            "class NotFound(id: int): System.Exception { override prop Message => string = \"No item {id}\"; }\n" +
            "fn Main() { try { throw NotFound(3); } catch (e: System.Exception) { Print(e.Message); } }");
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Equal("No item 3", Runner.Run(result.Files).Output.Trim());
    }

    private const string ClassShapes = "abstract class Shape { } class Circle: Shape { } class Other { } trait T { fn F() => int; } extend int: T { fn F() => int = 1; }\n";

    [Theory]
    [InlineData("PIP0230", "fn F(s: Shape) => int = match s { c: Circle => 1 };")]
    [InlineData("PIP0234", "fn F(s: Shape) => int = match s { o: Other => 1, _ => 2 };")]
    [InlineData("PIP0234", "fn F(s: Shape) => int = match s { n: int => 1, _ => 2 };")]
    [InlineData("PIP0234", "fn F(s: Shape) => int = match s { c: Circle? => 1, _ => 2 };")]
    [InlineData("PIP0234", "fn F(t: T) => int = match t { n: int => 1, _ => 2 };")]
    [InlineData("PIP0234", "fn F(n: int) => int = match n { t: T => 1, _ => 2 };")]
    [InlineData("PIP0211", "fn F(s: Shape) => int = match s { c: Nope => 1, _ => 2 };")]
    [InlineData("PIP0109", "fn F() { let b = base; }")]
    [InlineData("PIP0354", "fn F() { base.ToString(); }")]
    [InlineData("PIP0354", "abstract class A { abstract fn G() => int; } class B: A { override fn G() => int = base.G(); }")]
    [InlineData("PIP0354", "class B { static fn G() => string = base.ToString() ?? \"\"; }")]
    [InlineData("PIP0295", "fn F(o: Other) => Circle = (Circle)o;")]
    [InlineData("PIP0221", "fn F() { let xs = [Circle(), Other()]; }")]
    public void Type_test_base_and_downcast_errors(string code, string program) => AssertSingleError(code, ClassShapes + program);

    [Fact]
    public void Invariants_are_lowered_like_initializers()
    {
        var result = Compilation.Compile(
            "record Limit(max: int?) requires (max ?? 0) >= 0;\n" +
            "fn Main() { Print(Limit(Some(3)).max ?? 0); let bad = -1; try { Limit(Some(bad)); } catch (e: System.ArgumentException) { Print(e.Message); } }");
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Equal("3\nInvariant failed: (max ?? 0) >= 0 (max = Some(-1))", Runner.Run(result.Files).Output.Trim().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Records_and_enums_can_override_ToString()
    {
        var result = Compilation.Compile(
            "record Money(amount: decimal);\nextend Money { override fn ToString() => string = \"£{this.amount}\"; }\n" +
            "enum Suit { Hearts, Joker(n: int) }\nextend Suit { override fn ToString() => string = match this { Hearts => \"H\", Joker(n) => \"J{n}\" }; }\n" +
            "fn Main() { Print(\"{Money(2m)} {Suit.Hearts} {Suit.Joker(3)} {Option.Some(Money(1m))}\"); }");
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Equal("£2 H J3 Some(£1)", Runner.Run(result.Files).Output.Trim());
    }

    [Theory]
    [InlineData("PIP0352", "record M(a: int); extend M { fn ToString() => string = \"x\"; }")]
    [InlineData("PIP0352", "record M(a: int); extend M { override fn ToString(x: int) => string = \"x\"; }")]
    [InlineData("PIP0352", "record M(a: int); extend M { override fn Other() => string = \"x\"; }")]
    [InlineData("PIP0352", "record M(a: int); extend M { virtual fn ToString() => string = \"x\"; }")]
    [InlineData("PIP0352", "extend int { override fn ToString() => string = \"x\"; }")]
    public void ToString_override_errors(string code, string program) => AssertSingleError(code, program);

    [Theory]
    [InlineData("PIP0245", "record R(a: int); extend R: System.Collections.Generic.IEnumerable<int> { } fn Main() { }")]
    [InlineData("PIP0246", "record R(a: int); extend R: System.Collections.Generic.IEnumerable<int> { fn GetEnumerator() => System.Collections.Generic.IEnumerator<string> { yield return \"\"; } } fn Main() { }")]
    [InlineData("PIP0203", "record V(a: int); fn Main() { Print(V(1) < V(2)); }")]
    public void Enumerable_and_comparison_errors(string code, string program) => AssertSingleError(code, program);

    [Theory]
    [InlineData("PIP0261", "fn Abs(n: int) => int = n; fn Main() { let x = 1 ?> Abs; }")]
    [InlineData("PIP0210", "fn Main() { let x = 5 ? 1 : 2; }")]
    [InlineData("PIP0221", "fn Main() { let x = true ? 1 : \"a\"; }")]
    [InlineData("PIP0202", "fn Bad(n: int) => Result<int, int> = Ok(n); fn Main() { let r: Result<int, string> = Ok(1); let s = r ?> Bad; }")]
    [InlineData("PIP0200", "fn Main() { let x: int? = Some(1); let y = x ?> Missing; }")]
    public void Conditional_and_optional_pipe_errors(string code, string program) => AssertSingleError(code, program);
    [Fact]
    public void Comparing_without_IComparable_suggests_it()
    {
        var result = Compilation.Compile("record V(a: int); fn Main() { Print(V(1) < V(2)); }");
        Assert.Contains("Implement IComparable<V>", Assert.Single(result.Diagnostics).Message);
    }
    [Fact]
    public void Dotnet_implicit_conversions_apply()
    {
        var result = Compilation.Compile(
            "using System.Numerics;\nusing System.Xml.Linq;\nfn Twice(b: BigInteger) => BigInteger = b * 2;\n" +
            "fn Main() {\n    let big: BigInteger = 5;\n    let e = XElement(\"root\", XElement(\"child\", \"hi\"));\n" +
            "    Print(\"{BigInteger.Pow(2, 70)} {big + 1} {Twice(21)} {e.Element(\"child\")?.Value ?? \"\"}\");\n}");
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Equal("1180591620717411303424 6 42 hi", Runner.Run(result.Files).Output.Trim());
    }
    [Fact]
    public void An_always_matching_type_pattern_makes_later_arms_unreachable()
    {
        var result = Compilation.Compile(ClassShapes + "fn F(s: Shape) => int = match s { o: Shape => 1, c: Circle => 2 };\nfn Main() { }");
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Equal("PIP0231", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Base_calls_type_tests_and_downcasts_run()
    {
        var result = Compilation.Compile(
            "class A { virtual fn Name() => string = \"A\"; }\n" +
            "class B: A { override fn Name() => string = \"B<\" + base.Name() + \">\"; }\n" +
            "fn Kind(o: object) => string = match o { b: B => b.Name(), _: A => \"plain A\", s: string => s, _ => \"?\" };\n" +
            "fn Main() { let a: A = B(); Print(\"{Kind(a)} {Kind(A())} {Kind(\"s\")} {Kind(1)} {((B)a).Name()}\"); }");
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Equal("B<A> plain A s ? B<A>", Runner.Run(result.Files).Output.Trim());
    }
    [Fact]
    public void A_class_names_inherited_dotnet_members_bare_including_protected_ones()
    {
        var result = Compilation.Compile(
            "class Upper: System.Collections.ObjectModel.Collection<string> {\n    override fn InsertItem(index: int, item: string) { Items.Insert(index, item.ToUpper()); }\n    fn Size() => int = Count;\n}\nclass Oops: System.Exception(\"oops\") { fn Shout() => string = Message.ToUpper(); }\nfn Main() { let u = Upper(); u.Add(\"a\"); Print(\"{u[0]} {u.Size()} {Oops().Shout()}\"); }");
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Equal("A 1 OOPS", Runner.Run(result.Files).Output.Trim());
    }

    [Fact]    public void A_class_overrides_a_protected_dotnet_method_of_a_generic_base()
    {
        var result = Compilation.Compile(
            "class Recording: System.Collections.ObjectModel.Collection<string> {\n    public mut inserted = 0;\n    override fn InsertItem(index: int, item: string) { inserted += 1; }\n}\nfn Main() { let r = Recording(); r.Add(\"a\"); r.Add(\"b\"); Print(r.inserted); }");
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Equal("2", Runner.Run(result.Files).Output.Trim());
    }

    [Fact]
    public void An_interfaces_inherited_properties_can_be_read()
    {
        // IList<T>.Count is declared on ICollection<T>, which IList<T> inherits.
        var result = Compilation.Compile("fn Count(xs: System.Collections.Generic.IList<int>) => int = xs.Count;\nfn Main() { Print(Count([1, 2, 3])); }");
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Equal("3", Runner.Run(result.Files).Output.Trim());
    }

    [Theory]
    [InlineData("fn Main(args: string[]) { Print(string.Join(\"|\", args)); }")]
    [InlineData("async fn Main(args: string[]) { await System.Threading.Tasks.Task.Yield(); Print(string.Join(\"|\", args)); }")]
    public void Main_can_take_the_command_line_arguments(string program)
    {
        var result = Compilation.Compile(program);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Equal("a|--b", Runner.Run(result.Files, args: ["a", "--b"]).Output.Trim());
    }

    [Fact]
    public void Diagnostics_are_formatted_with_file_line_and_column()
    {
        var result = Compilation.Compile("fn Main() {\n    Print(y);\n}", "test.piper");
        Assert.Equal(
            "test.piper(2,11): error PIP0200: The name 'y' does not exist in the current scope.",
            Assert.Single(result.FormattedDiagnostics));
    }
}
