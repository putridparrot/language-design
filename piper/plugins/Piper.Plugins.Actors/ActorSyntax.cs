using System.Collections.Immutable;
using Piper.Binding;
using Piper.Plugins;
using Piper.Syntax;
using Piper.Text;

namespace Piper.Plugins.Actors;

/// <param name="Result">An ask's reply type; null for a tell.</param>
internal sealed record ActorHandler(Token Name, ImmutableArray<ParameterSyntax> Parameters, TypeSyntax? Result, BlockExpressionSyntax Body);

/// <summary>An `actor` declaration as read: its name, the parameters `Spawn` takes, its state and its handlers.</summary>
internal sealed record ActorDeclaration(Token Name, ImmutableArray<ParameterSyntax> Parameters, List<StatementSyntax> State, List<ActorHandler> Handlers);

/// <summary>
/// What the actor plugins share: reading an `actor` declaration (the same syntax whether actors run in one process or
/// across machines), the checks on its shape, and the analysis of its messages' types. Compiled into each plugin.
/// </summary>
internal static class ActorSyntax
{
    /// <summary>.NET types whose values can't change, which messages may carry.</summary>
    private static readonly HashSet<Type> ImmutableClrTypes =
    [
        typeof(DateTime), typeof(DateTimeOffset), typeof(DateOnly), typeof(TimeOnly), typeof(TimeSpan), typeof(Guid),
        typeof(Uri), typeof(Version), typeof(System.Numerics.BigInteger), typeof(Half), typeof(Int128), typeof(UInt128),
        typeof(Nullable<>), typeof(KeyValuePair<,>), typeof(Tuple<>), typeof(Tuple<,>), typeof(Tuple<,,>), typeof(Tuple<,,,>),
        typeof(ValueTuple<>), typeof(ValueTuple<,>), typeof(ValueTuple<,,>), typeof(ValueTuple<,,,>), typeof(ValueTuple<,,,,>),
    ];

    /// <summary>
    /// actor   = "actor" IDENT [ "(" parameters ")" ] "{" { ( "let" | "mut" ) variableDeclaration | handler } "}" ;
    /// handler = "on" IDENT "(" [ parameters ] ")" [ "=>" type ] block ;
    /// Null after errors in its shape, as generating the actor would only add confusing errors about the generated code.
    /// <paramref name="generatedNames"/> are the methods every actor gets, which handlers can't be called.
    /// </summary>
    public static ActorDeclaration? Parse(PluginParser p, IReadOnlySet<string> generatedNames)
    {
        var name = p.Expect(TokenKind.Identifier);
        bool valid = true;
        var parameters = p.Current.Kind == TokenKind.OpenParen ? Parameters(p, ref valid) : [];
        p.Expect(TokenKind.OpenBrace);

        var state = new List<StatementSyntax>();
        var handlers = new List<ActorHandler>();
        while (p.Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile))
        {
            if (p.Current.Kind is TokenKind.LetKeyword or TokenKind.MutKeyword)
            {
                state.Add(p.ParseVariableDeclaration());
            }
            else if (p.AtWord("on"))
            {
                if (ParseHandler(p, ref valid) is { } handler)
                    handlers.Add(handler);
            }
            else
            {
                p.Report(p.Current.Span, "Expected the actor's state ('let' or 'mut') or a handler ('on Name(…) { … }').");
                valid = false;
                p.NextToken();
            }
        }
        p.Expect(TokenKind.CloseBrace);

        if (handlers.Count == 0)
        {
            p.Report(name.Span, $"Actor '{name.Text}' has no handlers, so nothing can send it a message; add one, such as 'on Get() => int {{ … }}'.");
            valid = false;
        }
        var seen = new HashSet<string>();
        foreach (var handler in handlers)
        {
            if (generatedNames.Contains(handler.Name.Text))
            {
                p.Report(handler.Name.Span, $"'{handler.Name.Text}' is the name of a method every actor has; call the handler something else.");
                valid = false;
            }
            else if (!seen.Add(handler.Name.Text))
            {
                p.Report(handler.Name.Span, $"Actor '{name.Text}' already has a handler called '{handler.Name.Text}'.");
                valid = false;
            }
            // A handler's result is its block's value; `return` would leave the actor's loop instead.
            foreach (var @return in Returns(handler.Body))
            {
                p.Report(@return.Span, "A handler can't 'return'; its result is its block's last expression.");
                valid = false;
            }
        }
        return valid ? new ActorDeclaration(name, parameters, state, handlers) : null;
    }

    private static ActorHandler? ParseHandler(PluginParser p, ref bool valid)
    {
        p.NextToken();
        var name = p.Expect(TokenKind.Identifier);
        if (p.Current.Kind != TokenKind.OpenParen)
        {
            p.Report(p.Current.Span, $"Expected the handler's parameters, such as 'on {name.Text}(amount: int)', or '()' for none.");
            valid = false;
            return null;
        }
        var parameters = Parameters(p, ref valid);
        TypeSyntax? result = null;
        if (p.Current.Kind == TokenKind.FatArrow)
        {
            p.NextToken();
            result = p.ParseType();
        }
        // The handler runs in the actor's async loop, so it can await.
        return new ActorHandler(name, parameters, result, p.ParseAsyncBlock());
    }

    /// <summary>`(name: Type, …)`: each parameter needs a type, as it becomes a field of the message.</summary>
    private static ImmutableArray<ParameterSyntax> Parameters(PluginParser p, ref bool valid)
    {
        var parameters = ImmutableArray.CreateBuilder<ParameterSyntax>();
        foreach (var parameter in p.ParseLambdaParameters())
        {
            if (parameter.Type is { } type)
            {
                parameters.Add(new ParameterSyntax(parameter.Span, parameter.Name, type));
            }
            else
            {
                p.Report(parameter.Span, $"Give '{parameter.Name.Text}' a type, such as ({parameter.Name.Text}: int).");
                valid = false;
            }
        }
        return parameters.ToImmutable();
    }

    /// <summary>The `return` statements in <paramref name="node"/>, other than in a lambda inside it.</summary>
    private static IEnumerable<ReturnStatementSyntax> Returns(SyntaxNode node) => node switch
    {
        ReturnStatementSyntax @return => [@return],
        LambdaExpressionSyntax => [],
        _ => SyntaxWalker.ChildrenOf(node).SelectMany(Returns),
    };

    /// <summary>
    /// The fields of every message the analyzed plugin's actors take, as written (to report at) and as bound, other
    /// than the plugin's own (named with two underscores); and the actors' handle types.
    /// </summary>
    public static (IEnumerable<(SourceText File, ParameterSyntax Syntax, FieldSymbol Field)> Fields, HashSet<TypeSymbol> Handles) Messages(PluginAnalysis analysis)
    {
        var generated = analysis.Expansions.SelectMany(e => e.Result).ToHashSet(ReferenceEqualityComparer.Instance);
        var handles = analysis.Program.Types.OfType<RecordSymbol>().Where(record => generated.Contains(record.Declaration)).ToHashSet<TypeSymbol>();
        var fields =
            from expansion in analysis.Expansions
            from messages in expansion.Result.OfType<EnumDeclarationSyntax>()
            let symbol = analysis.Program.Types.OfType<EnumSymbol>().FirstOrDefault(e => ReferenceEquals(e.Declaration, messages))
            where symbol is not null
            from pair in messages.Variants.Zip(symbol.Variants)
            from field in pair.First.Fields.Zip(pair.Second.Fields)
            where !field.Second.Name.StartsWith("__", StringComparison.Ordinal)
            select (expansion.File, field.First, field.Second);
        return (fields, handles);
    }

    /// <summary>
    /// The part of <paramref name="type"/> that can change, or null if it's immutable throughout: records, enums, Piper's
    /// builtin types, immutable .NET types and <paramref name="handles"/> are. A trait is too, unless
    /// <paramref name="traitsAreOpaque"/>: across machines a value is sent as data, and a trait says nothing about that.
    /// </summary>
    public static string? MutablePart(TypeSymbol type, HashSet<TypeSymbol> handles, bool traitsAreOpaque, HashSet<TypeSymbol>? seen = null)
    {
        seen ??= [];
        if (type == TypeSymbol.Error || TypeSymbol.Builtins.Contains(type) || type is TypeParameterSymbol ||
            (type.IsTrait && !traitsAreOpaque) || handles.Contains(type.OriginalDefinition) || !seen.Add(type))
            return null;

        var parts = type.OriginalDefinition switch
        {
            RecordSymbol record => record.Fields.Select(f => f.Type),
            EnumSymbol @enum => @enum.Variants.SelectMany(v => v.Fields).Select(f => f.Type),
            ClrTypeSymbol { Type: var clr } when clr.IsPrimitive || clr.IsEnum || ImmutableClrTypes.Contains(clr) ||
                                                 clr.Namespace == "System.Collections.Immutable" => [],
            _ => null,
        };
        return parts is null
            ? type.ToString()
            : parts.Concat(type.TypeArguments).Select(part => MutablePart(part, handles, traitsAreOpaque, seen)).FirstOrDefault(part => part is not null);
    }
}

/// <summary>Holes for a template being written as text: each value gets a name of its own, <c>$h0</c>, <c>$h1</c>, ….</summary>
internal sealed class TemplateHoles
{
    private readonly List<(string Name, object Value)> _holes = [];

    /// <summary>A hole holding <paramref name="value"/>, to write into the template.</summary>
    public string this[object value]
    {
        get
        {
            _holes.Add(($"h{_holes.Count}", value));
            return "$" + _holes[^1].Name;
        }
    }

    public (string Name, object Value)[] All => [.. _holes];
}
