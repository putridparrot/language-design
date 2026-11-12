using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Piper.Plugins;
using Piper.Syntax;

namespace Piper.Plugins.StateMachines;

/// <param name="Guard">The condition after `if`; null when the transition is always taken.</param>
/// <param name="Action">The block run as the machine moves; null for none.</param>
internal sealed record Transition(Token Event, ImmutableArray<ParameterSyntax> Parameters, Token Target, ExpressionSyntax? Guard, BlockExpressionSyntax? Action);

internal sealed record State(Token Name, BlockExpressionSyntax? Enter, BlockExpressionSyntax? Exit, List<Transition> Transitions)
{
    public bool IsFinal => Transitions.Count == 0;
}

/// <summary>A `let` or `mut` in the machine, which becomes a field of its class: private unless <see cref="Public"/>.</summary>
internal sealed record Data(Token? Public, VariableDeclarationSyntax Declaration);

internal sealed record StateMachineDeclaration(Token Name, ImmutableArray<ParameterSyntax> Parameters, List<Data> Data, List<State> States);

/// <summary>
/// Adds <c>statemachine</c> declarations: objects that move between named states as events happen to them, such as an
/// order that's paid, then shipped.
/// <code>
/// statemachine Order(total: decimal) {
///     mut paid = 0m;
///     state Pending {
///         on Pay(amount: decimal) => Paid if paid + amount >= total { paid += amount; }
///         on Pay(amount: decimal) => Pending { paid += amount; }
///         on Cancel() => Cancelled;
///     }
///     state Paid {
///         enter { Print("paid"); }
///         on Ship(code: string) => Shipped;
///     }
///     state Shipped;
///     state Cancelled;
/// }
/// let order = Order(10m);  order.Pay(4m);  order.Pay(6m);  Print(order.State);
/// </code>
/// It's rewritten into ordinary Piper:
/// <list type="bullet">
/// <item>an enum of the states, <c>OrderState</c>; the first is where a machine starts, and one without transitions is
/// final;</item>
/// <item>an enum of the events, <c>OrderEvent</c>, with a variant for each, so events can be stored, sent and replayed;</item>
/// <item>a class, <c>Order</c>, with the machine's data as fields, <c>State</c>, <c>IsFinal</c>, <c>Events</c> (those
/// the current state has transitions for), a method for each event (<c>Pay</c>), which takes the first of the current
/// state's transitions for it whose guard holds, or throws; <c>CanPay</c>, which says whether one would be taken; and
/// <c>Fire(OrderEvent)</c>.</item>
/// </list>
/// A transition runs the state's <c>exit</c> block, then its own action, then moves and runs the new state's
/// <c>enter</c> block; <c>exit</c> and <c>enter</c> run only when the state changes. Once the program is type-checked,
/// the plugin warns about states that can't be reached (<see cref="Analyze"/>).
/// </summary>
public sealed class StateMachinePlugin : IPiperPlugin
{
    private static readonly HashSet<string> GeneratedNames = ["State", "IsFinal", "Events", "Fire", "CanFire", "Resume"];

    /// <summary>The states each expansion can't reach, keyed by the class it wrote, for <see cref="Analyze"/>.</summary>
    private static readonly ConditionalWeakTable<SyntaxNode, List<Token>> Unreachable = new();

    public string Name => "state-machines";

    public IReadOnlyList<PluginKeyword> Keywords { get; } =
    [
        new DeclarationKeyword("statemachine", p => Parse(p) is { } machine ? Expand(p, machine) : [])
        {
            Description = "Declares a state machine: a class that moves between named states as events happen to it. " +
                          "Each event is a method (`order.Pay(10m)`), with `CanPay(…)`, `State`, `IsFinal`, `Events`, and `Fire(OrderEvent)` to replay stored events.",
            Snippet = "statemachine ${1:Order} {\n\tstate ${2:Pending} {\n\t\ton ${3:Pay}() => ${4:Paid};\n\t}\n\tstate ${4:Paid};\n}",
            Words = new Dictionary<string, string>
            {
                ["state"] = "A state. The first is where a machine starts; one with no transitions (`state Done;`) is final.",
                ["on"] = "A transition: `on Event(…) => Target [if guard] [{ action }]`. An event takes the current state's first " +
                         "transition for it whose guard holds; with none, it throws.",
                ["enter"] = "Runs when the machine moves into this state from another.",
                ["exit"] = "Runs when the machine moves out of this state to another.",
                ["public"] = "Makes the machine's data readable (and, for `mut`, settable) from outside.",
            },
        },
    ];

    /// <summary>
    /// Warns about states no chain of transitions reaches from the first, which the machine can never be in. It's a
    /// warning, not an error, so a machine can be written a state at a time.
    /// </summary>
    public void Analyze(PluginAnalysis analysis)
    {
        foreach (var expansion in analysis.Expansions)
        {
            foreach (var node in expansion.Result)
            {
                if (!Unreachable.TryGetValue(node, out var states))
                    continue;
                foreach (var state in states)
                    analysis.ReportWarning(expansion.File, state.Span, $"No transition reaches '{state.Text}', so the machine can never be in it.");
            }
        }
    }

    /// <summary>
    /// statemachine = "statemachine" IDENT [ "(" parameters ")" ] "{" { [ "public" ] ( "let" | "mut" ) variableDeclaration | state } "}" ;
    /// state        = "state" IDENT ( ";" | "{" { "enter" block | "exit" block | transition } "}" ) ;
    /// transition   = "on" IDENT "(" [ parameters ] ")" "=>" IDENT [ "if" expression ] ( ";" | block ) ;
    /// Null after errors in its shape, as generating the machine would only add confusing errors about the generated code.
    /// </summary>
    private static StateMachineDeclaration? Parse(PluginParser p)
    {
        var name = p.Expect(TokenKind.Identifier);
        bool valid = true;
        var parameters = p.Current.Kind == TokenKind.OpenParen ? Parameters(p, ref valid) : [];
        p.Expect(TokenKind.OpenBrace);

        var data = new List<Data>();
        var states = new List<State>();
        while (p.Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile))
        {
            if (p.Current.Kind is TokenKind.LetKeyword or TokenKind.MutKeyword ||
                (p.AtWord("public") && p.Peek(1).Kind is TokenKind.LetKeyword or TokenKind.MutKeyword))
            {
                var @public = p.AtWord("public") ? p.ReadWord() : null;
                var statement = p.ParseVariableDeclaration();
                if (statement is VariableDeclarationSyntax { Using: null } declaration)
                {
                    data.Add(new Data(@public, declaration));
                }
                else
                {
                    p.Report(statement.Span, "A state machine's data is a 'let' or 'mut' with a name, such as 'mut paid = 0m;'.");
                    valid = false;
                }
            }
            else if (p.AtWord("state"))
            {
                if (ParseState(p, ref valid) is { } state)
                    states.Add(state);
            }
            else
            {
                p.Report(p.Current.Span, "Expected the machine's data ('let' or 'mut') or a state ('state Name { … }').");
                valid = false;
                p.NextToken();
            }
        }
        p.Expect(TokenKind.CloseBrace);

        if (states.Count == 0)
        {
            p.Report(name.Span, $"State machine '{name.Text}' has no states; add one, such as 'state Idle {{ on Start() => Running; }}'.");
            return null;
        }
        return valid && Check(p, name, states) ? new StateMachineDeclaration(name, parameters, data, states) : null;
    }

    private static State? ParseState(PluginParser p, ref bool valid)
    {
        p.ReadWord();
        var name = p.Expect(TokenKind.Identifier);
        BlockExpressionSyntax? enter = null, exit = null;
        var transitions = new List<Transition>();
        if (p.Current.Kind == TokenKind.Semicolon)
        {
            p.NextToken();
            return new State(name, enter, exit, transitions);
        }

        p.Expect(TokenKind.OpenBrace);
        while (p.Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile))
        {
            if (p.AtWord("enter") || p.AtWord("exit"))
            {
                var word = p.ReadWord();
                var block = p.ParseBlock();
                if ((word.Text == "enter" ? enter : exit) is not null)
                {
                    p.Report(word.Span, $"State '{name.Text}' already has an '{word.Text}' block.");
                    valid = false;
                }
                else if (word.Text == "enter")
                {
                    enter = block;
                }
                else
                {
                    exit = block;
                }
            }
            else if (p.AtWord("on"))
            {
                if (ParseTransition(p, ref valid) is { } transition)
                    transitions.Add(transition);
            }
            else
            {
                p.Report(p.Current.Span, "Expected a transition ('on Event(…) => State;'), or an 'enter' or 'exit' block.");
                valid = false;
                p.NextToken();
            }
        }
        p.Expect(TokenKind.CloseBrace);
        return new State(name, enter, exit, transitions);
    }

    private static Transition? ParseTransition(PluginParser p, ref bool valid)
    {
        p.ReadWord();
        var name = p.Expect(TokenKind.Identifier);
        if (p.Current.Kind != TokenKind.OpenParen)
        {
            p.Report(p.Current.Span, $"Expected the event's parameters, such as 'on {name.Text}(amount: int)', or '()' for none.");
            valid = false;
            return null;
        }
        var parameters = Parameters(p, ref valid);
        p.Expect(TokenKind.FatArrow);
        var target = p.Expect(TokenKind.Identifier);
        ExpressionSyntax? guard = null;
        if (p.Current.Kind == TokenKind.IfKeyword)
        {
            p.NextToken();
            guard = p.ParseExpressionBeforeBlock();
        }
        BlockExpressionSyntax? action = null;
        if (p.Current.Kind == TokenKind.Semicolon)
            p.NextToken();
        else
            action = p.ParseBlock();
        return new Transition(name, parameters, target, guard, action);
    }

    /// <summary>`(name: Type, …)`: each parameter needs a type, as it becomes a field of the event.</summary>
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

    /// <summary>The checks on the machine as a whole: its names, its targets, and that each event means one thing.</summary>
    private static bool Check(PluginParser p, Token name, List<State> states)
    {
        bool valid = true;
        var names = new HashSet<string>();
        foreach (var state in states)
        {
            if (!names.Add(state.Name.Text))
            {
                p.Report(state.Name.Span, $"State machine '{name.Text}' already has a state called '{state.Name.Text}'.");
                valid = false;
            }
        }

        var events = new Dictionary<string, Transition>();
        foreach (var state in states)
        {
            for (int i = 0; i < state.Transitions.Count; i++)
            {
                var transition = state.Transitions[i];
                var @event = transition.Event.Text;
                if (!names.Contains(transition.Target.Text))
                {
                    p.Report(transition.Target.Span, $"'{transition.Target.Text}' isn't one of {name.Text}'s states.");
                    valid = false;
                }
                if (GeneratedNames.Contains(@event))
                {
                    p.Report(transition.Event.Span, $"'{@event}' is the name of a member every state machine has; call the event something else.");
                    valid = false;
                }
                // A transition after one without a guard, for the same event in the same state, is never taken.
                if (state.Transitions.Take(i).FirstOrDefault(t => t.Event.Text == @event && t.Guard is null) is not null)
                {
                    p.Report(transition.Event.Span, $"This '{@event}' is never taken: an earlier one in '{state.Name.Text}' has no guard, so it always is.");
                    valid = false;
                }
                if (!events.TryGetValue(@event, out var first))
                {
                    events[@event] = transition;
                }
                else if (Signature(first) != Signature(transition))
                {
                    p.Report(transition.Event.Span, $"Every '{@event}' needs the same parameters, as the first does: ({Signature(first)}).");
                    valid = false;
                }
            }
        }
        foreach (var @event in events.Keys)
        {
            if (@event.StartsWith("Can", StringComparison.Ordinal) && events.ContainsKey(@event[3..]))
            {
                p.Report(events[@event].Event.Span, $"'{@event}' is the name of the method that says whether '{@event[3..]}' can happen; call the event something else.");
                valid = false;
            }
        }
        return valid;
    }

    /// <summary>A transition's parameters as written: `amount: decimal, note: string`.</summary>
    private static string Signature(Transition transition) =>
        string.Join(", ", transition.Parameters.Select(parameter => $"{parameter.Name.Text}: {TypeText(parameter.Type)}"));

    private static string TypeText(TypeSyntax type) => type switch
    {
        NamedTypeSyntax named => string.Join(".", named.Qualifier.Append(named.Name).Select(t => t.Text)) +
                                 (named.TypeArguments.IsEmpty ? "" : $"<{string.Join(", ", named.TypeArguments.Select(TypeText))}>"),
        OptionalTypeSyntax optional => TypeText(optional.Inner) + "?",
        ArrayTypeSyntax array => TypeText(array.Element) + $"[{new string(',', array.Rank - 1)}]",
        TupleTypeSyntax tuple => $"({string.Join(", ", tuple.Elements.Select(e => (e.Name is { } n ? n.Text + ": " : "") + TypeText(e.Type)))})",
        FunctionTypeSyntax function => $"({string.Join(", ", function.Parameters.Select(TypeText))}) => {TypeText(function.ReturnType)}",
        _ => type.ToString(),
    };

    /// <summary>The states no chain of transitions reaches from the first.</summary>
    private static List<Token> UnreachableStates(List<State> states)
    {
        var byName = states.GroupBy(s => s.Name.Text).ToDictionary(g => g.Key, g => g.First());
        var reached = new HashSet<string> { states[0].Name.Text };
        var pending = new Queue<State>([states[0]]);
        while (pending.TryDequeue(out var state))
        {
            foreach (var transition in state.Transitions)
            {
                if (reached.Add(transition.Target.Text))
                    pending.Enqueue(byName[transition.Target.Text]);
            }
        }
        return [.. states.Where(s => !reached.Contains(s.Name.Text)).Select(s => s.Name)];
    }

    private static ImmutableArray<DeclarationSyntax> Expand(PluginParser p, StateMachineDeclaration declaration)
    {
        var hole = new TemplateHoles();
        var machine = declaration.Name.Text;
        var stateType = machine + "State";
        var eventType = machine + "Event";
        var states = declaration.States;

        // Each event, in the order it first appears, with the parameters its first transition gives.
        var events = states.SelectMany(s => s.Transitions).GroupBy(t => t.Event.Text).Select(g => g.First()).ToList();
        string Arguments(Transition transition) => string.Join(", ", transition.Parameters.Select(parameter => parameter.Name.Text));

        // The user's blocks and guards each go in a private method of their own, written once, so an error in one is
        // reported once however many transitions use it.
        var members = new List<string>();
        foreach (var data in declaration.Data)
        {
            var variable = data.Declaration;
            var type = variable.Type is null ? "" : $": {hole[variable.Type]}";
            members.Add($"{(data.Public is null ? "" : "public ")}{variable.Keyword.Text} {hole[variable.Name]}{type} = {hole[variable.Initializer]};");
        }
        foreach (var state in states)
        {
            if (state.Enter is not null)
                members.Add($"private fn __Enter{state.Name.Text}() {{ {hole[state.Enter]}; }}");
            if (state.Exit is not null)
                members.Add($"private fn __Exit{state.Name.Text}() {{ {hole[state.Exit]}; }}");
        }
        var guards = new Dictionary<Transition, string>();
        var actions = new Dictionary<Transition, string>();
        int count = 0;
        foreach (var transition in states.SelectMany(s => s.Transitions))
        {
            if (transition.Guard is not null)
            {
                guards[transition] = $"__Guard{count}";
                members.Add($"private fn __Guard{count}({hole[transition.Parameters]}: unit) => bool = {hole[transition.Guard]};");
            }
            if (transition.Action is not null)
            {
                actions[transition] = $"__Action{count}";
                members.Add($"private fn __Action{count}({hole[transition.Parameters]}: unit) {{ {hole[transition.Action]}; }}");
            }
            count++;
        }

        foreach (var @event in events)
        {
            var name = @event.Event.Text;
            var arguments = Arguments(@event);
            var handling = states.Where(s => s.Transitions.Any(t => t.Event.Text == name)).ToList();
            // A match over every state needs no other arm, and would be warned about as unreachable.
            var otherwise = handling.Count < states.Count;

            var arms = handling.Select(state =>
            {
                var steps = state.Transitions.Where(t => t.Event.Text == name).Select(transition =>
                {
                    var moves = state.Name.Text != transition.Target.Text;
                    var target = states.First(s => s.Name.Text == transition.Target.Text);
                    var body = new List<string>();
                    if (moves && state.Exit is not null)
                        body.Add($"this.__Exit{state.Name.Text}();");
                    if (actions.TryGetValue(transition, out var action))
                        body.Add($"this.{action}({arguments});");
                    body.Add($"this.__state = {stateType}.{target.Name.Text};");
                    if (moves && target.Enter is not null)
                        body.Add($"this.__Enter{target.Name.Text}();");
                    body.Add("return;");
                    var block = $"{{ {string.Join(" ", body)} }}";
                    return guards.TryGetValue(transition, out var guard) ? $"if this.{guard}({arguments}) {block}" : block;
                });
                return $"{stateType}.{state.Name.Text} => {{ {string.Join("\n", steps)} }},";
            });
            members.Add($$"""
                /// Handles the '{{name}}' event: the current state's first transition for it whose guard holds is taken.
                /// Throws an InvalidOperationException if there's none.
                fn {{hole[@event.Event]}}({{hole[@event.Parameters]}}: unit) {
                    match this.__state {
                        {{string.Join("\n", arms)}}
                        {{(otherwise ? "_ => {}," : "")}}
                    }
                    throw System.InvalidOperationException($"{{machine}} can't {{name}} while {this.__state}");
                }
                """);

            var can = handling.Select(state =>
            {
                var conditions = state.Transitions.Where(t => t.Event.Text == name)
                    .Select(t => guards.TryGetValue(t, out var guard) ? $"this.{guard}({arguments})" : "true");
                return $"{stateType}.{state.Name.Text} => {string.Join(" || ", conditions)},";
            });
            members.Add($$"""
                /// Whether '{{name}}' would be taken now, in this state and with these arguments.
                fn Can{{name}}({{hole[@event.Parameters]}}: unit) => bool = match this.__state {
                    {{string.Join("\n", can)}}
                    {{(otherwise ? "_ => false," : "")}}
                };
                """);
        }

        var stateNames = states.Select(s => s.Name.Text).ToList();
        var finals = states.Select(s => $"{stateType}.{s.Name.Text} => {(s.IsFinal ? "true" : "false")},");
        var permitted = states.Select(s =>
        {
            var names = s.Transitions.Select(t => t.Event.Text).Distinct().Select(n => $"\"{n}\"").ToList();
            return $"{stateType}.{s.Name.Text} => {(names.Count == 0 ? "System.Collections.Generic.List<string>()" : $"[{string.Join(", ", names)}]")},";
        });
        var variants = events.Select(e => e.Parameters.IsEmpty ? e.Event.Text : $"{e.Event.Text}({hole[e.Parameters]}: unit)");
        var fires = events.Select(e => e.Parameters.IsEmpty
            ? $"{eventType}.{e.Event.Text} => this.{e.Event.Text}(),"
            : $"{eventType}.{e.Event.Text}({Arguments(e)}) => this.{e.Event.Text}({Arguments(e)}),");
        var cans = events.Select(e => e.Parameters.IsEmpty
            ? $"{eventType}.{e.Event.Text} => this.Can{e.Event.Text}(),"
            : $"{eventType}.{e.Event.Text}({Arguments(e)}) => this.Can{e.Event.Text}({Arguments(e)}),");
        var parameters = declaration.Parameters.IsEmpty ? "" : $"({hole[declaration.Parameters]}: unit)";
        // `Resume(state, …)`: the machine's parameters after the state, which mustn't clash with one of them.
        var stateParameter = declaration.Parameters.Any(p => p.Name.Text == "state") ? "savedState" : "state";
        var resumeParameters = declaration.Parameters.IsEmpty ? "" : $", {hole[declaration.Parameters]}: unit";
        var resumeArguments = string.Join(", ", declaration.Parameters.Select(p => p.Name.Text));

        var template = $$"""
            /// The states of a {{machine}}; it starts in {{stateNames[0]}}.
            enum {{stateType}} {
                {{string.Join(",\n", stateNames)}}
            }

            /// What can happen to a {{machine}}, as data: `{{machine}}.Fire` handles one.
            enum {{eventType}} {
                {{string.Join(",\n", events.Count == 0 ? ["__None"] : variants)}}
            }

            class {{hole[declaration.Name]}}{{parameters}} {
                mut __state = {{stateType}}.{{stateNames[0]}};

                {{string.Join("\n\n", members)}}

                /// The state the machine is in.
                prop State => {{stateType}} = this.__state;

                /// Whether the machine has finished: its state has no transitions.
                prop IsFinal => bool = match this.__state {
                    {{string.Join("\n", finals)}}
                };

                /// The events the current state has transitions for, whether or not their guards hold.
                prop Events => System.Collections.Generic.List<string> = match this.__state {
                    {{string.Join("\n", permitted)}}
                };

                /// A machine in a state it reached earlier, such as one saved in a database: made as the constructor makes
                /// one, then put in the state. No `enter` block runs, as it's already there.
                static fn Resume({{stateParameter}}: {{stateType}}{{resumeParameters}}) => {{machine}} {
                    let __resumed = {{machine}}({{resumeArguments}});
                    __resumed.__state = {{stateParameter}};
                    __resumed
                }

                /// Handles an event given as data, as calling its method does.
                fn Fire(event: {{eventType}}) {
                    match event {
                        {{string.Join("\n", events.Count == 0 ? ["_ => {},"] : fires)}}
                    }
                }

                /// Whether `Fire(event)` would take a transition now, rather than throw: as its `CanX` method says.
                fn CanFire(event: {{eventType}}) => bool = match event {
                    {{string.Join("\n", events.Count == 0 ? ["_ => false,"] : cans)}}
                };
            }
            """;
        var result = p.Template.Declarations(template, hole.All);
        var unreachable = UnreachableStates(states);
        if (unreachable.Count > 0)
            Unreachable.AddOrUpdate(result.OfType<ClassDeclarationSyntax>().Single(), unreachable);
        return result;
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
