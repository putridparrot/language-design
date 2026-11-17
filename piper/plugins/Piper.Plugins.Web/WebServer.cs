using System.Collections.Immutable;
using Piper.Plugins;
using Piper.Syntax;
using Piper.Text;
using Piper.Plugins.Shared;
using static Piper.Plugins.Shared.HostClauses;

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

    private static DeclarationKeyword ServerKeyword() => new("server", Server)
    {
        Description = "Declares a web app: where it listens, its services, policies, endpoints and health checks, its background work, and what it does as it stops, " +
                      "as `fn Name(…) => WebApplication`, with its middleware in the order ASP.NET Core needs. `Main` runs it: `await Name().RunAsync();`.",
        Snippet = "server ${1:App} {\n\turls \"${2:http://localhost:5080}\";\n\tendpoints ${3:Api};\n\t$0\n}",
        Words = new Dictionary<string, string>(HostClauses.Words)
        {
            ["urls"] = "Where it listens: a URL, or a list of them (`UseUrls`). A test's TestClient serves it from memory instead.",
            ["https"] = "Redirects HTTP to HTTPS, and outside Development tells browsers to use only HTTPS (HSTS). `https certificate \"server.pfx\" password secret;` or `https certificate \"tls.crt\" key \"tls.key\";` gives the certificate it serves.",
            ["forwarded"] = "Behind a proxy that ends TLS (an ingress, a load balancer): each request's scheme and client are those the proxy saw (`X-Forwarded-Proto`, `X-Forwarded-For`), so `https` doesn't redirect what arrived as HTTPS. `forwarded from \"10.0.0.0/8\";` believes only those proxies.",
            ["from"] = "The proxies `forwarded` believes: IP addresses (\"10.1.2.3\") or networks (\"10.0.0.0/8\"). Without, any.",
            ["certificate"] = "The certificate HTTPS serves: a .pfx file (with `password` if it has one), or a PEM file with its `key`, loaded as the server starts.",
            ["password"] = "The .pfx file's password.",
            ["key"] = "The PEM certificate's private key file.",
            ["problems"] = "Errors as ProblemDetails: an exception is a 500, and an error status without a body (404) has one, rather than a bare page.",
            ["use"] = "Middleware of the app's own (`app`), run after `problems` and before the policies' and the endpoints: `use { app.Use(…); }`.",
            ["policies"] = "A `policies` block: registers its policies, and adds their middleware where it must go, before the endpoints.",
            ["openapi"] = "Serves an OpenAPI document at /openapi/v1.json, describing Piper's types (needs the openapi plugin).",
            ["endpoints"] = "The `endpoints` blocks the app maps.",
            ["health"] = "Health checks at this path. With `ready`, it's liveness (healthy while the app runs) and `ready` runs the checks.",
            ["ready"] = "Readiness at this path: healthy when every check passes, so Kubernetes sends no requests until then.",
            ["check"] = "A health check, as a handler whose parameters are services: healthy when it gives true, unhealthy when it gives false or throws.",
        },
    };

    /// <summary>
    /// What a <c>server</c> says, for <see cref="Analyze"/>: the endpoints it maps, whether it has policies, and its
    /// handlers whose parameters are services. <c>PlainUrls</c> is where <c>urls</c> gives only http:// addresses to a
    /// server redirecting to HTTPS.
    /// </summary>
    private sealed record ServerData(Token Name, ImmutableArray<Token> Endpoints, bool HasPolicies, ImmutableArray<LambdaExpressionSyntax> Checks, ImmutableArray<LambdaExpressionSyntax> Hooks,
        ImmutableArray<LambdaExpressionSyntax> Workers, TextSpan? PlainUrls);

    /// <summary>
    /// server  = "server" IDENT [ lambdaParameters ] "{" { clause } "}" ;
    /// clause  = "urls" expression ";" | "https" [ "certificate" expression [ "password" expression | "key" expression ] ] ";"
    ///         | "forwarded" [ "from" expression { "," expression } ] ";"
    ///         | "problems" ";" | "use" block | "policies" IDENT [ arguments ] ";"
    ///         | "openapi" ";" | "endpoints" IDENT { "," IDENT } ";"
    ///         | "health" STRING [ "ready" STRING ] ( ";" | "{" { "check" STRING handler } "}" )
    ///         | hostClause ;
    /// hostClause (shared with `host`, see HostClauses)
    ///         = "logging" "off" ";" | "services" IDENT [ arguments ] ";" | "config" [ IDENT ":" ] IDENT ";"
    ///         | ( "singleton" | "scoped" | "transient" ) type [ "=>" type | "=" expression ] ";" | "configure" block
    ///         | "hosted" type ";" | "worker" [ "every" duration ] handler | "stopping" handler | "shutdown" duration ";" ;
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
        var host = new HostParts("app") { Name = name.Text };
        var problems = new List<StatementSyntax>();
        var forwarded = new List<StatementSyntax>();
        var https = new List<StatementSyntax>();
        ExpressionSyntax? urls = null;
        var middleware = new List<StatementSyntax>();
        var policyMiddleware = new List<StatementSyntax>();
        var mapping = new List<StatementSyntax>();
        var endpoints = new List<StatementSyntax>();
        var endpointNames = new List<Token>();
        var seen = new Dictionary<string, Token>();
        bool hasPolicies = false;
        var checks = new List<LambdaExpressionSyntax>();

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
                    urls = p.ParseExpression();
                    host.Building.Add(p.Template.Statement("Piper.Web.PiperServer.Urls(builder, $urls);", ("urls", urls)));
                    p.Expect(TokenKind.Semicolon);
                    break;

                case "https":
                    p.ReadWord();
                    Once(word);
                    if (p.AtWord("certificate"))
                    {
                        p.ReadWord();
                        var path = p.ParseExpression();
                        if (p.AtWord("key"))
                        {
                            p.ReadWord();
                            host.Building.Add(p.Template.Statement("Piper.Web.PiperServer.PemCertificate(builder, $path, $key);", ("path", path), ("key", p.ParseExpression())));
                        }
                        else if (p.AtWord("password"))
                        {
                            p.ReadWord();
                            host.Building.Add(p.Template.Statement("Piper.Web.PiperServer.Certificate(builder, $path, $password);", ("path", path), ("password", p.ParseExpression())));
                        }
                        else
                            host.Building.Add(p.Template.Statement("Piper.Web.PiperServer.Certificate(builder, $path);", ("path", path)));
                    }
                    https.Add(p.Template.Statement("Piper.Web.PiperServer.Https(app);"));
                    p.Expect(TokenKind.Semicolon);
                    break;

                case "forwarded":
                {
                    p.ReadWord();
                    Once(word);
                    // `from "10.0.0.0/8", "10.1.2.3"`: only these proxies are believed; a literal that isn't one is reported here.
                    var trusted = new List<ExpressionSyntax>();
                    if (p.AtWord("from"))
                    {
                        p.ReadWord();
                        do
                        {
                            if (trusted.Count > 0)
                                p.NextToken();
                            var entry = p.ParseExpression();
                            if (entry is LiteralExpressionSyntax { Value: string text } && !IsAddressOrNetwork(text))
                                p.Report(entry.Span, $"'{text}' isn't an IP address (10.1.2.3) or a network (10.0.0.0/8), so it can't be a trusted proxy.");
                            trusted.Add(entry);
                        }
                        while (p.Current.Kind == TokenKind.Comma);
                    }
                    var holes = trusted.Select((entry, i) => ($"t{i}", (object)entry)).ToList();
                    host.Building.Add(p.Template.Statement(
                        $"Piper.Web.PiperServer.Forwarded(builder{string.Concat(holes.Select(h => $", ${h.Item1}"))});", [.. holes]));
                    forwarded.Add(p.Template.Statement("Microsoft.AspNetCore.Builder.ForwardedHeadersExtensions.UseForwardedHeaders(app);"));
                    p.Expect(TokenKind.Semicolon);
                    break;
                }

                case "problems":
                    p.ReadWord();
                    Once(word);
                    host.Building.Add(p.Template.Statement($"{DependencyInjection}.ProblemDetailsServiceCollectionExtensions.AddProblemDetails(builder.Services);"));
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
                    host.Building.Add(p.Template.Statement("$policies(builder.Services, $arguments);", ("policies", policies), ("arguments", Arguments(p))));
                    var use = new Token(TokenKind.Identifier, policies.Span, "Use" + policies.Text);
                    policyMiddleware.Add(p.Template.Statement("$use(app);", ("use", use)));
                    p.Expect(TokenKind.Semicolon);
                    break;
                }

                case "openapi":
                    p.ReadWord();
                    Once(word);
                    host.Building.Add(p.Template.Statement("Piper.Web.OpenApi.PiperOpenApiExtensions.AddPiperOpenApi(builder.Services);"));
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
                    Health(p, host.Building, mapping, checks);
                    break;

                default:
                    if (!HostClauses.TryParse(p, word, host, Once))
                    {
                        p.Report(word.Span, $"'{word.Text}' isn't a server clause; expected 'urls', 'https', 'forwarded', 'policies', 'endpoints', 'health', 'problems', 'openapi', 'use', {HostClauses.Listed}.");
                        SkipClause(p);
                    }
                    break;
            }
        }
        p.Expect(TokenKind.CloseBrace);
        if (endpointNames.Count == 0)
            p.Report(name.Span, $"{name.Text} maps no endpoints; add 'endpoints Api;' naming an 'endpoints' block.");
        p.Data = new ServerData(name, [.. endpointNames], hasPolicies, [.. checks], [.. host.Hooks], [.. host.Workers],
            // Behind a proxy (`forwarded`), TLS ends there, so listening on http:// alone is expected.
            https.Count > 0 && forwarded.Count == 0 && urls is not null && OnlyPlainUrls(urls) ? urls.Span : null);

        // The proxy's forwarded headers first, so everything after sees the scheme and client it saw; then problems, so
        // it sees every exception; then HTTPS, so nothing is answered over HTTP; then the app's own
        // middleware; then the policies' (CORS, authentication, authorization, rate limits, caching), which must come
        // before the endpoints they guard.
        List<StatementSyntax> pipeline = [.. forwarded, .. problems, .. https, .. middleware, .. policyMiddleware, .. mapping, .. endpoints, .. host.Built];
        return p.Template.Declarations($$"""
            fn $name({{(parameters.Count == 0 ? "" : "$parameters: unit")}}) => Microsoft.AspNetCore.Builder.WebApplication {
                let builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
                $building;
                let app = builder.Build();
                Piper.Hosting.PiperHost.UseLogging(app.Services);
                $pipeline;
                app
            }
            """, [("name", name), ("building", host.Building), ("pipeline", pipeline),
                 .. parameters.Count == 0 ? Array.Empty<(string, object)>() : [("parameters", (object)parameters)]]);
    }

    /// <summary>A trusted proxy as <c>forwarded from</c> takes it: an IP address, or a network in CIDR form.</summary>
    private static bool IsAddressOrNetwork(string text) =>
        text.Contains('/') ? System.Net.IPNetwork.TryParse(text, out _) : System.Net.IPAddress.TryParse(text, out _);

    /// <summary>
    /// <c>urls</c> written as string literals (one, or a list), none of them https://: an app redirecting to HTTPS
    /// with nowhere to redirect to. An address from elsewhere (configuration) can't be known, so isn't reported.
    /// </summary>
    private static bool OnlyPlainUrls(ExpressionSyntax urls)
    {
        ImmutableArray<ExpressionSyntax> all = urls is CollectionExpressionSyntax list ? list.Elements : [urls];
        return all.Length > 0 && all.All(url => url is LiteralExpressionSyntax { Value: string text } && !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
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
            if (HostClauses.Handler(p) is { } check)
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
        foreach (var expansion in analysis.Expansions.Where(e => e.Data is ServerData { PlainUrls: not null }))
        {
            var server = (ServerData)expansion.Data!;
            analysis.ReportWarning(expansion.File, server.PlainUrls!.Value,
                $"{server.Name.Text} redirects to HTTPS, but listens only on http:// addresses; add an https:// one to 'urls'.");
        }
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
