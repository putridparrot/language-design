using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Piper.Plugins;
using Piper.Text;

namespace Piper.Tests;

/// <summary>
/// The state machines plugin (plugins/Piper.Plugins.StateMachines): what `statemachine` declarations do and check. The
/// plugin is loaded from its build output, as a program's piper.json would load it.
/// </summary>
public class StateMachinePluginTests
{
    private static readonly Lazy<ImmutableArray<IPiperPlugin>> StateMachines = new(() =>
    {
        var (plugins, errors) = PluginLoader.Load(PluginsDirectory(), ["Piper.Plugins.StateMachines/bin/plugin/Piper.Plugins.StateMachines.dll"]);
        Assert.Empty(errors);
        return plugins;
    });

    private static string PluginsDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "plugins"));

    private static CompilationResult Compile(string text) =>
        Compilation.Compile([new SourceText(text, "main.piper")], new CompilationOptions { Plugins = StateMachines.Value });

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

    private const string Door = """
        statemachine Door(name: string) {
            public mut opened = 0;

            state Closed {
                enter { Print("enter Closed"); }
                exit { Print("exit Closed"); }
                on Open() => Opened { opened += 1; Print("action Open"); }
                on Lock(code: int) => Locked if code > 0;
                on Knock() => Closed { Print("knock"); }
            }
            state Opened {
                enter { Print("enter Opened"); }
                on Close() => Closed;
            }
            state Locked {
                on Unlock(code: int) => Closed if code == 42;
                on Unlock(code: int) => Locked { Print($"wrong code {code}"); }
            }
        }

        """;

    [Fact]
    public void A_transition_runs_exit_then_its_action_then_enter_but_only_when_the_state_changes()
    {
        var output = Run(Door + """
            fn Main() {
                let door = Door("front");
                Print(door.State);
                door.Knock();
                door.Open();
                Print(door.State);
                door.Close();
                Print($"{door.State}, opened {door.opened}");
            }
            """);
        Assert.Equal("Closed\nknock\nexit Closed\naction Open\nenter Opened\nOpened\nenter Closed\nClosed, opened 1\n", output);
    }

    [Fact]
    public void Guards_choose_between_transitions_and_an_event_with_none_throws()
    {
        var output = Run(Door + """
            fn Main() {
                let door = Door("front");
                Print($"{door.CanLock(0)} {door.CanLock(1)} {string.Join(",", door.Events)}");
                door.Lock(1);
                door.Unlock(7);
                Print($"{door.State} {door.CanUnlock(7)} {door.CanOpen()}");
                try {
                    door.Open();
                } catch (e: System.InvalidOperationException) {
                    Print(e.Message);
                }
                door.Unlock(42);
                Print(door.State);
            }
            """);
        Assert.Equal("False True Open,Lock,Knock\nexit Closed\nwrong code 7\nLocked True False\nDoor can't Open while Locked\nenter Closed\nClosed\n", output);
    }

    [Fact]
    public void Events_are_data_that_can_be_replayed_and_a_state_without_transitions_is_final()
    {
        var output = Run("""
            statemachine Job {
                state Queued { on Start(worker: string) => Running; }
                state Running { on Finish() => Done; on Fail(reason: string) => Queued; }
                state Done;
            }
            fn Main() {
                let job = Job();
                for event in [JobEvent.Start("a"), JobEvent.Fail("crash"), JobEvent.Start("b"), JobEvent.Finish] {
                    job.Fire(event);
                }
                Print($"{job.State} {job.IsFinal} {job.Events.Count}");
            }
            """);
        Assert.Equal("Done True 0\n", output);
    }

    [Fact]
    public void An_error_in_a_guard_is_reported_at_the_users_code()
    {
        var text = """
            statemachine S {
                state A { on Go(n: int) => B if n + "x"; }
                state B;
            }
            fn Main() {}
            """;
        var errors = Compile(text).Diagnostics.Where(d => d.Severity == Piper.Diagnostics.DiagnosticSeverity.Error).ToList();
        var error = Assert.Single(errors);
        Assert.InRange(error.Span.Start, text.IndexOf("n + \"x\"", StringComparison.Ordinal), text.IndexOf("; }", StringComparison.Ordinal));
    }

    [Fact]
    public void A_state_nothing_reaches_is_warned_about()
    {
        var text = """
            statemachine S {
                state A { on Go() => B; }
                state B;
                state Orphan { on Go() => A; }
            }
            fn Main() {}
            """;
        var result = Compile(text);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var warning = Assert.Single(result.Diagnostics);
        Assert.Equal("PIP0505", warning.Code);
        Assert.Contains("'Orphan'", warning.Message);
        Assert.Equal(text.IndexOf("Orphan", StringComparison.Ordinal), warning.Span.Start);
    }

    [Theory]
    [InlineData("state A { on Go() => Nowhere; }", "Nowhere", "isn't one of S's states")]
    [InlineData("state A { on Go() => A; } state A {}", "A {}", "already has a state called 'A'")]
    [InlineData("state A { on Go(n: int) => A; on Stop() => B; } state B { on Go(n: string) => A; }", "Go(n: string)", "Every 'Go' needs the same parameters, as the first does: (n: int)")]
    [InlineData("state A { on Go() => B; on Go() => A; } state B;", "Go() => A", "is never taken")]
    [InlineData("state A { on Fire() => A; }", "Fire", "the name of a member every state machine has")]
    [InlineData("state A { on Go() => A; on CanGo() => A; }", "CanGo", "says whether 'Go' can happen")]
    [InlineData("state A { on Go(n) => A; }", "n)", "Give 'n' a type")]
    [InlineData("state A { enter { } enter { } on Go() => A; }", "enter { } on", "already has an 'enter' block")]
    [InlineData("", "S", "has no states")]
    public void Mistakes_in_its_shape_are_reported(string body, string at, string message)
    {
        var text = $"statemachine S {{ {body} }}\nfn Main() {{}}";
        AssertPluginError(text, at, message);
    }
}
