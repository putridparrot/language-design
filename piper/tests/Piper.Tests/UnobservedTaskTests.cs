namespace Piper.Tests;

/// <summary>
/// PIP0409: a call giving a Task whose value is thrown away, so nothing waits for it and its exception is lost.
/// Anywhere, not only in async functions (C#'s CS4014). `let _ =` drops one on purpose, and TaskScope.Start's task is
/// its scope's to observe.
/// </summary>
public class UnobservedTaskTests
{
    private const string Program = """
        using System;
        using System.Threading.Tasks;

        async fn Save(n: int) => Task<int> { await Task.Delay(1); n }
        async fn Ping() { await Task.Delay(1); }
        fn Plain() => Task = Task.CompletedTask;

        fn Sync(ready: bool) {
            Save(1);
            if ready { Ping() }
            match ready { true => Ping(), false => Plain() };
            let handler: Action = () => Ping();
            Task.Run(() => Ping());
            let _ = Save(2);
            let kept = Save(3);
            let returned: () => Task = () => Ping();
        }

        fn Last() { Plain() }

        async fn Main() {
            await Save(4);
            Ping();
            let work: () => Task = async () => { await Task.Yield(); Ping() };
            await work();
            await TaskScope.Run { scope =>
                scope.Start { stop => Ping() }
            }
        }

        async test "tests are checked too" {
            Ping();
            await Ping();
        }
        """;

    [Fact]
    public void A_task_nothing_waits_for_is_a_warning_wherever_it_is()
    {
        var result = Compilation.Compile(Program, "main.piper", new CompilationOptions(IncludeTests: true));
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var warnings = result.Diagnostics.Where(d => d.Code == "PIP0409").Select(d => Program[d.Span.Start..d.Span.End]).ToList();
        Assert.Equal(
            ["Save(1)", "Ping()", "Ping()", "Plain()", "Ping()", "Task.Run(() => Ping())", "Plain()", "Ping()", "Ping()", "Ping()"],
            warnings);
        Assert.All(result.Diagnostics, d => Assert.Equal("PIP0409", d.Code));
        Assert.Contains("'await' it, start it in a TaskScope", result.Diagnostics[0].Message);
    }
}
