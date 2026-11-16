using System.Collections.Immutable;
using System.Reflection;
using Piper.Binding;
using Piper.Plugins;
using Piper.Syntax;

namespace Piper.Plugins.Web;

/// <summary>
/// What a <c>server</c> is checked for once the program is type-checked: what ASP.NET Core and dependency injection
/// would only find when a request comes in.
/// <list type="bullet">
/// <item>a handler, health check or <c>stopping</c> hook taking a service of the program's own (a class or trait) that
/// nothing in the server registers: a 500 at the first request, or a failed check;</item>
/// <item>a singleton whose class takes a scoped service (a captive dependency: one request's instance kept for all), or
/// a service nothing registers;</item>
/// <item>endpoints needing a signed-in user (<c>authorize</c>) in a server with no authentication scheme.</item>
/// </list>
/// What the server registers is read from its code and the Piper functions it calls (its <c>services</c> and
/// <c>policies</c> blocks, <c>configure</c>): each generic call given the service collection
/// (<c>AddSingleton&lt;Clock, SystemClock&gt;</c>, <c>AddDbContext&lt;Db&gt;</c>) registers its type arguments. Where a
/// registration could be hidden (a method of the program's own given the services, or one taking a <c>Type</c>), these
/// are warnings rather than errors.
/// </summary>
public sealed partial class WebPlugin
{
    private enum Lifetime { Singleton, Scoped, Transient, Unknown }

    private sealed class Registrations
    {
        public Dictionary<TypeSymbol, Lifetime> Services { get; } = [];

        /// <summary>A singleton's class, made by DI (not a factory), whose constructor's parameters must be services too.</summary>
        public List<(TypeSymbol Service, ClassSymbol Implementation, Lifetime Lifetime)> Made { get; } = [];

        public bool HasAuthenticationScheme { get; set; }

        /// <summary>A registration may be out of sight, so what's missing is a warning, not an error.</summary>
        public bool MayBeHidden { get; set; }
    }

    private static void AnalyzeServerServices(PluginAnalysis analysis)
    {
        var endpoints = analysis.Expansions.Where(e => e.Data is EndpointsData).ToList();
        foreach (var expansion in analysis.Expansions.Where(e => e.Data is ServerData))
        {
            var server = (ServerData)expansion.Data!;
            if (FunctionOf(analysis, expansion) is not { } function)
                continue;
            var registered = new Registrations();
            Collect(analysis.Program, function, registered, []);

            void Report(Piper.Text.SourceText file, Piper.Text.TextSpan span, string message)
            {
                if (registered.MayBeHidden)
                    analysis.ReportWarning(file, span, message);
                else
                    analysis.ReportError(file, span, message);
            }

            // Handlers take services of the program's own (classes, traits) by type; a record is the request's body.
            foreach (var group in server.Endpoints)
            {
                if (endpoints.FirstOrDefault(e => ((EndpointsData)e.Data!).Name.Text == group.Text) is not { } mapped ||
                    FunctionOf(analysis, mapped) is not { } map)
                    continue;
                var data = (EndpointsData)mapped.Data!;
                foreach (var (syntax, lambda) in Pair(data.Handlers, HandlerLambdas(map.Body, IsMapping)))
                {
                    foreach (var (parameter, type) in Parameters(syntax, lambda))
                    {
                        if (IsOwnService(type) && !registered.Services.ContainsKey(type))
                            Report(mapped.File, parameter.Span, Missing(server, type, "this handler takes it"));
                    }
                }
                if (!data.Words.IsEmpty && data.Words.Any(w => w.Text == "authorize") && !registered.HasAuthenticationScheme)
                {
                    Report(expansion.File, group.Span,
                        $"{group.Text} has endpoints needing a signed-in user ('authorize'), but {server.Name.Text} adds no authentication scheme: " +
                        "add one in 'configure', such as builder.Services.AddAuthentication(\"Cookies\").AddCookie(\"Cookies\").");
                }
            }

            // A check's or hook's parameters all come from DI, records too.
            var checks = HandlerLambdas(function.Body, call => IsPiperWebCall(call, "PiperHealth", "AddCheck"));
            var hooks = HandlerLambdas(function.Body, call => IsPiperWebCall(call, "PiperServer", "OnStopping"));
            foreach (var (syntax, lambda) in Pair(server.Checks, checks).Concat(Pair(server.Hooks, hooks)))
            {
                foreach (var (parameter, type) in Parameters(syntax, lambda))
                {
                    if (IsOwnType(type) && !registered.Services.ContainsKey(type))
                        Report(expansion.File, parameter.Span, Missing(server, type, "it's given to this"));
                }
            }

            // What DI makes itself needs its constructor's services; a singleton mustn't keep a scoped one.
            foreach (var (service, implementation, lifetime) in registered.Made)
            {
                foreach (var parameter in implementation.Parameters.Where(p => IsOwnType(p.Type)))
                {
                    if (!registered.Services.TryGetValue(parameter.Type, out var needed))
                    {
                        Report(expansion.File, server.Name.Span,
                            $"{implementation.Name} (registered for {service.Name}) takes a {parameter.Type.Name}, which {server.Name.Text} doesn't register.");
                    }
                    else if (lifetime == Lifetime.Singleton && needed == Lifetime.Scoped)
                    {
                        Report(expansion.File, server.Name.Span,
                            $"{implementation.Name} is a singleton, but takes {parameter.Type.Name}, which is scoped: it would keep the first request's for every other. " +
                            $"Make {service.Name} scoped, or {parameter.Type.Name} a singleton.");
                    }
                }
            }
        }
    }

    private static string Missing(ServerData server, TypeSymbol type, string why) =>
        $"{server.Name.Text} registers no {type.Name}, but {why}: add 'singleton {type.Name};' (or scoped, or transient) to it, or to a services block it names.";

    /// <summary>The function a declaration's expansion became.</summary>
    private static BoundFunction? FunctionOf(PluginAnalysis analysis, PluginExpansion expansion) =>
        analysis.Program.Functions.FirstOrDefault(f => expansion.Result.Any(r => ReferenceEquals(r, f.Symbol.Declaration)));

    /// <summary>
    /// Registrations in <paramref name="function"/> and the Piper functions it calls: a generic .NET call given the
    /// service collection registers its type arguments; an authentication builder's Add… adds a scheme.
    /// </summary>
    private static void Collect(BoundProgram program, BoundFunction function, Registrations registered, HashSet<FunctionSymbol> visited)
    {
        if (!visited.Add(function.Symbol))
            return;
        foreach (var node in BoundTreeWalker.DescendantsAndSelf(function.Body))
        {
            switch (node)
            {
                case BoundCallExpression call when program.Functions.FirstOrDefault(f => f.Symbol == call.Function) is { } called:
                    Collect(program, called, registered, visited);
                    break;
                case BoundMethodCallExpression method when method.Arguments.Any(a => IsServicesOrBuilder(a.Type)):
                    registered.MayBeHidden = true;
                    break;
                case BoundClrInvocationExpression { Member: MethodInfo member } invocation:
                    var parameters = member.GetParameters();
                    // `.AddCookie(…)` (an extension) or `.AddScheme<…>(…)` (a method of AuthenticationBuilder) adds a scheme.
                    if (member.Name.StartsWith("Add", StringComparison.Ordinal) &&
                        (parameters.Any(p => p.ParameterType.Name == "AuthenticationBuilder") || member.DeclaringType?.Name == "AuthenticationBuilder"))
                        registered.HasAuthenticationScheme = true;
                    if (!parameters.Any(p => p.ParameterType.Name == "IServiceCollection") && invocation.Receiver?.Type is not ClrTypeSymbol { Type.Name: "IServiceCollection" })
                        break;
                    if (parameters.Any(p => p.ParameterType == typeof(Type) || p.ParameterType.Name == "ServiceDescriptor"))
                        registered.MayBeHidden = true;
                    if (invocation.TypeArguments.IsEmpty)
                        break;
                    var lifetime = LifetimeOf(member.Name);
                    foreach (var type in invocation.TypeArguments)
                        registered.Services.TryAdd(type, lifetime);
                    // Made by DI itself (no factory, no instance): AddSingleton<Store>() or AddSingleton<Clock, SystemClock>().
                    if (lifetime != Lifetime.Unknown && invocation.Arguments.Length == 1 &&
                        invocation.TypeArguments[^1].OriginalDefinition is ClassSymbol implementation)
                        registered.Made.Add((invocation.TypeArguments[0], implementation, lifetime));
                    break;
            }
        }
    }

    private static Lifetime LifetimeOf(string method) =>
        method.Contains("Singleton", StringComparison.Ordinal) ? Lifetime.Singleton
        : method.Contains("Scoped", StringComparison.Ordinal) || method.Contains("DbContext", StringComparison.Ordinal) ? Lifetime.Scoped
        : method.Contains("Transient", StringComparison.Ordinal) ? Lifetime.Transient
        : Lifetime.Unknown;

    private static bool IsServicesOrBuilder(TypeSymbol type) =>
        type is ClrTypeSymbol { Type.Name: "IServiceCollection" or "WebApplicationBuilder" };

    private static bool IsMapping(BoundClrInvocationExpression call) =>
        call.Member.DeclaringType?.Name == "EndpointRouteBuilderExtensions" && call.Member.Name.StartsWith("Map", StringComparison.Ordinal) &&
        call.Member.Name != "MapGroup";

    private static bool IsPiperWebCall(BoundClrInvocationExpression call, string type, string method) =>
        call.Member.DeclaringType?.FullName == $"Piper.Web.{type}" && call.Member.Name == method;

    /// <summary>The handler each matching call is given (its first lambda argument), in order.</summary>
    private static List<BoundLambdaExpression> HandlerLambdas(BoundNode body, Func<BoundClrInvocationExpression, bool> matches) =>
        [.. BoundTreeWalker.DescendantsAndSelf(body).OfType<BoundClrInvocationExpression>().Where(matches)
            .Select(call => call.Arguments.SelectMany(BoundTreeWalker.DescendantsAndSelf).OfType<BoundLambdaExpression>().FirstOrDefault())
            .OfType<BoundLambdaExpression>()];

    /// <summary>
    /// The handlers as written beside them as bound, matched by their parameters' names (the bound tree isn't walked in
    /// source order); those whose names more than one handler shares are paired in turn. Nothing if they don't line
    /// up, rather than a wrong report.
    /// </summary>
    private static IEnumerable<(LambdaExpressionSyntax Syntax, BoundLambdaExpression Bound)> Pair(
        ImmutableArray<LambdaExpressionSyntax> written, List<BoundLambdaExpression> bound)
    {
        if (written.Length != bound.Count)
            return [];
        static string Names(IEnumerable<string> names) => string.Join(",", names);
        var remaining = bound.ToList();
        var pairs = new List<(LambdaExpressionSyntax, BoundLambdaExpression)>();
        foreach (var syntax in written)
        {
            var names = Names(syntax.Parameters.Select(p => p.Name.Text));
            if (remaining.FirstOrDefault(b => Names(b.Parameters.Select(v => v.Name)) == names) is not { } match)
                return [];
            remaining.Remove(match);
            pairs.Add((syntax, match));
        }
        return pairs;
    }

    /// <summary>Each parameter as written, with its type as bound.</summary>
    private static IEnumerable<(LambdaParameterSyntax Parameter, TypeSymbol Type)> Parameters(LambdaExpressionSyntax syntax, BoundLambdaExpression lambda) =>
        syntax.Parameters.Length == lambda.Parameters.Length
            ? syntax.Parameters.Zip(lambda.Parameters).Where(pair => pair.First.Name.Text == pair.Second.Name).Select(pair => (pair.First, pair.Second.Type))
            : [];

    /// <summary>A class or trait of the program's own, which a handler can only get from DI (a record is a request's body).</summary>
    private static bool IsOwnService(TypeSymbol type) => type.OriginalDefinition is ClassSymbol || type.IsTrait;

    /// <summary>A class, trait or record of the program's own.</summary>
    private static bool IsOwnType(TypeSymbol type) => IsOwnService(type) || type.OriginalDefinition is RecordSymbol;
}
