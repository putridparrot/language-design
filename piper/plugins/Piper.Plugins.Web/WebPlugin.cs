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
public sealed class WebPlugin : IPiperPlugin
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

    public IReadOnlyList<PluginKeyword> Keywords { get; } = [new DeclarationKeyword("endpoints", Endpoints)];

    /// <summary>
    /// endpoints = "endpoints" IDENT [ STRING ] "{" { endpoint } "}" ;
    /// endpoint  = ( "get" | "post" | "put" | "delete" | "patch" ) STRING [ "async" ] lambdaParameters "=>" ( block [ ";" ] | expression ";" ) ;
    /// Routes are written as ASP.NET Core documents them: <c>"/users/{id}"</c> is a route parameter, not interpolation.
    /// </summary>
    private static IEnumerable<DeclarationSyntax> Endpoints(PluginParser p)
    {
        var name = p.Expect(TokenKind.Identifier);
        var prefix = p.Current.Kind is TokenKind.StringLiteral or TokenKind.InterpolatedStringLiteral ? Route(p, p.NextToken()) : null;
        p.Expect(TokenKind.OpenBrace);

        var router = prefix is null ? null : p.FreshName("group");
        var mappings = new List<StatementSyntax>();
        var mapped = new HashSet<string>();
        while (p.Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile))
        {
            var verb = p.Current;
            if (verb.Kind != TokenKind.Identifier || !Verbs.TryGetValue(verb.Text, out var method))
            {
                p.Report(verb.Span, "Expected 'get', 'post', 'put', 'delete' or 'patch'.");
                SkipClause(p);
                continue;
            }
            p.NextToken();

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

            mappings.Add(p.Template.Statement($"{Routing}.{method}($router, $route, $handler);",
                ("router", (object?)router ?? "app"), ("route", route), ("handler", handler)));
        }
        p.Expect(TokenKind.CloseBrace);

        if (router is not null)
            mappings.Insert(0, p.Template.Statement($"let $group = {Routing}.MapGroup(app, $prefix);", ("group", router), ("prefix", prefix!)));
        return p.Template.Declarations(
            "fn $name(app: Microsoft.AspNetCore.Routing.IEndpointRouteBuilder) { $mappings }",
            ("name", name), ("mappings", mappings));
    }

    /// <summary>
    /// A handler: <c>(id: int) => users.Find(id);</c>, <c>async (id: int) => { … }</c>. Its parameters need types, since
    /// ASP.NET Core takes it as a plain Delegate and reads them to bind the request. Null after a syntax error.
    /// </summary>
    private static LambdaExpressionSyntax? Handler(PluginParser p)
    {
        var async = p.AtWord("async") ? p.NextToken() : null;
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
