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

    /// <summary>`persisted by sku`: the actor's identity, naming its stream of events; null for an actor that isn't.</summary>
    public ExpressionSyntax? PersistedBy { get; init; }

    /// <summary>Its events (`event Received(quantity: int) { onHand += quantity; }`), the only way a persisted actor's state changes.</summary>
    public List<ActorEvent> Events { get; init; } = [];

    /// <summary>The names of its `transient mut` state, which isn't kept with its events or in its snapshots.</summary>
    public HashSet<string> Transient { get; init; } = [];

    /// <summary>`snapshot every 100;`: how many events between the snapshots of its state; null for none.</summary>
    public ExpressionSyntax? SnapshotEvery { get; init; }

    /// <summary>Its events' upgrades (`upgrade Placed version 1 (…) => Placed(…);`), which read events saved as an older version.</summary>
    public List<ActorUpgrade> Upgrades { get; init; } = [];
}

/// <summary>
/// An event of a persisted actor: its fields, and how it changes the state (replayed as the actor starts).
/// <paramref name="Version"/> is its current version (`event Placed version 2 (…)`), 1 unless it says.
/// </summary>
internal sealed record ActorEvent(Token Name, ImmutableArray<ParameterSyntax> Parameters, BlockExpressionSyntax Body, int Version = 1);

/// <summary>
/// `upgrade Placed version 1 (id: OrderId, total: Money) => Placed(id, total, "GBP");`: how an event saved as version
/// <paramref name="Version"/>, with those fields, is read now. <paramref name="Body"/> makes the next version, calling
/// the event by its name with that version's fields; replaying runs the upgrades in turn, up to the current version.
/// </summary>
internal sealed record ActorUpgrade(Token Name, int Version, ImmutableArray<ParameterSyntax> Parameters, ExpressionSyntax Body);

/// <summary>
/// `on failure resume|rollback|stop|restart [n per unit];`: what an actor does when a handler throws. <paramref name="Limit"/>
/// is how many restarts it may make in <paramref name="Window"/>, after which it stops.
/// </summary>
internal sealed record FailurePolicy(Token Word, string Kind, ExpressionSyntax? Limit, string? Window);

/// <summary>
/// What an actor plugin notes for its analysis: the actor's name, its failure policy, its state's names, and whether
/// it's persisted (whose state, like rollback's, must be immutable).
/// </summary>
internal sealed record ActorData(Token Name, FailurePolicy? Failure, List<string> State, bool Persisted = false)
{
    /// <summary>A persisted actor's events' current versions, by name, for the shape of its events (<see cref="ActorSyntax.CheckEventShapes"/>).</summary>
    public IReadOnlyDictionary<string, int> Versions { get; init; } = new Dictionary<string, int>();
}

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
        ["persisted"] = "The actor keeps its state as events, saved in the store its Spawn takes and replayed as it starts: `persisted by sku` names each one's stream.",
        ["by"] = "What identifies each actor, naming the stream its events are kept in: `persisted by sku` keeps Product/tea's.",
        ["event"] = "Something that happened to a persisted actor, and how it changes the state: `event Received(quantity: int) { onHand += quantity; }`. Handlers `emit` them; replaying them rebuilds the state.",
        ["transient"] = "A persisted actor's state that isn't kept: `transient mut asked = …;`. Its handlers change it themselves (no event), it starts afresh whenever the actor starts, " +
                        "and it's put back like the rest when a handler fails. For what isn't worth keeping, such as when each client last asked.",
        ["snapshot"] = "`snapshot every 100;`: keep the persisted actor's state (all but its transient state) after every 100 events saved, so starting again restores the latest " +
                       "and replays only the events since. One kept as other types than the state's now is ignored, and every event replayed.",
        ["every"] = "How many events between a persisted actor's snapshots: `snapshot every 100;`.",
        ["version"] = "An event's version: `event Placed version 2 (…) { … }`. Events are saved with theirs, so one of an older version is read through its `upgrade`.",
        ["upgrade"] = "How an event saved as an older version is read now: `upgrade Placed version 1 (id: OrderId, total: Money) => Placed(id, total, \"GBP\");`. " +
                      "The event's name makes the next version; replaying runs the upgrades in turn, up to the current one.",
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
        // `persisted by sku`: its events are kept, in the stream its identity names, and replayed as it starts.
        Token? persisted = null;
        ExpressionSyntax? persistedBy = null;
        if (p.AtWord("persisted"))
        {
            persisted = p.ReadWord();
            if (p.AtWord("by"))
            {
                p.ReadWord();
                persistedBy = p.ParseExpressionBeforeBlock();
            }
            else
            {
                p.Report(p.Current.Span, $"Expected what identifies each {name.Text}, whose events are kept under it: 'persisted by sku'.");
                valid = false;
            }
            foreach (var parameter in parameters.Where(parameter => parameter.Name.Text == "store"))
            {
                p.Report(parameter.Span, "A persisted actor's Spawn takes 'store' itself (where its events are kept), so its own parameter needs another name.");
                valid = false;
            }
        }
        p.Expect(TokenKind.OpenBrace);

        var state = new List<StatementSyntax>();
        var handlers = new List<ActorHandler>();
        var events = new List<ActorEvent>();
        var upgrades = new List<ActorUpgrade>();
        var functions = new List<StatementSyntax>();
        var transient = new HashSet<string>();
        ExpressionSyntax? snapshotEvery = null;
        FailurePolicy? failure = null;
        while (p.Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile))
        {
            if (p.AtWord("event") && p.Peek(1).Kind == TokenKind.Identifier &&
                (p.Peek(2).Kind == TokenKind.OpenParen || p.Peek(2) is { Kind: TokenKind.Identifier, Text: "version" }))
            {
                p.ReadWord();
                var eventName = p.Expect(TokenKind.Identifier);
                var version = ParseVersion(p, ref valid) ?? 1;
                var fields = Parameters(p, ref valid);
                events.Add(new ActorEvent(eventName, fields, p.ParseBlock(), version));
                continue;
            }
            // `upgrade Placed version 1 (id: OrderId, total: Money) => Placed(id, total, "GBP");`
            if (p.AtWord("upgrade") && p.Peek(1).Kind == TokenKind.Identifier && p.Peek(2) is { Kind: TokenKind.Identifier, Text: "version" })
            {
                p.ReadWord();
                var eventName = p.Expect(TokenKind.Identifier);
                var version = ParseVersion(p, ref valid) ?? 1;
                var fields = Parameters(p, ref valid);
                p.Expect(TokenKind.FatArrow);
                var body = p.ParseExpression();
                p.Expect(TokenKind.Semicolon);
                upgrades.Add(new ActorUpgrade(eventName, version, fields, body));
                continue;
            }
            // `transient mut seen = …;`: a persisted actor's state that isn't kept, which its handlers change themselves.
            if (p.AtWord("transient") && p.Peek(1).Kind is TokenKind.MutKeyword)
            {
                var word = p.ReadWord();
                var declaration = p.ParseVariableDeclaration();
                state.Add(declaration);
                if (persisted is null)
                {
                    p.Report(word.Span, $"Only a persisted actor's state can be transient: none of {name.Text}'s is kept.");
                    valid = false;
                }
                else if (declaration is VariableDeclarationSyntax { Name: { IsMissing: false } transientName })
                {
                    transient.Add(transientName.Text);
                }
                continue;
            }
            // `snapshot every 100;`: keep the state now and then, so starting replays only the events since.
            if (p.AtWord("snapshot") && p.Peek(1) is { Kind: TokenKind.Identifier, Text: "every" })
            {
                var word = p.ReadWord();
                p.ReadWord();
                var every = p.ParseExpression();
                p.Expect(TokenKind.Semicolon);
                if (persisted is null)
                {
                    p.Report(word.Span, $"Only a persisted actor takes snapshots, of the state its events make: 'actor {name.Text}(…) persisted by id {{ … }}'.");
                    valid = false;
                }
                else if (snapshotEvery is not null)
                {
                    p.Report(word.Span, $"Actor '{name.Text}' already says how often it takes snapshots.");
                    valid = false;
                }
                snapshotEvery ??= every;
                continue;
            }
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
            else if (failure is { Kind: "rollback" or "restart" } || persisted is not null)
            {
                var why = persisted is not null ? "A persisted actor's state is put back when a handler fails" : $"'{failure!.Kind}' puts the actor's state back";
                p.Report(statement.Span, $"{why}, so declare it one name at a time: 'mut count = 0;'.");
                valid = false;
            }
        }
        if (!CheckEvents(p, name, persisted, events, handlers, mutable, transient, failure))
            valid = false;
        if (!CheckUpgrades(p, name, persisted, events, upgrades))
            valid = false;
        // After all the state, which they may use.
        state.AddRange(functions);
        return valid
            ? new ActorDeclaration(name, parameters, state, handlers)
            {
                Failure = failure, Mutable = mutable, PersistedBy = persistedBy, Events = events, Transient = transient, SnapshotEvery = snapshotEvery,
                Upgrades = upgrades,
            }
            : null;
    }

    /// <summary>Each event's current version, by name.</summary>
    public static IReadOnlyDictionary<string, int> Versions(ActorDeclaration actor) =>
        actor.Events.GroupBy(e => e.Name.Text).ToDictionary(g => g.Key, g => g.First().Version);

    /// <summary>`version 2`, after an event's or upgrade's name: a whole number from 1. Null when there's none.</summary>
    private static int? ParseVersion(PluginParser p, ref bool valid)
    {
        if (!p.AtWord("version"))
            return null;
        p.ReadWord();
        var number = p.Expect(TokenKind.IntegerLiteral);
        if (number.IsMissing)
        {
            valid = false;
            return 1;
        }
        if (!int.TryParse(number.Text.Replace("_", ""), out var version) || version < 1)
        {
            p.Report(number.Span, "A version is a whole number from 1: 'version 2'.");
            valid = false;
            return 1;
        }
        return version;
    }

    /// <summary>
    /// An event's upgrades: each names one of the actor's events, at a version before its current one, once; each
    /// version's upgrade makes the next, so the next must be the current version or have an upgrade of its own; and its
    /// body, like an event's, is replayed, so it can't await or emit.
    /// </summary>
    private static bool CheckUpgrades(PluginParser p, Token name, Token? persisted, List<ActorEvent> events, List<ActorUpgrade> upgrades)
    {
        bool valid = true;
        void Error(TextSpan span, string message)
        {
            p.Report(span, message);
            valid = false;
        }

        if (persisted is null)
        {
            foreach (var upgrade in upgrades)
                Error(upgrade.Name.Span, $"An upgrade reads events saved before, which only a persisted actor keeps: 'actor {name.Text}(…) persisted by id {{ … }}'.");
            return valid;
        }
        var current = events.GroupBy(e => e.Name.Text).ToDictionary(g => g.Key, g => g.First().Version);
        var declared = new HashSet<(string, int)>();
        foreach (var upgrade in upgrades)
        {
            var eventName = upgrade.Name.Text;
            if (!current.TryGetValue(eventName, out var version))
            {
                Error(upgrade.Name.Span, $"{name.Text} has no event '{eventName}' to upgrade; its events are {string.Join(", ", current.Keys.Order())}.");
                continue;
            }
            if (upgrade.Version >= version)
            {
                Error(upgrade.Name.Span, version == 1
                    ? $"'{eventName}' is version 1, so there's nothing older to upgrade; when its fields change, make it 'event {eventName} version 2 (…)', and upgrade version 1 to it."
                    : $"'{eventName}' is version {version}, so an upgrade reads an older one: version 1 to {version - 1}.");
                continue;
            }
            if (!declared.Add((eventName, upgrade.Version)))
                Error(upgrade.Name.Span, $"{name.Text} already upgrades '{eventName}' version {upgrade.Version}.");
        }
        foreach (var upgrade in upgrades)
        {
            if (current.TryGetValue(upgrade.Name.Text, out var version) && upgrade.Version < version && upgrade.Version + 1 < version &&
                !declared.Contains((upgrade.Name.Text, upgrade.Version + 1)))
            {
                Error(upgrade.Name.Span,
                    $"Upgrading '{upgrade.Name.Text}' version {upgrade.Version} makes version {upgrade.Version + 1}, which is upgraded in turn, " +
                    $"but there's no 'upgrade {upgrade.Name.Text} version {upgrade.Version + 1} (…) => {upgrade.Name.Text}(…);'.");
            }
            foreach (var node in SyntaxWalker.DescendantsAndSelf(upgrade.Body))
            {
                if (node is AwaitExpressionSyntax wait)
                    Error(wait.Span, "An upgrade is run as the actor replays its events, so it can't await.");
                else if (node is NameExpressionSyntax emitted && emitted.Identifier.Text.StartsWith(EmitPrefix, StringComparison.Ordinal))
                    Error(emitted.Span, "An upgrade makes the event's next version; it can't emit another.");
            }
        }
        return valid;
    }

    /// <summary>
    /// A persisted actor's rules: it has events; its handlers change its state only through them (`emit`), so replaying
    /// them rebuilds it; each `emit` names one of them; an event's body only changes state (no `await`, no `emit`); and
    /// it doesn't `resume` after a failure, as its state is always put back with its unsaved events. Events need a
    /// persisted actor.
    /// </summary>
    private static bool CheckEvents(
        PluginParser p, Token name, Token? persisted, List<ActorEvent> events, List<ActorHandler> handlers, List<string> mutable, HashSet<string> transient,
        FailurePolicy? failure)
    {
        bool valid = true;
        void Error(TextSpan span, string message)
        {
            p.Report(span, message);
            valid = false;
        }

        var emits = handlers.SelectMany(h => SyntaxWalker.DescendantsAndSelf(h.Body)).Concat(events.SelectMany(e => SyntaxWalker.DescendantsAndSelf(e.Body)))
            .OfType<NameExpressionSyntax>().Where(n => n.Identifier.Text.StartsWith(EmitPrefix, StringComparison.Ordinal)).ToList();
        if (persisted is null)
        {
            foreach (var @event in events)
                Error(@event.Name.Span, $"Event '{@event.Name.Text}' needs a persisted actor, whose events are kept: 'actor {name.Text}(…) persisted by id {{ … }}'.");
            foreach (var emit in emits)
                Error(emit.Span, $"Only a persisted actor emits events: 'actor {name.Text}(…) persisted by id {{ … }}'.");
            return valid;
        }

        if (events.Count == 0)
            Error(persisted.Span, $"{name.Text} is persisted but has no events, so nothing of it is kept; add one, such as 'event Received(quantity: int) {{ onHand += quantity; }}'.");
        if (failure is { Kind: "resume" })
            Error(failure.Word.Span, "A persisted actor can't resume after a failure: its unsaved events are dropped, so its state is put back too. Use rollback (the default), restart or stop.");

        var known = events.Select(e => e.Name.Text).ToHashSet();
        var seen = new HashSet<string>();
        foreach (var @event in events.Where(e => !seen.Add(e.Name.Text)))
            Error(@event.Name.Span, $"{name.Text} already has an event called '{@event.Name.Text}'.");
        foreach (var emit in emits.Where(e => !known.Contains(e.Identifier.Text[EmitPrefix.Length..])))
            Error(emit.Span, $"{name.Text} has no event '{emit.Identifier.Text[EmitPrefix.Length..]}'; its events are {string.Join(", ", known.Order())}.");

        // A handler that changed the state itself would change it in a way replaying the events can't repeat. Transient
        // state isn't replayed, so handlers change it, and events can't (replaying would change it again).
        var state = mutable.Where(m => !transient.Contains(m)).ToHashSet();
        foreach (var assignment in handlers.SelectMany(h => SyntaxWalker.DescendantsAndSelf(h.Body)).OfType<AssignmentExpressionSyntax>())
        {
            if (assignment.Target is NameExpressionSyntax target && state.Contains(target.Identifier.Text))
                Error(assignment.Span, $"A persisted actor's state changes only through its events, which are replayed as it starts: emit one whose body changes '{target.Identifier.Text}'.");
        }
        foreach (var @event in events)
        {
            foreach (var node in SyntaxWalker.DescendantsAndSelf(@event.Body))
            {
                if (node is AssignmentExpressionSyntax { Target: NameExpressionSyntax target } changed && transient.Contains(target.Identifier.Text))
                    Error(changed.Span, $"'{target.Identifier.Text}' is transient, so it isn't kept, and an event's body can't change it: replaying the events would. Change it in the handler.");
                else if (node is AwaitExpressionSyntax wait)
                    Error(wait.Span, "An event's body changes the state, and is replayed as the actor starts, so it can't await: do that in the handler, before it emits.");
                else if (node is NameExpressionSyntax emitted && emitted.Identifier.Text.StartsWith(EmitPrefix, StringComparison.Ordinal))
                    Error(emitted.Span, "An event's body can't emit another: replaying it would emit again. Emit both from the handler.");
            }
        }
        return valid;
    }

    /// <summary>What `emit Received(5);` calls: the actor's function applying the event and keeping it to save.</summary>
    public const string EmitPrefix = "__emit_";

    /// <summary>The `emit` statement, which each actor plugin adds.</summary>
    public static StatementKeyword EmitKeyword() => new("emit", Emit)
    {
        Description = "In a persisted actor's handler: `emit Received(quantity);` applies the event to the state, and saves it with the " +
                      "message's other events when the handler finishes (none are saved if it throws).",
    };

    /// <summary>
    /// emit = "emit" IDENT "(" [ arguments ] ")" ";" ; in a persisted actor's handler: applies the event to the state,
    /// and keeps it to save when the handler finishes. It's `__emit_Received(5);`, which the actor declares.
    /// </summary>
    public static IEnumerable<StatementSyntax> Emit(PluginParser p)
    {
        var expression = p.ParseExpression();
        p.Expect(TokenKind.Semicolon);
        if (expression is not CallExpressionSyntax { Callee: NameExpressionSyntax { Identifier: var eventName } } call)
        {
            p.Report(expression.Span, "Expected the event to emit, with its fields: 'emit Received(quantity);'.");
            return [];
        }
        var callee = new NameExpressionSyntax(eventName.Span, new Token(TokenKind.Identifier, eventName.Span, EmitPrefix + eventName.Text));
        return [new ExpressionStatementSyntax(call.Span, call with { Callee = callee })];
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
    /// A persisted actor always drops the failed message's unsaved events and puts its state back as before it (so
    /// the default for it is rollback); its restart goes back to Spawn's state and replays its saved events, reading
    /// its stream afresh, which also recovers from another writer having added to it.
    public static (string Setup, string Before, string OnFailure) PolicyCode(ActorDeclaration actor, TemplateHoles hole, string stop)
    {
        string Restore(string from) => string.Concat(actor.Mutable.Select(name => $"{name} = {from}{name}; "));
        bool persisted = actor.PersistedBy is not null;
        var kind = actor.Failure?.Kind ?? (persisted ? "rollback" : "resume");

        var setup = new System.Text.StringBuilder();
        var before = "";
        if (kind == "rollback")
        {
            setup.Append(string.Concat(actor.Mutable.Select(name => $"mut __saved_{name} = {name};\n")));
            before = string.Concat(actor.Mutable.Select(name => $"__saved_{name} = {name}; "));
        }
        if (kind == "restart")
            setup.Append(string.Concat(actor.Mutable.Select(name => $"let __initial_{name} = {name};\n")));
        if (actor.Failure is { Kind: "restart", Limit: { } limit, Window: var window })
            setup.Append($"let __restarts = Piper.Runtime.PiperRestarts({hole[limit]}, System.TimeSpan.From{Capitalized(window!)}s(1));\n");

        var restart = Restore("__initial_") + (persisted ? Replay(actor) : "");
        var action = kind switch
        {
            "rollback" => Restore("__saved_"),
            "restart" when actor.Failure!.Limit is not null => $"if __restarts.Allow() {{ {restart}}} else {{ {stop} }}",
            "restart" => restart,
            "stop" => stop,
            _ => "",
        };
        return (setup.ToString(), before, (persisted ? "__events.Discard(); " : "") + action);
    }

    /// <summary>
    /// Rebuilds a persisted actor's state from its saved events (after putting it back as Spawn made it); one that
    /// takes snapshots restores its latest first, and replays only the events after it.
    /// </summary>
    public static string Replay(ActorDeclaration actor) =>
        (actor.SnapshotEvery is null ? "" : "__restore(await __events.LoadSnapshotAsync()); ") +
        "for __event in await __events.ReplayAsync() { __apply(__event); } ";

    /// <summary>
    /// Saves the events the handled message emitted, together, before its reply is sent; and for an actor that takes
    /// snapshots, its state once enough events have been saved since the last.
    /// </summary>
    public static string Commit(ActorDeclaration actor, TemplateHoles hole) =>
        actor.PersistedBy is null ? "" :
        actor.SnapshotEvery is { } every ? $"await __events.CommitAsync(); if __events.SnapshotDue({hole[every]}) {{ await __events.SaveSnapshotAsync(__capture()); }}" :
        "await __events.CommitAsync();";

    /// <summary>The state a snapshot keeps: a persisted actor's `mut` state that isn't transient.</summary>
    private static IEnumerable<string> Kept(ActorDeclaration actor) => actor.Mutable.Where(name => !actor.Transient.Contains(name));

    /// <summary>
    /// A persisted actor's events, for its plugin's template: <c>Enum</c>, the <c>{Actor}Event</c> enum (a variant for
    /// each event, saved as JSON); and <c>Setup</c>, after its state in Spawn: its event log (the stream its identity
    /// names, in the <c>store</c> Spawn takes), <c>__apply</c> (each event's body, changing the state), and an <c>__emit_…</c> for
    /// each, which applies the event and keeps it to save. Empty for an actor that isn't persisted.
    /// </summary>
    public static (string Enum, string Setup) EventCode(ActorDeclaration actor, TemplateHoles hole)
    {
        if (actor.PersistedBy is not { } identity)
            return ("", "");
        var type = actor.Name.Text + "Event";
        var variants = actor.Events.Select(e => e.Parameters.IsEmpty ? e.Name.Text : $"{e.Name.Text}({hole[e.Parameters]}: unit)");
        var arms = actor.Events.Select(e =>
        {
            var fields = e.Parameters.IsEmpty ? "" : $"({string.Join(", ", e.Parameters.Select(f => f.Name.Text))})";
            return $"{type}.{e.Name.Text}{fields} => {hole[e.Body]},";
        });
        var emits = actor.Events.Select(e =>
        {
            var arguments = e.Parameters.IsEmpty ? "" : $"({string.Join(", ", e.Parameters.Select(f => f.Name.Text))})";
            // A local function, whose parameters are written one by one (a whole list's hole is for declarations).
            var parameters = string.Join(", ", e.Parameters.Select(f => $"{f.Name.Text}: {hole[f.Type]}"));
            return $"fn {EmitPrefix}{e.Name.Text}({parameters}) {{\n" +
                   $"    let __event = {type}.{e.Name.Text}{arguments};\n    __apply(__event);\n    __events.Add(__event);\n}}\n";
        });
        // The stream is the actor's name and its identity, written invariantly (a template's hole can't go in a $"…").
        var (pastEnum, upgrades) = UpgradeCode(actor, hole, type);
        var setup =
            $"let __events = Piper.Runtime.PiperEventLog<{type}>(store, System.String.Concat(\"{actor.Name.Text}/\", " +
            $"System.Convert.ToString({hole[identity]}, System.Globalization.CultureInfo.InvariantCulture)));\n" +
            $"fn __apply(__applied: {type}) {{\n    match __applied {{\n{string.Join("\n", arms)}\n    }}\n}}\n" +
            string.Concat(emits) +
            upgrades +
            (actor.SnapshotEvery is null ? "" : SnapshotCode(actor));
        // Indented: a `fn` that starts a line is read as the next top-level declaration, not a local function.
        return ($"enum {type} {{\n{string.Join(",\n", variants)}\n}}\n" + pastEnum, string.Join("\n", setup.Split('\n').Select(line => "    " + line)));
    }

    /// <summary>
    /// For an actor whose events have versions: <c>Enum</c>, <c>{Actor}PastEvent</c>, a variant for each older version
    /// an upgrade reads (<c>Placed_1(id: OrderId, total: Money)</c>); and <c>Setup</c>, an <c>__upgrade_Placed_1</c> for
    /// each, in which the event's name makes the next version (the current one, or the next upgrade's), then
    /// <c>__upgrade</c>, which upgrades any of them to the current version, given to the event log with each event's
    /// current version, so it saves events with theirs and reads older ones through the upgrades.
    /// </summary>
    private static (string Enum, string Setup) UpgradeCode(ActorDeclaration actor, TemplateHoles hole, string type)
    {
        var versions = string.Join(",", actor.Events.Where(e => e.Version > 1).Select(e => $"{e.Name.Text}:{e.Version}"));
        if (actor.Upgrades.Count == 0)
            return ("", versions.Length == 0 ? "" : $"__events.Versions(\"{versions}\");\n");

        var past = actor.Name.Text + "PastEvent";
        static string Variant(ActorUpgrade u) => $"{u.Name.Text}_{u.Version}";
        static string Names(ImmutableArray<ParameterSyntax> fields) => string.Join(", ", fields.Select(f => f.Name.Text));
        string Typed(ImmutableArray<ParameterSyntax> fields) => string.Join(", ", fields.Select(f => $"{f.Name.Text}: {hole[f.Type]}"));

        var functions = new System.Text.StringBuilder();
        // The latest first: each calls the next version's.
        foreach (var upgrade in actor.Upgrades.OrderBy(u => u.Name.Text, StringComparer.Ordinal).ThenByDescending(u => u.Version))
        {
            var current = actor.Events.First(e => e.Name.Text == upgrade.Name.Text);
            var next = actor.Upgrades.FirstOrDefault(u => u.Name.Text == upgrade.Name.Text && u.Version == upgrade.Version + 1);
            var nextFields = next?.Parameters ?? current.Parameters;
            var made = next is not null ? $"__upgrade_{Variant(next)}({Names(nextFields)})"
                : nextFields.IsEmpty ? $"{type}.{current.Name.Text}"
                : $"{type}.{current.Name.Text}({Names(nextFields)})";
            functions.Append($"fn __upgrade_{Variant(upgrade)}({Typed(upgrade.Parameters)}) => {type} {{\n")
                .Append($"    fn {upgrade.Name.Text}({Typed(nextFields)}) => {type} = {made};\n")
                .Append($"    {hole[upgrade.Body]}\n}}\n");
        }
        var arms = actor.Upgrades.Select(u => u.Parameters.IsEmpty
            ? $"{past}.{Variant(u)} => __upgrade_{Variant(u)}(),"
            : $"{past}.{Variant(u)}({Names(u.Parameters)}) => __upgrade_{Variant(u)}({Names(u.Parameters)}),");
        functions.Append($"fn __upgrade(__past: {past}) => {type} {{\n    match __past {{\n{string.Join("\n", arms)}\n    }}\n}}\n")
            .Append($"__events.Upgrades<{past}>(\"{versions}\", (__past) => __upgrade(__past));\n");

        var variants = actor.Upgrades.Select(u => u.Parameters.IsEmpty ? Variant(u) : $"{Variant(u)}({hole[u.Parameters]}: unit)");
        return ($"enum {past} {{\n{string.Join(",\n", variants)}\n}}\n", functions.ToString());
    }

    /// <summary>
    /// <c>__capture</c>, the state as a snapshot; and <c>__restore</c>, which puts a snapshot's state back, all of it
    /// or (if it was kept as other types than the state's now) none, and then the events are replayed from the first.
    /// </summary>
    private static string SnapshotCode(ActorDeclaration actor)
    {
        var kept = Kept(actor).ToList();
        var capture = "Piper.Runtime.PiperSnapshot()" + string.Concat(kept.Select(name => $".With(\"{name}\", {name})"));
        var read = string.Concat(kept.Select(name => $"            let __kept_{name} = __snapshot.Get(\"{name}\", {name});\n"));
        var assign = string.Concat(kept.Select(name => $"            {name} = __kept_{name};\n"));
        return $"fn __capture() => Piper.Runtime.PiperSnapshot = {capture};\n" +
               "fn __restore(__snapshot: Piper.Runtime.PiperSnapshot) {\n" +
               "    if __snapshot.Found {\n" +
               "        try {\n" + read + assign +
               "        } catch (_: System.Exception) {\n" +
               "            __events.ForgetSnapshot();\n" +
               "        }\n" +
               "    }\n" +
               "}\n";
    }

    private static string Capitalized(string word) => char.ToUpperInvariant(word[0]) + word[1..];

    /// <summary>
    /// A persisted actor's events are kept for ever, and read back by every later version of it, so each version of
    /// each event it reads (the current one, and those its upgrades read) is recorded in <c>Name.events.json</c> beside
    /// it, with the types they hold. A change that events already saved couldn't be read as is an error: a version
    /// recorded that nothing reads now (an event removed, or an upgrade), a new field they don't have, a field of
    /// another type, or a change to a type they hold. Changing an event's fields means a new version, with an upgrade
    /// from the old one. As every shape is, it's recorded when it's new or the change compatible.
    /// </summary>
    public static void CheckEventShapes(PluginAnalysis analysis)
    {
        foreach (var expansion in analysis.Expansions)
        {
            if (expansion.Data is not ActorData { Persisted: true } data)
                continue;
            bool Generated(SyntaxNode? node) => node is not null && expansion.Result.Any(r => ReferenceEquals(r, node));
            var enums = analysis.Program.Types.OfType<EnumSymbol>().Where(e => Generated(e.Declaration)).ToList();
            var actor = data.Name.Text;
            if (enums.FirstOrDefault(e => e.Name == actor + "Event") is not { } events)
                continue;
            static WireOperation Version(string name, int version, IEnumerable<FieldSymbol> fields) =>
                new($"{name} version {version}", $"{name} version {version}", [.. fields.Select(f => (f.Name, f.Type))], null, []);
            var operations = events.Variants.Select(v => Version(v.Name, data.Versions.GetValueOrDefault(v.Name, 1), v.Fields)).ToList();
            if (enums.FirstOrDefault(e => e.Name == actor + "PastEvent") is { } past)
            {
                foreach (var variant in past.Variants)
                {
                    var at = variant.Name.LastIndexOf('_');
                    operations.Add(Version(variant.Name[..at], int.Parse(variant.Name[(at + 1)..], System.Globalization.CultureInfo.InvariantCulture), variant.Fields));
                }
            }
            var shape = WireShapes.Describe(actor, operations);
            analysis.CheckRecordedShape(expansion.File, data.Name.Span, $"{actor} events", $"{actor}.events.json", shape,
                before => EventBreaks(actor, before, shape).Select(change => (data.Name.Span, change)));
        }
    }

    /// <summary>What in <paramref name="now"/> events saved as <paramref name="before"/> recorded couldn't be read as.</summary>
    private static IEnumerable<string> EventBreaks(string actor, System.Text.Json.Nodes.JsonObject before, System.Text.Json.Nodes.JsonObject now)
    {
        const string Saved = "events saved already";
        static string? Text(System.Text.Json.Nodes.JsonNode? node) =>
            node is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        var newVersions = now["calls"] as System.Text.Json.Nodes.JsonObject ?? [];
        foreach (var (key, oldNode) in before["calls"] as System.Text.Json.Nodes.JsonObject ?? [])
        {
            var at = key.LastIndexOf(" version ", StringComparison.Ordinal);
            var (name, version) = at < 0 ? (key, "1") : (key[..at], key[(at + " version ".Length)..]);
            var change = $"or give '{name}' a new version, with an upgrade from version {version}";
            if (newVersions[key] is not System.Text.Json.Nodes.JsonObject newVersion)
            {
                yield return newVersions.Any(v => v.Key.StartsWith(name + " version ", StringComparison.Ordinal))
                    ? $"{name} events of version {version} may be saved already, but nothing reads them now; add 'upgrade {name} version {version} (…) => {name}(…);'"
                    : $"{actor} has no event '{name}' now, but {Saved} include them; keep it, so replaying them changes the state as it did";
                continue;
            }
            var oldFields = oldNode?["inputs"] as System.Text.Json.Nodes.JsonObject ?? [];
            foreach (var (field, type) in newVersion["inputs"] as System.Text.Json.Nodes.JsonObject ?? [])
            {
                var typeText = Text(type) ?? "";
                if (oldFields[field] is not { } oldType)
                {
                    if (!typeText.EndsWith('?'))
                        yield return $"{name} version {version} has a new field, '{field}' ({typeText}), which {Saved} don't have; make it optional ({typeText}?), {change}";
                }
                else if (Text(oldType) != typeText)
                {
                    yield return $"{name} version {version} has '{field}' as {typeText}, but {Saved} have {Text(oldType)}; {change}";
                }
            }
        }
        foreach (var change in WireShapes.BreakingTypes(before, now, Saved))
            yield return change;
    }

    /// <summary>
    /// <c>rollback</c> and <c>restart</c> put the state back by keeping its values, which only works if they're
    /// immutable: a <c>List</c> kept is the same list, changed. So each state variable's type must be immutable
    /// throughout (records, enums, Piper's builtin types, immutable collections), checked once types are known.
    /// </summary>
    public static void CheckPolicies(PluginAnalysis analysis, bool traitsAreOpaque)
    {
        foreach (var expansion in analysis.Expansions)
        {
            if (expansion.Data is not ActorData data || (data.Failure is not { Kind: "rollback" or "restart" } && !data.Persisted))
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
                var (span, why, instead) = data.Persisted
                    ? (data.Name.Span, $"{data.Name.Text} is persisted, so its state changes only through events and is put back when a handler fails", "")
                    : (data.Failure!.Word.Span, $"'{data.Failure.Kind}' puts {data.Name.Text}'s state back", ", or use 'on failure stop'");
                analysis.ReportError(expansion.File, span,
                    $"{why}, by keeping its values; but '{declaration.Variable.Name}' holds a {part}, which could be changed in place, " +
                    $"so it wouldn't be put back. Keep immutable values (records, ImmutableList, ImmutableDictionary){instead}.");
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
