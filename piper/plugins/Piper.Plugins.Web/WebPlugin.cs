using System.Collections.Immutable;
using Piper.Plugins;
using Piper.Syntax;
using Piper.Text;

namespace Piper.Plugins.Web;

/// <summary>
/// Adds a declaration for ASP.NET Core, rewritten into a function of ordinary Piper:
/// <c>endpoints Name "/api" { get "/users/{id}" (id: int) => …; … }</c> maps a minimal API, as
/// <c>fn Name(app: IEndpointRouteBuilder)</c>. It needs ASP.NET Core, which the plugin asks for, so a program using it
/// needn't. It also checks what ASP.NET Core would only find when the program runs: a route mapped twice, a route
/// parameter that no handler parameter takes, and a handler parameter without a type. Dependency injection is the
/// services plugin's (plugins/Piper.Plugins.Services), which works without the web.
/// </summary>
public sealed partial class WebPlugin : IPiperPlugin
{
    // The extension methods are called through their classes, so the code works whatever the user's file imports.
    private const string Routing = "Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions";

    private static readonly Dictionary<string, string> Verbs = new()
    {
        ["get"] = "MapGet",
        ["post"] = "MapPost",
        ["put"] = "MapPut",
        ["delete"] = "MapDelete",
        ["patch"] = "MapPatch",
    };

    public string Name => "web";

    public IReadOnlyList<string> Frameworks => ["Microsoft.AspNetCore.App"];

    /// <summary>Piper.Web, answering handlers' Options and Results, and holding <c>[Status]</c>.</summary>
    public IReadOnlyList<string> References => ["Piper.Web.dll"];

    private const string Responses = "Piper.Web.PiperResponses";

    public IReadOnlyList<PluginKeyword> Keywords { get; } =
    [
        new DeclarationKeyword("endpoints", Endpoints)
        {
            Description = "Declares an ASP.NET Core minimal API, as `fn Name(app: IEndpointRouteBuilder)`; call it with the app to map the routes. " +
                          "A string after the name prefixes every route.",
            Snippet = "endpoints ${1:Api} \"${2:/api}\" {\n\tget \"${3:/items/{id\\}}\" (${4:id: int}) => ${5:id};\n\t$0\n}",
            Words = new Dictionary<string, string>
            {
                ["get"] = "Maps a GET request to the route: `MapGet`.",
                ["post"] = "Maps a POST request to the route: `MapPost`.",
                ["put"] = "Maps a PUT request to the route: `MapPut`.",
                ["delete"] = "Maps a DELETE request to the route: `MapDelete`.",
                ["patch"] = "Maps a PATCH request to the route: `MapPatch`.",
                ["async"] = "The handler awaits, so it runs as an async lambda.",
                ["layout"] = "The function, `fn Page(body: Html) => Html`, putting each endpoint's markup in a page, unless htmx asked for just the fragment.",
                ["authorize"] = "Only signed-in users may call it, or with names, only those the authorization policies named allow: `RequireAuthorization`. After the group's route it applies to every endpoint.",
                ["anonymous"] = "Anyone may call it, even in a group needing authorization: `AllowAnonymous`.",
                ["ratelimit"] = "Calls are limited by the rate limit policy named, answering 429 when it's exceeded: `RequireRateLimiting`. `ratelimit off` turns a group's off: `DisableRateLimiting`.",
                ["off"] = "No rate limiting here, whatever the group says: `DisableRateLimiting`.",
                ["cors"] = "Browsers on other sites may call it as the CORS policy named allows: `RequireCors`.",
                ["cache"] = "Responses are cached, by the output cache policy named or the default one: `CacheOutput`.",
            },
        },
        new DeclarationKeyword("policies", Policies)
        {
            Description = "Declares the authorization, rate limit, CORS and output cache policies that endpoints name, as " +
                          "`fn Name(services: IServiceCollection)`, registering them, and `fn UseName(app: IApplicationBuilder)`, adding the middleware they need.",
            Snippet = "policies ${1:Security} {\n\tauthorize \"${2:admin}\" roles \"${3:Admin}\";\n\tratelimit \"${4:api}\" fixed ${5:100} per minute;\n\t$0\n}",
            Words = new Dictionary<string, string>
            {
                ["authorize"] = "An authorization policy: signed-in users, with `roles` or a `claim` if given.",
                ["roles"] = "The user must be in one of these roles: `RequireRole`.",
                ["claim"] = "The user must have this claim, with one of these values if given: `RequireClaim`.",
                ["ratelimit"] = "A rate limit policy; calls over the limit are answered 429.",
                ["fixed"] = "At most this many calls in each window of time: `AddFixedWindowLimiter`.",
                ["sliding"] = "At most this many calls in any window, counted in segments: `AddSlidingWindowLimiter`.",
                ["segments"] = "How many segments the sliding window is counted in.",
                ["bucket"] = "A bucket of this many tokens, one per call, refilled over time: `AddTokenBucketLimiter`.",
                ["refill"] = "How many tokens are put back each period.",
                ["concurrent"] = "At most this many calls at once: `AddConcurrencyLimiter`.",
                ["per"] = "The window or period: `per minute`, `per 10 seconds`.",
                ["cors"] = "A CORS policy: which other sites' pages may call the endpoints.",
                ["origins"] = "The sites allowed, such as \"https://partner.example\", with any header and method.",
                ["cache"] = "An output cache policy.",
                ["expire"] = "How long a cached response is used: `expire 30 seconds`.",
            },
        },
    ];

    /// <summary>
    /// endpoints = "endpoints" IDENT [ STRING ] [ "layout" IDENT ] { policyWord } "{" { endpoint } "}" ;
    /// endpoint  = ( "get" | "post" | "put" | "delete" | "patch" ) [ IDENT ] STRING [ "async" ] lambdaParameters "=>" ( block [ ";" ] | expression ";" ) ;
    /// A named endpoint (<c>get Lot "/lots/{id:int}" …</c>) has a route function in <c>&lt;Name&gt;Routes</c>, which links to it.
    /// Routes are written as ASP.NET Core documents them: <c>"/users/{id}"</c> is a route parameter, not interpolation.
    /// </summary>
    private static IEnumerable<DeclarationSyntax> Endpoints(PluginParser p)
    {
        var name = p.Expect(TokenKind.Identifier);
        var prefix = p.Current.Kind is TokenKind.StringLiteral or TokenKind.InterpolatedStringLiteral ? Route(p, p.NextToken()) : null;
        // `layout Page`: the function putting each endpoint's markup in a page, unless htmx asked for the fragment.
        Token? ReadLayout()
        {
            if (!p.AtWord("layout"))
                return null;
            p.ReadWord();
            return p.Expect(TokenKind.Identifier);
        }
        var layout = ReadLayout();
        // `authorize "admin" ratelimit "api"` after the group's route: for each endpoint, unless it says otherwise.
        var groupWords = ReadPolicyWords(p);
        layout ??= ReadLayout();
        var policyUses = new List<PolicyName>(NamesUsed(groupWords.Values));
        var policyWords = groupWords.Values.Select(w => w.Word).ToList();
        p.Expect(TokenKind.OpenBrace);

        var router = prefix is null ? null : p.FreshName("group");
        var mappings = new List<StatementSyntax>();
        var mapped = new HashSet<string>();
        var routeNames = new HashSet<string>();
        var routeFunctions = new List<string>();
        var routeHoles = new List<(string, object)>();
        while (p.Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile))
        {
            var verb = p.Current;
            if (verb.Kind != TokenKind.Identifier || !Verbs.TryGetValue(verb.Text, out var method))
            {
                p.Report(verb.Span, "Expected 'get', 'post', 'put', 'delete' or 'patch'.");
                SkipClause(p);
                continue;
            }
            p.ReadWord();
            // `get Lot "/lots/{id:int}" …`: a name, for the route function linking to it (SiteRoutes.Lot(id)).
            var endpointName = p.Current.Kind == TokenKind.Identifier ? p.NextToken() : null;
            if (endpointName is not null && !routeNames.Add(endpointName.Text))
                p.Report(endpointName.Span, $"There's already an endpoint named '{endpointName.Text}' in {name.Text}.");

            if (p.Current.Kind is not (TokenKind.StringLiteral or TokenKind.InterpolatedStringLiteral))
            {
                p.Report(p.Current.Span, "Expected the endpoint's route, such as \"/users/{id}\".");
                SkipClause(p);
                continue;
            }
            var routeToken = p.NextToken();
            var route = Route(p, routeToken);
            if (!mapped.Add($"{verb.Text} {route.Value}"))
                p.Report(routeToken.Span, $"'{verb.Text} {route.Value}' is already mapped.");

            // The endpoint's own words replace the group's for the same concern.
            var words = ReadPolicyWords(p);
            policyUses.AddRange(NamesUsed(words.Values));
            policyWords.AddRange(words.Values.Select(w => w.Word));
            foreach (var (concern, word) in groupWords)
                words.TryAdd(concern, word);

            var handler = Handler(p);
            if (handler is null)
            {
                SkipClause(p);
                continue;
            }
            // An untyped parameter is reported; mapping the handler anyway would only add confusing errors.
            if (handler.Parameters.Any(parameter => parameter.Type is null))
                continue;
            CheckRoute(p, routeToken, (string)route.Value!, handler);
            if (endpointName is not null)
                routeFunctions.Add(RouteFunction(endpointName, (string?)prefix?.Value + (string)route.Value!, routeToken.Span, handler, routeHoles));

            // A handler returning an Option or Result is answered as one: 200 or 404, or 200 or ProblemDetails.
            var endpoint = layout is null
                ? p.Template.Expression($"{Responses}.Answer({Routing}.{method}($router, $route, $handler))",
                    ("router", (object?)router ?? "app"), ("route", route), ("handler", handler))
                : p.Template.Expression($"{Responses}.Answer({Routing}.{method}($router, $route, $handler), $layout)",
                    ("router", (object?)router ?? "app"), ("route", route), ("handler", handler), ("layout", layout));
            mappings.Add(new ExpressionStatementSyntax(endpoint.Span, ApplyPolicies(p, endpoint, words.Values)));
        }
        p.Expect(TokenKind.CloseBrace);
        p.Data = new EndpointsData([.. policyUses], [.. policyWords]);

        if (router is not null)
            mappings.Insert(0, p.Template.Statement($"let $group = {Routing}.MapGroup(app, $prefix);", ("group", router), ("prefix", prefix!)));
        // htmx, which <Htmx.Script /> puts in pages, served by the app (mapped once however many groups there are).
        mappings.Insert(0, p.Template.Statement("Piper.Web.Htmx.MapScript(app);"));
        var map = p.Template.Declarations(
            "fn $name(app: Microsoft.AspNetCore.Routing.IEndpointRouteBuilder) { $mappings }",
            ("name", name), ("mappings", mappings));
        if (routeFunctions.Count == 0)
            return map;

        // `endpoints Site` with named endpoints: `class SiteRoutes { static fn Lot(id: LotId) => string = …; }`.
        var routes = new Token(TokenKind.Identifier, name.Span, name.Text + "Routes");
        return map.AddRange(p.Template.Declarations(
            $"class $routes {{\n{string.Join("\n", routeFunctions)}\n}}", [("routes", routes), .. routeHoles]));
    }

    /// <summary>
    /// <c>static fn Lot(id: LotId) =&gt; string = PiperRoutes.Path("/lots/", PiperRoutes.Segment(id));</c> for a named
    /// endpoint: the route's path (its group's prefix and its own), with a parameter for each route value, of the type
    /// the handler takes it as, so a link of the wrong type, or to a route that isn't there, doesn't compile.
    /// </summary>
    private static string RouteFunction(Token name, string route, TextSpan span, LambdaExpressionSyntax handler, List<(string, object)> holes)
    {
        string Hole(object value)
        {
            var hole = $"r{holes.Count}";
            holes.Add((hole, value));
            return "$" + hole;
        }
        LiteralExpressionSyntax Text(string text) => new(span, new Token(TokenKind.StringLiteral, span, text, text) { IsGenerated = true }, text);

        var path = route.Replace("//", "/");
        var parameters = new List<string>();
        var pieces = new List<string>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int at = 0;
        for (int open = path.IndexOf('{'); open >= 0; open = path.IndexOf('{', at))
        {
            int close = path.IndexOf('}', open);
            if (close < 0)
                break;
            if (open > at)
                pieces.Add(Hole(Text(path[at..open])));
            at = close + 1;
            var inside = path[(open + 1)..close];
            var parameterName = inside.TrimStart('*').Split(':', '=', '?')[0];
            if (handler.Parameters.FirstOrDefault(p => p.Name.Text.Equals(parameterName, StringComparison.OrdinalIgnoreCase)) is not { Type: { } type } parameter)
                continue;   // reported by CheckRoute
            var reference = Hole(parameter.Name);
            if (taken.Add(parameter.Name.Text))
                parameters.Add($"{reference}: {Hole(type)}");
            pieces.Add(inside.StartsWith('*') ? $"Piper.Runtime.PiperRoutes.CatchAll({reference})" : $"Piper.Runtime.PiperRoutes.Segment({reference})");
        }
        if (at < path.Length || pieces.Count == 0)
            pieces.Add(Hole(Text(at < path.Length ? path[at..] : "/")));
        return $"static fn {Hole(name)}({string.Join(", ", parameters)}) => string = Piper.Runtime.PiperRoutes.Path({string.Join(", ", pieces)});";
    }

    /// <summary>
    /// A handler: <c>(id: int) => users.Find(id);</c>, <c>async (id: int) => { … }</c>. Its parameters need types, since
    /// ASP.NET Core takes it as a plain Delegate and reads them to bind the request. Null after a syntax error.
    /// </summary>
    private static LambdaExpressionSyntax? Handler(PluginParser p)
    {
        var async = p.AtWord("async") ? p.ReadWord() : null;
        if (p.Current.Kind is not (TokenKind.OpenParen or TokenKind.Identifier))
        {
            p.Report(p.Current.Span, "Expected the endpoint's handler, such as (id: int) => …;");
            return null;
        }
        var start = p.Current.Span.Start;
        var parameters = p.ParseLambdaParameters();
        foreach (var parameter in parameters.Where(parameter => parameter.Type is null))
            p.Report(parameter.Span, $"Give '{parameter.Name.Text}' a type, such as ({parameter.Name.Text}: int); ASP.NET Core reads it to bind the request.");
        p.Expect(TokenKind.FatArrow);

        ExpressionSyntax body;
        if (p.Current.Kind == TokenKind.OpenBrace)
        {
            body = p.ParseBlock();
            if (p.Current.Kind == TokenKind.Semicolon)
                p.NextToken();
        }
        else
        {
            body = p.ParseExpression();
            p.Expect(TokenKind.Semicolon);
        }
        // An optional parameter (`name: string?`) is None in a request without it, rather than a 400.
        return new LambdaExpressionSyntax(TextSpan.FromBounds(async?.Span.Start ?? start, body.Span.End), parameters, body)
        {
            Async = async,
            OptionsMayBeLeftOut = true,
        };
    }

    /// <summary>Reports a route parameter (<c>{id}</c>, <c>{id:int}</c>, <c>{*path}</c>) that no handler parameter takes.</summary>
    private static void CheckRoute(PluginParser p, Token routeToken, string route, LambdaExpressionSyntax handler)
    {
        var names = handler.Parameters.Select(parameter => parameter.Name.Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int open = route.IndexOf('{'); open >= 0; open = route.IndexOf('{', open + 1))
        {
            int close = route.IndexOf('}', open);
            if (close < 0)
                break;
            var parameter = route[(open + 1)..close].TrimStart('*').Split(':', '=', '?')[0];
            if (parameter.Length > 0 && !names.Contains(parameter))
                p.Report(routeToken.Span, $"The route parameter '{{{parameter}}}' isn't one of the handler's parameters; add ({parameter}: string) or another type.");
        }
    }

    /// <summary>
    /// A route, as a plain string: <c>"/users/{id}"</c>, whose braces are ASP.NET Core's route parameters. A
    /// <c>$"…"</c> route is reported, as its holes would be read as Piper rather than as parameters.
    /// </summary>
    private static LiteralExpressionSyntax Route(PluginParser p, Token token)
    {
        if (token.Kind == TokenKind.StringLiteral)
            return new LiteralExpressionSyntax(token.Span, token, token.Value);

        p.Report(token.Span, "A route is a plain string, \"/users/{id}\", whose braces are route parameters; drop the '$'.");
        var value = token.Text.TrimStart('$').Trim('"');
        return new LiteralExpressionSyntax(token.Span, new Token(TokenKind.StringLiteral, token.Span, token.Text, value), value);
    }

    /// <summary>Skips to the end of a clause that can't be read: past its `;`, or up to the block's `}`.</summary>
    private static void SkipClause(PluginParser p)
    {
        int depth = 0;
        while (p.Current.Kind != TokenKind.EndOfFile)
        {
            var token = p.Current;
            if (token.Kind == TokenKind.CloseBrace && depth == 0)
                return;
            p.NextToken();
            if (token.Kind == TokenKind.OpenBrace)
                depth++;
            else if (token.Kind == TokenKind.CloseBrace)
                depth--;
            else if (token.Kind == TokenKind.Semicolon && depth == 0)
                return;
        }
    }
}
