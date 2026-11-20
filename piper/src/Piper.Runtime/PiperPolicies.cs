using System.Collections.Concurrent;

namespace Piper.Runtime;

/// <summary>
/// <c>[Timeout(ms)]</c> (the aspects plugin): an async function that hasn't finished in time throws a
/// <see cref="TimeoutException"/> to its caller. The work isn't stopped (nothing outside it can stop it safely), only
/// waited for no longer; a function that should stop takes a <c>CancellationToken</c> and passes it on.
/// </summary>
public static class PiperTimeout
{
    public static Task<T> CallAsync<T>(Func<Task<T>> call, int milliseconds, string name) =>
        CallAsync(call, TimeSpan.FromMilliseconds(milliseconds), name);

    /// <summary><c>[Timeout(2s)]</c>: the time to wait as a duration.</summary>
    public static async Task<T> CallAsync<T>(Func<Task<T>> call, TimeSpan within, string name)
    {
        var task = call();
        try
        {
            return await task.WaitAsync(within, PiperRuntime.Current.Time);
        }
        catch (TimeoutException) when (!task.IsCompleted)
        {
            throw new TimeoutException($"{name} took more than {Describe(within)}.");
        }
    }

    public static Task RunAsync(Func<Task> call, int milliseconds, string name) =>
        RunAsync(call, TimeSpan.FromMilliseconds(milliseconds), name);

    public static async Task RunAsync(Func<Task> call, TimeSpan within, string name)
    {
        var task = call();
        try
        {
            await task.WaitAsync(within, PiperRuntime.Current.Time);
        }
        catch (TimeoutException) when (!task.IsCompleted)
        {
            throw new TimeoutException($"{name} took more than {Describe(within)}.");
        }
    }

    /// <summary>"500 ms", or "2.5 s" from a second up.</summary>
    private static string Describe(TimeSpan time) =>
        time < TimeSpan.FromSeconds(1)
            ? $"{time.TotalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} ms"
            : $"{time.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} s";
}

/// <summary>A call refused at once because its circuit is open: what it calls has been failing.</summary>
public sealed class CircuitOpenException(string name, TimeSpan retryAfter)
    : Exception($"{name} is failing, so it isn't being called for now; try again in {Math.Ceiling(retryAfter.TotalSeconds)} s.")
{
    /// <summary>How long until a call is let through to try again.</summary>
    public TimeSpan RetryAfter { get; } = retryAfter;
}

/// <summary>
/// <c>[CircuitBreaker(failures, openMs)]</c> (the aspects plugin): a function that has thrown <c>failures</c> times in a
/// row isn't called for <c>openMs</c>: its calls throw <see cref="CircuitOpenException"/> at once, rather than waiting
/// on what's failing (and adding to its load). Then one call is let through to try: if it succeeds the circuit closes,
/// and if it throws it opens again. One circuit for each decorated function, shared by all its callers, as a service
/// that's down is down for everyone.
/// </summary>
public sealed class PiperCircuit
{
    private static readonly ConcurrentDictionary<string, PiperCircuit> Circuits = new();

    private readonly object _gate = new();
    private readonly string _name;
    private readonly int _failures;
    private readonly TimeSpan _open;
    private int _failed;
    private DateTime? _openUntil;
    private bool _trying;

    private PiperCircuit(string name, int failures, TimeSpan open)
    {
        if (failures < 1)
            throw new ArgumentOutOfRangeException(nameof(failures), failures, "A circuit opens after at least one failure.");
        _name = name;
        _failures = failures;
        _open = open;
    }

    /// <summary>The circuit of the function <paramref name="key"/> names (one per decorated function).</summary>
    public static PiperCircuit For(string key, string name, int failures, int openMilliseconds) =>
        For(key, name, failures, TimeSpan.FromMilliseconds(openMilliseconds));

    /// <summary><c>[CircuitBreaker(5, 30s)]</c>: how long it stays open as a duration.</summary>
    public static PiperCircuit For(string key, string name, int failures, TimeSpan open) =>
        Circuits.GetOrAdd(key, _ => new PiperCircuit(name, failures, open));

    /// <summary>Whether calls are being refused now.</summary>
    public bool IsOpen
    {
        get
        {
            lock (_gate)
                return _openUntil is { } until && (PiperRuntime.Current.UtcNow < until || _trying);
        }
    }

    public T Call<T>(Func<T> call)
    {
        Enter();
        try
        {
            var result = call();
            Succeeded();
            return result;
        }
        catch
        {
            Failed();
            throw;
        }
    }

    public void Run(Action call) => Call(() => { call(); return true; });

    public async Task<T> CallAsync<T>(Func<Task<T>> call)
    {
        Enter();
        try
        {
            var result = await call();
            Succeeded();
            return result;
        }
        catch
        {
            Failed();
            throw;
        }
    }

    public Task RunAsync(Func<Task> call) => CallAsync(async () => { await call(); return true; });

    /// <summary>
    /// A call returning a <c>Result</c>: a transient <c>Err</c> counts as a failure, as a throw does. Any other
    /// <c>Err</c> (not found, not allowed) is a success: what it called answered, so it isn't failing.
    /// </summary>
    public Result<T, E> CallResult<T, E>(Func<Result<T, E>> call)
    {
        Enter();
        Result<T, E> result;
        try
        {
            result = call();
        }
        catch
        {
            Failed();
            throw;
        }
        if (PiperTransient.IsTransientFailure(result))
            Failed();
        else
            Succeeded();
        return result;
    }

    /// <summary>An async call returning a <c>Result</c>, as <see cref="CallResult{T, E}"/>.</summary>
    public async Task<Result<T, E>> CallResultAsync<T, E>(Func<Task<Result<T, E>>> call)
    {
        Enter();
        Result<T, E> result;
        try
        {
            result = await call();
        }
        catch
        {
            Failed();
            throw;
        }
        if (PiperTransient.IsTransientFailure(result))
            Failed();
        else
            Succeeded();
        return result;
    }

    /// <summary>Lets a call through, or refuses it while the circuit is open (one is let through to try once it's time).</summary>
    private void Enter()
    {
        lock (_gate)
        {
            if (_openUntil is not { } until)
                return;
            var now = PiperRuntime.Current.UtcNow;
            if (now < until || _trying)
                throw new CircuitOpenException(_name, until > now ? until - now : TimeSpan.Zero);
            _trying = true;
        }
    }

    private void Succeeded()
    {
        lock (_gate)
        {
            _failed = 0;
            _openUntil = null;
            _trying = false;
        }
    }

    private void Failed()
    {
        lock (_gate)
        {
            _failed++;
            if (_trying || _failed >= _failures)
                _openUntil = PiperRuntime.Current.UtcNow + _open;
            _trying = false;
        }
    }
}
