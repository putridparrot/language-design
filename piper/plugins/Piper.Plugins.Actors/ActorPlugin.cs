using System.Collections.Immutable;
using Piper.Plugins;
using Piper.Syntax;

namespace Piper.Plugins.Actors;

/// <summary>
/// Adds <c>actor</c> declarations: objects with private state that handle one message at a time, so their state needs
/// no locks.
/// <code>
/// actor Counter(start: int) {
///     mut count = start;
///     on Increment(by: int) { count += by; }      // a "tell": the caller sends it and carries on
///     on Get() => int { count }                   // an "ask": the caller awaits the reply
/// }
/// let counter = Counter.Spawn(10);  counter.Increment(5);  Print(await counter.Get());
/// </code>
/// It's rewritten into ordinary Piper, on .NET's channels:
/// <list type="bullet">
/// <item>an enum of the messages, <c>CounterMessage</c>, with a variant for each handler;</item>
/// <item>a record, <c>Counter</c>, holding the mailbox, with a method for each handler (an ask's is async and returns
/// the reply), <c>Stop()</c> and <c>DisposeAsync()</c>, so <c>await using let counter = Counter.Spawn(10);</c> stops it;</item>
/// <item><c>Counter.Spawn(…)</c>, which declares the state and starts a loop matching each message to its handler. The state
/// lives in that loop's closure, so nothing else can reach it.</item>
/// </list>
/// An ask whose handler throws throws in the caller. A tell's exception is written to the error output, and the actor
/// carries on with the next message. Once the program is type-checked, the plugin warns about messages carrying
/// something mutable (<see cref="Analyze"/>). For actors on several machines, see the remote actors plugin, which reads
/// the same syntax.
/// </summary>
public sealed class ActorPlugin : IPiperPlugin
{
    private static readonly HashSet<string> GeneratedNames = ["Spawn", "Stop", "DisposeAsync"];

    public string Name => "actors";

    public IReadOnlyList<PluginKeyword> Keywords { get; } =
    [
        ActorSyntax.EmitKeyword(),
        new DeclarationKeyword("actor", p => ActorSyntax.Parse(p, GeneratedNames) is { } actor ? Expand(p, actor) : [])
        {
            Description = "Declares an actor: an object with private state that handles one message at a time, so its state needs no locks. " +
                          "`Counter.Spawn(…)` starts one, `counter.Increment(5)` sends it a message, and `await counter.Get()` asks it for an answer.",
            Snippet = ActorSyntax.Snippet,
            Words = ActorSyntax.Words,
        },
    ];

    /// <summary>
    /// Warns about a message field whose type is mutable, such as a <c>List&lt;T&gt;</c>: the sender and the actor
    /// could then change it at once, which is the sharing actors exist to avoid. Records, enums, Piper's builtin
    /// types, immutable .NET types and other actors' handles are fine. This needs the types, which only exist once the
    /// program is bound, so it's analysis, not part of the rewriting.
    /// </summary>
    public void Analyze(PluginAnalysis analysis)
    {
        var (fields, handles) = ActorSyntax.Messages(analysis);
        foreach (var (file, syntax, field) in fields)
        {
            if (ActorSyntax.MutablePart(field.Type, handles, traitsAreOpaque: false) is { } mutable)
                analysis.ReportWarning(file, syntax.Span,
                    $"'{field.Name}' holds a {mutable}, which the sender and the actor could both change at once. Send something immutable instead, such as a record or an ImmutableList.");
        }
        ActorSyntax.CheckPolicies(analysis, traitsAreOpaque: false);
    }

    private static ImmutableArray<DeclarationSyntax> Expand(PluginParser p, ActorDeclaration declaration)
    {
        p.Data = new ActorData(declaration.Name, declaration.Failure, [.. declaration.State.OfType<VariableDeclarationSyntax>().Select(v => v.Name.Text)],
            declaration.PersistedBy is not null);
        return Generate(p, declaration);
    }

    private static ImmutableArray<DeclarationSyntax> Generate(PluginParser p, ActorDeclaration declaration)
    {
        var hole = new TemplateHoles();
        var actor = declaration.Name.Text;
        var message = actor + "Message";
        const string Tasks = "System.Threading.Tasks";
        // What a throwing handler does to the actor (`on failure …`). Stopping closes the mailbox: messages already in it
        // aren't handled, and asks among them fail rather than wait for ever.
        bool canStop = declaration.Failure is { Kind: "stop" } or { Kind: "restart", Limit: not null };
        var (setup, before, onFailure) = ActorSyntax.PolicyCode(declaration, hole, "__stopped = true; __channel.Writer.TryComplete();");
        if (canStop)
            setup += "mut __stopped = false;\n";
        // A persisted actor's events: its Spawn takes their store, replays them as its loop starts, and saves each
        // message's once its handler has finished (before an ask's answer is sent).
        bool persisted = declaration.PersistedBy is not null;
        var (eventEnum, eventSetup) = ActorSyntax.EventCode(declaration, hole);
        var commit = ActorSyntax.Commit(declaration);
        var store = persisted ? "store: Piper.Runtime.IEventStore, " : "";
        var replay = persisted ? ActorSyntax.Replay : "";

        var variants = new List<string>();
        var arms = new List<string>();
        var stoppedArms = new List<string>();
        var methods = new List<string>();
        foreach (var handler in declaration.Handlers)
        {
            var names = handler.Parameters.Select(parameter => parameter.Name.Text).ToList();
            var send = $"if !this.__mailbox.TryWrite({{0}}) {{{{ throw System.InvalidOperationException(\"{actor} has stopped.\"); }}}}";
            if (handler.Result is null)
            {
                var fields = names.Count == 0 ? "" : $"({hole[handler.Parameters]}: unit)";
                var arguments = names.Count == 0 ? "" : $"({string.Join(", ", names)})";
                variants.Add($"{handler.Name.Text}{fields}");
                arms.Add(persisted
                    ? $"{handler.Name.Text}{arguments} => {{ {hole[handler.Body]}; {commit} }},"
                    : $"{handler.Name.Text}{arguments} => {hole[handler.Body]},");
                methods.Add($$"""
                    fn {{hole[handler.Name]}}({{hole[handler.Parameters]}}: unit) {
                        {{string.Format(send, $"{message}.{handler.Name.Text}{arguments}")}}
                    }
                    """);
            }
            else
            {
                var withReply = string.Join(", ", names.Append("__reply"));
                variants.Add($"{handler.Name.Text}({hole[handler.Parameters]}: unit, __reply: {Tasks}.TaskCompletionSource<{hole[handler.Result]}>)");
                arms.Add($$"""
                    {{handler.Name.Text}}({{withReply}}) => {
                        try {
                            // The body is the handler's declared result, so `Ok(x)` or `None` infer from it, as in a function.
                            let __result: {{hole[handler.Result]}} = {{hole[handler.Body]}};
                            {{commit}}
                            __reply.SetResult(__result);
                        } catch (__error: System.Exception) {
                            __reply.SetException(__error);
                            {{onFailure}}
                        }
                    },
                    """);
                var ignored = string.Join(", ", names.Select(_ => "_").Append("__reply"));
                stoppedArms.Add($"{handler.Name.Text}({ignored}) => __reply.SetException(System.InvalidOperationException(\"{actor} has stopped.\")),");
                methods.Add($$"""
                    async fn {{hole[handler.Name]}}({{hole[handler.Parameters]}}: unit) => {{Tasks}}.Task<{{hole[handler.Result]}}> {
                        let __reply = {{Tasks}}.TaskCompletionSource<{{hole[handler.Result]}}>({{Tasks}}.TaskCreationOptions.RunContinuationsAsynchronously);
                        {{string.Format(send, $"{message}.{handler.Name.Text}({withReply})")}}
                        await __reply.Task
                    }
                    """);
            }
        }
        // A stopped actor's tells are dropped; a match of only asks needs no catch-all (it would be unreachable).
        var ignoreTells = stoppedArms.Count < declaration.Handlers.Count ? "_ => {}" : "";

        var template = $$"""
            {{eventEnum}}
            enum {{message}} {
                {{string.Join(",\n", variants)}}
            }

            record {{hole[declaration.Name]}}(__mailbox: System.Threading.Channels.ChannelWriter<{{message}}>, __completion: {{Tasks}}.Task);

            extend {{actor}} {
                static fn Spawn({{store}}{{hole[declaration.Parameters]}}: unit) => {{actor}} {
                    let __channel = System.Threading.Channels.Channel.CreateUnbounded<{{message}}>();
                    {{hole[declaration.State]}};
                    {{eventSetup}}
                    {{setup}}
                    let __loop = {{Tasks}}.Task.Run(async () => {
                        {{replay}}
                        await for __message in __channel.Reader.ReadAllAsync() {
                            {{(canStop ? $"if __stopped {{ match __message {{ {string.Join("\n", stoppedArms)} {ignoreTells} }} continue; }}" : "")}}
                            {{before}}
                            try {
                                match __message {
                                    {{string.Join("\n", arms)}}
                                }
                            } catch (__error: System.Exception) {
                                System.Console.Error.WriteLine("{{actor}}: {__error.Message}");
                                {{onFailure}}
                            }
                        }
                    });
                    {{actor}}(__channel.Writer, __loop)
                }

                {{string.Join("\n\n", methods)}}

                /// Stops taking messages, and finishes once those already sent are handled.
                async fn Stop() {
                    this.__mailbox.TryComplete();
                    await this.__completion;
                }
            }

            extend {{actor}}: System.IAsyncDisposable {
                fn DisposeAsync() => {{Tasks}}.ValueTask = {{Tasks}}.ValueTask(this.Stop());
            }
            """;
        return p.Template.Declarations(template, hole.All);
    }
}
