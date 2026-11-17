using System.Collections.Immutable;
using Piper.Plugins;
using Piper.Syntax;
using static Piper.Plugins.Shared.HostClauses;

namespace Piper.Plugins.Web;

/// <summary>
/// Who may call an endpoint and how often: the policy words on endpoints and groups (<c>authorize "admin"</c>,
/// <c>anonymous</c>, <c>ratelimit "api"</c>, <c>cors "partners"</c>, <c>cache "short"</c>), the <c>policies</c>
/// declaration defining them, and the analysis checking the two agree.
/// </summary>
public sealed partial class WebPlugin
{
    private const string AuthorizationConventions = "Microsoft.AspNetCore.Builder.AuthorizationEndpointConventionBuilderExtensions";
    private const string RateLimitConventions = "Microsoft.AspNetCore.Builder.RateLimiterEndpointConventionBuilderExtensions";
    private const string CorsConventions = "Microsoft.AspNetCore.Builder.CorsEndpointConventionBuilderExtensions";
    private const string CacheConventions = "Microsoft.Extensions.DependencyInjection.OutputCacheConventionBuilderExtensions";
    private const string OpenApiConventions = "Microsoft.AspNetCore.Http.OpenApiRouteHandlerBuilderExtensions";

    /// <summary>What a policy word decides. An endpoint's word replaces its group's for the same concern.</summary>
    private enum Concern
    {
        Access,
        RateLimit,
        Cors,
        Cache,

        /// <summary>OpenAPI's tags: not a policy, so no `policies` block declares them and they need no middleware.</summary>
        Tags,
    }

    private static readonly Dictionary<string, Concern> PolicyWords = new()
    {
        ["authorize"] = Concern.Access,
        ["anonymous"] = Concern.Access,
        ["ratelimit"] = Concern.RateLimit,
        ["cors"] = Concern.Cors,
        ["cache"] = Concern.Cache,
        ["tags"] = Concern.Tags,
    };

    /// <summary>How each concern's policies are described in messages.</summary>
    private static readonly Dictionary<Concern, string> ConcernNames = new()
    {
        [Concern.Access] = "authorization",
        [Concern.RateLimit] = "rate limit",
        [Concern.Cors] = "CORS",
        [Concern.Cache] = "output cache",
        [Concern.Tags] = "OpenAPI tag",
    };

    /// <summary>A policy word and the policy names after it: <c>authorize "admin", "owner"</c>, or <c>ratelimit off</c>.</summary>
    private sealed record PolicyWord(Token Word, Concern Concern, ImmutableArray<LiteralExpressionSyntax> Names, bool Off);

    /// <summary>A named policy, used by an endpoint or declared by <c>policies</c>, for the analysis.</summary>
    private sealed record PolicyName(Concern Concern, string Name, Token Token);

    /// <summary>What an <c>endpoints</c> block noted for the analysis: the named policies it uses, and its policy words.</summary>
    private sealed record EndpointsData(Token Name, ImmutableArray<PolicyName> Uses, ImmutableArray<Token> Words, ImmutableArray<LambdaExpressionSyntax> Handlers)
    {
        /// <summary>The api the endpoints answer (<c>endpoints Api: TrackerApi</c>), checked once types are known (WebApiChecks.cs).</summary>
        public Token? Api { get; init; }

        /// <summary>The group's route prefix, if it has one.</summary>
        public string? Prefix { get; init; }

        /// <summary>Each endpoint: its HTTP method, its full route (the group's prefix and its own), and its handler.</summary>
        public ImmutableArray<(string Method, string Route, Token RouteToken, LambdaExpressionSyntax Handler)> Routes { get; init; } = [];
    }

    /// <summary>What a <c>policies</c> block noted for the analysis: the policies it declares.</summary>
    private sealed record PoliciesData(ImmutableArray<PolicyName> Declared);

    /// <summary>
    /// policies = { policyWord } ;
    /// policyWord = "authorize" [ STRING { "," STRING } ] | "anonymous" | "ratelimit" ( STRING | "off" ) | "cors" STRING | "cache" [ STRING ]
    ///            | "tags" STRING { "," STRING } ;
    /// Two words for one concern are reported: `authorize` with `anonymous` contradict, and a word given twice is a mistake.
    /// </summary>
    private static Dictionary<Concern, PolicyWord> ReadPolicyWords(PluginParser p)
    {
        var words = new Dictionary<Concern, PolicyWord>();
        while (p.Current.Kind == TokenKind.Identifier && PolicyWords.TryGetValue(p.Current.Text, out var concern))
        {
            var word = p.ReadWord();
            var names = ImmutableArray.CreateBuilder<LiteralExpressionSyntax>();
            bool off = false;
            if (word.Text == "ratelimit" && p.AtWord("off"))
            {
                p.ReadWord();
                off = true;
            }
            else if (word.Text != "anonymous" && p.Current.Kind is TokenKind.StringLiteral or TokenKind.InterpolatedStringLiteral)
            {
                names.Add(PolicyNameLiteral(p));
                while (word.Text is "authorize" or "tags" && p.Current.Kind == TokenKind.Comma)
                {
                    p.NextToken();
                    names.Add(PolicyNameLiteral(p));
                }
            }
            else if (word.Text is "ratelimit" or "cors" or "tags")
            {
                // Reported, and left out, as mapping it without a name would only add a confusing error.
                p.Report(p.Current.Span, word.Text switch
                {
                    "ratelimit" => "Expected the rate limit policy's name, such as ratelimit \"api\", or ratelimit off.",
                    "cors" => "Expected the CORS policy's name, such as cors \"partners\".",
                    _ => "Expected the OpenAPI tags, such as tags \"Lots\", \"Bids\".",
                });
                continue;
            }

            if (words.TryGetValue(concern, out var earlier))
            {
                p.Report(word.Span, earlier.Word.Text == word.Text
                    ? $"'{word.Text}' is already given here."
                    : $"'{word.Text}' contradicts '{earlier.Word.Text}': an endpoint either needs authorization or lets anyone call it.");
            }
            else
            {
                words[concern] = new PolicyWord(word, concern, names.ToImmutable(), off);
            }
        }
        return words;
    }

    /// <summary>A policy's name, a plain string; a <c>$"…"</c> one is reported, as policies are named once, as constants.</summary>
    private static LiteralExpressionSyntax PolicyNameLiteral(PluginParser p)
    {
        var token = p.Current;
        if (token.Kind != TokenKind.StringLiteral)
        {
            if (token.Kind == TokenKind.InterpolatedStringLiteral)
            {
                p.NextToken();
                p.Report(token.Span, "A policy's name is a plain string, such as \"admin\".");
                return new LiteralExpressionSyntax(token.Span, token with { Kind = TokenKind.StringLiteral }, token.Text);
            }
            p.Report(token.Span, "Expected a policy's name, such as \"admin\".");
            return new LiteralExpressionSyntax(token.Span, new Token(TokenKind.StringLiteral, token.Span, "\"\"", ""), "");
        }
        p.NextToken();
        return new LiteralExpressionSyntax(token.Span, token, token.Value);
    }

    /// <summary>
    /// <paramref name="endpoint"/>, an endpoint's convention builder, with <paramref name="words"/>' conventions:
    /// <c>RequireAuthorization(endpoint, "admin")</c>, <c>RequireRateLimiting(…, "api")</c> and so on.
    /// </summary>
    private static ExpressionSyntax ApplyPolicies(PluginParser p, ExpressionSyntax endpoint, IEnumerable<PolicyWord> words)
    {
        foreach (var word in words.OrderBy(w => w.Concern))
        {
            var call = word.Word.Text switch
            {
                "authorize" => $"{AuthorizationConventions}.RequireAuthorization",
                "anonymous" => $"{AuthorizationConventions}.AllowAnonymous",
                "ratelimit" when word.Off => $"{RateLimitConventions}.DisableRateLimiting",
                "ratelimit" => $"{RateLimitConventions}.RequireRateLimiting",
                "cors" => $"{CorsConventions}.RequireCors",
                "tags" => $"{OpenApiConventions}.WithTags",
                _ => $"{CacheConventions}.CacheOutput",
            };
            endpoint = p.Template.Expression($"{call}($arguments)", ("arguments", new List<ExpressionSyntax> { endpoint }.Concat(word.Names).ToList()));
        }
        return endpoint;
    }

    /// <summary>The named policies <paramref name="words"/> use, for the analysis.</summary>
    private static IEnumerable<PolicyName> NamesUsed(IEnumerable<PolicyWord> words) =>
        words.Where(w => w.Concern != Concern.Tags).SelectMany(w => w.Names.Select(n => new PolicyName(w.Concern, (string)n.Value!, n.Literal)));

    /// <summary>
    /// policies   = "policies" IDENT "{" { policy } "}" ;
    /// policy     = "authorize" STRING { "roles" STRING { "," STRING } | "claim" STRING [ "=" STRING { "," STRING } ] } ";"
    ///            | "ratelimit" STRING ( "fixed" expr "per" duration | "sliding" expr "per" duration "segments" expr
    ///                                 | "bucket" expr "refill" expr "per" duration | "concurrent" expr ) ";"
    ///            | "cors" STRING "origins" STRING { "," STRING } ";"
    ///            | "cache" STRING "expire" duration ";" ;
    /// duration   = [ expr ] ( "second" | "seconds" | "minute" | "minutes" | "hour" | "hours" | "millisecond" | "milliseconds" ) ;
    /// It becomes <c>fn Name(services)</c>, registering the policies, and <c>fn UseName(app)</c>, adding the middleware
    /// they need, in the order ASP.NET Core needs it.
    /// </summary>
    private static IEnumerable<DeclarationSyntax> Policies(PluginParser p)
    {
        var name = p.Expect(TokenKind.Identifier);
        // `policies Security(settings: Settings)`: what the policies are made from, such as limits from configuration,
        // passed when they're registered: `Security(builder.Services, settings)`.
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

        var authorization = new List<StatementSyntax>();
        var rateLimits = new List<StatementSyntax>();
        var cors = new List<StatementSyntax>();
        var caches = new List<StatementSyntax>();
        var declared = new List<PolicyName>();
        while (p.Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile))
        {
            var word = p.Current;
            if (word.Kind != TokenKind.Identifier || !PolicyWords.TryGetValue(word.Text, out var concern) || word.Text == "anonymous")
            {
                p.Report(word.Span, "Expected 'authorize', 'ratelimit', 'cors' or 'cache', each declaring a policy.");
                SkipClause(p);
                continue;
            }
            p.ReadWord();
            var policy = PolicyNameLiteral(p);
            var policyName = (string)policy.Value!;
            if (declared.Any(d => d.Concern == concern && d.Name == policyName))
                p.Report(policy.Span, $"The {ConcernNames[concern]} policy \"{policyName}\" is already declared.");
            declared.Add(new PolicyName(concern, policyName, policy.Literal));

            switch (concern)
            {
                case Concern.Access:
                    authorization.Add(p.Template.Statement("options.AddPolicy($name, policy => { $requirements });",
                        ("name", policy), ("requirements", AuthorizationRequirements(p))));
                    break;
                case Concern.RateLimit:
                    if (RateLimiter(p, policy) is { } limiter)
                        rateLimits.Add(limiter);
                    break;
                case Concern.Cors:
                    p.ExpectWord("origins");
                    var origins = new List<ExpressionSyntax> { PolicyOrigin(p) };
                    while (p.Current.Kind == TokenKind.Comma)
                    {
                        p.NextToken();
                        origins.Add(PolicyOrigin(p));
                    }
                    cors.Add(p.Template.Statement(
                        "options.AddPolicy($name, policy => { policy.WithOrigins($origins).AllowAnyHeader().AllowAnyMethod(); });",
                        ("name", policy), ("origins", origins)));
                    break;
                case Concern.Cache:
                    p.ExpectWord("expire");
                    caches.Add(p.Template.Statement("options.AddPolicy($name, (policy: Microsoft.AspNetCore.OutputCaching.OutputCachePolicyBuilder) => { policy.Expire($duration); });",
                        ("name", policy), ("duration", Duration(p, countOptional: false))));
                    break;
            }
            p.Expect(TokenKind.Semicolon);
        }
        p.Expect(TokenKind.CloseBrace);
        p.Data = new PoliciesData([.. declared]);

        // Authentication and authorization are always set up: endpoints may `authorize` with the default policy, and
        // a scheme the program adds (cookies, JWT bearer) needs them. The rest only when a policy needs it.
        var registrations = new List<StatementSyntax>
        {
            p.Template.Statement("Microsoft.Extensions.DependencyInjection.AuthenticationServiceCollectionExtensions.AddAuthentication(services);"),
            p.Template.Statement("Microsoft.Extensions.DependencyInjection.PolicyServiceCollectionExtensions.AddAuthorization(services, options => { $policies });",
                ("policies", authorization)),
        };
        var middleware = new List<StatementSyntax>();
        if (cors.Count > 0)
        {
            registrations.Add(p.Template.Statement("Microsoft.Extensions.DependencyInjection.CorsServiceCollectionExtensions.AddCors(services, options => { $policies });", ("policies", cors)));
            middleware.Add(p.Template.Statement("Microsoft.AspNetCore.Builder.CorsMiddlewareExtensions.UseCors(app);"));
        }
        middleware.Add(p.Template.Statement("Microsoft.AspNetCore.Builder.AuthAppBuilderExtensions.UseAuthentication(app);"));
        middleware.Add(p.Template.Statement("Microsoft.AspNetCore.Builder.AuthorizationAppBuilderExtensions.UseAuthorization(app);"));
        if (rateLimits.Count > 0)
        {
            // 429 Too Many Requests, which clients expect, rather than ASP.NET Core's 503.
            registrations.Add(p.Template.Statement("Microsoft.AspNetCore.Builder.RateLimiterServiceCollectionExtensions.AddRateLimiter(services, options => { options.RejectionStatusCode = 429; $limiters });",
                ("limiters", rateLimits)));
            middleware.Add(p.Template.Statement("Microsoft.AspNetCore.Builder.RateLimiterApplicationBuilderExtensions.UseRateLimiter(app);"));
        }
        if (caches.Count > 0)
        {
            registrations.Add(p.Template.Statement("Microsoft.Extensions.DependencyInjection.OutputCacheServiceCollectionExtensions.AddOutputCache(services, options => { $policies });", ("policies", caches)));
            middleware.Add(p.Template.Statement("Microsoft.AspNetCore.Builder.OutputCacheApplicationBuilderExtensions.UseOutputCache(app);"));
        }

        return p.Template.Declarations($$"""
            fn $name(services: Microsoft.Extensions.DependencyInjection.IServiceCollection{{(parameters.Count == 0 ? "" : ", $parameters: unit")}}) => Microsoft.Extensions.DependencyInjection.IServiceCollection {
                $registrations;
                services
            }

            fn $use(app: Microsoft.AspNetCore.Builder.IApplicationBuilder) { $middleware }
            """, [("name", name), ("use", $"Use{name.Text}"), ("registrations", registrations), ("middleware", middleware),
                 .. parameters.Count == 0 ? Array.Empty<(string, object)>() : [("parameters", (object)parameters)]]);
    }

    /// <summary>An authorization policy's requirements: <c>roles "Admin", "Owner"</c>, <c>claim "dept" = "IT"</c>; none is any signed-in user.</summary>
    private static List<StatementSyntax> AuthorizationRequirements(PluginParser p)
    {
        var requirements = new List<StatementSyntax> { p.Template.Statement("policy.RequireAuthenticatedUser();") };
        while (p.AtWord("roles") || p.AtWord("claim"))
        {
            var word = p.ReadWord();
            if (word.Text == "roles")
            {
                var roles = new List<ExpressionSyntax> { PolicyNameLiteral(p) };
                while (p.Current.Kind == TokenKind.Comma)
                {
                    p.NextToken();
                    roles.Add(PolicyNameLiteral(p));
                }
                requirements.Add(p.Template.Statement("policy.RequireRole($roles);", ("roles", roles)));
                continue;
            }
            var claim = new List<ExpressionSyntax> { PolicyNameLiteral(p) };
            if (p.Current.Kind == TokenKind.Equals)
            {
                p.NextToken();
                claim.Add(PolicyNameLiteral(p));
                while (p.Current.Kind == TokenKind.Comma)
                {
                    p.NextToken();
                    claim.Add(PolicyNameLiteral(p));
                }
            }
            requirements.Add(claim.Count == 1
                ? p.Template.Statement("policy.RequireClaim($type);", ("type", claim[0]))
                : p.Template.Statement("policy.RequireClaim($type, $values);", ("type", claim[0]), ("values", claim.Skip(1).ToList())));
        }
        return requirements;
    }

    /// <summary>A rate limiter: <c>fixed 100 per minute</c>, <c>sliding 20 per 10 seconds segments 5</c>, <c>bucket 50 refill 10 per second</c>, <c>concurrent 2</c>.</summary>
    private static StatementSyntax? RateLimiter(PluginParser p, LiteralExpressionSyntax policy)
    {
        const string Limiters = "Microsoft.AspNetCore.RateLimiting.RateLimiterOptionsExtensions";
        if (p.AtWord("fixed"))
        {
            p.ReadWord();
            var permits = p.ParseExpression();
            p.ExpectWord("per");
            return p.Template.Statement($"{Limiters}.AddFixedWindowLimiter(options, $name, limiter => {{ limiter.PermitLimit = $permits; limiter.Window = $window; }});",
                ("name", policy), ("permits", permits), ("window", Duration(p, countOptional: true)));
        }
        if (p.AtWord("sliding"))
        {
            p.ReadWord();
            var permits = p.ParseExpression();
            p.ExpectWord("per");
            var window = Duration(p, countOptional: true);
            p.ExpectWord("segments");
            var segments = p.ParseExpression();
            return p.Template.Statement($"{Limiters}.AddSlidingWindowLimiter(options, $name, limiter => {{ limiter.PermitLimit = $permits; limiter.Window = $window; limiter.SegmentsPerWindow = $segments; }});",
                ("name", policy), ("permits", permits), ("window", window), ("segments", segments));
        }
        if (p.AtWord("bucket"))
        {
            p.ReadWord();
            var tokens = p.ParseExpression();
            p.ExpectWord("refill");
            var refill = p.ParseExpression();
            p.ExpectWord("per");
            return p.Template.Statement($"{Limiters}.AddTokenBucketLimiter(options, $name, limiter => {{ limiter.TokenLimit = $tokens; limiter.TokensPerPeriod = $refill; limiter.ReplenishmentPeriod = $period; }});",
                ("name", policy), ("tokens", tokens), ("refill", refill), ("period", Duration(p, countOptional: true)));
        }
        if (p.AtWord("concurrent"))
        {
            p.ReadWord();
            return p.Template.Statement($"{Limiters}.AddConcurrencyLimiter(options, $name, limiter => {{ limiter.PermitLimit = $permits; }});",
                ("name", policy), ("permits", p.ParseExpression()));
        }
        p.Report(p.Current.Span, "Expected the kind of limit: 'fixed 100 per minute', 'sliding 100 per minute segments 6', 'bucket 50 refill 10 per second' or 'concurrent 4'.");
        SkipToSemicolon(p);
        return null;
    }

    /// <summary>A CORS origin, <c>"https://partner.example"</c>; any string expression works, so configuration can supply it.</summary>
    private static ExpressionSyntax PolicyOrigin(PluginParser p) => p.ParseExpression();

    private static void SkipToSemicolon(PluginParser p)
    {
        while (p.Current.Kind is not (TokenKind.Semicolon or TokenKind.CloseBrace or TokenKind.EndOfFile))
            p.NextToken();
    }

    /// <summary>
    /// Checks that endpoints and <c>policies</c> agree: each named policy an endpoint uses is declared, and each one
    /// declared is used. These are warnings, as a program may register policies in its own code. Endpoints using
    /// policy words in a program without any <c>policies</c> get one warning, about the setup they need.
    /// </summary>
    public void Analyze(PluginAnalysis analysis)
    {
        WarnAboutStatusFields(analysis);
        AnalyzePolicies(analysis);
        AnalyzeServers(analysis);
        AnalyzeServerServices(analysis);
        AnalyzeApis(analysis);
    }

    /// <summary>
    /// An error type, answered as ProblemDetails, with a field named `status` loses it: ProblemDetails has its own status,
    /// the HTTP one, so the field is left out. A warning at the field says so, once for each type.
    /// </summary>
    private static void WarnAboutStatusFields(PluginAnalysis analysis)
    {
        var warned = new HashSet<Piper.Binding.TypeSymbol>();
        foreach (var expansion in analysis.Expansions.Where(e => e.Data is EndpointsData))
        {
            var function = analysis.Program.Functions.FirstOrDefault(f => expansion.Result.Any(r => ReferenceEquals(r, f.Symbol.Declaration)));
            if (function is null)
                continue;
            foreach (var lambda in Piper.Binding.BoundTreeWalker.DescendantsAndSelf(function.Body).OfType<Piper.Binding.BoundLambdaExpression>())
            {
                if (ErrorType(lambda.ReturnType) is not { } error || !warned.Add(error.OriginalDefinition))
                    continue;
                foreach (var (file, field) in StatusFields(error.OriginalDefinition))
                {
                    analysis.ReportWarning(file, field.Span,
                        $"A ProblemDetails has its own 'status', the HTTP one, so this field is left out of the answer. Give it another name, such as 'current'.");
                }
            }
        }
    }

    /// <summary>The E of a handler's <c>Result&lt;T, E&gt;</c>, awaited or not.</summary>
    private static Piper.Binding.TypeSymbol? ErrorType(Piper.Binding.TypeSymbol type)
    {
        if (type is Piper.Binding.ClrTypeSymbol { TypeArguments: [var awaited] } task &&
            task.Type == typeof(Task<>) || type is Piper.Binding.ClrTypeSymbol { TypeArguments: [_] } valueTask && valueTask.Type == typeof(ValueTask<>))
        {
            type = type.TypeArguments[0];
        }
        return type.OriginalDefinition is Piper.Binding.EnumSymbol { Name: "Result" } && type.TypeArguments is [_, var error] ? error : null;
    }

    /// <summary>The fields named `status` of an enum's variants or a record, with the file declaring them.</summary>
    private static IEnumerable<(Piper.Text.SourceText File, Token Field)> StatusFields(Piper.Binding.TypeSymbol type)
    {
        IEnumerable<ParameterSyntax> fields = type switch
        {
            Piper.Binding.EnumSymbol { Declaration: { } declaration } => declaration.Variants.SelectMany(v => v.Fields),
            Piper.Binding.RecordSymbol record => record.Declaration.Fields,
            _ => [],
        };
        if (type.DeclaringFile is not { } file)
            yield break;
        foreach (var field in fields.Where(f => string.Equals(f.Name.Text, "status", StringComparison.OrdinalIgnoreCase)))
            yield return (file, field.Name);
    }

    private static void AnalyzePolicies(PluginAnalysis analysis)
    {
        var uses = analysis.Expansions.Where(e => e.Data is EndpointsData).ToList();
        var declarations = analysis.Expansions.Where(e => e.Data is PoliciesData).ToList();
        if (declarations.Count == 0)
        {
            if (uses.FirstOrDefault(e => !((EndpointsData)e.Data!).Words.IsEmpty) is { } first)
            {
                var word = ((EndpointsData)first.Data!).Words[0];
                analysis.ReportWarning(first.File, word.Span,
                    $"'{word.Text}' needs the app to register and use its middleware: declare the policies in a 'policies' block and call it, or set them up yourself.");
            }
            return;
        }

        var declared = declarations.SelectMany(e => ((PoliciesData)e.Data!).Declared.Select(d => (e.File, Policy: d))).ToList();
        var used = uses.SelectMany(e => ((EndpointsData)e.Data!).Uses.Select(u => (e.File, Policy: u))).ToList();
        foreach (var (file, policy) in used)
        {
            if (!declared.Any(d => d.Policy.Concern == policy.Concern && d.Policy.Name == policy.Name))
                analysis.ReportWarning(file, policy.Token.Span, $"No 'policies' block declares the {ConcernNames[policy.Concern]} policy \"{policy.Name}\".");
        }
        foreach (var (file, policy) in declared)
        {
            if (!used.Any(u => u.Policy.Concern == policy.Concern && u.Policy.Name == policy.Name))
                analysis.ReportWarning(file, policy.Token.Span, $"No endpoint uses the {ConcernNames[policy.Concern]} policy \"{policy.Name}\".");
        }
    }
}
