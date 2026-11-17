using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Piper.Telemetry;

/// <summary>
/// <c>telemetry;</c> in a <c>server</c> or <c>host</c>: traces, metrics and logs over OpenTelemetry's protocol (OTLP), to
/// wherever <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> says (Aspire's dashboard, Jaeger, an OpenTelemetry collector), configured
/// as OpenTelemetry's standard <c>OTEL_*</c> settings say. Without an endpoint it adds nothing, so a program run without a
/// collector pays nothing for it.
/// <list type="bullet">
/// <item>Traces: ASP.NET Core's requests, HttpClient's calls, and Piper's own (<c>Piper.*</c>: remote actors' handlers,
/// each continuing its sender's trace), so one request is one trace across services and nodes.</item>
/// <item>Metrics: ASP.NET Core's, Kestrel's, HttpClient's, .NET's runtime (memory, GC, threads), and Piper's (actors'
/// handler times).</item>
/// <item>Logs: what the program writes through <c>ILogger</c>, with each line's trace, so a log links to its request.</item>
/// </list>
/// .NET's libraries make their own metrics, and spans; OpenTelemetry's ASP.NET Core and HttpClient instrumentation names
/// and describes those spans (<c>POST /stock/{sku}/receive</c>, its status), which on their own are only
/// <c>HttpRequestIn</c>.
/// </summary>
public static class PiperTelemetry
{
    /// <summary>What's traced beyond ASP.NET Core and HttpClient (their instrumentation's): Piper's own sources.</summary>
    public static readonly string[] Sources = ["Piper.*"];

    /// <summary>What's measured: ASP.NET Core, Kestrel, HttpClient, .NET's runtime and Piper's own meters.</summary>
    public static readonly string[] Meters =
        ["Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel", "System.Net.Http", "System.Runtime", "Piper.*"];

    /// <summary>
    /// Adds OpenTelemetry to the program <paramref name="builder"/> builds, reporting it as <paramref name="service"/>
    /// (unless <c>OTEL_SERVICE_NAME</c> names it), if <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> says where to send it.
    /// </summary>
    public static void Add(IHostApplicationBuilder builder, string service)
    {
        if (string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            return;
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(builder.Configuration["OTEL_SERVICE_NAME"] is { Length: > 0 } named ? named : service))
            .WithTracing(tracing => tracing.AddSource(Sources).AddAspNetCoreInstrumentation().AddHttpClientInstrumentation())
            .WithMetrics(metrics => metrics.AddMeter(Meters))
            .UseOtlpExporter();
    }
}
