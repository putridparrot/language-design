namespace Piper.Tests;

/// <summary>
/// Structured concurrency: Piper.Runtime's <c>TaskScope</c>, used as Piper code uses it, with trailing closures
/// (<c>await TaskScope.Run { scope => … scope.Start { stop => … } … }</c>) and no <c>using Piper.Runtime;</c>.
/// </summary>
public class TaskScopeTests
{
    private static TestRunResult Run(string program, int? seed = 1, params string[] allowed)
    {
        var result = Compilation.Compile(program, "scope.piper", new CompilationOptions(LineDirectives: true, IncludeTests: true));
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.All(result.Diagnostics, d => Assert.Contains(d.Code, allowed));
        return Runner.RunTests(result.Files, references: result.References, seed: seed);
    }

    private static void AssertAllPass(TestRunResult run, int count)
    {
        Assert.Empty(run.Errors);
        Assert.True(run.Results.All(r => r.Passed), string.Join("\n", run.Results.Select(r => $"{r.Name}: {r.Failure}")));
        Assert.Equal(count, run.Results.Length);
    }

    private const string Usings = """
        using System;
        using System.Collections.Concurrent;
        using System.Threading;
        using System.Threading.Tasks;

        /// Loops until `stop` is cancelled, as a worker does, counting its rounds.
        async fn Worker(rounds: ConcurrentBag<int>, stop: CancellationToken) {
            while !stop.IsCancellationRequested {
                rounds.Add(1);
                try {
                    await Task.Delay(10, stop);
                } catch (_: OperationCanceledException) {
                }
            }
        }

        """;

    [Fact]
    public void A_scope_waits_for_its_tasks_and_gives_their_results()
    {
        AssertAllPass(Run(Usings + """
            async test "the scope waits for every task it started" {
                let done = ConcurrentBag<int>();
                await TaskScope.Run { scope =>
                    for i in 1..3 {
                        scope.Start { stop =>
                            await Task.Delay(20 * i);
                            done.Add(i);
                        }
                    }
                }
                AssertEqual(3, done.Count);
            }

            async test "a task's result, and the body's value" {
                let total = await TaskScope.Run { scope =>
                    let a = scope.Start { stop => await Task.FromResult(20) };
                    let b = scope.Start { stop => await Task.FromResult(22) };
                    await a + await b
                };
                AssertEqual(42, total);
            }

            async test "workers run until the scope is cancelled" {
                let rounds = ConcurrentBag<int>();
                await TaskScope.Run { scope =>
                    scope.Start { stop => Worker(rounds, stop) }
                    await Task.Delay(50);
                    scope.Cancel();
                }
                Assert(rounds.Count > 0);
            }

            async test "a scope given an outer token stops when it's cancelled" {
                let rounds = ConcurrentBag<int>();
                using let outer = CancellationTokenSource();
                outer.CancelAfter(50);
                await TaskScope.Run(outer.Token) { scope =>
                    for _ in 1..2 {
                        scope.Start { stop => Worker(rounds, stop) }
                    }
                }
                Assert(rounds.Count > 0);
            }
            """), 4);
    }

    [Fact]
    public void A_failure_stops_the_scope_and_is_the_one_thrown()
    {
        AssertAllPass(Run(Usings + """
            async test "a task that fails cancels the others, and its exception is thrown" {
                let rounds = ConcurrentBag<int>();
                mut caught = "";
                try {
                    await TaskScope.Run { scope =>
                        scope.Start { stop => Worker(rounds, stop) }
                        scope.Start { stop =>
                            await Task.Delay(30);
                            throw InvalidOperationException("broken");
                        }
                        // The body waits on the scope's token, so it stops too; the task's failure is what's reported.
                        await Task.Delay(TimeSpan.FromMinutes(10), scope.Token);
                    }
                } catch (e: InvalidOperationException) {
                    caught = e.Message;
                }
                AssertEqual("broken", caught);
            }

            async test "a body that throws doesn't wait forever for workers: they're cancelled" {
                let rounds = ConcurrentBag<int>();
                mut caught = "";
                try {
                    await TaskScope.Run { scope =>
                        scope.Start { stop => Worker(rounds, stop) }
                        await Task.Delay(30);
                        throw ArgumentException("the body failed");
                    }
                } catch (e: ArgumentException) {
                    caught = e.Message;
                }
                AssertEqual("the body failed", caught);
            }

            async test "a scope can't start tasks once it has finished" {
                mut kept: TaskScope? = None;
                await TaskScope.Run { scope => kept = Some(scope) }
                mut refused = false;
                try {
                    match kept { Some(scope) => scope.Start { stop => Task.CompletedTask }, None => Task.CompletedTask };
                } catch (_: InvalidOperationException) {
                    refused = true;
                }
                Assert(refused);
            }
            """, allowed: "PIP0410"), 3);
    }

    [Fact]
    public void Scopes_run_in_a_simulation()
    {
        AssertAllPass(Run(Usings + """
            async test "a scope's tasks are simulated, and a failure stops the rest" simulate runs 20 {
                let rounds = ConcurrentBag<int>();
                let start = DateTime.UtcNow;
                mut caught = "";
                try {
                    await TaskScope.Run { scope =>
                        for _ in 1..3 {
                            scope.Start { stop => Worker(rounds, stop) }
                        }
                        scope.Start { stop =>
                            await Task.Delay(TimeSpan.FromSeconds(1));
                            throw InvalidOperationException("a second in");
                        }
                    }
                } catch (e: InvalidOperationException) {
                    caught = e.Message;
                }
                AssertEqual("a second in", caught);
                Assert(DateTime.UtcNow - start >= TimeSpan.FromSeconds(1));
            }
            """), 1);
    }

    private const string Checked = """
        using System;
        using System.Collections.Generic;
        using System.Collections.Concurrent;
        using System.Threading.Tasks;

        class Holder { public mut scope: TaskScope? = None; }

        fn StartOne(scope: TaskScope) {
            scope.Start { stop => Task.CompletedTask }
        }

        async fn Races() {
            let names: List<string> = [];
            let safe = ConcurrentBag<string>();
            mut total = 0;
            await TaskScope.Run { scope =>
                scope.Start { stop => { names.Add("a"); Task.CompletedTask } }
                scope.Start { stop => { names.Add("b"); Task.CompletedTask } }
                scope.Start { stop => { safe.Add("c"); Task.CompletedTask } }
            }
            await TaskScope.Run { scope =>
                for i in 1..3 {
                    scope.Start { stop => { total += i; Task.CompletedTask } }
                }
            }
            await TaskScope.Run { scope =>
                scope.Start { stop => { total += 1; Task.CompletedTask } }
            }
        }

        async fn Escapes(holder: Holder) {
            mut kept: TaskScope? = None;
            mut later: () => unit = () => {};
            await TaskScope.Run { scope =>
                kept = Some(scope);
                holder.scope = Some(scope);
                later = () => scope.Cancel();
                let alias = scope;
                let token = scope.Token;
                StartOne(scope);
                scope.Start { stop => { scope.Cancel(); Task.CompletedTask } }
            }
            let leaked = await TaskScope.Run { scope => { await Task.Yield(); scope } };
        }

        fn Main() {}
        """;

    /// <summary>
    /// The tasks a scope starts run at once, so they're checked for data races as Task.WhenAll's Task.Run lambdas are
    /// (PIP0387): with each other, or, started in a loop, with themselves. A scope kept past its Run (stored outside
    /// the body, held by a lambda kept for later, or returned) is a warning (PIP0410); used in the body or its tasks,
    /// read for its token, or passed to a function, it isn't.
    /// </summary>
    [Fact]
    public void A_scopes_tasks_are_checked_for_races_and_a_scope_mustnt_outlive_its_body()
    {
        var result = Compilation.Compile(Checked, "checked.piper", new CompilationOptions());
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Equal(
            [
                ("PIP0387", "names.Add(\"a\")"), ("PIP0387", "names.Add(\"b\")"), ("PIP0387", "total += i"),
                ("PIP0410", "Some(scope)"), ("PIP0410", "Some(scope)"), ("PIP0410", "() => scope.Cancel()"),
                ("PIP0410", "{ await Task.Yield(); scope }"),
            ],
            result.Diagnostics.OrderBy(d => d.Span.Start).Select(d => (d.Code, Checked[d.Span.Start..d.Span.End])));
    }
}
