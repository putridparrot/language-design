using System.Collections.Immutable;
using Piper.Binding;
using Piper.Plugins;
using Piper.Syntax;
using Piper.Text;

namespace Piper.Plugins.Actors;

/// <param name="Result">An ask's reply type; null for a tell.</param>
internal sealed record ActorHandler(Token Name, ImmutableArray<ParameterSyntax> Parameters, TypeSyntax? Result, BlockExpressionSyntax Body);

/// <summary>An `actor` declaration as read: its name, the parameters `Spawn` takes, its state and its handlers.</summary>
internal sealed record ActorDeclaration(Token Name, ImmutableArray<ParameterSyntax> Parameters, List<StatementSyntax> State, List<ActorHandler> Handlers)
{
    /// <summary>What the actor does when a handler throws (`on failure rollback;`); null for the default, resume.</summary>
    public FailurePolicy? Failure { get; init; }

    /// <summary>The names of its `mut` state, which `rollback` and `restart` put back.</summary>
    public List<string> Mutable { get; init; } = [];
}

/// <summary>
/// `on failure resume|rollback|stop|restart [n per unit];`: what an actor does when a handler throws. <paramref name="Limit"/>
/// is how many restarts it may make in <paramref name="Window"/>, after which it stops.
/// </summary>
internal sealed record FailurePolicy(Token Word, string Kind, ExpressionSyntax? Limit, string? Window);

/// <summary>What an actor plugin notes for its analysis: the actor's name, its failure policy, and its state's names.</summary>
internal sealed record ActorData(Token Name, FailurePolicy? Failure, List<string> State);

/// <summary>
/// What the actor plugins share: reading an `actor` declaration (the same syntax whether actors run in one process or
/// across machines), the checks on its shape, and the analysis of its messages' types. Compiled into each plugin.
/// </summary>
internal static class ActorSyntax
{
    /// <summary>The editor's completion for <c>actor</c>: an actor with state, a tell and an ask.</summary>
    public const string Snippet =
        "actor ${1:Counter}(${2:start}: ${3:int}) {\n\tmut ${4:count} = ${2:start};\n\n\ton ${5:Increment}(by: int) {\n\t\t${4:count} += by;\n\t}\n\n\ton ${6:Get}() => ${3:int} { ${4:count} }\n}";

    /// <summary>What <c>on</c> means, for the editor's hover.</summary>
    public static readonly IReadOnlyDictionary<string, string> Words = new Dictionary<string, string>
    {
        ["on"] = "A handler for a message. `on Name(…) { … }` is a *tell*: the sender carries on at once. " +
                 "`on Name(…) => T { … }` is an *ask*: the sender awaits its result. The actor handles one message at a time. " +
                 "`on failure …;` says what the actor does when a handler throws.",
        ["failure"] = "What the actor does when a handler throws (the sender of an ask gets the error either way): `resume` (the default), `rollback`, `restart` or `stop`.",
        ["resume"] = "Carry on with the state as the failed handler left it: the default.",
        ["rollback"] = "Put the state back as it was before the failed message, so a handler either finishes or changes nothing. The state must be immutable values.",
        ["restart"] = "Put the state back as Spawn made it. `restart 3 per minute`: after more restarts than that in the window, stop. The state must be immutable values.",
        ["stop"] = "Stop the actor: later messages aren't handled, and an ask sent to it fails.",
        ["per"] = "The window the restarts are counted in: `per second`, `per minute` or `per hour`.",
    };

    private static readonly string[] Policies = ["resume", "rollback", "restart", "stop"];

    /// <summary>.NET types whose values can't change, which messages may carry.</summary>
    private static readonly HashSet<Type> ImmutableClrTypes =
    [
        typeof(DateTime), typeof(DateTimeOffset), typeof(DateOnly), typeof(TimeOnly), typeof(TimeSpan), typeof(Guid),
        typeof(Uri), typeof(Version), typeof(System.Numerics.BigInteger), typeof(Half), typeof(Int128), typeof(UInt128),
        typeof(Nullable<>), typeof(KeyValuePair<,>), typeof(Tuple<>), typeof(Tuple<,>), typeof(Tuple<,,>), typeof(Tuple<,,,>),
        typeof(ValueTuple<>), typeof(ValueTuple<,>), typeof(ValueTuple<,,>), typeof(ValueTuple<,,,>), typeof(ValueTuple<,,,,>),
    ];

    /// <summary>
    /// actor   = "actor" IDENT [ "(" parameters ")" ] "{" { ( "let" | "mut" ) variableDeclaration | function | handler } "}" ;
    /// handler = "on" IDENT "(" [ parameters ] ")" [ "=>" type ] block ;
    /// A function is the actor's own, for what its handlers share: it's a local function after the state, where the
    /// actor's loop starts, so it can read and change the state (only ever from a handler, one message at a time).
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
        var functions = new List<StatementSyntax>();
        FailurePolicy? failure = null;
        while (p.Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile))
        {
            if (p.Current.Kind is TokenKind.LetKeyword or TokenKind.MutKeyword)
            {
                state.Add(p.ParseVariableDeclaration());
            }
            else if (p.Current.Kind is TokenKind.FnKeyword or TokenKind.PrivateKeyword or TokenKind.StaticKeyword or TokenKind.OpenBracket ||
                     (p.AtWord("async") && p.Peek(1).Kind == TokenKind.FnKeyword))
            {
                var declaration = p.ParseDeclaration();
                if (declaration is FunctionDeclarationSyntax { Private: null, Static: null } function)
                {
                    functions.Add(new LocalFunctionStatementSyntax(function.Span, function));
                }
                else
                {
                    if (declaration is not null)
                        p.Report((declaration.Private ?? (declaration as FunctionDeclarationSyntax)?.Static)?.Span ?? declaration.Span, "An actor's functions are its own, used by its handlers: write 'fn', without 'private' or 'static'.");
                    valid = false;
                }
            }
            // `on failure rollback;`, not a handler called `failure`, which would be followed by `(`.
            else if (p.AtWord("on") && p.Peek(1) is { Kind: TokenKind.Identifier, Text: "failure" } && p.Peek(2).Kind == TokenKind.Identifier)
            {
                var policy = ParsePolicy(p, ref valid);
                if (failure is not null && policy is not null)
                {
                    p.Report(policy.Word.Span, $"Actor '{name.Text}' already says what it does on failure.");
                    valid = false;
                }
                failure ??= policy;
            }
            else if (p.AtWord("on"))
            {
                if (ParseHandler(p, ref valid) is { } handler)
                    handlers.Add(handler);
            }
            else
            {
                p.Report(p.Current.Span, "Expected the actor's state ('let' or 'mut'), a function ('fn') or a handler ('on Name(…) { … }').");
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
        // `rollback` and `restart` put the `mut` state back, so it must be plain `let`s and `mut`s, one name each.
        var mutable = new List<string>();
        foreach (var statement in state)
        {
            if (statement is VariableDeclarationSyntax { Keyword.Kind: var keyword, Name: { IsMissing: false } stateName })
            {
                if (keyword == TokenKind.MutKeyword)
                    mutable.Add(stateName.Text);
            }
            else if (failure is { Kind: "rollback" or "restart" })
            {
                p.Report(statement.Span, $"'{failure.Kind}' puts the actor's state back, so declare it one name at a time: 'mut count = 0;'.");
                valid = false;
            }
        }
        // After all the state, which they may use.
        state.AddRange(functions);
        return valid ? new ActorDeclaration(name, parameters, state, handlers) { Failure = failure, Mutable = mutable } : null;
    }

    /// <summary>`on failure rollback;` or `on failure restart 3 per minute;`.</summary>
    private static FailurePolicy? ParsePolicy(PluginParser p, ref bool valid)
    {
        p.ReadWord();
        p.ReadWord();
        var word = p.Current;
        if (word.Kind != TokenKind.Identifier || !Policies.Contains(word.Text))
        {
            p.Report(word.Span, "Expected what the actor does when a handler throws: 'resume', 'rollback', 'restart' or 'stop'.");
            valid = false;
            SkipToSemicolon(p);
            return null;
        }
        p.ReadWord();
        ExpressionSyntax? limit = null;
        string? window = null;
        if (word.Text == "restart" && p.Current.Kind != TokenKind.Semicolon)
        {
            limit = p.ParseExpression();
            if (!p.AtWord("per"))
            {
                p.Report(p.Current.Span, "Expected the window the restarts are counted in: 'restart 3 per minute'.");
                valid = false;
                SkipToSemicolon(p);
                return null;
            }
            p.ReadWord();
            if (p.Current is { Kind: TokenKind.Identifier, Text: "second" or "minute" or "hour" } unit)
            {
                p.ReadWord();
                window = unit.Text;
            }
            else
            {
                p.Report(p.Current.Span, "Expected 'second', 'minute' or 'hour'.");
                valid = false;
                SkipToSemicolon(p);
                return null;
            }
        }
        p.Expect(TokenKind.Semicolon);
        return new FailurePolicy(word, word.Text, limit, window);
    }

    private static void SkipToSemicolon(PluginParser p)
    {
        while (p.Current.Kind is not (TokenKind.Semicolon or TokenKind.CloseBrace or TokenKind.EndOfFile))
            p.NextToken();
        if (p.Current.Kind == TokenKind.Semicolon)
            p.NextToken();
    }

    /// <summary>
    /// The code an actor's failure policy adds, for its plugin's template: <c>Setup</c> after the state (the snapshots
    /// it puts back, and the restarts' count); <c>Before</c> each message (rollback's snapshot); and <c>OnFailure</c> in
    /// each handler's <c>catch</c>, given <paramref name="stop"/>, the plugin's way of stopping the actor.
    /// </summary>
    public static (string Setup, string Before, string OnFailure) PolicyCode(ActorDeclaration actor, TemplateHoles hole, string stop)
    {
        string Restore(string from) => string.Concat(actor.Mutable.Select(name => $"{name} = {from}{name}; "));
        switch (actor.Failure)
        {
            case { Kind: "rollback" }:
                return (string.Concat(actor.Mutable.Select(name => $"mut __saved_{name} = {name};\n")),
                        string.Concat(actor.Mutable.Select(name => $"__saved_{name} = {name}; ")),
                        Restore("__saved_"));
            case { Kind: "restart", Limit: { } limit, Window: var window }:
                return (string.Concat(actor.Mutable.Select(name => $"let __initial_{name} = {name};\n")) +
                        $"let __restarts = Piper.Runtime.PiperRestarts({hole[limit]}, System.TimeSpan.From{Capitalized(window!)}s(1));\n",
                        "",
                        $"if __restarts.Allow() {{ {Restore("__initial_")}}} else {{ {stop} }}");
            case { Kind: "restart" }:
                return (string.Concat(actor.Mutable.Select(name => $"let __initial_{name} = {name};\n")), "", Restore("__initial_"));
            case { Kind: "stop" }:
                return ("", "", stop);
            default:
                return ("", "", "");
        }
    }

    private static string Capitalized(string word) => char.ToUpperInvariant(word[0]) + word[1..];

    /// <summary>
    /// <c>rollback</c> and <c>restart</c> put the state back by keeping its values, which only works if they're
    /// immutable: a <c>List</c> kept is the same list, changed. So each state variable's type must be immutable
    /// throughout (records, enums, Piper's builtin types, immutable collections), checked once types are known.
    /// </summary>
    public static void CheckPolicies(PluginAnalysis analysis, bool traitsAreOpaque)
    {
        foreach (var expansion in analysis.Expansions)
        {
            if (expansion.Data is not ActorData { Failure: { Kind: "rollback" or "restart" } failure } data)
                continue;
            var actor = analysis.Program.Types.OfType<RecordSymbol>().FirstOrDefault(r => expansion.Result.Any(x => ReferenceEquals(x, r.Declaration)) && r.Name == data.Name.Text);
            var spawn = analysis.Program.Methods.FirstOrDefault(m => m.Symbol is MethodSymbol { Name: "Spawn" } method && ReferenceEquals(method.ContainingType.OriginalDefinition, actor));
            if (spawn is null)
                continue;
            foreach (var declaration in BoundTreeWalker.DescendantsAndSelf(spawn.Body).OfType<BoundVariableDeclaration>())
            {
                if (!data.State.Contains(declaration.Variable.Name) ||
                    MutablePart(declaration.Variable.Type, [], traitsAreOpaque) is not { } part)
                    continue;
                analysis.ReportError(expansion.File, failure.Word.Span,
                    $"'{failure.Kind}' puts {data.Name.Text}'s state back by keeping its values, but '{declaration.Variable.Name}' holds a {part}, " +
                    "which could be changed in place, so it wouldn't be put back. Keep immutable values (records, ImmutableList, ImmutableDictionary), or use 'on failure stop'.");
            }
        }
    }

    private static ActorHandler? ParseHandler(PluginParser p, ref bool valid)
    {
        p.ReadWord();
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
