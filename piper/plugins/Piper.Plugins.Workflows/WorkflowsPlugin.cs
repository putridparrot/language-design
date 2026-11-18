using Piper.Binding;
using Piper.Plugins;
using Piper.Syntax;
using Piper.Text;

namespace Piper.Plugins.Workflows;

/// <summary>
/// Durable workflows: a process that runs for minutes or weeks (an order paid, packed, shipped, refunded if it isn't),
/// surviving restarts by replaying its history of steps, over Piper.Workflows' <c>WorkflowEngine</c>.
/// <code>
/// workflow Fulfil(shop: Shop) {                       // what its activities use, as a consumer's
///     start (order: Order) => Receipt {               // its input, and its result
///         activity payment = shop.Charge(order);      // run once, its result recorded and replayed
///         timer TimeSpan.FromDays(2);                 // a durable timer
///         activity shipped = Shipped(order.id);
///         if !shipped { activity Refund(payment); }
///         Receipt(order.id, payment, shipped)
///     }
/// }
///
/// Fulfil(shop).Register(engine);                      // engine: WorkflowEngine(store)
/// await Fulfil.Start(engine, order.id, order);        // once per id
/// let receipt = await Fulfil.Outcome(engine, order.id);
/// </code>
/// A workflow's code must do the same each time it's replayed, so the compiler checks it, through what it calls (PIP0513):
/// the clock, random numbers, new ids, plain delays, background work, blocking and I/O are errors outside an
/// <c>activity</c>. The workflow's context is <c>flow</c> (<c>await flow.Now()</c>, <c>flow.Record(…)</c>).
/// </summary>
public sealed class WorkflowsPlugin : IPiperPlugin
{
    private const string Engine = "Piper.Workflows.WorkflowEngine";

    /// <summary>Whether a workflow's `start` block is being read: `activity` and `timer` are for there alone.</summary>
    [ThreadStatic]
    private static int _inWorkflow;

    public string Name => "workflows";

    public IReadOnlyDictionary<string, string> Packages { get; } = new Dictionary<string, string>();

    /// <summary>Piper.Workflows: the engine.</summary>
    public IReadOnlyList<string> References => ["Piper.Workflows.dll"];

    public IReadOnlyList<string> Providers => [];

    public IReadOnlyList<PluginKeyword> Keywords { get; } =
    [
        new DeclarationKeyword("workflow", Workflow)
        {
            Description = "A durable workflow: its `start` block runs for as long as it takes (days, with `timer`), surviving restarts by " +
                          "replaying its history, its `activity` steps run once and their results recorded. Its code must do the same each " +
                          "time it's replayed, which the compiler checks. `Name(deps).Register(engine)`, then `Name.Start(engine, id, …)`.",
            Snippet = "workflow ${1:Fulfil}(${2:shop}: ${3:Shop}) {\n\tstart (${4:order}: ${5:Order}) => ${6:Receipt} {\n\t\t$0\n\t}\n}",
            Words = new Dictionary<string, string>
            {
                ["start"] = "The workflow's code, given its input (the parameters, saved with it) and answering its result. `flow` is its context.",
            },
        },
        new StatementKeyword("activity", Activity)
        {
            Description = "A step with effects, in a workflow: `activity payment = shop.Charge(order);` runs the call once and records its " +
                          "result, which a replay gives back without running it again. Retried if it throws; after its last attempt, an " +
                          "ActivityFailedException, which the workflow can catch to compensate. Named after what it calls.",
        },
        new StatementKeyword("timer", Timer)
        {
            Description = "A durable wait, in a workflow: `timer TimeSpan.FromDays(2);`. Its due time is recorded, so a restart meanwhile " +
                          "waits only what's left.",
        },
    ];

    /// <summary>
    /// workflow = "workflow" IDENT [ "(" parameters ")" ] "{" "start" "(" parameters ")" "=>" type block "}" ;
    /// </summary>
    private static IEnumerable<DeclarationSyntax> Workflow(PluginParser p)
    {
        var name = p.Expect(TokenKind.Identifier);
        var dependencies = Parameters(p);
        p.Expect(TokenKind.OpenBrace);
        if (!p.AtWord("start"))
        {
            p.Report(p.Current.Span, "Expected the workflow's code: start (order: Order) => Receipt { … }");
            return [];
        }
        p.ReadWord();
        var inputs = Parameters(p);
        p.Expect(TokenKind.FatArrow);
        var result = p.ParseType();
        BlockExpressionSyntax body;
        _inWorkflow++;
        try
        {
            body = p.ParseAsyncBlock();
        }
        finally
        {
            _inWorkflow--;
        }
        p.Expect(TokenKind.CloseBrace);
        p.Data = new WorkflowData(name, Steps(body));

        // The input arrives as a record, `FulfilStart(order)`, saved with the workflow; its fields are its parameters again.
        var input = new Token(TokenKind.Identifier, name.Span, name.Text + "Start");
        var unpack = inputs.Select(parameter => p.Template.Statement("let $name = input.$name;", ("name", parameter.Name)));
        var statements = new List<StatementSyntax>([.. unpack, .. body.Statements]);
        // The block's last expression is its result; without one, it ends by returning.
        var code = body.Trailing is null ? "{ $statements; }" : "{ $statements; $trailing }";
        var arguments = inputs.Select(parameter => (ExpressionSyntax)new NameExpressionSyntax(parameter.Span, parameter.Name)).ToList();

        return p.Template.Declarations($$"""
            /// What a $name workflow is started with, saved as its input.
            record $input($inputs: unit);

            class $name($dependencies: unit) {
                /// The workflow's code, replayed from its history: it must do the same each time.
                [Piper.Runtime.PiperDeterministic]
                async fn Run(flow: Piper.Workflows.WorkflowContext, input: $input) => System.Threading.Tasks.Task<$result> {{code}}

                /// Makes `engine` run $name workflows with these dependencies.
                fn Register(engine: {{Engine}}) {
                    engine.Register<$input, $result>($label, (flow, input) => Run(flow, input));
                }

                /// Starts a $name workflow called `id`, unless one is already there.
                static fn Start(engine: {{Engine}}, id: string, $inputs: unit) => System.Threading.Tasks.Task =
                    engine.StartAsync<$input>($label, id, $input($arguments));

                /// Sends the $name workflow `id` the signal `signal`, for its `flow.Signal(signal)` (a payment arrived).
                static fn Signal<T>(engine: {{Engine}}, id: string, signal: string, value: T) => System.Threading.Tasks.Task =
                    engine.SignalAsync<T>($label, id, signal, value);

                /// The $name workflow `id`'s result, once it ends: a WorkflowFailedException if it failed.
                static fn Outcome(engine: {{Engine}}, id: string) => System.Threading.Tasks.Task<$result> =
                    engine.ResultAsync<$result>($label, id);
            }
            """,
            ("name", name), ("input", input), ("inputs", inputs), ("dependencies", dependencies), ("result", result),
            ("statements", statements), ("trailing", (object?)body.Trailing ?? Array.Empty<StatementSyntax>()),
            ("label", Text(name.Span, name.Text)), ("arguments", arguments));
    }

    /// <summary>
    /// activity = "activity" [ IDENT "=" ] call ";" : the call, run once as a step of the workflow, named after what it calls.
    /// </summary>
    private static IEnumerable<StatementSyntax> Activity(PluginParser p)
    {
        Token? variable = null;
        if (p.Current.Kind == TokenKind.Identifier && p.Peek(1).Kind == TokenKind.Equals)
        {
            variable = p.NextToken();
            p.NextToken();
        }
        var call = p.ParseExpression();
        p.Expect(TokenKind.Semicolon);
        if (_inWorkflow == 0)
        {
            p.Report(call.Span, "An activity is a step of a workflow: it's for a workflow's `start` block.");
            return [];
        }
        if (call is not CallExpressionSyntax { Callee: var callee } || Named(callee) is not { } named)
        {
            p.Report(call.Span, "An activity is a call, such as `activity payment = shop.Charge(order);`: it's named after what it calls.");
            return [];
        }
        var step = p.Template.Expression("await flow.Activity($label, () => $call)", ("label", Text(callee.Span, named)), ("call", call));
        return variable is null
            ? [p.Template.Statement("$step;", ("step", step))]
            : [p.Template.Statement("let $variable = $step;", ("variable", variable), ("step", step))];
    }

    /// <summary>timer = "timer" expression ";" : a durable wait.</summary>
    private static IEnumerable<StatementSyntax> Timer(PluginParser p)
    {
        var wait = p.ParseExpression();
        p.Expect(TokenKind.Semicolon);
        if (_inWorkflow == 0)
        {
            p.Report(wait.Span, "A timer is a step of a workflow: it's for a workflow's `start` block (elsewhere, `await Task.Delay(…)`).");
            return [];
        }
        return [p.Template.Statement("await flow.Delay($wait);", ("wait", wait))];
    }

    /// <summary>What a workflow noted as it was read: its name, and its steps, in the order they're written.</summary>
    private sealed record WorkflowData(Token Name, List<string> Steps);

    /// <summary>
    /// The workflow's steps as its code writes them: each call on <c>flow</c> that its history records (an activity, a
    /// timer, a signal, a value recorded), described as <c>activity shop.Charge</c>, in the order they appear.
    /// </summary>
    private static List<string> Steps(BlockExpressionSyntax body) =>
    [
        .. SyntaxWalker.DescendantsAndSelf(body).OfType<CallExpressionSyntax>()
            .Where(call => call.Callee is MemberAccessExpressionSyntax { Target: NameExpressionSyntax { Identifier.Text: "flow" } })
            .OrderBy(call => call.Span.Start)
            .Select(call =>
            {
                var member = ((MemberAccessExpressionSyntax)call.Callee).Member.Text;
                var named = call.Arguments.FirstOrDefault() is LiteralExpressionSyntax { Value: string text } ? text : "";
                return member switch
                {
                    "Activity" => $"activity {named}",
                    "Delay" => "timer",
                    "Signal" => $"signal {named}",
                    "Record" => $"recorded {named}",
                    "Now" => "recorded now",
                    "NewGuid" => "recorded guid",
                    _ => null,
                };
            })
            .OfType<string>(),
    ];

    /// <summary>
    /// Once types are known: each workflow's shape (its input's fields, its result, and its steps, each at its place) is
    /// recorded in <c>Name.shape.json</c>, as an api's is, and a change that instances already running couldn't replay
    /// is an error: a step inserted, removed or reordered before others, a new input they weren't started with, another
    /// result. A step added after the others, or an optional input, is fine. <c>--accept-breaking</c> records a
    /// deliberate one (with no instances running, or ready to fail).
    /// </summary>
    public void Analyze(PluginAnalysis analysis)
    {
        foreach (var expansion in analysis.Expansions)
        {
            if (expansion.Data is not WorkflowData data)
                continue;
            var name = data.Name.Text;
            var input = analysis.Program.Types.OfType<RecordSymbol>()
                .FirstOrDefault(r => r.Name == name + "Start" && expansion.Result.Any(x => ReferenceEquals(x, r.Declaration)));
            var run = analysis.Program.Methods.Select(m => m.Symbol).OfType<MethodSymbol>()
                .FirstOrDefault(m => m.Name == "Run" && m.ContainingType.Name == name);
            if (input is null || run is null)
                continue;
            var result = run.ReturnType.TypeArguments.FirstOrDefault() ?? run.ReturnType;
            List<WireOperation> operations =
            [
                new WireOperation("start", $"start {name}", [.. input.Fields.Select(f => (f.Name, f.Type))], result.ToString(), [result]),
                .. data.Steps.Select((step, i) => new WireOperation($"{i + 1}: {step}", step, [], null, [])),
            ];
            analysis.CheckWireShape(expansion.File, data.Name.Span, name, operations, replayed: true);
        }
    }

    /// <summary>What a call calls, as it's written: <c>Refund</c>, <c>shop.Charge</c>; null if it isn't a name.</summary>
    private static string? Named(ExpressionSyntax callee) => callee switch
    {
        NameExpressionSyntax name => name.Identifier.Text,
        MemberAccessExpressionSyntax access when Named(access.Target) is { } target => $"{target}.{access.Member.Text}",
        _ => null,
    };

    /// <summary>`(shop: Shop, mail: Mail)`, each with a type; none if there are no parentheses.</summary>
    private static List<ParameterSyntax> Parameters(PluginParser p)
    {
        var parameters = new List<ParameterSyntax>();
        if (p.Current.Kind != TokenKind.OpenParen)
            return parameters;
        foreach (var parameter in p.ParseLambdaParameters())
        {
            if (parameter.Type is null)
                p.Report(parameter.Span, $"Give '{parameter.Name.Text}' a type, such as ({parameter.Name.Text}: Order).");
            else
                parameters.Add(new ParameterSyntax(parameter.Span, parameter.Name, parameter.Type));
        }
        return parameters;
    }

    private static LiteralExpressionSyntax Text(TextSpan span, string text)
    {
        var token = new Token(TokenKind.StringLiteral, span, "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"", text) { IsGenerated = true };
        return new LiteralExpressionSyntax(span, token, text);
    }
}
