using System.Collections.Immutable;
using Piper.Binding;
using Piper.Plugins;
using Piper.Syntax;
using Piper.Text;

namespace Piper.Plugins.Api;

/// <summary>
/// The <c>api</c> declaration: an HTTP API's shape (each call's route, parameters and result) and a typed client calling it.
/// <code>
/// api TrackerApi "/api" {
///     fn Projects() => List&lt;Project&gt; = get "/projects";
///     fn Issue(id: IssueId) => IssueView? = get "/issues/{id:int}";
///     fn AddIssue(projectId: ProjectId, issue: NewIssue) => Result&lt;IssueView, IssueError&gt; = post "/projects/{projectId:int}/issues";
/// }
/// </code>
/// becomes <c>class TrackerApi(http: HttpClient)</c>, whose methods send the requests:
/// <c>async fn Issue(id: IssueId) =&gt; Task&lt;Result&lt;IssueView?, ApiError&gt;&gt;</c>. A call's type is what the server's
/// handler answers, and the client reads the answer as Piper.Web answers it: a 404 is an Option's None, and a Result's
/// error is read back from its ProblemDetails (<c>ApiError&lt;IssueError&gt;</c>'s <c>Error</c>). The web plugin checks a
/// server's <c>endpoints Api: TrackerApi</c> against it, so a change to either that the other doesn't follow is a
/// compile error rather than a failure in production. Shared as source (piper.json's <c>"projects"</c>), the api, with
/// the records it names, is the one place both sides' types come from; the client needs no web.
/// </summary>
public sealed class ApiPlugin : IPiperPlugin
{
    private static readonly Dictionary<string, string> Verbs = new()
    {
        ["get"] = "GET",
        ["post"] = "POST",
        ["put"] = "PUT",
        ["delete"] = "DELETE",
        ["patch"] = "PATCH",
    };

    public string Name => "api";

    public IReadOnlyList<PluginKeyword> Keywords { get; } =
    [
        new DeclarationKeyword("api", Api)
        {
            Description = "Declares an HTTP API's calls (`fn Name(…) => Type = get \"/route\";`), as a client: `class Name(http: HttpClient)`, " +
                          "each call an async method giving a Result. A server's `endpoints Name: Api` must answer each one. A string after the name prefixes every route.",
            Snippet = "api ${1:Api} \"${2:/api}\" {\n\tfn ${3:Item}(${4:id: int}) => ${5:Item?} = get \"${6:/items/{id\\}}\";\n\t$0\n}",
            Words = new Dictionary<string, string>
            {
                ["get"] = "The call is a GET request to the route.",
                ["post"] = "The call is a POST request to the route; a parameter that's neither in the route nor a query value is its JSON body.",
                ["put"] = "The call is a PUT request to the route; a parameter that's neither in the route nor a query value is its JSON body.",
                ["delete"] = "The call is a DELETE request to the route.",
                ["patch"] = "The call is a PATCH request to the route; a parameter that's neither in the route nor a query value is its JSON body.",
            },
        },
    ];

    /// <summary>
    /// api  = "api" IDENT [ STRING ] "{" { call } "}" ;
    /// call = "fn" IDENT lambdaParameters "=>" type "=" ( "get" | "post" | "put" | "delete" | "patch" ) STRING ";" ;
    /// </summary>
    private static IEnumerable<DeclarationSyntax> Api(PluginParser p)
    {
        var name = p.Expect(TokenKind.Identifier);
        var prefix = "";
        if (p.Current.Kind is TokenKind.StringLiteral or TokenKind.InterpolatedStringLiteral)
            prefix = Route(p, p.NextToken());
        p.Expect(TokenKind.OpenBrace);

        var http = p.FreshName("http");
        var holes = new List<(string, object)> { ("name", name), ("http", http) };
        var methods = new List<string>();
        var names = new HashSet<string>();
        var routes = new HashSet<string>();
        while (p.Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile))
        {
            if (p.Current.Kind != TokenKind.FnKeyword)
            {
                p.Report(p.Current.Span, "Expected a call, such as fn Item(id: int) => Item? = get \"/items/{id}\";");
                Skip(p);
                continue;
            }
            p.NextToken();
            var callName = p.Expect(TokenKind.Identifier);
            if (callName.Text.Length > 0 && !names.Add(callName.Text))
                p.Report(callName.Span, $"There's already a call named '{callName.Text}' in {name.Text}.");
            if (p.Current.Kind != TokenKind.OpenParen)
            {
                p.Report(p.Current.Span, $"Expected {callName.Text}'s parameters, such as (id: int), or ().");
                Skip(p);
                continue;
            }
            var parameters = p.ParseLambdaParameters();
            foreach (var parameter in parameters.Where(parameter => parameter.Type is null))
                p.Report(parameter.Span, $"Give '{parameter.Name.Text}' a type, such as ({parameter.Name.Text}: int); it's what the call sends.");
            p.Expect(TokenKind.FatArrow);
            var result = p.ParseType();
            p.Expect(TokenKind.Equals);
            var verb = p.Current;
            if (verb.Kind != TokenKind.Identifier || !Verbs.TryGetValue(verb.Text, out var method))
            {
                p.Report(verb.Span, "Expected 'get', 'post', 'put', 'delete' or 'patch', and the call's route.");
                Skip(p);
                continue;
            }
            p.ReadWord();
            if (p.Current.Kind is not (TokenKind.StringLiteral or TokenKind.InterpolatedStringLiteral))
            {
                p.Report(p.Current.Span, "Expected the call's route, such as \"/items/{id}\".");
                Skip(p);
                continue;
            }
            var routeToken = p.NextToken();
            var route = Route(p, routeToken);
            p.Expect(TokenKind.Semicolon);
            var full = Join(prefix, route);
            if (!routes.Add($"{method} {Normalized(full)}"))
                p.Report(routeToken.Span, $"'{verb.Text} {full}' is already a call of {name.Text}.");
            if (parameters.Any(parameter => parameter.Type is null))
                continue;
            if (Call(p, callName, parameters, result, method, full, routeToken, holes) is { } code)
                methods.Add(code);
        }
        p.Expect(TokenKind.CloseBrace);
        p.Data = name;

        return p.Template.Declarations(
            $"class $name($http: System.Net.Http.HttpClient) {{\n{string.Join("\n", methods)}\n}}", [.. holes]);
    }

    /// <summary>
    /// Checks each api's shape (its calls, and the types they send and answer) against the one recorded beside it,
    /// <c>Name.shape.json</c>, so a change that breaks clients already built is an error (see <see cref="WireShapes"/>).
    /// </summary>
    public void Analyze(PluginAnalysis analysis)
    {
        foreach (var expansion in analysis.Expansions)
        {
            if (expansion.Data is not Token name ||
                analysis.Program.Types.OfType<ClassSymbol>().FirstOrDefault(c => expansion.Result.Any(r => ReferenceEquals(r, c.Declaration))) is not { } client)
                continue;
            var operations = analysis.Program.Methods
                .Select(m => m.Symbol)
                .OfType<MethodSymbol>()
                .Where(m => ReferenceEquals(m.ContainingType.OriginalDefinition, client))
                .Select(Operation)
                .OfType<WireOperation>()
                .ToList();
            analysis.CheckWireShape(expansion.File, name.Span, name.Text, operations);
        }
    }

    /// <summary>
    /// A client method as what crosses the wire: <c>GET /api/issues/{int}</c>, its route values by position (their names
    /// aren't sent) and its other values by name, and what it answers, from its type,
    /// <c>Task&lt;Result&lt;T, ApiError&gt;&gt;</c> (<c>T</c>) or <c>Task&lt;Result&lt;T, ApiError&lt;E&gt;&gt;&gt;</c> (<c>Result&lt;T, E&gt;</c>).
    /// </summary>
    private static WireOperation? Operation(MethodSymbol method)
    {
        if (method.Attributes.FirstOrDefault(a => a.Type.Name is "ApiCallAttribute" or "ApiCall") is not
                { Arguments: [BoundLiteralExpression { Value: string verb }, BoundLiteralExpression { Value: string route }] } ||
            method.ReturnType.TypeArguments is not [var result] || result.TypeArguments is not [var value, var failure])
            return null;

        var inputs = new List<(string, TypeSymbol)>();
        var shape = new System.Text.StringBuilder();
        int at = 0, position = 0;
        for (int open = route.IndexOf('{'); open >= 0; open = route.IndexOf('{', at))
        {
            int close = route.IndexOf('}', open);
            if (close < 0)
                break;
            shape.Append(route, at, open - at);
            at = close + 1;
            var inside = route[(open + 1)..close];
            var valueName = inside.TrimStart('*').Split(':', '=', '?')[0];
            var constraint = inside.Contains(':') ? inside[(inside.IndexOf(':') + 1)..] : "";
            shape.Append('{').Append(inside.StartsWith('*') ? "*" : "").Append(constraint).Append('}');
            position++;
            if (method.Parameters.FirstOrDefault(p => p.Name.Equals(valueName, StringComparison.OrdinalIgnoreCase)) is { } parameter)
                inputs.Add(($"{{{position}}}", parameter.Type));
        }
        shape.Append(route, at, route.Length - at);
        var routeValues = new HashSet<string>(RouteValueNames(route), StringComparer.OrdinalIgnoreCase);
        inputs.AddRange(method.Parameters.Where(p => !routeValues.Contains(p.Name)).Select(p => (p.Name, p.Type)));

        var error = failure.TypeArguments is [var e] ? e : null;
        return new WireOperation(
            $"{verb} {shape}", method.Name, [.. inputs],
            error is null ? value.ToString() : $"Result<{value}, {error}>",
            error is null ? [value] : [value, error]);
    }

    private static IEnumerable<string> RouteValueNames(string route)
    {
        for (int open = route.IndexOf('{'); open >= 0; open = route.IndexOf('{', open + 1))
        {
            int close = route.IndexOf('}', open);
            if (close < 0)
                yield break;
            yield return route[(open + 1)..close].TrimStart('*').Split(':', '=', '?')[0];
        }
    }

    /// <summary>
    /// <c>[ApiCall("GET", "/api/issues/{id:int}")] async fn Issue(id: IssueId) =&gt; Task&lt;Result&lt;IssueView?, ApiError&gt;&gt; =
    /// await PiperClient.SendOptional&lt;IssueView?&gt;(http, "GET", PiperRoutes.Path("/api/issues/", PiperRoutes.Segment(id)), None);</c>.
    /// Route values fill the route; simple and optional values are the query; any other value is the JSON body.
    /// </summary>
    private static string? Call(
        PluginParser p, Token name, IReadOnlyList<LambdaParameterSyntax> parameters, TypeSyntax result, string method, string route,
        Token routeToken, List<(string, object)> holes)
    {
        string Hole(object value)
        {
            var hole = $"h{holes.Count}";
            holes.Add((hole, value));
            return "$" + hole;
        }
        string Text(string text) =>
            Hole(new LiteralExpressionSyntax(routeToken.Span, new Token(TokenKind.StringLiteral, routeToken.Span, text, text) { IsGenerated = true }, text));

        // The path: the route's text, with each {value} filled.
        var pieces = new List<string>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int at = 0;
        for (int open = route.IndexOf('{'); open >= 0; open = route.IndexOf('{', at))
        {
            int close = route.IndexOf('}', open);
            if (close < 0)
                break;
            if (open > at)
                pieces.Add(Text(route[at..open]));
            at = close + 1;
            var inside = route[(open + 1)..close];
            var valueName = inside.TrimStart('*').Split(':', '=', '?')[0];
            if (parameters.FirstOrDefault(q => q.Name.Text.Equals(valueName, StringComparison.OrdinalIgnoreCase)) is not { } parameter)
            {
                p.Report(routeToken.Span, $"The route value '{{{valueName}}}' isn't one of {name.Text}'s parameters; add ({valueName}: string) or another type.");
                return null;
            }
            taken.Add(parameter.Name.Text);
            pieces.Add(inside.StartsWith('*')
                ? $"Piper.Runtime.PiperRoutes.CatchAll({Hole(parameter.Name)})"
                : $"Piper.Runtime.PiperRoutes.Segment({Hole(parameter.Name)})");
        }
        if (at < route.Length || pieces.Count == 0)
            pieces.Add(Text(at < route.Length ? route[at..] : "/"));
        var path = $"Piper.Runtime.PiperRoutes.Path({string.Join(", ", pieces)})";

        // The query (simple or optional values), then the body (anything else), as ASP.NET Core binds them.
        string? body = null;
        foreach (var parameter in parameters.Where(q => !taken.Contains(q.Name.Text)))
        {
            if (IsQueryValue(parameter.Type!))
            {
                path = $"Piper.Runtime.PiperRoutes.WithQuery({path}, Piper.Runtime.PiperRoutes.Query({Text(parameter.Name.Text)}, {Hole(parameter.Name)}))";
                continue;
            }
            if (body is not null)
            {
                p.Report(parameter.Span, $"{name.Text} already sends a body; a request has one, so '{parameter.Name.Text}' must be a route or query value (a simple type, or optional).");
                return null;
            }
            if (method is "GET" or "DELETE")
            {
                p.Report(parameter.Span, $"A {method} request has no body, so '{parameter.Name.Text}' must be a route or query value (a simple type, or optional).");
                return null;
            }
            body = $"Some(Piper.Runtime.PiperClient.Body({Hole(parameter.Name)}))";
        }

        // How the answer is read, from what the handler answers.
        string type, send;
        switch (result)
        {
            case OptionalTypeSyntax:
                type = $"Result<{Hole(result)}, Piper.Runtime.ApiError>";
                send = $"Piper.Runtime.PiperClient.SendOptional<{Hole(result)}>";
                break;
            case NamedTypeSyntax { Qualifier.IsEmpty: true, Name.Text: "Result", TypeArguments: [var value, var error] }:
                if (value is OptionalTypeSyntax)
                {
                    p.Report(result.Span, $"A call answering Result<{value}, …> isn't supported yet: answer the value, and an error when it's missing.");
                    return null;
                }
                type = $"Result<{Hole(value)}, Piper.Runtime.ApiError<{Hole(error)}>>";
                send = $"Piper.Runtime.PiperClient.SendResult<{Hole(value)}, {Hole(error)}>";
                break;
            case NamedTypeSyntax { Qualifier.IsEmpty: true, Name.Text: "unit", TypeArguments.IsEmpty: true }:
                p.Report(result.Span, "A call answering nothing isn't supported yet; answer a value, such as the thing changed.");
                return null;
            default:
                type = $"Result<{Hole(result)}, Piper.Runtime.ApiError>";
                send = $"Piper.Runtime.PiperClient.Send<{Hole(result)}>";
                break;
        }

        var signature = string.Join(", ", parameters.Select(q => $"{Hole(q.Name)}: {Hole(q.Type!)}"));
        return $"[Piper.Runtime.ApiCall({Text(method)}, {Text(route)})]\n" +
               $"async fn {Hole(name)}({signature}) => System.Threading.Tasks.Task<{type}> =\n" +
               $"    await {send}($http, {Text(method)}, {path}, {body ?? "None"});";
    }

    private static readonly HashSet<string> SimpleTypes =
    [
        "string", "int", "long", "short", "byte", "sbyte", "uint", "ulong", "ushort", "decimal", "double", "float", "bool",
        "char", "DateTime", "DateTimeOffset", "DateOnly", "TimeOnly", "TimeSpan", "Guid",
    ];

    /// <summary>
    /// Whether a value that isn't in the route goes in the query, as ASP.NET Core binds a handler's parameter: a simple
    /// builtin type, or anything optional (the web plugin's rule, so the client sends what the server reads).
    /// </summary>
    private static bool IsQueryValue(TypeSyntax type) =>
        type is OptionalTypeSyntax ||
        type is NamedTypeSyntax { Qualifier.IsEmpty: true, TypeArguments.IsEmpty: true, Name.Text: var name } && SimpleTypes.Contains(name);

    /// <summary>The prefix and route as one path: "/api" and "/items" are "/api/items".</summary>
    private static string Join(string prefix, string route) =>
        prefix.Length == 0 ? route : prefix.TrimEnd('/') + "/" + route.TrimStart('/');

    /// <summary>A route without its values' names and constraints, so "/items/{id:int}" and "/items/{key}" are the same.</summary>
    private static string Normalized(string route) =>
        System.Text.RegularExpressions.Regex.Replace(route.TrimEnd('/'), @"\{[^}]*\}", "{}").ToLowerInvariant();

    private static string Route(PluginParser p, Token token)
    {
        if (token.Kind == TokenKind.StringLiteral)
            return (string)token.Value!;
        p.Report(token.Span, "A route is a plain string, \"/items/{id}\", whose braces are route values; drop the '$'.");
        return token.Text.TrimStart('$').Trim('"');
    }

    /// <summary>Skips to the end of a call after a mistake: past its ';', or to the api's closing brace.</summary>
    private static void Skip(PluginParser p)
    {
        while (p.Current.Kind is not (TokenKind.EndOfFile or TokenKind.CloseBrace))
        {
            if (p.NextToken().Kind == TokenKind.Semicolon)
                return;
        }
    }
}
