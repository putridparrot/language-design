using System.Collections.Immutable;
using System.Reflection;
using Piper.Binding;
using Piper.Plugins;
using Piper.Syntax;
using Piper.Text;

namespace Piper.Plugins.Shared;

/// <summary>
/// What a <c>server</c> (the web plugin) and a <c>host</c> (the hosting plugin) are checked for once the program is
/// type-checked: a handler (a worker, a stopping hook, a health check, an endpoint's) taking a service of the program's
/// own that nothing registers, which dependency injection would only find when it runs; and a class DI makes, as a
/// singleton, needing a scoped service (a captive dependency) or one nothing registers.
/// What's registered is read from the declaration's function and the Piper functions it calls (its <c>services</c>
/// blocks, <c>configure</c>): each generic call given the service collection (<c>AddSingleton&lt;Clock, SystemClock&gt;</c>,
/// <c>AddDbContext&lt;Db&gt;</c>) registers its type arguments. Where a registration could be hidden (a method of the
/// program's own given the services, or one taking a <c>Type</c>), what's missing is a warning, not an error.
/// </summary>
internal static class ServiceChecks
{
    public enum Lifetime { Singleton, Scoped, Transient, Unknown }

    public sealed class Registrations
    {
        public Dictionary<TypeSymbol, Lifetime> Services { get; } = [];

        /// <summary>A class DI makes itself (not a factory), whose constructor's parameters must be services too.</summary>
        public List<(TypeSymbol Service, ClassSymbol Implementation, Lifetime Lifetime)> Made { get; } = [];

        public bool HasAuthenticationScheme { get; set; }

        /// <summary>A registration may be out of sight, so what's missing is a warning, not an error.</summary>
        public bool MayBeHidden { get; set; }
    }

    /// <summary>What <paramref name="function"/> registers, and the Piper functions it calls.</summary>
    public static Registrations RegisteredBy(BoundProgram program, BoundFunction function)
    {
        var registered = new Registrations();
        Collect(program, function, registered, []);
        return registered;
    }

    /// <summary>
    /// Checks the handlers whose parameters all come from DI (workers, stopping hooks, health checks: records too),
    /// and the classes DI makes, against what <paramref name="registered"/> holds. <paramref name="owner"/> is the
    /// declaration's name, for the messages.
    /// </summary>
    public static void CheckServices(
        PluginAnalysis analysis, SourceText file, Token owner, Registrations registered,
        IEnumerable<(LambdaExpressionSyntax Syntax, BoundLambdaExpression Bound)> handlers)
    {
        foreach (var (syntax, lambda) in handlers)
        {
            foreach (var (parameter, type) in Parameters(syntax, lambda))
            {
                if (IsOwnType(type) && !registered.Services.ContainsKey(type))
                    Report(analysis, registered, file, parameter.Span, Missing(owner, type, "it's given to this"));
            }
        }

        // What DI makes itself needs its constructor's services; a singleton mustn't keep a scoped one.
        foreach (var (service, implementation, lifetime) in registered.Made)
        {
            foreach (var parameter in implementation.Parameters.Where(p => IsOwnType(p.Type)))
            {
                if (!registered.Services.TryGetValue(parameter.Type, out var needed))
                {
                    Report(analysis, registered, file, owner.Span,
                        $"{implementation.Name} (registered for {service.Name}) takes a {parameter.Type.Name}, which {owner.Text} doesn't register.");
                }
                else if (lifetime == Lifetime.Singleton && needed == Lifetime.Scoped)
                {
                    Report(analysis, registered, file, owner.Span,
                        $"{implementation.Name} is a singleton, but takes {parameter.Type.Name}, which is scoped: it would keep the first request's for every other. " +
                        $"Make {service.Name} scoped, or {parameter.Type.Name} a singleton.");
                }
            }
        }
    }

    /// <summary>An error, or a warning where a registration may be out of sight.</summary>
    public static void Report(PluginAnalysis analysis, Registrations registered, SourceText file, TextSpan span, string message)
    {
        if (registered.MayBeHidden)
            analysis.ReportWarning(file, span, message);
        else
            analysis.ReportError(file, span, message);
    }

    public static string Missing(Token owner, TypeSymbol type, string why) =>
        $"{owner.Text} registers no {type.Name}, but {why}: add 'singleton {type.Name};' (or scoped, or transient) to it, or to a services block it names.";

    /// <summary>The function a declaration's expansion became.</summary>
    public static BoundFunction? FunctionOf(PluginAnalysis analysis, PluginExpansion expansion) =>
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
        type is ClrTypeSymbol { Type.Name: "IServiceCollection" or "WebApplicationBuilder" or "HostApplicationBuilder" or "IHostApplicationBuilder" };

    /// <summary>A call to Piper.Hosting's <c>PiperHost</c>, which a server's workers and stopping hooks share with a <c>host</c>'s.</summary>
    public static bool IsHostCall(BoundClrInvocationExpression call, string method) =>
        call.Member.DeclaringType?.FullName == "Piper.Hosting.PiperHost" && call.Member.Name == method;

    /// <summary>The handler each matching call is given (its first lambda argument), in order.</summary>
    public static List<BoundLambdaExpression> HandlerLambdas(BoundNode body, Func<BoundClrInvocationExpression, bool> matches) =>
        [.. BoundTreeWalker.DescendantsAndSelf(body).OfType<BoundClrInvocationExpression>().Where(matches)
            .Select(call => call.Arguments.SelectMany(BoundTreeWalker.DescendantsAndSelf).OfType<BoundLambdaExpression>().FirstOrDefault())
            .OfType<BoundLambdaExpression>()];

    /// <summary>
    /// The handlers as written beside them as bound, matched by their parameters' names (the bound tree isn't walked in
    /// source order); those whose names more than one handler shares are paired in turn. Nothing if they don't line
    /// up, rather than a wrong report.
    /// </summary>
    public static IEnumerable<(LambdaExpressionSyntax Syntax, BoundLambdaExpression Bound)> Pair(
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
    public static IEnumerable<(LambdaParameterSyntax Parameter, TypeSymbol Type)> Parameters(LambdaExpressionSyntax syntax, BoundLambdaExpression lambda) =>
        syntax.Parameters.Length == lambda.Parameters.Length
            ? syntax.Parameters.Zip(lambda.Parameters).Where(pair => pair.First.Name.Text == pair.Second.Name).Select(pair => (pair.First, pair.Second.Type))
            : [];

    /// <summary>A class or trait of the program's own, which a handler can only get from DI (a record is a request's body).</summary>
    public static bool IsOwnService(TypeSymbol type) => type.OriginalDefinition is ClassSymbol || type.IsTrait;

    /// <summary>A class, trait or record of the program's own.</summary>
    public static bool IsOwnType(TypeSymbol type) => IsOwnService(type) || type.OriginalDefinition is RecordSymbol;
}
