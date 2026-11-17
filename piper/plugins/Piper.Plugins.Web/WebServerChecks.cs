using Piper.Binding;
using Piper.Plugins;
using static Piper.Plugins.Shared.ServiceChecks;

namespace Piper.Plugins.Web;

/// <summary>
/// What a <c>server</c> is checked for once the program is type-checked: what ASP.NET Core and dependency injection
/// would only find when a request comes in.
/// <list type="bullet">
/// <item>a handler, health check, worker or <c>stopping</c> hook taking a service of the program's own (a class or
/// trait) that nothing in the server registers: a 500 at the first request, or a failed check;</item>
/// <item>a singleton whose class takes a scoped service (a captive dependency: one request's instance kept for all), or
/// a service nothing registers;</item>
/// <item>endpoints needing a signed-in user (<c>authorize</c>) in a server with no authentication scheme.</item>
/// </list>
/// The checks a <c>host</c> shares are in plugins/Shared/ServiceChecks.cs; the endpoints' are here.
/// </summary>
public sealed partial class WebPlugin
{
    private static void AnalyzeServerServices(PluginAnalysis analysis)
    {
        var endpoints = analysis.Expansions.Where(e => e.Data is EndpointsData).ToList();
        foreach (var expansion in analysis.Expansions.Where(e => e.Data is ServerData))
        {
            var server = (ServerData)expansion.Data!;
            if (FunctionOf(analysis, expansion) is not { } function)
                continue;
            var registered = RegisteredBy(analysis.Program, function);

            // Endpoints' handlers take services of the program's own (classes, traits) by type; a record is the request's body.
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
                            Report(analysis, registered, mapped.File, parameter.Span, Missing(server.Name, type, "this handler takes it"));
                    }
                }
                if (!data.Words.IsEmpty && data.Words.Any(w => w.Text == "authorize") && !registered.HasAuthenticationScheme)
                {
                    Report(analysis, registered, expansion.File, group.Span,
                        $"{group.Text} has endpoints needing a signed-in user ('authorize'), but {server.Name.Text} adds no authentication scheme: " +
                        "add one in 'configure', such as builder.Services.AddAuthentication(\"Cookies\").AddCookie(\"Cookies\").");
                }
            }

            // A check's, hook's or worker's parameters all come from DI, records too; and what DI makes needs its own.
            var checks = HandlerLambdas(function.Body, call => IsPiperWebCall(call, "PiperHealth", "AddCheck"));
            var hooks = HandlerLambdas(function.Body, call => IsHostCall(call, "OnStopping"));
            var workers = HandlerLambdas(function.Body, call => IsHostCall(call, "Worker"));
            CheckServices(analysis, expansion.File, server.Name, registered,
                Pair(server.Checks, checks).Concat(Pair(server.Hooks, hooks)).Concat(Pair(server.Workers, workers)));
        }
    }

    private static bool IsMapping(BoundClrInvocationExpression call) =>
        call.Member.DeclaringType?.Name == "EndpointRouteBuilderExtensions" && call.Member.Name.StartsWith("Map", StringComparison.Ordinal) &&
        call.Member.Name != "MapGroup";

    private static bool IsPiperWebCall(BoundClrInvocationExpression call, string type, string method) =>
        call.Member.DeclaringType?.FullName == $"Piper.Web.{type}" && call.Member.Name == method;
}
