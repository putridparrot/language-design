using System.Reflection;
using Piper.Text;

namespace Piper.Binding;

public sealed partial class Binder
{
    /// <summary>
    /// What a <c>simulate</c> test reaches that its simulation can't run: anything blocking its one thread, reading the
    /// real clock in a way the compiler doesn't route (<see cref="Piper.Runtime.PiperRouting"/>), or running work on
    /// real threads. Followed from the test through the program's functions and methods (and the lambdas in them); each
    /// is a warning (PIP0512) at the call, naming the test and the way it gets there, with what to use instead.
    /// </summary>
    private void CheckSimulated(IEnumerable<(string Name, BoundFunction Function)> tests, IEnumerable<BoundFunction> functions)
    {
        var bodies = Bodies(functions);
        var reported = new HashSet<(SourceText?, int)>();
        foreach (var (name, test) in tests)
        {
            Reach(test, bodies, effectsAllowed: false, Unsimulated, (span, path, why) =>
            {
                if (reported.Add((_diagnostics.CurrentSource, span.Start)))
                    _diagnostics.ReportNotSimulated(span, name, path, why);
            });
        }
    }

    /// <summary>
    /// What a function marked <see cref="Piper.Runtime.PiperDeterministicAttribute"/> (a workflow's body) reaches that
    /// could differ when it's replayed: the clock, random numbers, new ids, delays and background work, threads, blocking,
    /// I/O. Followed through the program's functions and methods, but not into what's passed to a
    /// <see cref="Piper.Runtime.PiperEffectsAttribute"/> parameter (an activity, run once and recorded); each is an error
    /// (PIP0513) at the call, naming the function and the way it gets there, with what to use instead.
    /// </summary>
    private void CheckDeterministic(IEnumerable<BoundFunction> functions)
    {
        var all = functions.ToList();
        var bodies = Bodies(all);
        var reported = new HashSet<(SourceText?, int)>();
        foreach (var root in all.Where(f => f.Symbol.Attributes.Any(a => a.Type is ClrTypeSymbol { Type: var attribute } && attribute == typeof(Piper.Runtime.PiperDeterministicAttribute))))
        {
            var name = root.Symbol is MethodSymbol { ContainingType: var type } ? type.Name : root.Symbol.Name;
            Reach(root, bodies, effectsAllowed: true, Nondeterministic, (span, path, why) =>
            {
                if (reported.Add((_diagnostics.CurrentSource, span.Start)))
                    _diagnostics.ReportNotDeterministic(span, name, path, why);
            });
        }
    }

    private static Dictionary<object, BoundFunction> Bodies(IEnumerable<BoundFunction> functions)
    {
        var bodies = new Dictionary<object, BoundFunction>();
        foreach (var function in functions)
        {
            bodies.TryAdd(function.Symbol, function);
            if (function.Symbol.Declaration is { } declaration)
                bodies.TryAdd(declaration, function);
        }
        return bodies;
    }

    /// <summary>
    /// Follows <paramref name="root"/> through the functions it calls, reporting each node <paramref name="problem"/>
    /// finds fault with, in the file it's in, with the way there. With <paramref name="effectsAllowed"/>, what's passed to
    /// a <c>[PiperEffects]</c> parameter isn't followed.
    /// </summary>
    private void Reach(BoundFunction root, Dictionary<object, BoundFunction> bodies, bool effectsAllowed,
        Func<BoundNode, string?> problem, Action<TextSpan, string, string> report)
    {
        BoundFunction? Body(FunctionSymbol symbol) =>
            bodies.TryGetValue(symbol, out var body) || (symbol.Declaration is { } d && bodies.TryGetValue(d, out body)) ? body : null;

        var seen = new HashSet<BoundFunction>();
        var pending = new Queue<(BoundFunction Function, string Path)>([(root, "")]);
        while (pending.TryDequeue(out var item))
        {
            var (function, path) = item;
            if (!seen.Add(function))
                continue;
            var nodes = BoundTreeWalker.DescendantsAndSelf(function.Body).ToList();
            var allowed = effectsAllowed ? Effects(nodes) : [];
            foreach (var node in nodes)
            {
                if (allowed.Contains(node))
                    continue;
                var callee = node switch
                {
                    BoundCallExpression call => call.Function,
                    BoundMethodCallExpression call => call.Method,
                    _ => null,
                };
                if (callee is not null && Body(callee) is { } body)
                {
                    pending.Enqueue((body, path.Length == 0 ? callee.Name : $"{path} → {callee.Name}"));
                    continue;
                }
                if (problem(node) is not { } why || node.SourceSpan is not { } span)
                    continue;
                if (function.Symbol.DeclaringFile is not null)
                    InFile(FileOf(function.Symbol));
                report(span, path, why);
            }
        }
    }

    /// <summary>The nodes inside arguments given to <c>[PiperEffects]</c> parameters (activities): free to have effects.</summary>
    private static HashSet<BoundNode> Effects(List<BoundNode> nodes)
    {
        var allowed = new HashSet<BoundNode>();
        foreach (var invocation in nodes.OfType<BoundClrInvocationExpression>())
        {
            var parameters = invocation.Member.GetParameters();
            for (var i = 0; i < invocation.Arguments.Length && i < parameters.Length; i++)
            {
                if (parameters[i].IsDefined(typeof(Piper.Runtime.PiperEffectsAttribute), inherit: false))
                    allowed.UnionWith(BoundTreeWalker.DescendantsAndSelf(invocation.Arguments[i]));
            }
        }
        return allowed;
    }

    /// <summary>The .NET member a node uses, with its type's name (a generic type's definition's) and the member's ("new" for a constructor).</summary>
    private static (MemberInfo Member, string Type, string Name)? Member(BoundNode node)
    {
        var member = node switch
        {
            BoundClrInvocationExpression invocation => (MemberInfo)invocation.Member,
            BoundClrMemberExpression access => access.Member,
            _ => null,
        };
        if (member?.DeclaringType is not { } type)
            return null;
        // Task<int>.Result is Task`1's, as its definition names it.
        var typeName = (type.IsGenericType ? type.GetGenericTypeDefinition().FullName : type.FullName) ?? type.Name;
        return (member, typeName, member is ConstructorInfo ? "new" : member.Name);
    }

    /// <summary>Why the node can't run in a simulation, and what to use instead; null if it can.</summary>
    private static string? Unsimulated(BoundNode node) => Member(node) switch
    {
        null => null,
        (_, "System.Threading.Thread", "Sleep") =>
            "Thread.Sleep blocks the simulation's only thread; `await Task.Delay(…)` waits in simulated time",
        (_, "System.Diagnostics.Stopwatch", "StartNew" or "new" or "Start" or "Restart") =>
            "a Stopwatch measures real time; Stopwatch.GetTimestamp() and Stopwatch.GetElapsedTime(start) are simulated",
        (_, "System.Environment", "TickCount" or "TickCount64") =>
            "Environment.TickCount is the real clock; Stopwatch.GetTimestamp() is simulated",
        (_, "System.TimeProvider", "System") =>
            "TimeProvider.System is the real clock; PiperRuntime.Current.Time is the simulation's in one, and the real one otherwise",
        (_, "System.Threading.Thread", "new") or (_, "System.Threading.ThreadPool", _) or (_, "System.Threading.Tasks.TaskFactory", "StartNew") =>
            "this runs work on a real thread, outside the simulation; Task.Run(…) is simulated",
        (_, "System.Threading.CancellationTokenSource", "CancelAsync") =>
            "CancelAsync runs what waits on the token on a real thread, outside the simulation; Cancel() runs it in place",
        (_, "System.Threading.Tasks.Parallel", _) =>
            "Parallel runs work on real threads, outside the simulation; Task.WhenAll of Task.Run(…) is simulated",
        (_, "System.Threading.Timer" or "System.Timers.Timer", "new") =>
            "a real timer, outside the simulation; `await Task.Delay(…)` in a loop, or a timer from PiperRuntime.Current.Time, is simulated",
        (_, "System.Threading.Tasks.Task", "Wait" or "WaitAll" or "WaitAny") or (_, "System.Threading.Tasks.Task`1", "Result") or
        (_, "System.Runtime.CompilerServices.TaskAwaiter" or "System.Runtime.CompilerServices.TaskAwaiter`1", "GetResult") =>
            "this waits by blocking the simulation's only thread, which then can't run what it waits for; `await` it instead",
        _ => null,
    };

    /// <summary>Why the node could do differently when it's replayed, and what to use instead; null if it can't.</summary>
    private static string? Nondeterministic(BoundNode node) => Member(node) switch
    {
        null => null,
        (_, "System.DateTime" or "System.DateTimeOffset", "Now" or "UtcNow" or "Today") or
        (_, "System.Diagnostics.Stopwatch", _) or (_, "System.Environment", "TickCount" or "TickCount64") or (_, "System.TimeProvider", _) =>
            "it reads the clock, which is later when it's replayed; `await flow.Now()` records the time",
        (_, "System.Threading.Tasks.Task", "Delay") or (_, "System.Threading.Thread", "Sleep") =>
            "this wait isn't durable: a restart forgets it; `timer …;` waits durably",
        (_, "System.Random", _) =>
            "random numbers differ when it's replayed; `await flow.Record(\"…\", () => Random.Shared.Next(…))` records one",
        (_, "System.Guid", "NewGuid" or "CreateVersion7") =>
            "a new id differs when it's replayed; `await flow.NewGuid()` records one",
        (_, "System.Threading.Tasks.Task", "Run" or "WaitAll" or "WaitAny" or "Wait") or (_, "System.Threading.Thread", _) or
        (_, "System.Threading.ThreadPool", _) or (_, "System.Threading.Tasks.Parallel", _) or (_, "System.Threading.Tasks.TaskFactory", _) or
        (_, "System.Threading.Timer" or "System.Timers.Timer", _) =>
            "it runs or waits on work outside the workflow, which a replay can't repeat; put it in an activity (`activity x = …;`)",
        (_, "System.Threading.Tasks.Task`1", "Result") or (_, "System.Runtime.CompilerServices.TaskAwaiter" or "System.Runtime.CompilerServices.TaskAwaiter`1", "GetResult") =>
            "it blocks, rather than awaiting; `await` it",
        (_, "System.Environment", "GetEnvironmentVariable" or "GetEnvironmentVariables") =>
            "the environment can change between runs; read it in an activity, or pass it in",
        (_, var type, _) when IsInputOutput(type) =>
            "it does I/O, whose result can differ when it's replayed and whose effects would happen again; put it in an activity (`activity x = …;`)",
        _ => null,
    };

    /// <summary>A type whose members read or write outside the program: files, the network, a database, a process.</summary>
    private static bool IsInputOutput(string type) =>
        type is "System.IO.File" or "System.IO.Directory" or "System.IO.FileInfo" or "System.IO.DirectoryInfo" or "System.IO.FileStream"
            or "System.IO.StreamReader" or "System.IO.StreamWriter" or "System.Console" or "System.Diagnostics.Process"
        || type.StartsWith("System.Net.", StringComparison.Ordinal)
        || type.StartsWith("Microsoft.EntityFrameworkCore.", StringComparison.Ordinal)
        || type.StartsWith("Microsoft.Data.", StringComparison.Ordinal);
}
