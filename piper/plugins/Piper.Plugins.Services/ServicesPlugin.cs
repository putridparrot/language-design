using Piper.Plugins;
using Piper.Syntax;

namespace Piper.Plugins.Services;

/// <summary>
/// Adds a declaration for dependency injection with Microsoft.Extensions.DependencyInjection, rewritten into a function
/// of ordinary Piper: <c>services Name { singleton Clock => SystemClock; … }</c> becomes
/// <c>fn Name(services: IServiceCollection) => IServiceCollection</c>. It works in any program: a console app builds a
/// provider from it (<c>Name(ServiceCollection()).BuildServiceProvider()</c>), and an ASP.NET Core app passes it
/// <c>builder.Services</c>. The plugin asks for the Microsoft.Extensions.DependencyInjection package, so a program using
/// it needn't. It also checks what DI would only find when the program runs: a service registered twice.
/// </summary>
public sealed class ServicesPlugin : IPiperPlugin
{
    private const string DependencyInjection = "Microsoft.Extensions.DependencyInjection";

    // The extension methods are called through their class, so the code works whatever the user's file imports.
    private const string Registrations = DependencyInjection + ".ServiceCollectionServiceExtensions";

    private static readonly Dictionary<string, string> Lifetimes = new()
    {
        ["singleton"] = "AddSingleton",
        ["scoped"] = "AddScoped",
        ["transient"] = "AddTransient",
    };

    public string Name => "services";

    /// <summary>
    /// The DI package, for .NET 10. An ASP.NET Core program has it already, as part of its framework, whose version
    /// wins when it's newer.
    /// </summary>
    public IReadOnlyDictionary<string, string> Packages { get; } = new Dictionary<string, string> { [DependencyInjection] = "10.0.0" };

    public IReadOnlyList<PluginKeyword> Keywords { get; } = [new DeclarationKeyword("services", Services)];

    /// <summary>
    /// services     = "services" IDENT "{" { registration } "}" ;
    /// registration = ( "singleton" | "scoped" | "transient" ) type [ "=>" type | "=" expression ] ";" ;
    /// </summary>
    private static IEnumerable<DeclarationSyntax> Services(PluginParser p)
    {
        var name = p.Expect(TokenKind.Identifier);
        p.Expect(TokenKind.OpenBrace);

        var registrations = new List<StatementSyntax>();
        var registered = new Dictionary<string, Token>();
        while (p.Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile))
        {
            var lifetime = p.Current;
            if (lifetime.Kind != TokenKind.Identifier || !Lifetimes.TryGetValue(lifetime.Text, out var method))
            {
                p.Report(lifetime.Span, "Expected 'singleton', 'scoped' or 'transient'.");
                SkipClause(p);
                continue;
            }
            p.NextToken();

            var service = p.ParseType();
            var key = TypeText(service);
            if (registered.TryGetValue(key, out var first))
                p.Report(service.Span, $"'{key}' is already registered (as {first.Text}); a later registration would replace it.");
            else
                registered[key] = lifetime;

            if (p.Current.Kind == TokenKind.FatArrow)
            {
                // `singleton Clock => SystemClock;`: the service, and the type that implements it.
                p.NextToken();
                var implementation = p.ParseType();
                registrations.Add(p.Template.Statement($"{Registrations}.{method}<$service, $implementation>(services);",
                    ("service", service), ("implementation", implementation)));
            }
            else if (p.Current.Kind == TokenKind.Equals)
            {
                // `singleton Greeter = Greeter("Hello");` (made when first needed), or a factory taking the service provider.
                p.NextToken();
                var value = p.ParseExpression();
                var factory = value is LambdaExpressionSyntax { Parameters.Length: 1 }
                    ? value
                    : p.Template.Expression("$provider => $value", ("provider", p.FreshName("provider")), ("value", value));
                registrations.Add(p.Template.Statement($"{Registrations}.{method}<$service>(services, $factory);", ("service", service), ("factory", factory)));
            }
            else
            {
                registrations.Add(p.Template.Statement($"{Registrations}.{method}<$service>(services);", ("service", service)));
            }
            p.Expect(TokenKind.Semicolon);
        }
        p.Expect(TokenKind.CloseBrace);

        return p.Template.Declarations(
            $"fn $name(services: {DependencyInjection}.IServiceCollection) => {DependencyInjection}.IServiceCollection {{ $registrations; services }}",
            ("name", name), ("registrations", registrations));
    }

    /// <summary>How a type is written, to tell registrations of the same service apart.</summary>
    private static string TypeText(TypeSyntax type) => type switch
    {
        NamedTypeSyntax named => string.Join(".", named.Qualifier.Select(q => q.Text).Append(named.Name.Text)) +
                                 (named.TypeArguments.IsEmpty ? "" : $"<{string.Join(", ", named.TypeArguments.Select(TypeText))}>"),
        _ => type.ToString(),
    };

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
