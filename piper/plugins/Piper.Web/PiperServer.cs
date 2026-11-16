using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Piper.Web;

/// <summary>
/// What the code a <c>server</c> declaration writes calls: where the app listens, how long it has to stop, and the
/// hooks run as it stops. A hook or a health check is a handler whose parameters are services, resolved as a
/// minimal API's are, which may return a value or a task.
/// </summary>
public static class PiperServer
{
    /// <summary><c>urls "http://localhost:5080";</c></summary>
    public static void Urls(WebApplicationBuilder builder, string url) => builder.WebHost.UseUrls(url);

    /// <summary><c>urls ["http://localhost:5080", "https://localhost:5081"];</c></summary>
    public static void Urls(WebApplicationBuilder builder, IEnumerable<string> urls) => builder.WebHost.UseUrls([.. urls]);

    /// <summary><c>logging off;</c>: no logging providers, so the app writes nothing to the console.</summary>
    public static void LoggingOff(WebApplicationBuilder builder) => builder.Logging.ClearProviders();

    /// <summary><c>shutdown 30 seconds;</c>: how long requests and <c>stopping</c> hooks have to finish once it's asked to stop.</summary>
    public static void ShutdownTimeout(WebApplicationBuilder builder, TimeSpan timeout) =>
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = timeout);

    /// <summary>
    /// <c>stopping (jobs: Jobs) =&gt; await jobs.Finish();</c>: run when the app is asked to stop (Ctrl+C, or SIGTERM from
    /// Kubernetes), before the server stops, and waited for.
    /// </summary>
    public static void OnStopping(WebApplication app, Delegate hook) =>
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            try
            {
                Invoke(app.Services, hook, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"A 'stopping' hook failed: {e}");
            }
        });

    /// <summary>
    /// Calls <paramref name="handler"/> with its parameters from a new scope's services (a CancellationToken is
    /// <paramref name="cancellation"/>), awaiting it if it returns a task; what it returned, or null.
    /// </summary>
    internal static async Task<object?> Invoke(IServiceProvider services, Delegate handler, CancellationToken cancellation)
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
}

/// <summary>
/// What a <c>server</c>'s <c>health "/healthz" ready "/ready" { check … }</c> writes calls: ASP.NET Core's health
/// checks, with checks written as handlers.
/// </summary>
public static class PiperHealth
{
    private const string ReadyTag = "piper-ready";

    /// <summary>Registers the health check service, which the endpoints need even with no checks.</summary>
    public static void Add(IServiceCollection services) => services.AddHealthChecks();

    /// <summary>
    /// <c>check "database" (db: Db) =&gt; await db.Database.CanConnectAsync();</c>: healthy when it gives true (or a
    /// <see cref="HealthCheckResult"/> saying so), unhealthy when it gives false or throws.
    /// </summary>
    public static void AddCheck(IServiceCollection services, string name, Delegate check) =>
        services.AddHealthChecks().Add(new HealthCheckRegistration(name, provider => new HandlerCheck(provider, check), null, [ReadyTag]));

    /// <summary>
    /// Maps the health endpoints. With only <paramref name="health"/>, it runs every check. With
    /// <paramref name="ready"/> too, <paramref name="health"/> is liveness, healthy while the app runs (so Kubernetes
    /// doesn't restart it when a database is down), and <paramref name="ready"/> runs the checks (so it's sent no
    /// requests until they pass).
    /// </summary>
    public static void Map(IEndpointRouteBuilder app, string health, string? ready)
    {
        if (ready is null)
        {
            app.MapHealthChecks(health);
            return;
        }
        app.MapHealthChecks(health, new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks(ready, new HealthCheckOptions { Predicate = registration => registration.Tags.Contains(ReadyTag) });
    }

    private sealed class HandlerCheck(IServiceProvider services, Delegate check) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                return await PiperServer.Invoke(services, check, cancellationToken) switch
                {
                    HealthCheckResult result => result,
                    false => HealthCheckResult.Unhealthy(),
                    _ => HealthCheckResult.Healthy(),
                };
            }
            catch (Exception e)
            {
                return HealthCheckResult.Unhealthy(e.Message, e);
            }
        }
    }
}
