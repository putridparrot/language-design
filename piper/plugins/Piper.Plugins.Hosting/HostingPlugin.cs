using Piper.Plugins;
using Piper.Plugins.Shared;
using Piper.Syntax;

namespace Piper.Plugins.Hosting;

/// <summary>
/// The <c>host</c> declaration: what a program without a web server is made of (its configuration, services, background
/// work and what it does as it stops), over .NET's generic host, as one function building it:
/// <code>
/// host GridWorker(options: Options) {
///     config settings: Settings;
///     singleton Options = options;
///     worker async (options: Options, stopping: CancellationToken) => await RunWorkers(options, stopping);
///     stopping (log: Log) => log.Flush();
///     shutdown 30 seconds;
/// }
/// </code>
/// becomes <c>fn GridWorker(options: Options) =&gt; IHost</c>, which <c>Main</c> runs (<c>await GridWorker(options).RunAsync();</c>),
/// stopping on Ctrl+C or SIGTERM, and a test starts and stops. Its clauses are a <c>server</c>'s (the web plugin's)
/// that aren't about the web, so moving a program between the two keeps them.
/// </summary>
public sealed class HostingPlugin : IPiperPlugin
{
    public string Name => "hosting";

    /// <summary>.NET's generic host, with configuration, logging and dependency injection.</summary>
    public IReadOnlyDictionary<string, string> Packages { get; } = new Dictionary<string, string> { ["Microsoft.Extensions.Hosting"] = "10.0.0" };

    /// <summary>Piper.Hosting, running workers and stopping hooks.</summary>
    public IReadOnlyList<string> References => ["Piper.Hosting.dll"];

    public IReadOnlyList<PluginKeyword> Keywords { get; } =
    [
        new DeclarationKeyword("host", Host)
        {
            Description = "Declares a program without a web server: its configuration, services, background work and what it does as it stops, " +
                          "as `fn Name(…) => IHost`, over .NET's generic host. `Main` runs it: `await Name().RunAsync();` (with `using Microsoft.Extensions.Hosting;`), stopping on Ctrl+C.",
            Snippet = "host ${1:Worker} {\n\tworker async (stopping: CancellationToken) => {\n\t\t$0\n\t};\n}",
            Words = HostClauses.Words,
        },
    ];

    /// <summary>What a <c>host</c> noted for the analysis: its name, and its stopping hooks' and workers' handlers.</summary>
    private sealed record HostData(Token Name, System.Collections.Immutable.ImmutableArray<LambdaExpressionSyntax> Hooks,
        System.Collections.Immutable.ImmutableArray<LambdaExpressionSyntax> Workers);

    /// <summary>
    /// A host's workers and stopping hooks take services: one taking a service of the program's own that nothing in the
    /// host registers would fail as it starts or stops, so it's reported now, as a server's are; and so is a singleton
    /// needing a scoped service, or a service nothing registers (see ServiceChecks).
    /// </summary>
    public void Analyze(PluginAnalysis analysis)
    {
        foreach (var expansion in analysis.Expansions.Where(e => e.Data is HostData))
        {
            var host = (HostData)expansion.Data!;
            if (ServiceChecks.FunctionOf(analysis, expansion) is not { } function)
                continue;
            var hooks = ServiceChecks.HandlerLambdas(function.Body, call => ServiceChecks.IsHostCall(call, "OnStopping"));
            var workers = ServiceChecks.HandlerLambdas(function.Body, call => ServiceChecks.IsHostCall(call, "Worker"));
            ServiceChecks.CheckServices(analysis, expansion.File, host.Name, ServiceChecks.RegisteredBy(analysis.Program, function),
                ServiceChecks.Pair(host.Hooks, hooks).Concat(ServiceChecks.Pair(host.Workers, workers)));
        }
    }

    /// <summary>
    /// host   = "host" IDENT [ lambdaParameters ] "{" { clause } "}" ;
    /// clause = "logging" "off" ";" | "services" IDENT [ arguments ] ";" | "config" [ IDENT ":" ] IDENT ";"
    ///        | ( "singleton" | "scoped" | "transient" ) type [ "=>" type | "=" expression ] ";" | "configure" block
    ///        | "hosted" type ";" | "worker" [ "every" duration ] handler | "stopping" handler | "shutdown" duration ";" ;
    /// </summary>
    private static IEnumerable<DeclarationSyntax> Host(PluginParser p)
    {
        var name = p.Expect(TokenKind.Identifier);
        var parameters = new List<ParameterSyntax>();
        if (p.Current.Kind == TokenKind.OpenParen)
        {
            foreach (var parameter in p.ParseLambdaParameters())
            {
                if (parameter.Type is null)
                    p.Report(parameter.Span, $"Give '{parameter.Name.Text}' a type, such as ({parameter.Name.Text}: Settings).");
                else
                    parameters.Add(new ParameterSyntax(parameter.Span, parameter.Name, parameter.Type));
            }
        }
        p.Expect(TokenKind.OpenBrace);

        var built = p.FreshName("host");
        var parts = new HostParts(built.Text) { Name = name.Text };
        var seen = new HashSet<string>();
        void Once(Token word)
        {
            if (!seen.Add(word.Text))
                p.Report(word.Span, $"'{word.Text}' is already given for {name.Text}.");
        }

        while (p.Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile))
        {
            var word = p.Current;
            if (word.Kind != TokenKind.Identifier || !HostClauses.TryParse(p, word, parts, Once))
            {
                p.Report(word.Span, word.Kind == TokenKind.Identifier
                    ? $"'{word.Text}' isn't a host clause; expected {HostClauses.Listed}."
                    : "Expected a host clause, such as 'services', 'worker' or 'stopping'.");
                HostClauses.SkipClause(p);
            }
        }
        p.Expect(TokenKind.CloseBrace);
        p.Data = new HostData(name, [.. parts.Hooks], [.. parts.Workers]);

        return p.Template.Declarations($$"""
            fn $name({{(parameters.Count == 0 ? "" : "$parameters: unit")}}) => Microsoft.Extensions.Hosting.IHost {
                let builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
                $building;
                let $host = builder.Build();
                $built;
                $host
            }
            """, [("name", name), ("building", parts.Building), ("host", built), ("built", parts.Built),
                 .. parameters.Count == 0 ? Array.Empty<(string, object)>() : [("parameters", (object)parameters)]]);
    }
}
