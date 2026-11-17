using Piper.Text;

namespace Piper.Tests;

/// <summary>
/// The telemetry plugin (plugins/Piper.Plugins.Telemetry): <c>telemetry;</c> in a server or host adds OpenTelemetry,
/// exporting over OTLP, when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> says where to; and nothing without one. The plugins
/// are loaded by name from a piper.json, as the CLI would, so the OpenTelemetry packages are restored.
/// </summary>
public class TelemetryPluginTests
{
    private static readonly Lazy<LoadedProject> Project = new(() =>
    {
        var folder = Directory.CreateTempSubdirectory("piper-telemetry").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, PiperProject.FileName), """{ "plugins": ["web", "hosting", "telemetry"] }""");
            var (project, errors) = LoadedProject.Load(folder);
            Assert.True(errors.IsEmpty, string.Join("\n", errors.Select(e => e.Format())));
            return project!;
        }
        finally
        {
            // The packages' assemblies are in the NuGet cache, so the folder isn't needed once it's loaded.
            Directory.Delete(folder, recursive: true);
        }
    });

    private static CompilationResult Compile(string text, bool tests = false) =>
        Compilation.Compile([new SourceText(text, "main.piper")],
            new CompilationOptions(LineDirectives: tests, IncludeTests: tests) { Plugins = Project.Value.Plugins, References = Project.Value.References });

    [Fact]
    public void A_server_exports_telemetry_only_when_told_where()
    {
        var result = Compile("""
            using System;
            using Microsoft.Extensions.DependencyInjection;
            using Piper.Web;

            endpoints Api { get "/" () => "hi"; }
            server Shop { logging off; telemetry; endpoints Api; }
            server Named { logging off; telemetry "shop-api"; endpoints Api; }

            fn Main() {}

            async test "without an endpoint, there's no OpenTelemetry" {
                Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", None);
                using let site = await TestClient.Start(() => Shop());
                Assert(site.Services.GetService<OpenTelemetry.Trace.TracerProvider>() is None);
                AssertEqual("hi", await site.GetStringAsync("/"));
            }

            async test "with one, traces and metrics are set up" {
                Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:4317");
                using let site = await TestClient.Start(() => Named());
                Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", None);
                Assert(site.Services.GetService<OpenTelemetry.Trace.TracerProvider>() is Some(_));
                Assert(site.Services.GetService<OpenTelemetry.Metrics.MeterProvider>() is Some(_));
                AssertEqual("hi", await site.GetStringAsync("/"));
            }
            """, tests: true);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var run = Runner.RunTests(result.Files, references: result.References);
        Assert.Empty(run.Errors);
        Assert.True(run.Results.All(r => r.Passed), string.Join("\n", run.Results.Select(r => $"{r.Name}: {r.Failure}")));
        Assert.Equal(2, run.Results.Length);
    }

    [Fact]
    public void A_host_can_export_telemetry_too()
    {
        var result = Compile("""
            using System.Threading;
            host Worker { logging off; telemetry; worker (stopping: CancellationToken) => { }; }
            fn Main() {}
            """);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        Assert.Contains("PiperTelemetry.Add(builder, \"Worker\")", result.CSharp);
    }

    [Fact]
    public void Telemetry_is_given_once()
    {
        var result = Compile("""
            endpoints Api { get "/" () => "hi"; }
            server S { telemetry; telemetry; endpoints Api; }
            fn Main() {}
            """);
        Assert.Contains(result.FormattedDiagnostics, d => d.Contains("'telemetry' is already given for S"));
    }
}
