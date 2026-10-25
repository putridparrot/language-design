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

    public IReadOnlyList<PluginKeyword> Keywords { get; } = [new DeclarationKeyword("actor", Actor)];

    private static IEnumerable<DeclarationSyntax> Actor(PluginParser p)
    {
        if (ActorSyntax.Parse(p, GeneratedNames) is not { } actor)
            return [];
        var clashing = actor.Parameters.Where(parameter => ReservedParameters.Contains(parameter.Name.Text)).ToList();
        foreach (var parameter in clashing)
            p.Report(parameter.Span, $"'Spawn' takes '{parameter.Name.Text}' itself, so the actor's own parameter needs another name.");
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
    }

    private static ImmutableArray<DeclarationSyntax> Expand(PluginParser p, ActorDeclaration declaration)
    {
        var hole = new TemplateHoles();
        var actor = declaration.Name.Text;
        var message = actor + "Message";
        var fault = actor + "Fault";
        const string Tasks = "System.Threading.Tasks";

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
                        } catch (__error: System.Exception) {
                            System.Console.Error.WriteLine("{{actor}}: {__error.Message}");
                        }
                    }
                    """);
                methods.Add($$"""
                    fn {{hole[handler.Name]}}({{hole[handler.Parameters]}}: unit) {
                        this.__system.Root.Send(this.__pid, {{message}}.{{handler.Name.Text}}{{arguments}});
                    }
                    """);
            }
            else
            {
                arms.Add($$"""
                    {{message}}.{{handler.Name.Text}}{{arguments}} => {
                        try {
                            __context.Respond({{hole[handler.Body]}});
                        } catch (__error: System.Exception) {
                            __context.Respond({{fault}}(__error.Message));
                        }
                    }
                    """);
                methods.Add($$"""
                    async fn {{hole[handler.Name]}}({{hole[handler.Parameters]}}: unit) => {{Tasks}}.Task<{{hole[handler.Result]}}> {
                        let __reply = await Proto.SenderContextExtensions.RequestAsync<object>(
                            this.__system.Root, this.__pid, {{message}}.{{handler.Name.Text}}{{arguments}}, this.__timeout);
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
        bool awaits = declaration.Handlers.Any(handler => SyntaxWalker.Awaits(handler.Body));
        var receive = awaits
            ? $"async __context => {{ match __context.Message {{ {string.Join("\n", arms)} _ => {{}} }} }}"
            : $"__context => {{ match __context.Message {{ {string.Join("\n", arms)} _ => {{}} }} {Tasks}.Task.CompletedTask }}";

        var template = $$"""
            enum {{message}} {
                {{string.Join(",\n", variants)}}
            }

            /// An ask's handler threw: the exception's message, sent back to the caller.
            record {{fault}}(__error: string);

            record {{hole[declaration.Name]}}(__system: Proto.ActorSystem, __pid: Proto.PID, __timeout: System.TimeSpan);

            extend {{actor}} {
                /// Starts the actor in this process under `name`, so other processes can reach it with `Connect`.
                static fn Spawn(system: Proto.ActorSystem, name: string, {{hole[declaration.Parameters]}}: unit) => {{actor}} {
                    {{hole[declaration.State]}};
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
