using Piper.Plugins;

namespace Piper.Plugins.Telemetry;

/// <summary>
/// Traces, metrics and logs over OpenTelemetry for a program, which a <c>server</c> (the web plugin) or <c>host</c> (the
/// hosting plugin) asks for with <c>telemetry;</c>: it brings OpenTelemetry's SDK and OTLP exporter, and Piper.Telemetry,
/// and adds no syntax. It's a plugin of its own so that only programs wanting telemetry carry the packages.
/// </summary>
public sealed class TelemetryPlugin : IPiperPlugin
{
    public string Name => "telemetry";

    public IReadOnlyList<PluginKeyword> Keywords => [];

    public IReadOnlyDictionary<string, string> Packages { get; } = new Dictionary<string, string>
    {
        ["OpenTelemetry.Extensions.Hosting"] = "1.20.1",
        ["OpenTelemetry.Exporter.OpenTelemetryProtocol"] = "1.20.1",
        ["OpenTelemetry.Instrumentation.AspNetCore"] = "1.20.1",
        ["OpenTelemetry.Instrumentation.Http"] = "1.20.1",
    };

    /// <summary>ASP.NET Core, whose requests the instrumentation names and describes (method, route, status).</summary>
    public IReadOnlyList<string> Frameworks => ["Microsoft.AspNetCore.App"];

    /// <summary>Piper.Telemetry: <c>PiperTelemetry.Add</c>, which <c>telemetry;</c> calls.</summary>
    public IReadOnlyList<string> References => ["Piper.Telemetry.dll"];
}
