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
                    $"{label}: {count}"
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

    /// <summary>An actor's own functions: what its handlers share, reading and changing its state.</summary>
    [Fact]
    public void An_actor_can_have_functions_its_handlers_share()
    {
        var output = Run("""
            using System.Threading.Tasks;
            actor Ledger(limit: int) {
                mut balance = 0;
                mut moves = 0;

                fn Move(amount: int) {
                    balance += amount;
                    moves += 1;
                }

                fn Fits(amount: int) => bool = balance + amount <= limit;

                async fn Audit() => Task<string> {
                    await Task.Yield();
                    $"{balance} after {moves}"
                }

                on Deposit(amount: int) => bool {
                    if Fits(amount) { Move(amount); true } else { false }
                }
                on Withdraw(amount: int) { Move(-amount); }
                on Report() => string { await Audit() }
            }
            async fn Main() {
                await using let ledger = Ledger.Spawn(100);
                Print(await ledger.Deposit(70));
                Print(await ledger.Deposit(50));
                ledger.Withdraw(20);
                Print(await ledger.Report());
            }
            """);
        Assert.Equal("True\nFalse\n50 after 2\n", output);
    }

    [Fact]
    public void An_actors_functions_are_its_own()
    {
        AssertPluginError("""
            actor Counter {
                mut count = 0;
                private fn Bump() { count += 1; }
                on Increment() { Bump(); }
            }
            fn Main() {}
            """, "private fn Bump", "An actor's functions are its own, used by its handlers: write 'fn', without 'private' or 'static'.");
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
                    Print($"caught: {e.Message}");
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

    /// <summary>
    /// `on failure rollback`: a handler that throws changes nothing, as its state is put back as it was before the
    /// message, for a tell (logged) and an ask (whose caller gets the error). Without a policy, the change stays.
    /// </summary>
    [Fact]
    public void Rollback_puts_the_state_back_as_it_was_before_the_failed_message()
    {
        var output = Run("""
            using System;

            actor Account(name: string) {
                mut balance = 0;
                mut last = "none";
                on failure rollback;
                on Deposit(amount: int) {
                    balance += amount;
                    last = $"deposit {amount}";
                    if amount < 0 { throw InvalidOperationException("negative"); }
                }
                on Withdraw(amount: int) => int {
                    balance -= amount;
                    last = $"withdraw {amount}";
                    if balance < 0 { throw InvalidOperationException("overdrawn"); }
                    balance
                }
                on Show() => string { $"{name}: {balance} after {last}" }
            }

            actor Loose {
                mut count = 0;
                on Add(n: int) { count += n; if n == 0 { throw InvalidOperationException("zero"); } }
                on Get() => int { count }
            }

            async fn Main() {
                let account = Account.Spawn("ada");
                account.Deposit(10);
                account.Deposit(-5);
                Print(await account.Show());
                try {
                    await account.Withdraw(20);
                } catch (e: InvalidOperationException) {
                    Print(e.Message);
                }
                Print(await account.Show());
                Print(await account.Withdraw(4));

                let loose = Loose.Spawn();
                loose.Add(3);
                loose.Add(0);
                Print(await loose.Get());
            }
            """);
        Assert.Equal("ada: 10 after deposit 10\noverdrawn\nada: 10 after deposit 10\n6\n3\n", output);
    }

    /// <summary>
    /// `on failure restart`: the state goes back to how Spawn made it. With a limit, more restarts than that in the
    /// window stop the actor: a message sent to it then fails at once, and one already waiting is answered with an error.
    /// </summary>
    [Fact]
    public void Restart_resets_the_state_and_stops_after_its_limit()
    {
        var output = Run("""
            using System;

            actor Counter(start: int) {
                mut count = start;
                on failure restart 1 per minute;
                on Add(n: int) { count += n; if n == 0 { throw InvalidOperationException("zero"); } }
                on Get() => int { count }
            }

            async fn Main() {
                let counter = Counter.Spawn(5);
                counter.Add(2);
                Print(await counter.Get());
                counter.Add(0);
                Print(await counter.Get());
                counter.Add(1);
                counter.Add(0);
                let waiting = counter.Get();
                try {
                    Print(await waiting);
                } catch (e: InvalidOperationException) {
                    Print(e.Message);
                }
                try {
                    counter.Add(1);
                } catch (e: InvalidOperationException) {
                    Print(e.Message);
                }
            }
            """);
        Assert.Equal("7\n5\nCounter has stopped.\nCounter has stopped.\n", output);
    }

    [Fact]
    public void Stop_ends_the_actor_at_its_first_failure()
    {
        var output = Run("""
            using System;

            actor Fragile {
                on failure stop;
                on Check(ok: bool) => string { if !ok { throw InvalidOperationException("broken"); } "fine" }
            }

            async fn Main() {
                let fragile = Fragile.Spawn();
                Print(await fragile.Check(true));
                try { await fragile.Check(false); } catch (e: InvalidOperationException) { Print(e.Message); }
                try { await fragile.Check(true); } catch (e: InvalidOperationException) { Print(e.Message); }
            }
            """);
        Assert.Equal("fine\nbroken\nFragile has stopped.\n", output);
    }

    [Theory]
    [InlineData("mut items: System.Collections.Generic.List<int> = [];\non failure rollback;", "rollback",
        "'rollback' puts A's state back by keeping its values, but 'items' holds a List<int>")]
    [InlineData("let seen: System.Collections.Generic.List<int> = [];\non failure restart;", "restart",
        "'restart' puts A's state back by keeping its values, but 'seen' holds a List<int>")]
    [InlineData("mut n = 0;\non failure explode;", "explode", "Expected what the actor does when a handler throws")]
    [InlineData("mut n = 0;\non failure restart 3;", ";\non Get", "Expected the window the restarts are counted in")]
    [InlineData("mut n = 0;\non failure restart 3 per week;", "week", "Expected 'second', 'minute' or 'hour'")]
    [InlineData("mut n = 0;\non failure stop;\non failure resume;", "resume", "Actor 'A' already says what it does on failure")]
    [InlineData("let (a, b) = (1, 2);\non failure rollback;", "let (a", "'rollback' puts the actor's state back, so declare it one name at a time")]
    public void Failure_policies_are_checked(string body, string at, string message)
    {
        var text = $"fn Main() {{}}\nactor A {{\n{body}\non Get() => int {{ 1 }}\n}}";
        var error = Assert.Single(Compile(text).Diagnostics, d => d.Message.Contains(message));
        Assert.Equal(text.IndexOf(at, StringComparison.Ordinal), error.Span.Start);
    }
}
