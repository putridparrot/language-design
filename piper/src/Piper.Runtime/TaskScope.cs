using System.Runtime.ExceptionServices;

namespace Piper.Runtime;

/// <summary>
/// On a method giving a Task: something else observes the task (waits for it, and reports its failure), so the caller
/// may drop it without Piper's warning that nothing waits for it (PIP0409). <see cref="TaskScope.Start(Func{CancellationToken, Task})"/>
/// is one: its scope waits for every task it starts.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ObservedTaskAttribute : Attribute;

/// <summary>
/// Structured concurrency: tasks started in a scope (<see cref="Start(Func{CancellationToken, Task})"/>) can't outlive
/// it, and their failures aren't lost. <see cref="Run(Func{TaskScope, Task})"/> finishes only when
/// its body and every task the body started have finished:
/// <code>
/// await TaskScope.Run { scope =>
///     for i in 1..3 { scope.Start { stop => Work(i, stop) } }
///     await Serve();
///     scope.Cancel();          // the workers run until serving ends
/// }
/// </code>
/// <list type="bullet">
/// <item>At the end of the body, the scope waits for its tasks: nothing is cancelled unless <see cref="Cancel"/> says so.</item>
/// <item>If the body throws, the tasks are cancelled and waited for, then its exception is rethrown.</item>
/// <item>If a task fails (with anything but cancellation), the scope is cancelled, so the others stop, and once all
/// have finished, <c>Run</c> throws that failure: the first, if several failed, as <c>await Task.WhenAll</c> does.</item>
/// <item>Tasks run through <see cref="PiperRouting"/>, as <c>Task.Run</c> does, so a simulation runs them too.</item>
/// </list>
/// </summary>
public sealed class TaskScope
{
    private readonly CancellationTokenSource _cancellation;
    private readonly List<Task> _tasks = [];
    private readonly Lock _gate = new();
    private Exception? _failure;
    private bool _finished;

    private TaskScope(CancellationToken outer) =>
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(outer);

    /// <summary>Cancelled when <see cref="Cancel"/> is called, a task fails, the body throws, or the outer token is cancelled.</summary>
    public CancellationToken Token => _cancellation.Token;

    /// <summary>Whether the scope has been cancelled.</summary>
    public bool IsCancellationRequested => _cancellation.IsCancellationRequested;

    /// <summary>Asks every task in the scope to stop, through the token each was given.</summary>
    public void Cancel() => _cancellation.Cancel();

    /// <summary>Runs <paramref name="body"/> with a new scope; done when it and every task it started are.</summary>
    public static Task Run(Func<TaskScope, Task> body) => Run(CancellationToken.None, body);

    /// <summary>The same, for a body that doesn't await: it starts tasks, and the scope waits for them.</summary>
    public static Task Run(Action<TaskScope> body) => Run(CancellationToken.None, body);

    /// <summary>The same, giving the body's value once every task has finished.</summary>
    public static Task<T> Run<T>(Func<TaskScope, Task<T>> body) => Run(CancellationToken.None, body);

    /// <summary>
    /// A scope cancelled when <paramref name="cancellation"/> is, as well: <c>TaskScope.Run(stop) { scope => … }</c>.
    /// The token comes first so the body can be a trailing closure.
    /// </summary>
    public static async Task Run(CancellationToken cancellation, Func<TaskScope, Task> body) =>
        await Run<bool>(cancellation, async scope => { await body(scope); return true; });

    /// <inheritdoc cref="Run(CancellationToken, Func{TaskScope, Task})"/>
    public static async Task Run(CancellationToken cancellation, Action<TaskScope> body) =>
        await Run<bool>(cancellation, scope => { body(scope); return Task.FromResult(true); });

    /// <inheritdoc cref="Run(CancellationToken, Func{TaskScope, Task})"/>
    public static async Task<T> Run<T>(CancellationToken cancellation, Func<TaskScope, Task<T>> body)
    {
        var scope = new TaskScope(cancellation);
        try
        {
            T result = default!;
            Exception? thrown = null;
            try
            {
                result = await body(scope);
            }
            catch (Exception e)
            {
                thrown = e;
                // A cancellation the scope caused (a task failed) isn't the body's own failure: the task's is reported.
                if (e is not OperationCanceledException || !scope.IsCancellationRequested)
                    scope.Fail(e);
                scope.Cancel();
            }
            await scope.FinishAsync();
            if (scope._failure is { } failure)
                ExceptionDispatchInfo.Throw(failure);
            if (thrown is not null)
                ExceptionDispatchInfo.Throw(thrown);
            return result;
        }
        finally
        {
            scope._cancellation.Dispose();
        }
    }

    /// <summary>
    /// Starts <paramref name="work"/> as one of the scope's tasks, giving it the scope's token. The task it gives can be
    /// awaited for its result; the scope waits for it anyway.
    /// </summary>
    [ObservedTask]
    public Task Start(Func<CancellationToken, Task> work) =>
        Start<bool>(async token => { await work(token); return true; });

    /// <inheritdoc cref="Start(Func{CancellationToken, Task})"/>
    [ObservedTask]
    public Task<T> Start<T>(Func<CancellationToken, Task<T>> work)
    {
        lock (_gate)
        {
            if (_finished)
                throw new InvalidOperationException("This task scope has finished, so it can't start more tasks: start them inside its body.");
            var task = PiperRouting.Run(() => Watch(work));
            _tasks.Add(task);
            return task;
        }
    }

    private async Task<T> Watch<T>(Func<CancellationToken, Task<T>> work)
    {
        try
        {
            return await work(Token);
        }
        catch (OperationCanceledException) when (IsCancellationRequested)
        {
            throw;   // stopped as asked: not a failure
        }
        catch (Exception e)
        {
            Fail(e);
            Cancel();
            throw;
        }
    }

    /// <summary>Keeps the first failure, from the body or a task.</summary>
    private void Fail(Exception e)
    {
        lock (_gate)
            _failure ??= e;
    }

    /// <summary>Waits for every task, including any a task started while the others ran, then refuses new ones.</summary>
    private async Task FinishAsync()
    {
        while (true)
        {
            Task[] running;
            lock (_gate)
            {
                running = [.. _tasks.Where(t => !t.IsCompleted)];
                if (running.Length == 0)
                {
                    _finished = true;
                    return;
                }
            }
            try
            {
                await Task.WhenAll(running);
            }
            catch
            {
                // Each task's failure is kept by Watch; a cancelled one isn't a failure.
            }
        }
    }
}
