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
        "'rollback' puts A's state back, by keeping its values; but 'items' holds a List<int>")]
    [InlineData("let seen: System.Collections.Generic.List<int> = [];\non failure restart;", "restart",
        "'restart' puts A's state back, by keeping its values; but 'seen' holds a List<int>")]
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

    private const string Shelf = """
        using System;
        using Piper.Runtime;

        actor Shelf(sku: string) persisted by sku {
            mut onHand = 0;
            mut reserved = 0;

            event Received(quantity: int) { onHand += quantity; }
            event Reserved(quantity: int) { onHand -= quantity; reserved += quantity; }

            on Receive(quantity: int) { emit Received(quantity); }
            on Reserve(quantity: int) => bool {
                if quantity > onHand {
                    false
                } else {
                    emit Reserved(quantity);
                    true
                }
            }
            on Broken(quantity: int) => int {
                emit Received(quantity);
                throw InvalidOperationException("broken");
            }
            on Show() => string { $"{sku}: {onHand} on hand, {reserved} reserved" }
        }

        """;

    /// <summary>
    /// A persisted actor's state changes only through its events, which are saved as each message is handled and
    /// replayed when an actor with the same identity starts: a second Shelf for "tea" picks up where the first left
    /// off. A handler that throws saves none of its events, and its state is put back.
    /// </summary>
    [Fact]
    public void A_persisted_actor_keeps_its_state_through_its_events()
    {
        var output = Run(Shelf + """
            async fn Main() {
                let store = MemoryEventStore();
                let first = Shelf.Spawn(store, "tea");
                first.Receive(10);
                Print(await first.Reserve(4));
                Print(await first.Reserve(20));
                try { await first.Broken(100); } catch (e: InvalidOperationException) { Print(e.Message); }
                Print(await first.Show());
                await first.Stop();

                let again = Shelf.Spawn(store, "tea");
                Print(await again.Show());
                let other = Shelf.Spawn(store, "milk");
                Print(await other.Show());
                Print(string.Join(", ", store.Streams));
                Print(string.Join(" ", await store.LoadAsync("Shelf/tea")));
            }
            """);
        Assert.Equal(
            "True\nFalse\nbroken\ntea: 6 on hand, 4 reserved\ntea: 6 on hand, 4 reserved\nmilk: 0 on hand, 0 reserved\nShelf/milk, Shelf/tea\n" +
            "{\"kind\":\"Received\",\"quantity\":10} {\"kind\":\"Reserved\",\"quantity\":4}\n",
            output);
    }

    /// <summary>
    /// Two actors with one identity both writing its stream: the second's save finds the stream changed, so its
    /// handler fails (and its state is put back); with `restart`, it reads the stream afresh and catches up.
    /// </summary>
    [Fact]
    public void Two_writers_of_one_stream_are_caught()
    {
        var output = Run(Shelf.Replace("persisted by sku {", "persisted by sku {\n    on failure restart;") + """
            async fn Main() {
                let store = MemoryEventStore();
                let a = Shelf.Spawn(store, "tea");
                let b = Shelf.Spawn(store, "tea");
                Print(await a.Show());
                Print(await b.Show());
                a.Receive(5);
                Print(await a.Show());
                try { await b.Reserve(0); } catch (e: EventStreamConflictException) { Print("conflict"); }
                Print(await b.Show());
            }
            """);
        Assert.Equal("tea: 0 on hand, 0 reserved\ntea: 0 on hand, 0 reserved\ntea: 5 on hand, 0 reserved\nconflict\ntea: 5 on hand, 0 reserved\n", output);
    }

    /// <summary>
    /// `transient` state isn't kept with the events: handlers change it themselves, it's put back when a handler fails
    /// like the rest, and an actor starting again (or restarting) starts it afresh.
    /// </summary>
    [Fact]
    public void A_persisted_actors_transient_state_isnt_kept()
    {
        var output = Run("""
            using System;
            using Piper.Runtime;

            actor Counter(id: string) persisted by id {
                mut total = 0;
                transient mut asked = 0;
                event Added(k: int) { total += k; }
                on Add(k: int) { emit Added(k); }
                on Ask() => string { asked += 1; $"{total} after {asked} asks" }
                on Broken() => int { asked += 100; throw InvalidOperationException("broken") }
            }

            async fn Main() {
                let store = MemoryEventStore();
                let first = Counter.Spawn(store, "c");
                first.Add(5);
                Print(await first.Ask());
                try { await first.Broken(); } catch (e: InvalidOperationException) { Print(e.Message); }
                Print(await first.Ask());
                await first.Stop();
                Print(await Counter.Spawn(store, "c").Ask());
            }
            """);
        Assert.Equal("5 after 1 asks\nbroken\n5 after 2 asks\n5 after 1 asks\n", output);
    }

    /// <summary>
    /// `snapshot every 2;` keeps the state after every second event saved, and an actor starting again restores the
    /// latest and replays only the events after it: shown by planting one whose state the events couldn't make. One
    /// kept as other types than the state's now is ignored, and every event replayed.
    /// </summary>
    [Fact]
    public void A_persisted_actor_starts_from_its_latest_snapshot()
    {
        var output = Run("""
            using System;
            using System.Collections.Immutable;
            using System.Linq;
            using Piper.Runtime;

            record Tally(total: int, items: ImmutableList<string>);

            actor Counter(id: string) persisted by id {
                mut tally = Tally(0, ImmutableList.Create<string>());
                transient mut asked = 0;
                snapshot every 2;
                event Added(k: int) { tally = Tally(tally.total + k, tally.items.Add($"{k}")); }
                on Add(k: int) => int { emit Added(k); tally.total }
                on Show() => string { asked += 1; $"{tally.total} from {string.Join("+", tally.items)}, asked {asked}" }
            }

            async fn Main() {
                let store = MemoryEventStore();
                let first = Counter.Spawn(store, "c");
                for k in [1, 2, 3] { await first.Add(k); }
                Print(await first.Show());
                await first.Stop();
                match await store.LoadSnapshotAsync("Counter/c") {
                    Some(kept) => Print($"snapshot after {kept.Version} events"),
                    None => Print("no snapshot"),
                }
                Print(await Counter.Spawn(store, "c").Show());

                let planted = MemoryEventStore();
                await planted.AppendAsync("Counter/c", 0, [...[1, 2, 3].Select(k => $"{{\"kind\":\"Added\",\"k\":{k}}}")]);
                let state = PiperSnapshot().With("tally", Tally(100, ImmutableList.Create("planted"))).Write();
                await planted.SaveSnapshotAsync("Counter/c", StoredSnapshot(2, state));
                Print(await Counter.Spawn(planted, "c").Show());

                let other = MemoryEventStore();
                await Counter.Spawn(other, "c").Add(5);
                await other.SaveSnapshotAsync("Counter/c", StoredSnapshot(1, PiperSnapshot().With("tally", 100).Write()));
                Print(await Counter.Spawn(other, "c").Show());
            }
            """);
        Assert.Equal(
            "6 from 1+2+3, asked 1\nsnapshot after 2 events\n6 from 1+2+3, asked 1\n103 from planted+3, asked 1\n5 from 5, asked 1\n",
            output);
    }

    /// <summary>
    /// An event's versions: saved with its version when that's after 1, and an older one read through its upgrades,
    /// in turn (1 to 2 to 3), whichever form it was saved in; one saved by a later program is an error, not a guess.
    /// </summary>
    [Fact]
    public void A_persisted_actor_upgrades_events_saved_as_older_versions()
    {
        var output = Run("""
            using System;
            using System.Linq;
            using Piper.Runtime;

            actor Till(id: string) persisted by id {
                mut total = 0;
                mut log = "";
                event Paid version 3 (pence: int, currency: string, note: string) { total += pence; log = $"{log}[{pence} {currency} {note}]"; }
                event Opened version 2 (by: string) { log = $"{log}(opened by {by})"; }
                upgrade Paid version 1 (pounds: int) => Paid(pounds * 100, "GBP");
                upgrade Paid version 2 (pence: int, currency: string) => Paid(pence, currency, "-");
                upgrade Opened version 1 () => Opened("nobody");
                on Pay(pence: int, currency: string) { emit Paid(pence, currency, "new"); }
                on Show() => string { $"{total}: {log}" }
            }

            async fn Main() {
                let store = MemoryEventStore();
                await store.AppendAsync("Till/t", 0, [
                    "\"Opened\"",
                    "{\"kind\":\"Paid\",\"pounds\":2}",
                    "{\"kind\":\"Paid\",\"$version\":2,\"pence\":50,\"currency\":\"EUR\"}",
                    "{\"kind\":\"Paid\",\"$version\":3,\"pence\":7,\"currency\":\"USD\",\"note\":\"tip\"}",
                ]);
                let till = Till.Spawn(store, "t");
                till.Pay(1, "GBP");
                Print(await till.Show());
                await till.Stop();
                Print((await store.LoadAsync("Till/t")).Last());

                let later = MemoryEventStore();
                await later.AppendAsync("Till/t", 0, ["{\"kind\":\"Paid\",\"$version\":4,\"pence\":1}"]);
                try {
                    Print(await Till.Spawn(later, "t").Show());
                } catch (e: Exception) {
                    Print(e.Message.Contains("version 4, saved by a later version of the program") ? "refused" : e.Message);
                }
            }
            """);
        Assert.Equal(
            "258: (opened by nobody)[200 GBP -][50 EUR -][7 USD tip][1 GBP new]\n" +
            "{\"kind\":\"Paid\",\"pence\":1,\"currency\":\"GBP\",\"note\":\"new\",\"$version\":3}\n" +
            "refused\n",
            output);
    }

    /// <summary>
    /// A persisted actor's events are recorded, each version it reads, in Name.events.json: changing an event's fields
    /// in a way saved events couldn't be read as is an error, until it's a new version with an upgrade from the old;
    /// and dropping that upgrade later is an error too, as events of the old version may still be saved.
    /// </summary>
    [Fact]
    public void A_persisted_actors_events_are_recorded_with_their_versions()
    {
        var folder = Directory.CreateTempSubdirectory("piper-events").FullName;
        try
        {
            CompilationResult CompileIn(string events, WireShapeMode mode = WireShapeMode.Record)
            {
                var text = $"fn Main() {{}}\nactor Till(id: string) persisted by id {{\nmut total = 0;\n{events}\non Get() => int {{ total }}\n}}";
                var path = Path.Combine(folder, "till.piper");
                File.WriteAllText(path, text);
                return Compilation.Compile([new SourceText(text, path)], new CompilationOptions { Plugins = Actors.Value, WireShapes = mode });
            }
            string Errors(CompilationResult result) => string.Join("\n", result.FormattedDiagnostics);

            var first = CompileIn("event Paid(pounds: int) { total += pounds * 100; }");
            Assert.True(first.Success, Errors(first));
            var recorded = File.ReadAllText(Path.Combine(folder, "Till.events.json"));
            Assert.Contains("\"Paid version 1\"", recorded);

            Assert.Contains("Paid version 1 has a new field, 'currency' (string), which events saved already don't have",
                Errors(CompileIn("event Paid(pounds: int, currency: string) { total += pounds * 100; }")));
            Assert.Contains("Paid version 1 has 'pounds' as string, but events saved already have int",
                Errors(CompileIn("event Paid(pounds: string) { total += 1; }")));
            Assert.Contains("Paid events of version 1 may be saved already, but nothing reads them now; add 'upgrade Paid version 1",
                Errors(CompileIn("event Paid version 2 (pence: int, currency: string) { total += pence; }")));
            Assert.Contains("Till has no event 'Paid' now", Errors(CompileIn("event Taken(pence: int) { total += pence; }")));
            // An optional field is read as None from the events saved without it.
            Assert.True(CompileIn("event Paid(pounds: int, note: string?) { total += pounds * 100; }", WireShapeMode.Check).Success);

            var upgraded = CompileIn("event Paid version 2 (pence: int, currency: string) { total += pence; }\nupgrade Paid version 1 (pounds: int) => Paid(pounds * 100, \"GBP\");");
            Assert.True(upgraded.Success, Errors(upgraded));
            Assert.Contains("\"Paid version 2\"", File.ReadAllText(Path.Combine(folder, "Till.events.json")));
            Assert.Contains("Paid events of version 1 may be saved already",
                Errors(CompileIn("event Paid version 2 (pence: int, currency: string) { total += pence; }")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("event Added(k: int) { n += k; }\nupgrade Added version 1 (k: int) => Added(k);", "Added version 1 (k",
        "'Added' is version 1, so there's nothing older to upgrade")]
    [InlineData("event Added version 2 (k: int) { n += k; }\nupgrade Added version 2 (k: int) => Added(k);", "Added version 2 (k: int) =>",
        "'Added' is version 2, so an upgrade reads an older one")]
    [InlineData("event Added version 3 (k: int) { n += k; }\nupgrade Added version 1 (k: int) => Added(k);", "Added version 1",
        "there's no 'upgrade Added version 2 (…) => Added(…);'")]
    [InlineData("event Added version 2 (k: int) { n += k; }\nupgrade Lost version 1 (k: int) => Lost(k);", "Lost version 1",
        "A has no event 'Lost' to upgrade")]
    [InlineData("event Added version 2 (k: int) { n += k; }\nupgrade Added version 1 (k: int) => Added(k);\nupgrade Added version 1 (k: int) => Added(k);", "Added version 1 (k: int) => Added(k);\non",
        "A already upgrades 'Added' version 1")]
    [InlineData("event Added version 0 (k: int) { n += k; }", "0 (k", "A version is a whole number from 1")]
    public void An_events_upgrades_are_checked(string events, string at, string message)
    {
        var text = $"fn Main() {{}}\nactor A(id: string) persisted by id {{\nmut n = 0;\n{events}\non Add(k: int) {{ emit Added(k); }}\non Get() => int {{ n }}\n}}";
        var error = Assert.Single(Compile(text).Diagnostics, d => d.Message.Contains(message));
        Assert.Equal(text.IndexOf(at, StringComparison.Ordinal), error.Span.Start);
    }

    [Theory]
    [InlineData("mut n = 0;\nevent Added(k: int) { n += k; }\non Add(k: int) { n += k; }", "n += k; }\non Get",
        "A persisted actor's state changes only through its events")]
    [InlineData("mut n = 0;\ntransient mut seen = 0;\nevent Added(k: int) { n += k; seen += 1; }\non Add(k: int) { emit Added(k); }", "seen += 1",
        "'seen' is transient, so it isn't kept, and an event's body can't change it")]
    [InlineData("mut n = 0;\ntransient mut seen: System.Collections.Generic.List<int> = [];\nevent Added(k: int) { n += k; }\non Add(k: int) { emit Added(k); }", "A",
        "but 'seen' holds a List<int>")]
    [InlineData("mut n = 0;\nevent Added(k: int) { n += k; }\non Add(k: int) { emit Lost(k); }", "__emit_Lost",
        "A has no event 'Lost'; its events are Added")]
    [InlineData("mut n = 0;\non Add(k: int) { }", "persisted", "A is persisted but has no events")]
    [InlineData("mut n = 0;\nevent Added(k: int) { n += k; }\non failure resume;\non Add(k: int) { emit Added(k); }", "resume",
        "A persisted actor can't resume after a failure")]
    [InlineData("mut items: System.Collections.Generic.List<int> = [];\nevent Added(k: int) { items = items; }\non Add(k: int) { emit Added(k); }", "A",
        "A is persisted, so its state changes only through events")]
    public void A_persisted_actors_rules_are_checked(string body, string at, string message)
    {
        var text = $"fn Main() {{}}\nactor A(id: string) persisted by id {{\n{body}\non Get() => int {{ 1 }}\n}}";
        var result = Compile(text);
        var error = Assert.Single(result.Diagnostics, d => d.Message.Contains(message));
        Assert.Equal(at == "__emit_Lost" ? text.IndexOf("Lost(k)", StringComparison.Ordinal) : at == "A" ? text.IndexOf("A(", StringComparison.Ordinal) : text.IndexOf(at, StringComparison.Ordinal), error.Span.Start);
    }

    [Theory]
    [InlineData("actor A { mut n = 0; event Added(k: int) { n += k; } on Get() => int { n } }", "Event 'Added' needs a persisted actor")]
    [InlineData("actor A(store: string) persisted by store { mut n = 0; event Added(k: int) { n += k; } on Get() => int { n } }",
        "A persisted actor's Spawn takes 'store' itself")]
    [InlineData("actor A(id: string) persisted { mut n = 0; on Get() => int { n } }", "Expected what identifies each A")]
    [InlineData("actor A { transient mut n = 0; on Get() => int { n } }", "Only a persisted actor's state can be transient")]
    [InlineData("actor A { mut n = 0; snapshot every 10; on Get() => int { n } }", "Only a persisted actor takes snapshots")]
    [InlineData("actor A(id: string) persisted by id { mut n = 0; snapshot every 10; snapshot every 5; event Added(k: int) { n += k; } on Get() => int { n } }",
        "already says how often it takes snapshots")]
    public void Events_need_a_persisted_actor(string actor, string message)
    {
        Assert.Contains(Compile("fn Main() {}\n" + actor).Diagnostics, d => d.Message.Contains(message));
    }
}
