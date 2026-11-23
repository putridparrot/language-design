using Piper.Binding;
using Piper.Text;

namespace Piper.Tests;

/// <summary>
/// A build leaves out what only the program's tests use (<see cref="TestOnlyCode"/>): an executable keeps what Main
/// reaches, a library what its public declarations reach, and C# written out what anything not private reaches. What
/// the tests don't use at all stays, and so does all of it under <c>piper test</c>.
/// </summary>
public class TestOnlyCodeTests
{
    private const string Shop = """
        record Order(id: int, total: decimal);
        record Fixture(orders: List<Order>);
        enum Seed { Small, Large }
        trait Describe { fn Describe() => string; }

        extend Order {
            fn Doubled() => Order = Order(this.id, this.total * 2m);
            fn Label() => string = $"#{this.id}";
            operator +(other: Order) => Order = Order(this.id, this.total + other.total);
        }
        extend Order: Describe { fn Describe() => string = this.Label(); }

        fn Total(orders: List<Order>) => decimal = orders.Sum(o => o.total);
        fn MakeFixture(seed: Seed) => Fixture = Fixture(match seed { Seed.Small => [Order(1, 1m)], Seed.Large => [Order(1, 1m), Order(2, 2m)] });
        fn Check(fixture: Fixture) => bool = Total(fixture.orders) > 0m;
        private fn Unused() => decimal = Total([]);
        private fn TestHelper() => int = 42;

        [System.Obsolete("kept: .NET may look for it")]
        fn Attributed() => bool = Check(MakeFixture(Seed.Small));

        fn Main() {
            let order = Order(1, 10m);
            Print(Total([order]));
            Print(order.Describe());
        }

        test "totals" {
            let fixture = MakeFixture(Seed.Large);
            Assert(Check(fixture));
            AssertEqual(4m, Order(1, 2m).Doubled().total);
            AssertEqual(42, TestHelper());
            AssertEqual(3m, (Order(1, 1m) + Order(2, 2m)).total);
        }
        """;

    /// <summary>The program without its attributed function, which the tests don't use, so kept everything it reaches.</summary>
    private static readonly string WithoutAttributed =
        Shop.Replace("[System.Obsolete(\"kept: .NET may look for it\")]\nfn Attributed() => bool = Check(MakeFixture(Seed.Small));\n", "");

    private static string CSharp(string program, ProgramRoots roots, bool tests = false)
    {
        var result = Compilation.Compile([new SourceText("using System.Collections.Generic;\nusing System.Linq;\n" + program, "shop.piper")],
            new CompilationOptions(IncludeTests: tests) { Roots = roots });
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        return result.CSharp!;
    }

    [Fact]
    public void An_executable_leaves_out_what_only_its_tests_use()
    {
        var csharp = CSharp(Shop, ProgramRoots.Main);
        // Only the test uses these.
        Assert.DoesNotContain("Doubled", csharp);
        Assert.DoesNotContain("TestHelper", csharp);
        // Main uses these, or C# calls them: the operator, and a trait's method.
        Assert.Contains("record Order", csharp);
        Assert.Contains("Total(", csharp);
        Assert.Contains("operator +", csharp);
        Assert.Contains("Describe()", csharp);
        Assert.Contains("Label()", csharp);
        // The test uses these too, but so does something kept: a function with an attribute, and dead code (Unused),
        // which the tests don't use, and which stays as it is.
        Assert.Contains("MakeFixture", csharp);
        Assert.Contains("Fixture", csharp);
        Assert.Contains("enum Seed", csharp.Replace("record Seed", "enum Seed"));
        Assert.Contains("Unused", csharp);
    }

    [Fact]
    public void Without_an_attribute_keeping_them_a_tests_fixtures_go_too()
    {
        var csharp = CSharp(WithoutAttributed, ProgramRoots.Main);
        Assert.DoesNotContain("MakeFixture", csharp);
        Assert.DoesNotContain("Fixture", csharp);
        Assert.DoesNotContain("Seed", csharp);
        Assert.DoesNotContain("Check(", csharp);
        Assert.DoesNotContain("Attributed", csharp);
        Assert.Contains("Total(", csharp);
    }

    [Fact]
    public void Written_out_C_sharp_keeps_whatever_isnt_private()
    {
        var csharp = CSharp(WithoutAttributed, ProgramRoots.Visible);
        Assert.DoesNotContain("TestHelper", csharp);
        Assert.Contains("MakeFixture", csharp);
        Assert.Contains("Doubled", csharp);
    }

    [Fact]
    public void A_library_keeps_what_its_public_declarations_use()
    {
        var program = WithoutAttributed
            .Replace("fn Check(fixture: Fixture)", "public fn Check(fixture: Fixture)")
            .Replace("record Fixture(", "public record Fixture(")
            .Replace("record Order(", "public record Order(")
            .Replace("fn Total(", "public fn Total(");
        var csharp = CSharp(program, ProgramRoots.Exported);
        Assert.Contains("Check(", csharp);
        Assert.DoesNotContain("MakeFixture", csharp);
        Assert.DoesNotContain("Doubled", csharp);
    }

    [Fact]
    public void Piper_test_keeps_everything()
    {
        var csharp = CSharp(Shop, ProgramRoots.Main, tests: true);
        Assert.Contains("Doubled", csharp);
        Assert.Contains("TestHelper", csharp);
    }

    [Fact]
    public void What_remains_still_runs()
    {
        var result = Compilation.Compile([new SourceText("using System.Collections.Generic;\nusing System.Linq;\n" + Shop, "shop.piper")],
            new CompilationOptions { Roots = ProgramRoots.Main });
        var run = Runner.Run(result.Files);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        Assert.Equal("10\n#1\n", run.Output.ReplaceLineEndings("\n"));
    }
}
