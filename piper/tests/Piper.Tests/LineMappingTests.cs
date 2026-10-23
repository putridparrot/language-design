namespace Piper.Tests;

/// <summary><c>#line</c> directives map generated C# back to Piper source: for runtime errors and C# compile errors.</summary>
public class LineMappingTests
{
    private const string Crashing = """
        fn check(value: int) => int {
            if value < 0 {
                throw System.InvalidOperationException("negative: {value}");
            }
            value
        }

        fn main() {
            print(check(1));
            print(check(-5));
        }
        """;

    [Fact]
    public void Directives_name_the_piper_line_of_each_statement()
    {
        var result = Compilation.Compile(Crashing, "crash.piper", new CompilationOptions(LineDirectives: true));
        Assert.True(result.Success);
        Assert.Contains("#line 3 \"crash.piper\"", result.CSharp);
        Assert.Contains("#line 10 \"crash.piper\"", result.CSharp);
        Assert.Contains("#line default", result.CSharp);
    }

    [Fact]
    public void Directives_are_off_by_default()
    {
        Assert.DoesNotContain("#line", Compilation.Compile(Crashing, "crash.piper").CSharp);
    }

    [Fact]
    public void Unhandled_exceptions_report_piper_lines()
    {
        var result = Compilation.Compile(Crashing, "crash.piper", new CompilationOptions(LineDirectives: true));
        var run = Runner.Run(result.Files);

        Assert.False(run.Success);
        var error = Assert.Single(run.Errors);
        Assert.Contains("System.InvalidOperationException: negative: -5", error);
        Assert.Contains("at check in crash.piper:line 3", error);
        Assert.Contains("at main in crash.piper:line 10", error);
        Assert.Equal("1\n", run.Output);
    }

    [Fact]
    public void Csharp_errors_are_reported_as_internal_errors_at_the_mapped_line()
    {
        var build = Runner.Build("#line 7 \"broken.piper\"\nclass C { int x = \"text\"; }\n");

        Assert.False(build.Success);
        var error = Assert.Single(build.Errors);
        Assert.StartsWith("broken.piper(7,", error);
        Assert.Contains("internal compiler error", error);
        Assert.Contains("CS0029", error);
    }
}
