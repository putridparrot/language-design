using System.Collections.Immutable;
using Piper.Plugins;
using Piper.Syntax;

namespace Piper.Plugins.Actors;

/// <summary>
/// Adds <c>actor</c> declarations, as <see cref="ActorPlugin"/> does, but on Piper.Actors.Remote's actor system, so an
/// actor in one process can be used from others, on the same machine or across a network:
/// <code>
/// let system = await PiperNode.StartAsync("127.0.0.1", 8000);
/// let counter = Counter.Spawn(system, "counter", 10);                    // host it here, under a name
/// let remote = Counter.Connect(system, "127.0.0.1:8000", "counter");     // or reach one hosted elsewhere
/// remote.Increment(5);  Print(await remote.Get());
/// </code>
/// It's rewritten into ordinary Piper:
/// <list type="bullet">
/// <item>an enum of the messages, <c>CounterMessage</c>, which is sent between nodes as JSON;</item>
/// <item>a record, <c>Counter</c>, holding the actor system and the actor's address (its ActorRef), with a method for
/// each handler: a tell sends the message, an ask sends it and awaits the reply (sent as the handler's declared result
/// type), for up to 10 seconds by default (<c>counter.WithTimeout(TimeSpan.FromSeconds(30))</c> changes that);</item>
/// <item><c>Counter.Spawn(system, name, …)</c>, which declares the state and starts an actor whose receive function
/// matches each message to its handler, and <c>Counter.Connect(system, address, name)</c>.</item>
/// </list>
/// An ask whose handler throws answers with the exception's message, which the caller throws as an
/// InvalidOperationException; a tell's exception is written to the error output. The nodes talk over a transport chosen
/// by the program (HTTP/2 on ASP.NET Core's Kestrel, in memory, or a plugin's), and the plugin warns about message
/// fields that can't be sent as data (<see cref="Analyze"/>).
/// </summary>
public sealed class RemoteActorPlugin : IPiperPlugin
{
    private static readonly HashSet<string> GeneratedNames = ["Spawn", "Connect", "WithTimeout", "Stop", "InCluster", "HostInCluster"];

    /// <summary>Names the generated <c>Spawn</c> and <c>Connect</c> take, which an actor's own parameters can't use.</summary>
    private static readonly HashSet<string> ReservedParameters = ["system", "name", "address"];

    public string Name => "remote-actors";

    /// <summary>ASP.NET Core, whose Kestrel serves the http transport.</summary>
    public IReadOnlyList<string> Frameworks => ["Microsoft.AspNetCore.App"];

    /// <summary>Piper.Actors.Remote: the actor system, its transports, and tracing through actors (see PiperActorTracing).</summary>
    public IReadOnlyList<string> References => ["Piper.Actors.Remote.dll"];

    /// <summary>
    /// What a program can choose by configuration: the transport (memory, the local one, and http) and a cluster's
    /// membership (memory, the local one, and static).
    /// </summary>
    public IReadOnlyList<string> Providers => ["Piper.Actors.Remote.PiperNode.RegisterProviders", "Piper.Actors.Remote.Cluster.RegisterProviders"];

    public IReadOnlyList<PluginKeyword> Keywords { get; } =
    [
        new DeclarationKeyword("actor", Actor)
        {
            Description = "Declares an actor that other processes and machines can use: an object with private state that handles one message at a time. " +
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
        const string Tasks = "System.Threading.Tasks";
        const string Remote = "Piper.Actors.Remote";
        const string Tracing = Remote + ".PiperActorTracing";
        // What a throwing handler does to the actor (`on failure …`): stopping it is the actor system's, here.
        var (setup, before, onFailure) = ActorSyntax.PolicyCode(declaration, hole, "__context.Stop();");
        // A persisted actor's events: its Spawn takes their store, it replays them as the actor starts (before any
        // message), and saves each message's once its handler has finished (before an ask's answer is sent).
        bool persisted = declaration.PersistedBy is not null;
        var (eventEnum, eventSetup) = ActorSyntax.EventCode(declaration, hole);
        var commit = ActorSyntax.Commit(declaration, hole);
        var store = persisted ? "store: Piper.Runtime.IEventStore, " : "";
        var replay = persisted ? $"if __context.Message is _: {Remote}.Started {{ {ActorSyntax.Replay(declaration)}}}" : "";
        // Its store refused its events: the same actor is running on another node too (in a cluster, while the nodes
        // disagree on who owns it). This one stops, so the next message starts it again from what was saved.
        var conflict = persisted ? "if __error is _: Piper.Runtime.EventStreamConflictException { __context.Stop(); }\n" : "";

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
                            {{conflict}}{{onFailure}}
                        }
                    }
                    """);
                methods.Add($$"""
                    fn {{hole[handler.Name]}}({{hole[handler.Parameters]}}: unit) {
                        {{Tracing}}.Send<{{message}}>(this.__system, this.__ref, {{message}}.{{handler.Name.Text}}{{arguments}});
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
                            // Sent to another node as the declared result type, which the asker reads it as.
                            __context.Respond<{{hole[handler.Result]}}>(__result);
                        } catch (__error: System.Exception) {
                            {{Tracing}}.Failed(__error);
                            __context.Fail(__error.Message);
                            {{conflict}}{{onFailure}}
                        }
                    }
                    """);
                methods.Add($$"""
                    async fn {{hole[handler.Name]}}({{hole[handler.Parameters]}}: unit) => {{Tasks}}.Task<{{hole[handler.Result]}}> {
                        await {{Tracing}}.RequestAsync<{{message}}, {{hole[handler.Result]}}>(
                            this.__system, this.__ref, {{message}}.{{handler.Name.Text}}{{arguments}}, this.__timeout)
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

            record {{hole[declaration.Name]}}(__system: {{Remote}}.ActorSystem, __ref: {{Remote}}.ActorRef, __timeout: System.TimeSpan);

            extend {{actor}} {
                /// Starts the actor on this node under `name`, so other nodes can reach it with `Connect`.
                static fn Spawn(system: {{Remote}}.ActorSystem, name: string, {{store}}{{hole[declaration.Parameters]}}: unit) => {{actor}} {
                    {{hole[declaration.State]}};
                    {{eventSetup}}
                    {{setup}}
                    {{actor}}(system, system.Spawn<{{message}}>(name, {{receive}}), System.TimeSpan.FromSeconds(10))
                }

                /// The actor called `name` on the node at `address` (host:port), which may be this one.
                static fn Connect(system: {{Remote}}.ActorSystem, address: string, name: string) => {{actor}} =
                    {{actor}}(system, {{Remote}}.ActorRef(address, name), System.TimeSpan.FromSeconds(10));

                /// The actor with `key` in the cluster `system` has joined, wherever it runs: each message goes to the
                /// node that owns the key now, which starts the actor as the message arrives (see HostInCluster).
                static fn InCluster(system: {{Remote}}.ActorSystem, key: string) => {{actor}} =
                    {{actor}}(system, {{Remote}}.ActorRef.InCluster("{{actor}}", key), System.TimeSpan.FromSeconds(10));

                /// Makes this node host {{actor}}s for its cluster: `activate` starts the one with a key when a message
                /// for it arrives here, its owner, given the node, the name to spawn it under, and the key:
                /// `(node, name, key) => {{actor}}.Spawn(node, name, …)`.
                static fn HostInCluster(cluster: {{Remote}}.Cluster, activate: ({{Remote}}.ActorSystem, string, string) => {{actor}}) {
                    cluster.Host("{{actor}}", (__node, __name, __key) => { activate(__node, __name, __key); });
                }

                /// A copy whose asks wait up to `timeout` for a reply before throwing a TimeoutException.
                fn WithTimeout(timeout: System.TimeSpan) => {{actor}} = { ...this, __timeout: timeout };

                {{string.Join("\n\n", methods)}}

                /// Stops the actor, wherever it runs.
                async fn Stop() {
                    await this.__system.StopAsync(this.__ref);
                }
            }
            """;
        return p.Template.Declarations(template, hole.All);
    }
}
