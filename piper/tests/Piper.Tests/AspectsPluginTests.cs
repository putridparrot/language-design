using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Piper.Plugins;
using Piper.Text;

namespace Piper.Tests;

/// <summary>
/// The aspects plugin (plugins/Piper.Plugins.Aspects): the decorators [Trace], [Timed] and [Retry]. The plugin is
/// loaded from its build output, as a program's piper.json would load it.
/// </summary>
public class AspectsPluginTests
{
    private static readonly Lazy<ImmutableArray<IPiperPlugin>> Aspects = new(() =>
    {
        var (plugins, errors) = PluginLoader.Load(PluginsDirectory(), ["Piper.Plugins.Aspects/bin/plugin/Piper.Plugins.Aspects.dll"]);
        Assert.Empty(errors);
        return plugins;
    });

    private static string PluginsDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "plugins"));

    private static CompilationResult Compile(string text) =>
        Compilation.Compile([new SourceText(text, "main.piper")], new CompilationOptions { Plugins = Aspects.Value });

    private static string Run(string text)
    {
        var result = Compile(text);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Empty(result.Diagnostics);
        var run = Runner.Run(result.Files);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        return run.Output.Replace("\r\n", "\n");
    }

    [Fact]
    public void Trace_writes_the_call_and_the_return()
    {
        var output = Run("""
            using System;

            [Trace(Console.Out)]
            fn Add(a: int, b: int) => int = a + b;

            [Trace(Console.Out)]
            fn Hello() { Print("hello"); }

            fn Main() {
                Print(Add(1, 2));
                Hello();
            }
            """);
        Assert.Equal("→ Add(a = 1, b = 2)\n← Add\n3\n→ Hello()\nhello\n← Hello\n", output);
    }

    [Fact]
    public void Trace_sees_every_return()
    {
        var output = Run("""
            using System;

            [Trace(Console.Out)]
            fn Sign(n: int) => string {
                if n < 0 { return "negative"; }
                "not negative"
            }

            fn Main() {
                Print(Sign(-1));
                Print(Sign(1));
            }
            """);
        Assert.Equal("→ Sign(n = -1)\n← Sign\nnegative\n→ Sign(n = 1)\n← Sign\nnot negative\n", output);
    }

    [Fact]
    public void Timed_writes_how_long_a_call_took()
    {
        var output = Run("""
            using System;

            [Timed(Console.Out)]
            fn Work() => int = 42;

            fn Main() { Print(Work()); }
            """);
        Assert.Matches(@"^Work took \d+(\.|,)\d ms\n42\n$", output);
    }

    [Fact]
    public void Retry_runs_the_body_again_until_it_succeeds()
    {
        var output = Run("""
            using System;

            class State { public static mut calls = 0; }

            [Retry(3)]
            fn Flaky() => string {
                State.calls += 1;
                Print($"attempt {State.calls}");
                if State.calls < 3 { throw InvalidOperationException("not yet"); }
                "done"
            }

            fn Main() { Print(Flaky()); }
            """);
        Assert.Equal("attempt 1\nattempt 2\nattempt 3\ndone\n", output);
    }

    [Fact]
    public void Retry_lets_the_last_attempts_exception_escape()
    {
        var output = Run("""
            using System;

            [Retry(2, 1)]
            fn Fails() {
                Print("trying");
                throw InvalidOperationException("broken");
            }

            fn Main() {
                try { Fails(); } catch (e: InvalidOperationException) { Print($"gave up: {e.Message}"); }
            }
            """);
        Assert.Equal("trying\ntrying\ngave up: broken\n", output);
    }

    [Fact]
    public void Retry_checks_preconditions_once_before_any_attempt()
    {
        var output = Run("""
            using System;

            [Retry(3)]
            fn Half(n: int) => int requires n % 2 == 0 {
                Print("attempt");
                n / 2
            }

            fn Main() {
                try { Half(int.Parse("3")); } catch (e: ArgumentException) { Print("precondition"); }
                Print(Half(4));
            }
            """);
        Assert.Equal("precondition\nattempt\n2\n", output);
    }

    [Fact]
    public void Retry_works_on_async_functions()
    {
        var output = Run("""
            using System;
            using System.Threading.Tasks;

            class State { public static mut calls = 0; }

            [Retry(3, 1)]
            async fn Fetch() => Task<int> {
                State.calls += 1;
                await Task.Yield();
                if State.calls < 2 { throw InvalidOperationException("not yet"); }
                State.calls
            }

            async fn Main() { Print(await Fetch()); }
            """);
        Assert.Equal("2\n", output);
    }

    [Fact]
    public void Retry_works_on_async_functions_without_a_value()
    {
        var output = Run("""
            using System;
            using System.Threading.Tasks;

            class State { public static mut calls = 0; }

            [Retry(3)]
            async fn Ping() => Task {
                State.calls += 1;
                await Task.Yield();
                if State.calls < 2 { throw InvalidOperationException("not yet"); }
                Print("pong");
            }

            async fn Main() { await Ping(); }
            """);
        Assert.Equal("pong\n", output);
    }

    [Fact]
    public void Decorators_nest_with_the_first_written_outermost()
    {
        var output = Run("""
            using System;

            class State { public static mut calls = 0; }

            [Trace(Console.Out)]
            [Retry(2)]
            fn Flaky() => int {
                State.calls += 1;
                if State.calls < 2 { throw InvalidOperationException("not yet"); }
                State.calls
            }

            fn Main() { Print(Flaky()); }
            """);
        // Traced once: the retries are inside the trace.
        Assert.Equal("→ Flaky()\n← Flaky\n2\n", output);
    }

    [Fact]
    public void Methods_can_be_decorated()
    {
        var output = Run("""
            using System;

            class Counter(start: int) {
                mut n = start;

                [Trace(Console.Out)]
                fn Add(by: int) => int {
                    n += by;
                    n
                }
            }

            record Point(x: int, y: int);

            extend Point {
                [Trace(Console.Out)]
                fn Sum() => int = this.x + this.y;
            }

            extend int {
                [Trace(Console.Out)]
                fn Twice() => int = this * 2;
            }

            fn Main() {
                Print(Counter(1).Add(2));
                Print(Point(3, 4).Sum());
                Print(5.Twice());
            }
            """);
        Assert.Equal("→ Add(by = 2)\n← Add\n3\n→ Sum()\n← Sum\n7\n→ Twice()\n← Twice\n10\n", output);
    }

    [Fact]
    public void Retry_on_an_iterator_is_an_error()
    {
        var result = Compile("""
            using System.Collections.Generic;

            [Retry(3)]
            fn Numbers() => IEnumerable<int> { yield return 1; }

            fn Main() { }
            """);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal("PIP0503", error.Code);
        Assert.Contains("iterator", error.Message);
    }

    [Fact]
    public void Trace_on_a_record_says_to_trace_its_methods()
    {
        var text = """
            [Trace]
            record Point(x: int);

            fn Main() { }
            """;
        var error = Assert.Single(Compile(text).Diagnostics);
        Assert.Equal("PIP0503", error.Code);
        Assert.Contains("extend blocks", error.Message);
    }

    [Fact]
    public void The_decorator_doesnt_reach_the_csharp()
    {
        var result = Compile("""
            using System;

            [Trace]
            [Obsolete("old")]
            fn Old() { }

            fn Main() { }
            """);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var csharp = string.Concat(result.Files.Select(f => f.Text));
        Assert.DoesNotContain("[Trace", csharp);
        Assert.Contains("Obsolete", csharp);
        Assert.Contains("Console.Error.WriteLine", csharp);
    }

    [Fact]
    public void Trace_on_a_class_traces_its_methods()
    {
        var output = Run("""
            using System;

            [Trace(Console.Out)]
            class Calculator {
                prop Name: string { get; } = "calc";
                fn Add(a: int, b: int) => int = a + b;
                static fn Zero() => int = 0;
            }

            fn Main() {
                let c = Calculator();
                Print(c.Add(1, Calculator.Zero()));
                Print(c.Name);
            }
            """);
        Assert.Equal("→ Zero()\n← Zero\n→ Add(a = 1, b = 0)\n← Add\n1\ncalc\n", output);
    }

    /// <summary>A program whose Main watches a [Notify] class's PropertyChanged, printing each property named.</summary>
    private const string Watch = """
        p.PropertyChanged += (sender, e) => Print($"changed {e.PropertyName ?? ""}");
        """;

    [Fact]
    public void Notify_raises_PropertyChanged_when_a_property_changes()
    {
        var output = Run($$"""
            using System;

            [Notify]
            class Person(first: string) {
                prop First: string { get; set; } = first;
                prop Age: int { get; private set; } = 0;
                public mut nickname: string = "";
                prop Untouched: string { get; init; } = "fixed";

                fn Birthday() { Age += 1; }
            }

            fn Main() {
                let p = Person("Ada");
                {{Watch}}
                p.First = "Augusta";
                p.First = "Augusta";      // the same value: nothing raised
                p.Birthday();
                p.nickname = "Countess";
                Print($"{p.First} {p.Age} {p.nickname} {p.Untouched}");
            }
            """);
        Assert.Equal("changed First\nchanged Age\nchanged nickname\nAugusta 1 Countess fixed\n", output);
    }

    [Fact]
    public void Notify_raises_the_computed_properties_that_use_a_property()
    {
        var output = Run($$"""
            using System;

            [Notify]
            class Person(first: string, last: string) {
                prop First: string { get; set; } = first;
                prop Last: string { get; set; } = last;
                prop FullName => string = $"{First} {this.Last}";
                prop Greeting => string = $"Hello, {FullName}";
                prop Initial => char = First[0];
            }

            fn Main() {
                let p = Person("Ada", "Byron");
                {{Watch}}
                p.Last = "King";
                Print(p.Greeting);
            }
            """);
        Assert.Equal("changed Last\nchanged FullName\nchanged Greeting\nHello, Ada King\n", output);
    }

    [Fact]
    public void Notify_uses_what_the_class_already_has()
    {
        var result = Compile("""
            using System.ComponentModel;

            [Notify]
            class Person: INotifyPropertyChanged {
                event PropertyChanged: PropertyChangedEventHandler;
                prop Name: string { get; set; } = "";

                protected fn OnPropertyChanged(name: string) {
                    PropertyChanged(this, PropertyChangedEventArgs(name));
                }
            }

            fn Main() { }
            """);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Notify_needs_a_property_to_have_a_type()
    {
        var text = """
            [Notify]
            class Counter {
                prop Count { get; set; } = 0;
            }

            fn Main() { }
            """;
        var error = Assert.Single(Compile(text).Diagnostics);
        Assert.Equal("PIP0503", error.Code);
        Assert.Contains("to have a type", error.Message);
        Assert.Equal(text.IndexOf("Count {", StringComparison.Ordinal), error.Span.Start);
    }

    [Fact]
    public void Notify_on_a_record_is_an_error()
    {
        var error = Assert.Single(Compile("[Notify]\nrecord Point(x: int);\nfn Main() { }").Diagnostics);
        Assert.Equal("PIP0503", error.Code);
        Assert.Contains("a record can't change", error.Message);
    }

    [Fact]
    public void Notify_on_a_function_is_an_error()
    {
        var error = Assert.Single(Compile("[Notify]\nfn F() { }\nfn Main() { }").Diagnostics);
        Assert.Equal("PIP0507", error.Code);
    }

    [Fact]
    public void A_command_runs_its_method_when_CanX_allows()
    {
        var output = Run("""
            using System;
            using System.Threading.Tasks;

            [Notify]
            class Editor {
                prop Name: string { get; set; } = "";
                prop IsValid => bool = Name.Length > 0;

                [Command]
                fn Save() { Print($"saved {Name}"); }
                fn CanSave() => bool = IsValid;

                [Command]
                async fn LoadAsync() => Task { Print("loading"); await Task.Yield(); }
            }

            fn Main() {
                let e = Editor();
                e.SaveCommand.CanExecuteChanged += (sender, args) => Print("can save changed");
                Print(e.SaveCommand.CanExecute(None));
                e.Name = "Widget";          // CanSave reads IsValid, which reads Name
                Print(e.SaveCommand.CanExecute(None));
                e.SaveCommand.Execute(None);
                Print(e.LoadCommand.CanExecute(None));
                e.LoadCommand.Execute(None);
            }
            """);
        Assert.Equal("False\ncan save changed\nTrue\nsaved Widget\nTrue\nloading\n", output);
    }

    [Fact]
    public void A_commands_CanX_can_be_a_property()
    {
        var output = Run("""
            [Notify]
            class Counter {
                prop Count: int { get; set; } = 0;
                prop CanReset => bool = Count > 0;

                [Command]
                fn Reset() { Count = 0; }
            }

            fn Main() {
                let c = Counter();
                c.ResetCommand.CanExecuteChanged += (sender, args) => Print("changed");
                c.Count = 3;
                c.ResetCommand.Execute(None);
                Print($"{c.Count} {c.ResetCommand.CanExecute(None)}");
            }
            """);
        Assert.Equal("changed\nchanged\n0 False\n", output);
    }

    [Fact]
    public void NotifyIgnore_and_AlsoNotify_adjust_what_is_raised()
    {
        var output = Run($$"""
            using System;

            [Notify]
            class Order {
                [AlsoNotify("Summary")]
                prop Quantity: int { get; set; } = 1;
                [NotifyIgnore]
                prop Note: string { get; set; } = "";

                fn Summary() => string = $"{Quantity} ordered";
            }

            fn Main() {
                let p = Order();
                {{Watch}}
                p.Quantity = 2;
                p.Note = "fragile";
                Print(p.Summary());
            }
            """);
        Assert.Equal("changed Quantity\nchanged Summary\n2 ordered\n", output);
    }

    [Fact]
    public void A_marker_without_its_type_decorator_is_an_error()
    {
        var result = Compile("""
            class Plain {
                [Command]
                fn Save() { }
                [NotifyIgnore]
                prop Name: string { get; set; } = "";
            }

            [Command]
            fn Top() { }

            fn Main() { }
            """);
        Assert.Equal(["PIP0508", "PIP0508", "PIP0508"], result.Diagnostics.Select(d => d.Code));
        Assert.Contains("only means something on a member of a class with [Notify]", result.Diagnostics[0].Message);
    }

    [Fact]
    public void A_command_needs_a_public_method_without_parameters()
    {
        var error = Assert.Single(Compile("""
            [Notify]
            class Editor {
                [Command]
                fn Open(path: string) { }
            }

            fn Main() { }
            """).Diagnostics);
        Assert.Equal("PIP0503", error.Code);
        Assert.Contains("taking no parameters", error.Message);
    }

    [Fact]
    public void A_failing_command_is_reported_to_OnCommandFailed()
    {
        var output = Run("""
            using System;
            using System.Threading.Tasks;

            [Notify]
            class Editor {
                [Command]
                fn Save() { throw InvalidOperationException("disk full"); }

                [Command]
                async fn LoadAsync() => Task { await Task.Yield(); throw InvalidOperationException("not found"); }

                fn OnCommandFailed(command: string, error: Exception) { Print($"{command} failed: {error.Message}"); }
            }

            async fn Main() {
                let e = Editor();
                e.SaveCommand.Execute(None);
                e.LoadCommand.Execute(None);
                await Task.Delay(200);
            }
            """);
        Assert.Equal("Save failed: disk full\nLoad failed: not found\n", output);
    }

    /// <summary>Without OnCommandFailed, an async command's exception is posted to where it started, as async void's is.</summary>
    [Fact]
    public void An_async_commands_unhandled_failure_is_thrown_where_it_started()
    {
        var output = Run("""
            using System;
            using System.Collections.Generic;
            using System.Threading;

            /// A synchronization context keeping what's posted to it, as a UI thread's queue would.
            class Queue: SynchronizationContext {
                public let posted: List<SendOrPostCallback> = [];
                override fn Post(d: SendOrPostCallback, state: object) { posted.Add(d); }
            }

            [Notify]
            class Editor {
                [Command]
                async fn LoadAsync() => System.Threading.Tasks.Task {
                    await System.Threading.Tasks.Task.CompletedTask;
                    throw InvalidOperationException("not found");
                }
            }

            fn Main() {
                let queue = Queue();
                SynchronizationContext.SetSynchronizationContext(queue);
                defer SynchronizationContext.SetSynchronizationContext(None);
                Editor().LoadCommand.Execute(None);
                Print(queue.posted.Count);
                try {
                    queue.posted[0](None);
                } catch (e: InvalidOperationException) {
                    Print($"thrown: {e.Message}");
                }
            }
            """);
        Assert.Equal("1\nthrown: not found\n", output);
    }

    [Fact]
    public void OnCommandFailed_cant_be_private()
    {
        var error = Assert.Single(Compile("""
            using System;

            [Notify]
            class Editor {
                [Command]
                fn Save() { }
                private fn OnCommandFailed(command: string, error: Exception) { }
            }

            fn Main() { }
            """).Diagnostics);
        Assert.Contains("not private", error.Message);
    }
}
