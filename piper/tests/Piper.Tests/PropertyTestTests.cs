namespace Piper.Tests;

/// <summary>Property tests, <c>test "name" for (x: int in 1..10) { … }</c>: generating values, and shrinking failures.</summary>
public class PropertyTestTests
{
    private const int Seed = 1234;

    private static TestRunResult Run(string program, int? seed = Seed)
    {
        var result = Compilation.Compile(program, "props.piper", new CompilationOptions(LineDirectives: true, IncludeTests: true));
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        return Runner.RunTests(result.Files, references: result.References, seed: seed);
    }

    private static string Failure(string program, int? seed = Seed)
    {
        var run = Run(program, seed);
        Assert.Empty(run.Errors);
        return Assert.Single(run.Results).Failure ?? throw new Xunit.Sdk.XunitException("The test passed.");
    }

    [Fact]
    public void Values_of_each_kind_are_generated_and_kept_in_their_ranges()
    {
        var run = Run("""
            using System.Collections.Generic;
            using System.Linq;

            record Money(amount: decimal) requires amount > 0m;
            enum Shape { Dot, Circle(r: int), Rect(w: int, h: int) }
            enum Tree { Leaf, Node(left: Tree, value: int, right: Tree) }
            enum Colour { Red, Green, Blue }
            newtype LotId = int;

            fn Size(t: Tree) => int = match t { Leaf => 0, Node(l, _, r) => Size(l) + 1 + Size(r) };

            test "lists" for (xs: List<int>, words: string[], counts: Dictionary<string, int>, tags: HashSet<char>) {
                AssertEqual(xs, xs.AsEnumerable().Reverse().Reverse().ToList());
                Assert(words.Length >= 0 && counts.Count >= 0 && tags.Count >= 0);
            }

            test "ranges" for (a: int in 1..500, b: long in 0..<10, c: char in 'a'..'z', d: byte in 250..255) {
                Assert(a >= 1 && a <= 500);
                Assert(b >= 0 && b < 10);
                Assert(c >= 'a' && c <= 'z');
                Assert(d >= 250);
            }

            test "collections" for (name: string in ["ada", "bob"], vowel: char in "aeiou", colour: Colour in Colour.Values) {
                Assert(name == "ada" || name == "bob");
                Assert("aeiou".Contains(vowel));
                Assert(colour is Red | Green | Blue);
            }

            test "records and enums" for (m: Money, id: LotId, note: string?, t: Tree, s: Shape, pair: (int, bool), r: Result<int, string>) {
                Assert(m.amount > 0m, "a record's requires holds");
                Assert(Size(t) >= 0);
            }

            test "requires" for (a: int, b: int) requires b != 0 {
                AssertEqual(a, (a / b) * b + a % b);
            }

            async test "async" for (n: int in 0..10) {
                await System.Threading.Tasks.Task.Yield();
                Assert(n <= 10);
            }
            """);
        Assert.Empty(run.Errors);
        Assert.True(run.Success, string.Join("\n", run.Results.Select(r => $"{r.Name}: {r.Failure}")));
        Assert.Equal(6, run.Results.Length);
    }

    [Fact]
    public void A_failure_shrinks_to_the_smallest_values_and_names_them_with_the_seed()
    {
        var failure = Failure("""
            using System.Collections.Generic;
            using System.Linq;

            test "small sums" for (xs: List<int> , flag: bool) {
                Assert(xs.Select(x => (long)x).Sum() < 100, "the sum is under 100");
            }
            """);
        Assert.Matches(@"^Falsified after (the first case|\d+ cases)(, shrunk \d+ times?)? \(seed 1234\):\n  xs = \[100\]\n  flag = False\nAssertion failed: the sum is under 100\n   at props\.piper:line 5$", failure);
    }

    [Theory]
    [InlineData("test \"t\" for (n: int in 1..100000) { Assert(n < 777); }", "n = 777")]
    [InlineData("test \"t\" for (s: string) { Assert(s.Length < 3); }", "s = \"aaa\"")]
    [InlineData("test \"t\" for (n: long) { Assert(n > -50); }", "n = -50")]
    [InlineData("test \"t\" for (d: double) { Assert(d < 1000.0); }", "d = 1000")]
    [InlineData("enum Shape { Dot, Circle(r: int), Rect(w: int, h: int) }\ntest \"t\" for (s: Shape) { Assert(s is Dot); }", "s = Circle { r = 0 }")]
    [InlineData("test \"t\" for (n: int?) { Assert(n is None); }", "n = Some(0)")]
    public void Failures_shrink_to_the_boundary(string program, string smallest)
    {
        Assert.Contains($"\n  {smallest}\n", Failure(program));
    }

    /// <summary>Generators written in Piper (LINQ's names, and a few to build with) make values, and still shrink.</summary>
    [Fact]
    public void Generators_written_in_piper_make_values_and_shrink()
    {
        var run = Run("""
            using System.Collections.Generic;
            using System.Linq;

            newtype LotId = int;

            fn Evens() => PiperGen<int> = PiperGen.For<int>().Where(n => n % 2 == 0);
            fn Ids() => PiperGen<LotId> = PiperGen.Range(1, 1000).Select(n => LotId(n));
            fn Words() => PiperGen<string> = PiperGen.OneOf(PiperGen.Constant("ada"), PiperGen.In(["bob", "cy"]));
            fn Counted() => PiperGen<List<int>> = PiperGen.Range(1, 5).SelectMany(n => PiperGen.ListOf(PiperGen.Constant(n), n, n));

            test "evens" for (n: int in Evens()) { Assert(n % 2 == 0); }
            test "words" for (w: string in Words()) { Assert(w == "ada" || w == "bob" || w == "cy"); }
            test "counted" for (xs: List<int>, counted: List<int> in Counted()) { AssertEqual(counted.Count, counted[0]); }
            test "a shuffle keeps its items" for (order: List<int> in PiperGen.Shuffled(Enumerable.Range(0, 8))) {
                AssertEqual(Enumerable.Range(0, 8).ToList(), order.Order().ToList());
            }
            test "ids under 500" for (id: LotId in Ids()) { Assert(id.value < 500); }
            test "shuffles stay sorted" for (order: List<int> in PiperGen.Shuffled(Enumerable.Range(0, 5))) {
                AssertEqual(Enumerable.Range(0, 5).ToList(), order);
            }
            """);
        Assert.Empty(run.Errors);
        Assert.Equal([true, true, true, true, false, false], run.Results.Select(r => r.Passed));
        Assert.Contains("\n  id = 500\n", run.Results[4].Failure);
        // The smallest shuffle that isn't in order: two neighbours swapped, the first pair.
        Assert.Contains("\n  order = [1, 0, 2, 3, 4]\n", run.Results[5].Failure);
    }

    /// <summary>
    /// `runs 5` is how many cases must pass. A test whose `requires` refuses every case gives up after ten times that,
    /// so the count shows in its message; 100 is the default.
    /// </summary>
    [Fact]
    public void A_property_test_says_how_many_cases_must_pass()
    {
        var run = Run("""
            test "few" for (n: int) runs 5 requires n == 123456789 { }
            test "default" for (n: int) requires n == 123456789 { }
            test "many" for (n: int in 1..10) runs 2000 { Assert(n >= 1); }
            """);
        Assert.Empty(run.Errors);
        Assert.Contains("51 were discarded", run.Results[0].Failure);
        Assert.Contains("1001 were discarded", run.Results[1].Failure);
        Assert.True(run.Results[2].Passed);
    }

    [Theory]
    [InlineData("test \"t\" for (n: int) runs 0 { }")]
    [InlineData("test \"t\" for (n: int) runs many { }")]
    public void Runs_must_be_a_whole_number_above_zero(string program)
    {
        var result = Compilation.Compile(program, "props.piper", new CompilationOptions(IncludeTests: true));
        Assert.Contains(result.Diagnostics, d => d.Code == "PIP0115");
    }

    [Fact]
    public void The_same_seed_finds_the_same_failure()
    {
        const string program = "test \"t\" for (xs: System.Collections.Generic.List<int>) { Assert(xs.Count < 4); }";
        Assert.Equal(Failure(program, 42), Failure(program, 42));
        Assert.Contains("(seed 42)", Failure(program, 42));
    }

    [Fact]
    public void A_record_whose_requires_fails_is_never_generated_and_an_exception_fails_the_case()
    {
        var failure = Failure("""
            record Money(amount: decimal) requires amount > 0m;

            fn Split(m: Money, ways: int) => decimal = m.amount / ways;

            test "splits" for (m: Money, ways: int in 0..3) {
                Assert(Split(m, ways) > 0m);
            }
            """);
        Assert.Contains("\n  ways = 0\n", failure);
        Assert.Contains("Unhandled exception: System.DivideByZeroException", failure);
        Assert.Contains("props.piper:line 3", failure);
    }

    [Fact]
    public void A_test_whose_requires_refuses_almost_everything_gives_up()
    {
        var failure = Failure("test \"t\" for (n: int) requires n == 12345 { }");
        Assert.StartsWith("Gave up after 0 passing cases: ", failure);
    }

    [Fact]
    public void A_type_that_cannot_be_generated_says_so()
    {
        var failure = Failure("test \"t\" for (s: System.IO.Stream) { }");
        Assert.StartsWith("Unhandled exception: System.NotSupportedException: Values of type Stream can't be generated.", failure);
    }

    [Fact]
    public void Property_tests_and_their_generators_are_left_out_of_other_builds()
    {
        var result = Compilation.Compile("fn Main() { }\ntest \"t\" for (n: int in 1..5, s: string) { Assert(n > 0); }", "props.piper");
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Equal(1, result.TestCount);
        Assert.DoesNotContain("PiperTest", result.CSharp);
        Assert.DoesNotContain("PiperGen", result.CSharp);
    }

    [Theory]
    [InlineData("PIP0398", "test \"t\" for (n: int in ..5) { }")]
    [InlineData("PIP0399", "test \"t\" for (s: string in 1..5) { }")]
    [InlineData("PIP0211", "test \"t\" for (n: Nope) { }")]
    [InlineData("PIP0274", "test \"t\" for (n: int in [\"a\"]) { }")]
    public void Invalid_property_tests_are_errors(string code, string program)
    {
        var diagnostics = Compilation.Compile(program).Diagnostics;
        Assert.True(diagnostics.Length == 1 && diagnostics[0].Code == code, $"Expected exactly one {code} but got:\n{string.Join("\n", diagnostics)}");
    }
}
