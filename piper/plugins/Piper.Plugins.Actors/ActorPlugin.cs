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
        [new DeclarationKeyword("actor", p => ActorSyntax.Parse(p, GeneratedNames) is { } actor ? Expand(p, actor) : [])];

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
    }

    private static ImmutableArray<DeclarationSyntax> Expand(PluginParser p, ActorDeclaration declaration)
    {
        var hole = new TemplateHoles();
        var actor = declaration.Name.Text;
        var message = actor + "Message";
        const string Tasks = "System.Threading.Tasks";

        var variants = new List<string>();
        var arms = new List<string>();
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
                arms.Add($"{handler.Name.Text}{arguments} => {hole[handler.Body]},");
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
                            __reply.SetResult({{hole[handler.Body]}});
                        } catch (__error: System.Exception) {
                            __reply.SetException(__error);
                        }
                    },
                    """);
                methods.Add($$"""
                    async fn {{hole[handler.Name]}}({{hole[handler.Parameters]}}: unit) => {{Tasks}}.Task<{{hole[handler.Result]}}> {
                        let __reply = {{Tasks}}.TaskCompletionSource<{{hole[handler.Result]}}>({{Tasks}}.TaskCreationOptions.RunContinuationsAsynchronously);
                        {{string.Format(send, $"{message}.{handler.Name.Text}({withReply})")}}
                        await __reply.Task
                    }
                    """);
            }
        }

        var template = $$"""
            enum {{message}} {
                {{string.Join(",\n", variants)}}
            }

            record {{hole[declaration.Name]}}(__mailbox: System.Threading.Channels.ChannelWriter<{{message}}>, __completion: {{Tasks}}.Task);

            extend {{actor}} {
                static fn Spawn({{hole[declaration.Parameters]}}: unit) => {{actor}} {
                    let __channel = System.Threading.Channels.Channel.CreateUnbounded<{{message}}>();
                    {{hole[declaration.State]}};
                    let __loop = {{Tasks}}.Task.Run(async () => {
                        await for __message in __channel.Reader.ReadAllAsync() {
                            try {
                                match __message {
                                    {{string.Join("\n", arms)}}
                                }
                            } catch (__error: System.Exception) {
                                System.Console.Error.WriteLine("{{actor}}: {__error.Message}");
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
