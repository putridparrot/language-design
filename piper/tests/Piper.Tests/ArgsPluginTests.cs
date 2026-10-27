using System.Collections.Immutable;
using Piper.Plugins;
using Piper.Text;

namespace Piper.Tests;

/// <summary>
/// The args plugin (plugins/Piper.Plugins.Args): `args Options { … }` becomes a record with Parse and FromArgs, which
/// read the command line with Piper.Runtime's PiperArguments. Loaded by name, as it comes with piper. (FromArgs ends the
/// program on a mistake, so these use Parse; samples/todo-api uses FromArgs.)
/// </summary>
public class ArgsPluginTests
{
    private static readonly Lazy<ImmutableArray<IPiperPlugin>> Args = new(() =>
    {
        var (plugins, errors) = PluginLoader.Load(AppContext.BaseDirectory, ["args"]);
        Assert.Empty(errors);
        return plugins;
    });

    private static CompilationResult Compile(string text) =>
        Compilation.Compile([new SourceText(text, "main.piper")], new CompilationOptions { Plugins = Args.Value });

    private const string Declaration = """
        using System.Collections.Generic;

        args Options "Greets people." {
            greeting: string = "Hello" "What to say";
            times: int = 1 "How many times";
            loud: bool "Shout it";
            name: string? "Who to greet";
            ratio: double = 0.5;
            dryRun: bool;
            files: List<string> "Extra names";
        }

        fn Show(args: string[]) => string = match Options.Parse(args) {
            Ok(o) => $"{o.greeting}|{o.times}|{o.loud}|{o.name ?? "-"}|{o.ratio}|{o.dryRun}|{string.Join(",", o.files)}",
            Err(e) => $"error: {e.Split('\n')[0]}",
        };

        """;

    /// <summary>What Show gives for each command line, one per line.</summary>
    private static string[] Run(params string[][] commandLines)
    {
        var calls = string.Join("\n", commandLines.Select(line => $"    Print(Show([{string.Join(", ", line.Select(a => $"\"{a}\""))}]));"));
        var result = Compile(Declaration + $"fn Main() {{\n{calls}\n}}\n");
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Empty(result.Diagnostics);
        var run = Runner.Run(result.Files);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        return run.Output.TrimEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
    }

    [Fact]
    public void Reads_options_flags_and_the_other_arguments()
    {
        Assert.Equal(
            [
                "Hello|1|False|-|0.5|False|",
                "Hi|3|True|Ada|0.25|True|x,y",
                "Hello|1|False|-|0.5|False|--loud",
            ],
            Run([], ["--greeting", "Hi", "--times=3", "--loud", "x", "--name", "Ada", "--ratio", "0.25", "--dry-run", "y"], ["--", "--loud"]));
    }

    [Fact]
    public void Mistakes_are_errors_naming_the_option()
    {
        Assert.Equal(
            [
                "error: Unknown option '--tims'; did you mean '--times'?",
                "error: '--times' must be an int, not 'abc'.",
                "error: '--name' needs a value: --name <string>.",
                "error: '--loud' is a flag, so it takes no value.",
                "error: Unknown option '-x'; options are written '--name'.",
            ],
            Run(["--tims", "3"], ["--times", "abc"], ["--name"], ["--loud=yes"], ["-x"]));
    }

    [Fact]
    public void Help_gives_the_usage()
    {
        var result = Compile(Declaration + """
            fn Main() {
                match Options.Parse(["--help"]) {
                    Ok(_) => Print("parsed"),
                    Err(usage) => Print(usage),
                }
            }
            """);
        Assert.Empty(result.Diagnostics);
        var run = Runner.Run(result.Files);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        Assert.Contains("[options] [files...]", run.Output);
        Assert.Contains("Greets people.", run.Output);
        Assert.Contains("--times <int>", run.Output);
        Assert.Contains("How many times (default: 1)", run.Output);
        Assert.Contains("--dry-run", run.Output);
        Assert.Contains("-h, --help", run.Output);
    }

    [Fact]
    public void Required_options_and_unexpected_arguments_are_reported()
    {
        var result = Compile("""
            args Options { port: int; }
            fn Main() {
                let lines: string[][] = [[], ["--port", "1", "extra"], ["--port", "8"]];
                for args in lines {
                    match Options.Parse(args) {
                        Ok(o) => Print($"port {o.port}"),
                        Err(e) => Print(e.Split('\n')[0]),
                    }
                }
            }
            """);
        Assert.Empty(result.Diagnostics);
        var run = Runner.Run(result.Files);
        Assert.Equal(
            ["'--port' is required.", "Unexpected argument 'extra'; this program takes only options.", "port 8"],
            run.Output.TrimEnd().Split('\n').Select(l => l.TrimEnd('\r')));
    }

    [Theory]
    [InlineData("args O { x: List<int>; }", "List<int>", "An option is a bool")]
    [InlineData("args O { x: bool = true; }", "true", "A flag is false unless it's given")]
    [InlineData("args O { x: int? = 1; }", "1", "An optional option is None when it isn't given")]
    [InlineData("args O { x: int; x: string; }", "x: string", "'--x' is already an option")]
    [InlineData("args O { dryRun: bool; dry_run: bool; }", "dry_run", "'--dry-run' is already an option")]
    [InlineData("using System.Collections.Generic; args O { a: List<string>; b: List<string>; }", "List<string>; }", "Only one field can take")]
    public void Declarations_it_cant_read_are_reported(string text, string at, string message)
    {
        var errors = Compile(text + " fn Main() { }").Diagnostics;
        var error = Assert.Single(errors, e => e.Code == "PIP0503");
        Assert.Contains(message, error.Message);
        Assert.Equal(text.IndexOf(at, StringComparison.Ordinal), error.Span.Start);
    }
}
