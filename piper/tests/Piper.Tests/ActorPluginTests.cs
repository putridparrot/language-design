using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Piper.Plugins;
using Piper.Text;

namespace Piper.Tests;

/// <summary>
/// The actors plugin (plugins/Piper.Plugins.Actors): what `actor` declarations do and check. The plugin is loaded from
/// its build output, as a program's piper.json would load it.
/// </summary>
public class ActorPluginTests
{
    private static readonly Lazy<ImmutableArray<IPiperPlugin>> Actors = new(() =>
    {
        var (plugins, errors) = PluginLoader.Load(PluginsDirectory(), ["Piper.Plugins.Actors/bin/plugin/Piper.Plugins.Actors.dll"]);
        Assert.Empty(errors);
        return plugins;
    });

    private static string PluginsDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "plugins"));

    private static CompilationResult Compile(string text) =>
        Compilation.Compile([new SourceText(text, "main.piper")], new CompilationOptions { Plugins = Actors.Value });

    private static string Run(string text)
    {
        var result = Compile(text);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Empty(result.Diagnostics);
        var run = Runner.Run(result.Files);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        return run.Output.Replace("\r\n", "\n");
    }

    /// <summary>The one error <paramref name="text"/> has, which must be the plugin's, at <paramref name="at"/>.</summary>
    private static void AssertPluginError(string text, string at, string message)
    {
        var error = Assert.Single(Compile(text).Diagnostics);
        Assert.Equal("PIP0503", error.Code);
        Assert.Contains(message, error.Message);
        Assert.Equal(text.IndexOf(at, StringComparison.Ordinal), error.Span.Start);
    }

    [Fact]
    public void An_actor_handles_tells_and_asks_in_order()
    {
        var output = Run("""
            using System.Threading.Tasks;
            actor Counter(start: int) {
                mut count = start;
                on Increment(by: int) { count += by; }
                on Reset() { count = 0; }
                on Get() => int { count }
                on Describe(label: string) => string {
                    await Task.Yield();
                    "{label}: {count}"
                }
            }
            async fn Main() {
                await using let counter = Counter.Spawn(10);
                counter.Increment(5);
                Print(await counter.Get());
                counter.Reset();
                counter.Increment(2);
                Print(await counter.Describe("now"));
            }
            """);
        Assert.Equal("15\nnow: 2\n", output);
    }

    [Fact]
    public void Concurrent_tells_are_all_handled()
    {
        var output = Run("""
            using System.Linq;
            using System.Threading.Tasks;
            actor Tally {
                mut total = 0;
                on Add() { total += 1; }
                on Total() => int { total }
            }
            async fn Main() {
                let tally = Tally.Spawn();
                await Task.WhenAll(Enumerable.Range(0, 500).Select(i => Task.Run(() => tally.Add())));
                Print(await tally.Total());
                await tally.Stop();
            }
            """);
        Assert.Equal("500\n", output);
    }

    [Fact]
    public void A_failing_ask_throws_in_the_caller_and_a_failing_tell_doesnt_stop_the_actor()
    {
        var output = Run("""
            actor Checker {
                mut seen = 0;
                on Check(n: int) {
                    seen += 1;
                    if n < 0 { throw System.ArgumentException("negative"); }
                }
                on Strict(n: int) => int {
                    if n < 0 { throw System.ArgumentException("no negatives"); }
                    n
                }
                on Seen() => int { seen }
            }
            async fn Main() {
                let checker = Checker.Spawn();
                checker.Check(-1);
                checker.Check(1);
                try {
                    await checker.Strict(-5);
                } catch (e: System.ArgumentException) {
                    Print("caught: {e.Message}");
                }
                Print(await checker.Seen());
            }
            """);
        Assert.Equal("caught: no negatives\n2\n", output);
    }

    [Fact]
    public void A_stopped_actor_refuses_messages()
    {
        var output = Run("""
            actor Box {
                on Get() => int { 1 }
            }
            async fn Main() {
                let box = Box.Spawn();
                await box.Stop();
                try {
                    await box.Get();
                } catch (e: System.InvalidOperationException) {
                    Print(e.Message);
                }
            }
            """);
        Assert.Equal("Box has stopped.\n", output);
    }

    [Fact]
    public void A_handler_parameter_needs_a_type()
    {
        AssertPluginError("actor A { on Add(n) { } }\nfn Main() {}", "n)", "Give 'n' a type");
    }

    [Fact]
    public void Handlers_need_distinct_names_that_arent_generated_ones()
    {
        AssertPluginError("actor A { on Add() { } on Add() { } }\nfn Main() {}", "Add() { } }", "already has a handler called 'Add'");
        AssertPluginError("actor A { on Stop() { } }\nfn Main() {}", "Stop", "the name of a method every actor has");
    }

    [Fact]
    public void A_handler_cant_return()
    {
        AssertPluginError("actor A { on Get() => int { return 1; } }\nfn Main() {}", "return", "A handler can't 'return'");
    }

    [Fact]
    public void An_actor_needs_a_handler()
    {
        AssertPluginError("actor Empty { mut n = 0; }\nfn Main() {}", "Empty", "has no handlers");
    }

    [Fact]
    public void Messages_carrying_something_mutable_are_warned_about()
    {
        var text = """
            using System.Collections.Generic;
            using System.Collections.Immutable;
            record Order(id: int, lines: List<string>);
            record Point(x: int, y: int);
            actor Logger { on Log(text: string) { } }
            actor Shop {
                on Add(items: List<string>) { }
                on Place(order: Order) { }
                on Batch(points: Point[]) { }
                on Move(to: Point, at: System.DateTime, tags: ImmutableList<string>, maybe: int?, logger: Logger) { }
            }
            fn Main() {}
            """;
        var result = Compile(text);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.All(result.Diagnostics, d => Assert.Equal(("PIP0505", Diagnostics.DiagnosticSeverity.Warning), (d.Code, d.Severity)));
        Assert.Equal(
            [text.IndexOf("items:", StringComparison.Ordinal), text.IndexOf("order:", StringComparison.Ordinal), text.IndexOf("points:", StringComparison.Ordinal)],
            result.Diagnostics.Select(d => d.Span.Start));
        Assert.Contains("'order' holds a List<string>", result.Diagnostics[1].Message);
    }

    [Fact]
    public void An_error_in_a_handler_is_reported_where_it_is()
    {
        var text = "actor A { mut n = 0; on Set(s: string) { n = s; } }\nfn Main() {}";
        var error = Assert.Single(Compile(text).Diagnostics);
        Assert.NotEqual("PIP0503", error.Code);
        Assert.Equal(text.IndexOf("s; }", StringComparison.Ordinal), error.Span.Start);
    }
}
