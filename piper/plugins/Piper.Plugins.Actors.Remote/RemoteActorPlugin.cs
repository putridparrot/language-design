using System.Collections.Immutable;
using Piper.Plugins;
using Piper.Syntax;

namespace Piper.Plugins.Actors;

/// <summary>
/// Adds <c>actor</c> declarations, as <see cref="ActorPlugin"/> does, but on Proto.Actor, so an actor in one process can
/// be used from others, on the same machine or across a network:
/// <code>
/// let system = Proto.ActorSystem().WithRemote(Proto.Remote.RemoteConfig.BindToLocalhost(8000));
/// await system.Remote().StartAsync();
/// let counter = Counter.Spawn(system, "counter", 10);                    // host it here, under a name
/// let remote = Counter.Connect(system, "127.0.0.1:8000", "counter");     // or reach one hosted elsewhere
/// remote.Increment(5);  Print(await remote.Get());
/// </code>
/// It's rewritten into ordinary Piper:
/// <list type="bullet">
/// <item>an enum of the messages, <c>CounterMessage</c>, which Proto.Actor sends between processes as JSON;</item>
/// <item>a record, <c>Counter</c>, holding the actor system and the actor's address (its PID), with a method for each
/// handler: a tell sends the message, an ask sends it and awaits the reply, for up to 10 seconds by default
/// (<c>counter.WithTimeout(TimeSpan.FromSeconds(30))</c> changes that);</item>
/// <item><c>Counter.Spawn(system, name, …)</c>, which declares the state and makes a Proto.Actor actor whose receive
/// function matches each message to its handler, and <c>Counter.Connect(system, address, name)</c>.</item>
/// </list>
/// An ask whose handler throws sends back a <c>CounterFault</c>, which the caller throws as an exception; a tell's
/// exception is written to the error output. The plugin brings in Proto.Remote and ASP.NET Core (whose server carries
/// Proto.Actor's gRPC), and warns about message fields that can't be sent as data (<see cref="Analyze"/>).
/// </summary>
public sealed class RemoteActorPlugin : IPiperPlugin
{
    private static readonly HashSet<string> GeneratedNames = ["Spawn", "Connect", "WithTimeout", "Stop"];

    /// <summary>Names the generated <c>Spawn</c> and <c>Connect</c> take, which an actor's own parameters can't use.</summary>
    private static readonly HashSet<string> ReservedParameters = ["system", "name", "address"];

    public string Name => "remote-actors";

    public IReadOnlyDictionary<string, string> Packages { get; } = new Dictionary<string, string> { ["Proto.Remote"] = "1.8.0" };

    public IReadOnlyList<string> Frameworks => ["Microsoft.AspNetCore.App"];

    /// <summary>Piper.Actors.Remote: sending and handling messages with their trace context (see PiperActorTracing).</summary>
    public IReadOnlyList<string> References => ["Piper.Actors.Remote.dll"];

    public IReadOnlyList<PluginKeyword> Keywords { get; } =
    [
        new DeclarationKeyword("actor", Actor)
        {
            Description = "Declares an actor that other processes and machines can use, on Proto.Actor: an object with private state that handles one message at a time. " +
                          "`Counter.Spawn(system, \"counter\", …)` hosts one under a name, and `Counter.Connect(system, \"host:port\", \"counter\")` reaches one elsewhere.",
            Snippet = ActorSyntax.Snippet,
            Words = ActorSyntax.Words,
        },
        ActorSyntax.EmitKeyword(),
    ];

    private static IEnumerable<DeclarationSyntax> Actor(PluginParser p)
    {
        if (ActorSyntax.Parse(p, GeneratedNames) is not { } actor)
            return [];
        var clashing = actor.Parameters.Where(parameter => ReservedParameters.Contains(parameter.Name.Text)).ToList();
        foreach (var parameter in clashing)
            p.Report(parameter.Span, $"'Spawn' takes '{parameter.Name.Text}' itself, so the actor's own parameter needs another name.");
        p.Data = new ActorData(actor.Name, actor.Failure, [.. actor.State.OfType<VariableDeclarationSyntax>().Select(v => v.Name.Text)],
            actor.PersistedBy is not null);
        return clashing.Count == 0 ? Expand(p, actor) : [];
    }

    /// <summary>
    /// Warns about a message field whose type can't be sent between processes as data, or that could change after
    /// it's sent: a <c>List&lt;T&gt;</c>, an array, a trait (which says nothing about the data), another actor's handle
    /// (send its name instead), or a record holding one.
    /// </summary>
    public void Analyze(PluginAnalysis analysis)
    {
        var (fields, _) = ActorSyntax.Messages(analysis);
        foreach (var (file, syntax, field) in fields)
        {
            if (ActorSyntax.MutablePart(field.Type, handles: [], traitsAreOpaque: true) is { } part)
                analysis.ReportWarning(file, syntax.Span,
                    $"'{field.Name}' holds a {part}, which can't be sent to another process as data, or could change after it's sent. " +
                    "Send records, enums, Piper's builtin types or immutable .NET types; for another actor, send its name.");
        }
        CheckShapes(analysis);
        ActorSyntax.CheckPolicies(analysis, traitsAreOpaque: true);
    }

    /// <summary>
    /// Checks each actor's messages against the shape recorded beside it, <c>Counter.shape.json</c>: during a rolling
    /// update old and new processes send each other messages, so a handler removed, a value it newly needs, a field
    /// changed or a reply of another type breaks them mid-rollout (see <see cref="WireShapes"/>).
    /// </summary>
    private static void CheckShapes(PluginAnalysis analysis)
    {
        foreach (var expansion in analysis.Expansions)
        {
            bool Generated(Syntax.SyntaxNode? node) => node is not null && expansion.Result.Any(r => ReferenceEquals(r, node));
            var generatedEnums = analysis.Program.Types.OfType<Binding.EnumSymbol>().Where(e => Generated(e.Declaration)).ToList();
            if (generatedEnums.FirstOrDefault(e => e.Name.EndsWith("Message", StringComparison.Ordinal)) is not { } messages ||
                analysis.Program.Types.OfType<Binding.RecordSymbol>().FirstOrDefault(r => Generated(r.Declaration) && r.Name + "Message" == messages.Name) is not { } actor)
                continue;
            // An ask's method answers a Task of its reply; a tell's answers nothing.
            var methods = analysis.Program.Methods
                .Select(m => m.Symbol)
                .OfType<Binding.MethodSymbol>()
                .Where(m => ReferenceEquals(m.ContainingType.OriginalDefinition, actor))
                .GroupBy(m => m.Name)
                .ToDictionary(g => g.Key, g => g.First());
            var operations = messages.Variants.Select(variant =>
            {
                var reply = methods.TryGetValue(variant.Name, out var method) && method.ReturnType.TypeArguments is [var answer] ? answer : null;
                return new WireOperation(
                    $"{actor.Name}.{variant.Name}", $"{actor.Name}.{variant.Name}",
                    [.. variant.Fields.Select(f => (f.Name, f.Type))],
                    reply?.ToString() ?? "nothing (a tell)",
                    reply is null ? [] : [reply]);
            }).ToList();
            // A persisted actor's saved events are read back by later versions of it: one that old streams can't be read
            // as (a variant gone, a new field needed) is a breaking change, as a message is.
            if (generatedEnums.FirstOrDefault(e => e.Name == actor.Name + "Event") is { } events)
                operations.Add(new WireOperation($"{actor.Name} events", $"{actor.Name} events", [("event", events)], null, []));
            analysis.CheckWireShape(expansion.File, actor.Declaration.Name.Span, actor.Name, operations);
        }
    }

    private static ImmutableArray<DeclarationSyntax> Expand(PluginParser p, ActorDeclaration declaration)
    {
        var hole = new TemplateHoles();
        var actor = declaration.Name.Text;
        var message = actor + "Message";
        var fault = actor + "Fault";
        const string Tasks = "System.Threading.Tasks";
        const string Tracing = "Piper.Actors.Remote.PiperActorTracing";
        // What a throwing handler does to the actor (`on failure …`): stopping it is Proto.Actor's, here.
        var (setup, before, onFailure) = ActorSyntax.PolicyCode(declaration, hole, "__context.Stop(__context.Self);");
        // A persisted actor's events: its Spawn takes their store, it replays them as Proto.Actor starts it (before any
        // message), and saves each message's once its handler has finished (before an ask's answer is sent).
        bool persisted = declaration.PersistedBy is not null;
        var (eventEnum, eventSetup) = ActorSyntax.EventCode(declaration, hole);
        var commit = ActorSyntax.Commit(declaration);
        var store = persisted ? "store: Piper.Runtime.IEventStore, " : "";
        var replay = persisted ? $"if __context.Message is _: Proto.Started {{ {ActorSyntax.Replay}}}" : "";

        var variants = new List<string>();
        var arms = new List<string>();
        var methods = new List<string>();
        foreach (var handler in declaration.Handlers)
        {
            var names = handler.Parameters.Select(parameter => parameter.Name.Text).ToList();
            var fields = names.Count == 0 ? "" : $"({hole[handler.Parameters]}: unit)";
            var arguments = names.Count == 0 ? "" : $"({string.Join(", ", names)})";
            variants.Add($"{handler.Name.Text}{fields}");

            if (handler.Result is null)
            {
                arms.Add($$"""
                    {{message}}.{{handler.Name.Text}}{{arguments}} => {
                        try {
                            {{hole[handler.Body]}};
                            {{commit}}
                        } catch (__error: System.Exception) {
                            {{Tracing}}.Failed(__error);
                            System.Console.Error.WriteLine("{{actor}}: {__error.Message}");
                            {{onFailure}}
                        }
                    }
                    """);
                methods.Add($$"""
                    fn {{hole[handler.Name]}}({{hole[handler.Parameters]}}: unit) {
                        {{Tracing}}.Send(this.__system, this.__pid, {{message}}.{{handler.Name.Text}}{{arguments}});
                    }
                    """);
            }
            else
            {
                arms.Add($$"""
                    {{message}}.{{handler.Name.Text}}{{arguments}} => {
                        try {
                            // The body is the handler's declared result, so `Ok(x)` or `None` infer from it, as in a function.
                            let __result: {{hole[handler.Result]}} = {{hole[handler.Body]}};
                            {{commit}}
                            __context.Respond(__result);
                        } catch (__error: System.Exception) {
                            {{Tracing}}.Failed(__error);
                            __context.Respond({{fault}}(__error.Message));
                            {{onFailure}}
                        }
                    }
                    """);
                methods.Add($$"""
                    async fn {{hole[handler.Name]}}({{hole[handler.Parameters]}}: unit) => {{Tasks}}.Task<{{hole[handler.Result]}}> {
                        let __reply = await {{Tracing}}.RequestAsync(
                            this.__system, this.__pid, {{message}}.{{handler.Name.Text}}{{arguments}}, this.__timeout);
                        match __reply {
                            {{fault}}(__error) => throw System.InvalidOperationException(__error),
                            _ => ({{hole[handler.Result]}})__reply,
                        }
                    }
                    """);
            }
        }

        // A receive function that awaits must be async; one that doesn't returns a finished task (an async lambda
        // without an await would be warned about).
        bool awaits = persisted || declaration.Handlers.Any(handler => SyntaxWalker.Awaits(handler.Body));
        // Each of the actor's messages is handled in a span continuing its sender's trace (from its headers), and timed.
        var span = $"using let __span = {Tracing}.Start<{message}>(__context, \"{actor}\");";
        var receive = awaits
            ? $"async __context => {{ {span} {replay} {before} match __context.Message {{ {string.Join("\n", arms)} _ => {{}} }} }}"
            : $"__context => {{ {span} {before} match __context.Message {{ {string.Join("\n", arms)} _ => {{}} }} {Tasks}.Task.CompletedTask }}";

        var template = $$"""
            {{eventEnum}}
            enum {{message}} {
                {{string.Join(",\n", variants)}}
            }

            /// An ask's handler threw: the exception's message, sent back to the caller.
            record {{fault}}(__error: string);

            record {{hole[declaration.Name]}}(__system: Proto.ActorSystem, __pid: Proto.PID, __timeout: System.TimeSpan);

            extend {{actor}} {
                /// Starts the actor in this process under `name`, so other processes can reach it with `Connect`.
                static fn Spawn(system: Proto.ActorSystem, name: string, {{store}}{{hole[declaration.Parameters]}}: unit) => {{actor}} {
                    {{hole[declaration.State]}};
                    {{eventSetup}}
                    {{setup}}
                    let __props = Proto.Props.FromFunc({{receive}});
                    {{actor}}(system, system.Root.SpawnNamed(__props, name), System.TimeSpan.FromSeconds(10))
                }

                /// The actor called `name` in the process at `address` (host:port), which may be this one.
                static fn Connect(system: Proto.ActorSystem, address: string, name: string) => {{actor}} =
                    {{actor}}(system, Proto.PID(address, name), System.TimeSpan.FromSeconds(10));

                /// A copy whose asks wait up to `timeout` for a reply before throwing a TimeoutException.
                fn WithTimeout(timeout: System.TimeSpan) => {{actor}} = { ...this, __timeout: timeout };

                {{string.Join("\n\n", methods)}}

                /// Stops the actor, wherever it runs.
                async fn Stop() {
                    await this.__system.Root.StopAsync(this.__pid);
                }
            }
            """;
        return p.Template.Declarations(template, hole.All);
    }
}
