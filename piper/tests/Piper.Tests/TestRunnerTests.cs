namespace Piper.Tests;

/// <summary><c>test</c> blocks, <c>Assert</c> and <c>AssertEqual</c>, and the runner behind <c>piper test</c>.</summary>
public class TestRunnerTests
{
    private const string Program = """
        using System.Collections.Generic;
        using System.Threading.Tasks;

        fn Add(a: int, b: int) => int = a + b;

        test "adds" {
            AssertEqual(5, Add(2, 3));
        }

        test "fails an assert" {
            let total = Add(4, 4);
            Assert(total == 9);
        }

        test "fails with a message" {
            Assert(false, "not yet");
        }

        test "compares lists" {
            let xs: List<int> = [1, 2];
            AssertEqual([1, 2, 3], xs);
        }

        test "throws" {
            let zero = 0;
            Print(1 / zero);
        }

        async test "awaits" {
            await Task.Yield();
            AssertEqual("ab", "a" + "b");
        }
        """;

    private static TestRunResult Run(string program, string? filter = null)
    {
        var result = Compilation.Compile(program, "maths.piper", new CompilationOptions(LineDirectives: true, IncludeTests: true));
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        return Runner.RunTests(result.Files, filter);
    }

    [Fact]
    public void Runs_each_test_in_order_and_reports_failures()
    {
        var run = Run(Program);

        Assert.Empty(run.Errors);
        Assert.False(run.Success);
        Assert.Equal(
            [("adds", true), ("fails an assert", false), ("fails with a message", false), ("compares lists", false), ("throws", false), ("awaits", true)],
            run.Results.Select(r => (r.Name, r.Passed)));
        Assert.Equal("Assertion failed: total == 9\n   at maths.piper:line 12", run.Results[1].Failure);
        Assert.StartsWith("Assertion failed: not yet", run.Results[2].Failure);
        Assert.StartsWith("Expected [1, 2, 3] but got [1, 2]", run.Results[3].Failure);
        Assert.StartsWith("Unhandled exception: System.DivideByZeroException", run.Results[4].Failure);
        Assert.Contains("maths.piper:line 26", run.Results[4].Failure);
    }

    [Fact]
    public void A_filter_picks_tests_by_name()
    {
        var run = Run(Program, filter: "ADD");
        Assert.True(run.Success);
        Assert.Equal("adds", Assert.Single(run.Results).Name);
    }

    [Fact]
    public void Tests_are_left_out_of_other_builds_but_still_checked()
    {
        var result = Compilation.Compile(Program, "maths.piper");
        Assert.True(result.Success);
        Assert.Equal(6, result.TestCount);
        Assert.DoesNotContain("PiperTest", result.CSharp);

        var broken = Compilation.Compile("test \"broken\" { let x: int = \"text\"; }");
        Assert.False(broken.Success);
    }

    [Fact]
    public void Dotnet_operators_overloads_and_private_helpers_work()
    {
        // Regressions found writing the samples: .NET types' own operators, C#'s better-conversion-target rule for
        // overloads (Task.WhenAll on a List), and a private function whose signature uses a private type.
        var run = Run("""
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;

            private record Entry(name: string);
            private fn Entries() => List<Entry> = [Entry("a"), Entry("b")];
            fn Count() => int = Entries().Count;

            async fn One() => Task<int> { await Task.Yield(); 1 }

            async test "dotnet" {
                let start = DateOnly(2026, 1, 1);
                Assert(start.AddDays(1) > start);
                AssertEqual(TimeSpan.FromDays(2), DateTime(2026, 1, 3) - DateTime(2026, 1, 1));
                mut span = TimeSpan.FromHours(1);
                span += TimeSpan.FromHours(1);
                AssertEqual(TimeSpan.FromHours(-2), -span);

                let tasks: List<Task<int>> = [One(), One()];
                let results = await Task.WhenAll(tasks);
                AssertEqual(2, results.Length);

                AssertEqual(2, Count());
            }
            """);
        Assert.True(run.Success, string.Join("\n", run.Results.Select(r => r.Failure)));
    }

    [Fact]
    public void A_broken_precondition_fails_the_test_with_its_condition_and_values()
    {
        var run = Run("""
            fn Divide(a: int, b: int) => int requires b != 0 = a / b;

            test "divides" {
                let zero = 0;
                Print(Divide(1, zero));
            }
            """);
        var failure = Assert.Single(run.Results).Failure!;
        Assert.StartsWith("Unhandled exception: System.ArgumentException: Precondition failed: b != 0 (b = 0)", failure);
        Assert.Contains("at Divide in maths.piper:line 1", failure);
        Assert.Contains("maths.piper:line 5", failure);
    }

    [Theory]
    [InlineData("PIP0336", "test \"t\" { AssertEqual(1, \"one\"); }")]
    [InlineData("PIP0337", "test $\"t{1}\" { }")]
    [InlineData("PIP0338", "test \"t\" { } test \"t\" { }")]
    [InlineData("PIP0205", "test \"t\" { Assert(); }")]
    [InlineData("PIP0205", "test \"t\" { AssertEqual(1); }")]
    [InlineData("PIP0202", "test \"t\" { Assert(1); }")]
    [InlineData("PIP0299", "test \"t\" { await System.Threading.Tasks.Task.Yield(); }")]
    public void Invalid_tests_are_errors(string code, string program)
    {
        var diagnostics = Compilation.Compile(program).Diagnostics;
        Assert.True(diagnostics.Length == 1 && diagnostics[0].Code == code, $"Expected exactly one {code} but got:\n{string.Join("\n", diagnostics)}");
    }
}
