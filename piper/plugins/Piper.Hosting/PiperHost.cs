using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Piper.Hosting;

/// <summary>
/// What the code a <c>host</c> or a <c>server</c> declaration writes calls: its workers, the hooks run as it stops,
/// how long it has to stop, and its logging. A worker or a hook is a handler whose parameters are services, resolved
/// from a scope of their own (a <c>CancellationToken</c> is the one the host gives), which may return a value or a task.
/// </summary>
public static class PiperHost
{
    /// <summary>
    /// <c>provider IEventStore from "Events";</c>: the provider of <typeparamref name="T"/> the configuration's
    /// <paramref name="section"/> chooses (<c>Events:Provider</c>, the local one if none), made from its settings, and
    /// registered as a singleton. A provider that can't be made stops the program as it starts, saying why.
    /// </summary>
    public static T Provider<T>(IHostApplicationBuilder builder, string section) where T : class
    {
        var provider = Provider<T>(builder.Configuration, section);
        builder.Services.AddSingleton(provider);
        return provider;
    }

    /// <summary>The provider of <typeparamref name="T"/> that <paramref name="section"/> of any configuration chooses.</summary>
    public static T Provider<T>(Microsoft.Extensions.Configuration.IConfiguration configuration, string section) where T : class =>
        Piper.Runtime.PiperProviders.Create<T>(Settings(configuration, section));

    /// <summary>The settings in <paramref name="section"/> of the configuration, for a provider to read (a <c>database</c> clause's).</summary>
    public static Piper.Runtime.ProviderSettings Settings(Microsoft.Extensions.Configuration.IConfiguration configuration, string section) =>
        new(section, Microsoft.Extensions.Configuration.ConfigurationExtensions.AsEnumerable(configuration.GetSection(section), makePathsRelative: true));

    /// <summary><c>logging off;</c>: no logging providers, so the program writes nothing to the console.</summary>
    public static void LoggingOff(IHostApplicationBuilder builder) => builder.Logging.ClearProviders();

    /// <summary>
    /// What Piper.Runtime reports (a persisted actor's snapshot that wasn't kept) goes to the program's logger, under
    /// <c>Piper.Events</c>, rather than standard error: a server or host calls it once it's built.
    /// </summary>
    public static void UseLogging(IServiceProvider services)
    {
        if (services.GetService<ILoggerFactory>()?.CreateLogger(Piper.Runtime.PiperDiagnostics.Name) is not { } logger)
            return;
        Piper.Runtime.PiperDiagnostics.Log = (message, exception) =>
        {
            if (exception is null)
                logger.LogInformation("{Message}", message);
            else
                logger.LogWarning(exception, "{Message}", message);
        };
    }

    /// <summary><c>shutdown 30 seconds;</c>: how long workers, <c>stopping</c> hooks and requests have to finish once it's asked to stop.</summary>
    public static void ShutdownTimeout(IHostApplicationBuilder builder, TimeSpan timeout) =>
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = timeout);

    /// <summary>
    /// <c>worker (jobs: Jobs, stopping: CancellationToken) =&gt; RunWorkers(jobs, stopping);</c>: run in the background
    /// as the host starts, for as long as it runs, with its services from one scope. A <c>CancellationToken</c>
    /// parameter is cancelled as the host stops, and the host waits for it to finish (up to <c>shutdown</c>'s time).
    /// If it throws, the host logs it and stops, as .NET's hosted services do, rather than carrying on without it.
    /// </summary>
    public static void Worker(IHostApplicationBuilder builder, Delegate worker) =>
        builder.Services.AddSingleton<IHostedService>(services => new HandlerWorker(services, worker, null));

    /// <summary>
    /// <c>worker every 1 second (auction: Auctioneer) =&gt; auction.Tick(DateTime.UtcNow);</c>: run as the host starts,
    /// then once each period (a run taking longer than the period skips the ticks it missed), each run with services
    /// from a scope of its own. If a run throws, it's logged, and the next runs as usual.
    /// </summary>
    public static void Worker(IHostApplicationBuilder builder, TimeSpan every, Delegate worker) =>
        builder.Services.AddSingleton<IHostedService>(services => new HandlerWorker(services, worker, every));

    /// <summary>
    /// <c>stopping (jobs: Jobs) =&gt; await jobs.Finish();</c>: run when the host is asked to stop (Ctrl+C, or SIGTERM
    /// from Kubernetes), before its workers are, and waited for.
    /// </summary>
    public static void OnStopping(IHost host, Delegate hook)
    {
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Register(() =>
        {
            try
            {
                Invoke(host.Services, hook, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"A 'stopping' hook failed: {e}");
            }
        });
    }

    /// <summary>
    /// Calls <paramref name="handler"/> with its parameters from a new scope's services (a CancellationToken is
    /// <paramref name="cancellation"/>), awaiting it if it returns a task; what it returned, or null.
    /// </summary>
    public static async Task<object?> Invoke(IServiceProvider services, Delegate handler, CancellationToken cancellation)
    {
        await using var scope = services.CreateAsyncScope();
        var arguments = handler.Method.GetParameters()
            .Skip(handler.Target is not null && handler.Method.IsStatic ? 1 : 0)
            .Select(p => p.ParameterType == typeof(CancellationToken) ? cancellation : scope.ServiceProvider.GetRequiredService(p.ParameterType))
            .ToArray();
        object? result;
        try
        {
            result = handler.DynamicInvoke(arguments);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            throw e.InnerException;
        }
        switch (result)
        {
            case Task task:
                await task.ConfigureAwait(false);
                return task.GetType().IsGenericType ? task.GetType().GetProperty("Result")!.GetValue(task) : null;
            case ValueTask valueTask:
                await valueTask.ConfigureAwait(false);
                return null;
            default:
                // A ValueTask<T>: as a task, for its result.
                if (result?.GetType() is { IsGenericType: true } type && type.GetGenericTypeDefinition() == typeof(ValueTask<>))
                {
                    var asTask = (Task)type.GetMethod("AsTask")!.Invoke(result, null)!;
                    await asTask.ConfigureAwait(false);
                    return asTask.GetType().GetProperty("Result")!.GetValue(asTask);
                }
                return result;
        }
    }

    private sealed class HandlerWorker(IServiceProvider services, Delegate worker, TimeSpan? every) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stopping)
        {
            try
            {
                if (every is not { } period)
                {
                    await Invoke(services, worker, stopping);
                    return;
                }
                using var timer = new PeriodicTimer(period);
                do
                {
                    try
                    {
                        await Invoke(services, worker, stopping);
                    }
                    catch (Exception e) when (!stopping.IsCancellationRequested)
                    {
                        services.GetService<ILoggerFactory>()?.CreateLogger("Piper.Hosting.Worker")
                            .LogError(e, "A worker's run failed; it runs again in {Period}.", period);
                    }
                }
                while (await timer.WaitForNextTickAsync(stopping));
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                // Stopping, as asked.
            }
        }
    }
}
