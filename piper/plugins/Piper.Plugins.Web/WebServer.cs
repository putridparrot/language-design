using System.Collections.Immutable;
using Piper.Plugins;
using Piper.Syntax;

namespace Piper.Plugins.Web;

/// <summary>
/// The <c>server</c> declaration: what a web app is made of (where it listens, its services, policies, endpoints,
/// health checks and what it does as it stops), as one function building it, with its middleware in the order
/// ASP.NET Core needs:
/// <code>
/// server Tracker(settings: Settings, clock: Clock) {
///     urls settings.urls;
///     services AppServices;
///     singleton Clock = clock;
///     problems;
///     policies Security(settings);
///     endpoints Api, Admin;
///     health "/healthz" ready "/ready" { check "database" async (db: TrackerDb) => await db.Database.CanConnectAsync(); }
/// }
/// </code>
/// becomes <c>fn Tracker(settings: Settings, clock: Clock) =&gt; WebApplication</c>, which <c>Main</c> runs
/// (<c>await Tracker(settings, SystemClock()).RunAsync();</c>) and a test serves with <c>TestClient.Start</c>.
/// </summary>
public sealed partial class WebPlugin
{
    private const string DependencyInjection = "Microsoft.Extensions.DependencyInjection";

    private static readonly Dictionary<string, string> Lifetimes = new()
    {
        ["singleton"] = "AddSingleton",
        ["scoped"] = "AddScoped",
        ["transient"] = "AddTransient",
    };

    private static DeclarationKeyword ServerKeyword() => new("server", Server)
    {
        Description = "Declares a web app: where it listens, its services, policies, endpoints and health checks, and what it does as it stops, " +
                      "as `fn Name(…) => WebApplication`, with its middleware in the order ASP.NET Core needs. `Main` runs it: `await Name().RunAsync();`.",
        Snippet = "server ${1:App} {\n\turls \"${2:http://localhost:5080}\";\n\tendpoints ${3:Api};\n\t$0\n}",
        Words = new Dictionary<string, string>
        {
            ["urls"] = "Where it listens: a URL, or a list of them (`UseUrls`). A test's TestClient serves it from memory instead.",
            ["logging"] = "`logging off;` removes the logging providers, for an app that writes nothing to the console.",
            ["config"] = "Reads a `config` record from the app's configuration as it starts (stopping it, listing every problem, if one is missing or invalid), and registers it, and as `IOptions<T>`. `config smtp: Smtp;` names it for the clauses after.",
            ["services"] = "A `services` block registering the app's services: `services AppServices;`, with its arguments if it takes any.",
            ["singleton"] = "Registers one instance, shared by everything: `AddSingleton`.",
            ["scoped"] = "Registers one instance per request: `AddScoped`.",
            ["transient"] = "Registers a new instance each time it's asked for: `AddTransient`.",
            ["configure"] = "Code run on the builder (`builder`) before the app is built, for what the declaration doesn't say: `configure { builder.Services.AddAntiforgery(); }`.",
            ["problems"] = "Errors as ProblemDetails: an exception is a 500, and an error status without a body (404) has one, rather than a bare page.",
            ["use"] = "Middleware of the app's own (`app`), run after `problems` and before the policies' and the endpoints: `use { app.Use(…); }`.",
            ["policies"] = "A `policies` block: registers its policies, and adds their middleware where it must go, before the endpoints.",
            ["openapi"] = "Serves an OpenAPI document at /openapi/v1.json, describing Piper's types (needs the openapi plugin).",
            ["endpoints"] = "The `endpoints` blocks the app maps.",
            ["health"] = "Health checks at this path. With `ready`, it's liveness (healthy while the app runs) and `ready` runs the checks.",
            ["ready"] = "Readiness at this path: healthy when every check passes, so Kubernetes sends no requests until then.",
            ["check"] = "A health check, as a handler whose parameters are services: healthy when it gives true, unhealthy when it gives false or throws.",
            ["async"] = "The handler awaits, so it runs as an async lambda.",
            ["stopping"] = "Run when the app is asked to stop (Ctrl+C, or SIGTERM from Kubernetes), and waited for, before the server stops: a handler whose parameters are services.",
            ["shutdown"] = "How long requests and `stopping` have to finish once the app is asked to stop: `shutdown 30 seconds;`.",
        },
    };

    /// <summary>What a <c>server</c> says, for <see cref="Analyze"/>: the endpoints it maps, and whether it has policies.</summary>
    private sealed record ServerData(Token Name, ImmutableArray<Token> Endpoints, bool HasPolicies, ImmutableArray<LambdaExpressionSyntax> Checks, ImmutableArray<LambdaExpressionSyntax> Hooks);

    /// <summary>
    /// server  = "server" IDENT [ lambdaParameters ] "{" { clause } "}" ;
    /// clause  = "urls" expression ";" | "logging" "off" ";" | "services" IDENT [ arguments ] ";" | "config" [ IDENT ":" ] IDENT ";"
    ///         | ( "singleton" | "scoped" | "transient" ) type [ "=>" type | "=" expression ] ";"
    ///         | "configure" block | "problems" ";" | "use" block | "policies" IDENT [ arguments ] ";"
    ///         | "openapi" ";" | "endpoints" IDENT { "," IDENT } ";"
    ///         | "health" STRING [ "ready" STRING ] ( ";" | "{" { "check" STRING handler } "}" )
    ///         | "stopping" handler | "shutdown" duration ";" ;
    /// </summary>
    private static IEnumerable<DeclarationSyntax> Server(PluginParser p)
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

        // Before the app is built (registrations), and after, in the order middleware must run.
        var building = new List<StatementSyntax>();
        var problems = new List<StatementSyntax>();
        var middleware = new List<StatementSyntax>();
        var policyMiddleware = new List<StatementSyntax>();
        var mapping = new List<StatementSyntax>();
        var endpoints = new List<StatementSyntax>();
        var stopping = new List<StatementSyntax>();
        var endpointNames = new List<Token>();
        var seen = new Dictionary<string, Token>();
        bool hasPolicies = false;
        var checks = new List<LambdaExpressionSyntax>();
        var hooks = new List<LambdaExpressionSyntax>();

        // A clause that may only be written once (`urls`, `problems`, `health`…).
        void Once(Token word)
        {
            if (seen.TryGetValue(word.Text, out _))
                p.Report(word.Span, $"'{word.Text}' is already given for {name.Text}.");
            else
                seen[word.Text] = word;
        }

        while (p.Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile))
        {
            var word = p.Current;
            if (word.Kind != TokenKind.Identifier)
            {
                p.Report(word.Span, "Expected a server clause, such as 'urls', 'services', 'endpoints' or 'health'.");
                SkipClause(p);
                continue;
            }
            switch (word.Text)
            {
                case "urls":
                    p.ReadWord();
                    Once(word);
                    building.Add(p.Template.Statement("Piper.Web.PiperServer.Urls(builder, $urls);", ("urls", p.ParseExpression())));
                    p.Expect(TokenKind.Semicolon);
                    break;

                case "logging":
                    p.ReadWord();
                    Once(word);
                    p.ExpectWord("off");
                    building.Add(p.Template.Statement("Piper.Web.PiperServer.LoggingOff(builder);"));
                    p.Expect(TokenKind.Semicolon);
                    break;

                case "services":
                {
                    p.ReadWord();
                    var services = p.Expect(TokenKind.Identifier);
                    building.Add(p.Template.Statement("$services(builder.Services, $arguments);", ("services", services), ("arguments", Arguments(p))));
                    p.Expect(TokenKind.Semicolon);
                    break;
                }

                case "config":
                {
                    // `config Smtp;`, or `config smtp: Smtp;` to use it in the clauses after (`urls smtp.url;`).
                    p.ReadWord();
                    var local = p.Current.Kind == TokenKind.Identifier && p.Peek(1).Kind == TokenKind.Colon ? p.NextToken() : null;
                    if (local is not null)
                        p.NextToken();
                    var config = p.Expect(TokenKind.Identifier);
                    var value = local ?? p.FreshName(char.ToLowerInvariant(config.Text[0]) + config.Text[1..]);
                    var from = new Token(TokenKind.Identifier, config.Span, config.Text + "From");
                    // Read as the app is built, so a missing or invalid value stops it at startup, listing every problem.
                    building.Add(p.Template.Statement("let $value = $from(builder.Configuration);", ("value", value), ("from", from)));
                    building.Add(p.Template.Statement($"{DependencyInjection}.ServiceCollectionServiceExtensions.AddSingleton<$config>(builder.Services, $value);",
                        ("config", config), ("value", value)));
                    // As IOptions<Smtp> too, for code written for .NET's options.
                    building.Add(p.Template.Statement(
                        $"{DependencyInjection}.ServiceCollectionServiceExtensions.AddSingleton<Microsoft.Extensions.Options.IOptions<$config>>(builder.Services, Microsoft.Extensions.Options.Options.Create($value));",
                        ("config", config), ("value", value)));
                    p.Expect(TokenKind.Semicolon);
                    break;
                }

                case "singleton" or "scoped" or "transient":
                    p.ReadWord();
                    building.Add(Registration(p, Lifetimes[word.Text]));
                    p.Expect(TokenKind.Semicolon);
                    break;

                case "configure":
                    p.ReadWord();
                    building.Add(new ExpressionStatementSyntax(p.Current.Span, p.ParseBlock()));
                    break;

                case "problems":
                    p.ReadWord();
                    Once(word);
                    building.Add(p.Template.Statement($"{DependencyInjection}.ProblemDetailsServiceCollectionExtensions.AddProblemDetails(builder.Services);"));
                    // An exception is a 500 ProblemDetails, and a status without a body (a 404) gets one.
                    problems.Add(p.Template.Statement("Microsoft.AspNetCore.Builder.ExceptionHandlerExtensions.UseExceptionHandler(app);"));
                    problems.Add(p.Template.Statement("Microsoft.AspNetCore.Builder.StatusCodePagesExtensions.UseStatusCodePages(app);"));
                    p.Expect(TokenKind.Semicolon);
                    break;

                case "use":
                    p.ReadWord();
                    middleware.Add(new ExpressionStatementSyntax(p.Current.Span, p.ParseBlock()));
                    break;

                case "policies":
                {
                    p.ReadWord();
                    hasPolicies = true;
                    var policies = p.Expect(TokenKind.Identifier);
                    building.Add(p.Template.Statement("$policies(builder.Services, $arguments);", ("policies", policies), ("arguments", Arguments(p))));
                    var use = new Token(TokenKind.Identifier, policies.Span, "Use" + policies.Text);
                    policyMiddleware.Add(p.Template.Statement("$use(app);", ("use", use)));
                    p.Expect(TokenKind.Semicolon);
                    break;
                }

                case "openapi":
                    p.ReadWord();
                    Once(word);
                    building.Add(p.Template.Statement("Piper.Web.OpenApi.PiperOpenApiExtensions.AddPiperOpenApi(builder.Services);"));
                    mapping.Add(p.Template.Statement("Microsoft.AspNetCore.Builder.OpenApiEndpointRouteBuilderExtensions.MapOpenApi(app);"));
                    p.Expect(TokenKind.Semicolon);
                    break;

                case "endpoints":
                    p.ReadWord();
                    do
                    {
                        if (endpointNames.Count > 0 && p.Current.Kind == TokenKind.Comma)
                            p.NextToken();
                        var group = p.Expect(TokenKind.Identifier);
                        if (endpointNames.Any(e => e.Text == group.Text))
                            p.Report(group.Span, $"'{group.Text}' is already mapped by {name.Text}.");
                        endpointNames.Add(group);
                        endpoints.Add(p.Template.Statement("$group(app);", ("group", group)));
                    }
                    while (p.Current.Kind == TokenKind.Comma);
                    p.Expect(TokenKind.Semicolon);
                    break;

                case "health":
                    p.ReadWord();
                    Once(word);
                    Health(p, building, mapping, checks);
                    break;

                case "stopping":
                    p.ReadWord();
                    if (Handler(p) is { } hook && hook.Parameters.All(parameter => parameter.Type is not null))
                    {
                        stopping.Add(p.Template.Statement("Piper.Web.PiperServer.OnStopping(app, $hook);", ("hook", hook)));
                        hooks.Add(hook);
                    }
                    break;

                case "shutdown":
                    p.ReadWord();
                    Once(word);
                    building.Add(p.Template.Statement("Piper.Web.PiperServer.ShutdownTimeout(builder, $timeout);", ("timeout", Duration(p, countOptional: false))));
                    p.Expect(TokenKind.Semicolon);
                    break;

                default:
                    p.Report(word.Span, $"'{word.Text}' isn't a server clause; expected 'urls', 'config', 'services', 'singleton', 'policies', 'endpoints', 'health', 'problems', 'openapi', 'configure', 'use', 'stopping' or 'shutdown'.");
                    SkipClause(p);
                    break;
            }
        }
        p.Expect(TokenKind.CloseBrace);
        if (endpointNames.Count == 0)
            p.Report(name.Span, $"{name.Text} maps no endpoints; add 'endpoints Api;' naming an 'endpoints' block.");
        p.Data = new ServerData(name, [.. endpointNames], hasPolicies, [.. checks], [.. hooks]);

        // problems first, so it sees every exception; then the app's own middleware; then the policies' (CORS,
        // authentication, authorization, rate limits, caching), which must come before the endpoints they guard.
        List<StatementSyntax> pipeline = [.. problems, .. middleware, .. policyMiddleware, .. mapping, .. endpoints, .. stopping];
        return p.Template.Declarations($$"""
            fn $name({{(parameters.Count == 0 ? "" : "$parameters: unit")}}) => Microsoft.AspNetCore.Builder.WebApplication {
                let builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
                $building;
                let app = builder.Build();
                $pipeline;
                app
            }
            """, [("name", name), ("building", building), ("pipeline", pipeline),
                 .. parameters.Count == 0 ? Array.Empty<(string, object)>() : [("parameters", (object)parameters)]]);
    }

    /// <summary>A call's arguments after a block's name, <c>(settings, 5)</c>, or none.</summary>
    private static List<ExpressionSyntax> Arguments(PluginParser p)
    {
        var arguments = new List<ExpressionSyntax>();
        if (p.Current.Kind != TokenKind.OpenParen)
            return arguments;
        p.NextToken();
        while (p.Current.Kind is not (TokenKind.CloseParen or TokenKind.EndOfFile))
        {
            arguments.Add(p.ParseExpression());
            if (p.Current.Kind != TokenKind.Comma)
                break;
            p.NextToken();
        }
        p.Expect(TokenKind.CloseParen);
        return arguments;
    }

    /// <summary><c>singleton Clock =&gt; SystemClock</c>, <c>singleton Clock = clock</c> or <c>scoped Db</c>, as the services plugin reads them.</summary>
    private static StatementSyntax Registration(PluginParser p, string method)
    {
        const string Registrations = DependencyInjection + ".ServiceCollectionServiceExtensions";
        var service = p.ParseType();
        if (p.Current.Kind == TokenKind.FatArrow)
        {
            p.NextToken();
            return p.Template.Statement($"{Registrations}.{method}<$service, $implementation>(builder.Services);",
                ("service", service), ("implementation", p.ParseType()));
        }
        if (p.Current.Kind == TokenKind.Equals)
        {
            p.NextToken();
            var value = p.ParseExpression();
            var factory = value is LambdaExpressionSyntax { Parameters.Length: 1 }
                ? value
                : p.Template.Expression("$provider => $value", ("provider", p.FreshName("provider")), ("value", value));
            return p.Template.Statement($"{Registrations}.{method}<$service>(builder.Services, $factory);", ("service", service), ("factory", factory));
        }
        return p.Template.Statement($"{Registrations}.{method}<$service>(builder.Services);", ("service", service));
    }

    /// <summary><c>health "/healthz" [ready "/ready"] ( ";" | "{" { check "name" handler } "}" )</c>.</summary>
    private static void Health(PluginParser p, List<StatementSyntax> building, List<StatementSyntax> mapping, List<LambdaExpressionSyntax> checks)
    {
        var health = HealthPath(p);
        ExpressionSyntax ready = p.Template.Expression("None");
        if (p.AtWord("ready"))
        {
            p.ReadWord();
            ready = HealthPath(p);
        }
        building.Add(p.Template.Statement("Piper.Web.PiperHealth.Add(builder.Services);"));
        mapping.Add(p.Template.Statement("Piper.Web.PiperHealth.Map(app, $health, $ready);", ("health", health), ("ready", ready)));
        if (p.Current.Kind != TokenKind.OpenBrace)
        {
            p.Expect(TokenKind.Semicolon);
            return;
        }

        p.NextToken();
        var names = new HashSet<string>();
        while (p.Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile))
        {
            if (!p.AtWord("check"))
            {
                p.Report(p.Current.Span, "Expected a health check: check \"database\" (db: Db) => …;");
                SkipClause(p);
                continue;
            }
            p.ReadWord();
            if (p.Current.Kind != TokenKind.StringLiteral)
            {
                p.Report(p.Current.Span, "Expected the check's name, such as \"database\".");
                SkipClause(p);
                continue;
            }
            var nameToken = p.NextToken();
            var checkName = new LiteralExpressionSyntax(nameToken.Span, nameToken, nameToken.Value);
            if (!names.Add((string)nameToken.Value!))
                p.Report(nameToken.Span, $"There's already a check named \"{nameToken.Value}\".");
            if (Handler(p) is { } check && check.Parameters.All(parameter => parameter.Type is not null))
            {
                building.Add(p.Template.Statement("Piper.Web.PiperHealth.AddCheck(builder.Services, $name, $check);", ("name", checkName), ("check", check)));
                checks.Add(check);
            }
        }
        p.Expect(TokenKind.CloseBrace);
    }

    /// <summary>A health endpoint's path, a plain string such as "/healthz".</summary>
    private static ExpressionSyntax HealthPath(PluginParser p)
    {
        if (p.Current.Kind == TokenKind.StringLiteral)
            return Route(p, p.NextToken());
        p.Report(p.Current.Span, "Expected the health endpoint's path, such as \"/healthz\".");
        return p.Template.Expression("\"/healthz\"");
    }

    /// <summary>
    /// A server mapping endpoints that use policy words (<c>authorize</c>, <c>ratelimit</c>…) with no <c>policies</c>
    /// clause would answer them without the middleware they need: a warning at the endpoints' name in the server.
    /// </summary>
    private static void AnalyzeServers(PluginAnalysis analysis)
    {
        // Without any `policies` block, AnalyzePolicies has already warned, once, about the setup they need.
        if (!analysis.Expansions.Any(e => e.Data is PoliciesData))
            return;
        var groups = analysis.Expansions.Where(e => e.Data is EndpointsData).ToDictionary(e => ((EndpointsData)e.Data!).Name.Text, e => (EndpointsData)e.Data!);
        foreach (var expansion in analysis.Expansions.Where(e => e.Data is ServerData))
        {
            var server = (ServerData)expansion.Data!;
            if (server.HasPolicies)
                continue;
            foreach (var group in server.Endpoints)
            {
                if (groups.TryGetValue(group.Text, out var data) && !data.Words.IsEmpty)
                {
                    analysis.ReportWarning(expansion.File, group.Span,
                        $"{group.Text} uses '{data.Words[0].Text}', which needs its policies' middleware: add 'policies Name;' to {server.Name.Text}.");
                }
            }
        }
    }
}
