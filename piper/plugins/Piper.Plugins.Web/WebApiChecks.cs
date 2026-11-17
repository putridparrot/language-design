using Piper.Binding;
using Piper.Plugins;
using Piper.Syntax;

namespace Piper.Plugins.Web;

/// <summary>
/// <c>endpoints Api: TrackerApi "/api" { … }</c> answers the api plugin's <c>api TrackerApi</c>, whose client calls it.
/// Once the program is type-checked, each of the api's calls must have an endpoint of the same HTTP method and route,
/// whose handler takes the call's values (the same names and types: what the client sends is what the server reads)
/// and answers its type; and each endpoint must be one of the api's calls. So the server changing in a way its clients
/// don't follow, or the api changing in a way the server doesn't, is a compile error, not a failure in production.
/// The api is found by the client class it became: its methods carry Piper.Runtime's <c>[ApiCall(method, route)]</c>.
/// </summary>
public sealed partial class WebPlugin
{
    private sealed record ApiCall(string Method, string Route, MethodSymbol Symbol);

    private static void AnalyzeApis(PluginAnalysis analysis)
    {
        foreach (var expansion in analysis.Expansions.Where(e => e.Data is EndpointsData { Api: not null }))
        {
            var data = (EndpointsData)expansion.Data!;
            var api = data.Api!;
            void Error(Piper.Text.TextSpan span, string message) => analysis.ReportError(expansion.File, span, message);

            var type = analysis.Program.Types.FirstOrDefault(t => t.Name == api.Text);
            var calls = type is null ? [] : CallsOf(analysis.Program, type);
            if (calls.Count == 0)
            {
                Error(api.Span, type is null
                    ? $"There's no api named {api.Text}; declare it with the api plugin: api {api.Text} \"/api\" {{ fn Item(id: int) => Item? = get \"/items/{{id}}\"; }}"
                    : $"{api.Text} isn't an api: declare it with the api plugin's 'api', which a server's endpoints can answer.");
                continue;
            }

            // The handlers as bound, to compare their parameters' and results' types with the calls'.
            var bound = FunctionOf(analysis, expansion) is { } map ? MappedHandlers(map.Body, data) : [];

            var answered = new HashSet<ApiCall>();
            foreach (var (method, route, routeToken, handler) in data.Routes)
            {
                var call = calls.FirstOrDefault(c => c.Method == method && SameRoute(c.Route, route));
                if (call is null)
                {
                    Error(routeToken.Span, $"'{method} {route}' isn't one of {api.Text}'s calls, so its clients can't call it: add it to {api.Text}, or remove it.");
                    continue;
                }
                answered.Add(call);
                if (!bound.TryGetValue(handler, out var lambda))
                    continue;
                CheckHandler(call, api, routeToken, handler, lambda, Error);
            }

            foreach (var call in calls.Where(c => !answered.Contains(c)))
            {
                Error(api.Span, $"{api.Text}'s call {call.Symbol.Name} ({call.Method} {call.Route}) has no endpoint in {data.Name.Text}, so it would answer 404: " +
                                $"add {call.Method.ToLowerInvariant()} \"{RouteWithin(call.Route, data)}\" to {data.Name.Text}.");
            }
        }
    }

    /// <summary>
    /// Each endpoint's handler as bound, found by its mapping call: <c>MapGet(group, "/issues/{id:int}", handler)</c>'s
    /// method and route are the endpoint's, so two handlers taking the same parameters aren't confused.
    /// </summary>
    private static Dictionary<LambdaExpressionSyntax, BoundLambdaExpression> MappedHandlers(BoundNode body, EndpointsData data)
    {
        var found = new Dictionary<LambdaExpressionSyntax, BoundLambdaExpression>();
        foreach (var call in BoundTreeWalker.DescendantsAndSelf(body).OfType<BoundClrInvocationExpression>().Where(IsMapping))
        {
            var method = call.Member.Name["Map".Length..].ToUpperInvariant();
            var arguments = call.Arguments.SelectMany(BoundTreeWalker.DescendantsAndSelf).ToList();
            if (arguments.OfType<BoundLiteralExpression>().FirstOrDefault(l => l.Value is string) is not { Value: string route } ||
                arguments.OfType<BoundLambdaExpression>().FirstOrDefault() is not { } lambda)
                continue;
            var full = JoinRoute(data.Prefix, route);
            if (data.Routes.FirstOrDefault(r => r.Method == method && r.Route == full) is { Handler: { } handler })
                found.TryAdd(handler, lambda);
        }
        return found;
    }

    /// <summary>The api's calls: the client class's methods marked <c>[ApiCall(method, route)]</c>.</summary>
    private static List<ApiCall> CallsOf(BoundProgram program, TypeSymbol type) =>
        [.. program.Methods
            .Select(m => m.Symbol)
            .OfType<MethodSymbol>()
            .Where(m => ReferenceEquals(m.ContainingType.OriginalDefinition, type.OriginalDefinition))
            .Select(m => m.Attributes.FirstOrDefault(a => a.Type.Name is "ApiCallAttribute" or "ApiCall") is
                { Arguments: [BoundLiteralExpression { Value: string method }, BoundLiteralExpression { Value: string route }] }
                ? new ApiCall(method, route, m)
                : null)
            .OfType<ApiCall>()];

    /// <summary>
    /// The handler takes each value the call sends, by name, as its type; takes nothing else the client would have to
    /// send (a required simple value, or a body); and answers the call's type.
    /// </summary>
    private static void CheckHandler(
        ApiCall call, Token api, Token routeToken, LambdaExpressionSyntax handler, BoundLambdaExpression lambda,
        Action<Piper.Text.TextSpan, string> error)
    {
        var name = $"{api.Text}.{call.Symbol.Name}";
        var parameters = Parameters(handler, lambda).ToList();
        foreach (var sent in call.Symbol.Parameters)
        {
            var taken = parameters.FirstOrDefault(p => p.Parameter.Name.Text.Equals(sent.Name, StringComparison.OrdinalIgnoreCase));
            if (taken.Parameter is null)
                error(routeToken.Span, $"{name} sends '{sent.Name}' ({sent.Type}), which this handler doesn't take: add ({sent.Name}: {sent.Type}).");
            else if (!SameType(taken.Type, sent.Type))
                error(taken.Parameter.Span, $"{name} sends '{sent.Name}' as {sent.Type}, but this handler takes it as {taken.Type}.");
        }
        foreach (var (parameter, type) in parameters)
        {
            if (call.Symbol.Parameters.Any(p => p.Name.Equals(parameter.Name.Text, StringComparison.OrdinalIgnoreCase)) || type.IsOption)
                continue;
            // A service, the user or the request is the server's own; a value or a body would have to come from the client.
            if (IsQueryValue(parameter.Type!) || type.OriginalDefinition is RecordSymbol || (type.OriginalDefinition is EnumSymbol && !type.IsResult))
                error(parameter.Span, $"This handler takes '{parameter.Name.Text}' ({type}), which {name} doesn't send: add it to the call, or make it optional.");
        }

        var expected = Answered(call.Symbol.ReturnType);
        var answers = Unwrapped(lambda.BodyType);
        if (expected is not null && answers != TypeSymbol.Error && !Same(expected.Value, answers))
            error(routeToken.Span, $"{name} answers {Describe(expected.Value)}, but this handler answers {answers}.");
    }

    /// <summary>
    /// What the server answers a call with, from the client method's type, <c>Task&lt;Result&lt;T, ApiError&gt;&gt;</c>
    /// (the handler answers <c>T</c>, an Option included) or <c>Task&lt;Result&lt;T, ApiError&lt;E&gt;&gt;&gt;</c> (it answers
    /// <c>Result&lt;T, E&gt;</c>): the value, and the error type if it's a Result.
    /// </summary>
    private static (TypeSymbol Value, TypeSymbol? Error)? Answered(TypeSymbol returnType)
    {
        if (returnType is not ClrTypeSymbol { TypeArguments: [var result] } || !result.IsResult || result.TypeArguments is not [var value, var failure])
            return null;
        return failure is ClrTypeSymbol { TypeArguments: [var error] } ? (value, error) : (value, null);
    }

    private static bool Same((TypeSymbol Value, TypeSymbol? Error) expected, TypeSymbol answers) =>
        expected.Error is null
            ? SameType(expected.Value, answers)
            : answers.IsResult && answers.TypeArguments is [var value, var error] && SameType(value, expected.Value) && SameType(error, expected.Error);

    private static string Describe((TypeSymbol Value, TypeSymbol? Error) expected) =>
        expected.Error is null ? expected.Value.ToString() : $"Result<{expected.Value}, {expected.Error}>";

    /// <summary>A handler's answer once awaited: its body's type, or a Task's value for a lambda returning a Task itself.</summary>
    private static TypeSymbol Unwrapped(TypeSymbol type) =>
        type is ClrTypeSymbol { TypeArguments: [var value] } clr && clr.Definition.Type == typeof(Task<>) ? value : type;

    /// <summary>The same type: the same symbol, or the same definition with the same type arguments.</summary>
    private static bool SameType(TypeSymbol a, TypeSymbol b) =>
        ReferenceEquals(a, b) ||
        ReferenceEquals(a.OriginalDefinition, b.OriginalDefinition) && a.TypeArguments.Length == b.TypeArguments.Length &&
            !a.TypeArguments.IsEmpty && a.TypeArguments.Zip(b.TypeArguments).All(pair => SameType(pair.First, pair.Second)) ||
        a is ClrTypeSymbol && b is ClrTypeSymbol && a.ToString() == b.ToString();

    /// <summary>Routes that match the same requests: "/items/{id:int}" and "/items/{key}" do.</summary>
    private static bool SameRoute(string a, string b) => RouteShape(a) == RouteShape(b);

    private static string RouteShape(string route) =>
        System.Text.RegularExpressions.Regex.Replace(route.Replace("//", "/").TrimEnd('/'), @"\{[^}]*\}", "{}").ToLowerInvariant();

    /// <summary>A call's full route as an endpoint of <paramref name="data"/> writes it: without the group's prefix.</summary>
    private static string RouteWithin(string route, EndpointsData data) =>
        data.Prefix is { Length: > 0 } prefix && route.StartsWith(prefix.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
            ? "/" + route[prefix.TrimEnd('/').Length..].TrimStart('/')
            : route;

    /// <summary>The prefix and route as one path: "/api" and "/items" are "/api/items".</summary>
    private static string JoinRoute(string? prefix, string route) =>
        string.IsNullOrEmpty(prefix) ? route : prefix.TrimEnd('/') + "/" + route.TrimStart('/');
}
