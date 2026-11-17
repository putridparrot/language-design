using System.Collections.Immutable;
using Piper.Binding;
using Piper.Plugins;
using Piper.Plugins.Actors;
using Piper.Syntax;
using Piper.Text;

namespace Piper.Plugins.Messaging;

/// <summary>
/// Messages between services, over whichever broker the app chooses by configuration (the in-memory transport locally):
/// <code>
/// messages Orders {
///     command PlaceOrder(orderId: OrderId, quantity: int) queue "orders";
///     event OrderPlaced(orderId: OrderId, total: decimal) topic "order-events" key orderId;
/// }
///
/// consumer Billing(db: BillingDb) {
///     on OrderPlaced(e) {
///         if AlreadyBilled(db, e.orderId) { return Handled.Done; }
///         Handled.Done
///     }
/// }
/// </code>
/// A <c>messages</c> block's are records, each marked as a command (to its queue, for one receiver) or an event (to its
/// topic, for every subscriber, in order per key). A <c>consumer</c> is a class whose <c>Start(bus)</c> subscribes its
/// handlers, each answering what became of its message: <c>Handled.Done</c>, <c>Handled.RetryLater(after)</c> or
/// <c>Handled.Reject(reason)</c>. Checked as it compiles: messages are data that can cross a process; a command has
/// exactly one handler; a handler's message is declared; a key is a field; and the messages' shape is recorded
/// (<c>Orders.shape.json</c>), so a change that would break a running consumer, or messages waiting in a queue, is an error.
/// </summary>
public sealed class MessagingPlugin : IPiperPlugin
{
    public string Name => "messaging";

    /// <summary>Piper.Messaging: the transport interface, the in-memory transport and the bus.</summary>
    public IReadOnlyList<string> References => ["Piper.Messaging.dll"];

    /// <summary>The in-memory transport, the local provider of IMessageTransport.</summary>
    public IReadOnlyList<string> Providers => ["Piper.Messaging.MemoryTransport.RegisterProviders"];

    public IReadOnlyList<PluginKeyword> Keywords { get; } =
    [
        new DeclarationKeyword("messages", Messages)
        {
            Description = "Declares messages between services: `command Name(…) queue \"q\";` (for one receiver) and `event Name(…) topic \"t\" key field;` " +
                          "(for every subscriber, in order per key). Each is a record; `bus.Send(…)` and `bus.Publish(…)` carry them.",
            Snippet = "messages ${1:Orders} {\n\tcommand ${2:PlaceOrder}(${3:id: int}) queue \"${4:orders}\";\n\tevent ${5:OrderPlaced}(${6:id: int}) topic \"${7:order-events}\";\n}",
            Words = new Dictionary<string, string>
            {
                ["command"] = "A message for one receiver: sent (`bus.Send(…)`) to its queue, where one consumer's instance handles it, once.",
                ["event"] = "A message for every subscriber: published (`bus.Publish(…)`) to its topic, where each consumer gets its own copy.",
                ["queue"] = "The queue a command is sent to (default: its name, in kebab-case).",
                ["topic"] = "The topic an event is published to (default: its name, in kebab-case).",
                ["key"] = "The field whose value orders an event's messages: those with the same key are handled in the order they were published.",
            },
        },
        new DeclarationKeyword("consumer", Consumer)
        {
            Description = "Declares a consumer of messages: a class whose `Start(bus)` subscribes its handlers, `on Message(m) { … }`, each answering " +
                          "what became of the message: `Handled.Done`, `Handled.RetryLater(after)` or `Handled.Reject(reason)`. A handler that throws is retried, then dead-lettered.",
            Snippet = "consumer ${1:Billing} {\n\ton ${2:OrderPlaced}(${3:e}) {\n\t\t$0\n\t\tHandled.Done\n\t}\n}",
            Words = new Dictionary<string, string>
            {
                ["on"] = "A handler for a message: `on OrderPlaced(e) { … }`, answering a Handled. It may await.",
            },
        },
    ];

    /// <summary>A declared message: its name, whether it's a command, where it goes, and its key's field (an event's).</summary>
    private sealed record Message(Token Name, bool Command, string Destination, Token? Key);

    private sealed record MessagesData(Token Name, ImmutableArray<Message> Messages);

    private sealed record ConsumerData(Token Name, ImmutableArray<Token> Handles);

    /// <summary>
    /// messages = "messages" IDENT "{" { ( "command" | "event" ) IDENT "(" fields ")" [ ( "queue" | "topic" ) STRING ] [ "key" IDENT ] ";" } "}" ;
    /// </summary>
    private static IEnumerable<DeclarationSyntax> Messages(PluginParser p)
    {
        var name = p.Expect(TokenKind.Identifier);
        p.Expect(TokenKind.OpenBrace);
        var declarations = new List<DeclarationSyntax>();
        var messages = new List<Message>();
        while (p.Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile))
        {
            var kind = p.Current;
            if (!(p.AtWord("command") || p.AtWord("event")))
            {
                p.Report(kind.Span, "Expected a message: 'command Name(…) queue \"q\";' or 'event Name(…) topic \"t\";'.");
                Skip(p);
                continue;
            }
            p.ReadWord();
            bool command = kind.Text == "command";
            var message = p.Expect(TokenKind.Identifier);
            var fields = new List<ParameterSyntax>();
            if (p.Current.Kind == TokenKind.OpenParen)
            {
                foreach (var field in p.ParseLambdaParameters())
                {
                    if (field.Type is null)
                        p.Report(field.Span, $"Give '{field.Name.Text}' a type, such as ({field.Name.Text}: int).");
                    else
                        fields.Add(new ParameterSyntax(field.Span, field.Name, field.Type));
                }
            }
            var destination = Kebab(message.Text);
            var where = command ? "queue" : "topic";
            if (p.AtWord("queue") || p.AtWord("topic"))
            {
                var word = p.ReadWord();
                if (word.Text != where)
                    p.Report(word.Span, command ? "A command goes to a queue (one receiver): write 'queue'." : "An event goes to a topic (every subscriber): write 'topic'.");
                destination = (string?)p.Expect(TokenKind.StringLiteral).Value ?? destination;
            }
            Token? key = null;
            if (p.AtWord("key"))
            {
                var word = p.ReadWord();
                key = p.Expect(TokenKind.Identifier);
                if (command)
                    p.Report(word.Span, "A command has one receiver, so it has no key to order by; an event can have one.");
                else if (fields.All(f => f.Name.Text != key.Text))
                    p.Report(key.Span, $"'{key.Text}' isn't a field of {message.Text}: its key must be one.");
            }
            p.Expect(TokenKind.Semicolon);
            if (messages.Any(m => m.Name.Text == message.Text))
                p.Report(message.Span, $"{name.Text} already has a message called '{message.Text}'.");
            messages.Add(new Message(message, command, destination, key));

            var attribute = command
                ? $"[Piper.Messaging.Command($destination)]"
                : key is null || command ? "[Piper.Messaging.Event($destination)]" : "[Piper.Messaging.Event($destination, Key = $key)]";
            var holes = new List<(string, object)> { ("destination", Text(message.Span, destination)), ("message", message), ("fields", fields) };
            if (key is not null && !command)
                holes.Add(("key", Text(key.Span, key.Text)));
            declarations.AddRange(p.Template.Declarations($"{attribute}\nrecord $message($fields: unit);", [.. holes]));
        }
        p.Expect(TokenKind.CloseBrace);
        p.Data = new MessagesData(name, [.. messages]);
        return declarations;
    }

    /// <summary>
    /// consumer = "consumer" IDENT [ "(" parameters ")" ] "{" { "on" IDENT "(" IDENT ")" [ "=>" "Handled" ] block } "}" ;
    /// </summary>
    private static IEnumerable<DeclarationSyntax> Consumer(PluginParser p)
    {
        var name = p.Expect(TokenKind.Identifier);
        var parameters = new List<ParameterSyntax>();
        if (p.Current.Kind == TokenKind.OpenParen)
        {
            foreach (var parameter in p.ParseLambdaParameters())
            {
                if (parameter.Type is null)
                    p.Report(parameter.Span, $"Give '{parameter.Name.Text}' a type, such as ({parameter.Name.Text}: Db).");
                else
                    parameters.Add(new ParameterSyntax(parameter.Span, parameter.Name, parameter.Type));
            }
        }
        p.Expect(TokenKind.OpenBrace);

        var holes = new List<(string, object)> { ("name", name), ("parameters", parameters), ("consumer", Text(name.Span, name.Text)) };
        var handlers = new List<string>();
        var handles = new List<Token>();
        while (p.Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile))
        {
            if (!p.AtWord("on"))
            {
                p.Report(p.Current.Span, "Expected a handler: 'on MessageName(m) { … }'.");
                Skip(p);
                continue;
            }
            p.ReadWord();
            var message = p.Expect(TokenKind.Identifier);
            p.Expect(TokenKind.OpenParen);
            var value = p.Expect(TokenKind.Identifier);
            p.Expect(TokenKind.CloseParen);
            if (p.Current.Kind == TokenKind.FatArrow)
            {
                p.NextToken();
                var result = p.Expect(TokenKind.Identifier);
                if (result.Text != "Handled")
                    p.Report(result.Span, "A handler answers what became of its message: a Handled.");
            }
            var body = p.ParseAsyncBlock();
            if (handles.Any(h => h.Text == message.Text))
                p.Report(message.Span, $"{name.Text} already handles {message.Text}.");
            handles.Add(message);

            string Hole(object node)
            {
                holes.Add(($"h{holes.Count}", node));
                return "$" + holes[^1].Item1;
            }
            // An async handler if its body awaits; otherwise a plain one (an async lambda without an await is warned about).
            handlers.Add(SyntaxWalker.Awaits(body)
                ? $"Piper.Messaging.Bus.HandlerAsync<{Hole(message)}>(async ({Hole(value)}: {Hole(message)}) => {Hole(body)})"
                : $"Piper.Messaging.Bus.Handler<{Hole(message)}>(({Hole(value)}: {Hole(message)}) => {Hole(body)})");
        }
        p.Expect(TokenKind.CloseBrace);
        p.Data = new ConsumerData(name, [.. handles]);

        return p.Template.Declarations($$"""
            class $name($parameters: unit) {
                /// Starts handling its messages from `bus`: its queues' commands, and a copy of its topics' events. Disposing
                /// the result stops it.
                async fn Start(bus: Piper.Messaging.Bus) => System.Threading.Tasks.Task<System.IAsyncDisposable> =
                    await bus.Consume($consumer, [{{string.Join(", ", handlers)}}]);
            }
            """, [.. holes]);
    }

    /// <summary>
    /// Once types are known: a message's fields must be data that can cross a process (no mutable collections, no
    /// classes); a command has exactly one handler; a handler's message is one declared; and each block's messages are
    /// recorded as a shape, so a change that breaks their consumers is reported.
    /// </summary>
    public void Analyze(PluginAnalysis analysis)
    {
        var declared = analysis.Expansions.Where(e => e.Data is MessagesData).ToList();
        var messages = declared.SelectMany(e => ((MessagesData)e.Data!).Messages).ToList();
        var records = analysis.Program.Types.OfType<RecordSymbol>().ToList();

        foreach (var expansion in declared)
        {
            var data = (MessagesData)expansion.Data!;
            var operations = new List<WireOperation>();
            foreach (var message in data.Messages)
            {
                if (records.FirstOrDefault(r => expansion.Result.Any(x => ReferenceEquals(x, r.Declaration)) && r.Name == message.Name.Text) is not { } record)
                    continue;
                foreach (var field in record.Fields)
                {
                    if (ActorSyntax.MutablePart(field.Type, [], traitsAreOpaque: true) is { } part)
                        analysis.ReportError(expansion.File, message.Name.Span,
                            $"{message.Name.Text}'s '{field.Name}' holds a {part}, which can't cross to another service as data, or could change after it's sent. " +
                            "Send records, enums, Piper's builtin types and immutable collections (ImmutableList).");
                }
                operations.Add(new WireOperation($"{message.Destination}: {message.Name.Text}", $"{(message.Command ? "command" : "event")} {message.Name.Text}",
                    [.. record.Fields.Select(f => (f.Name, f.Type))], null, []));
            }
            analysis.CheckWireShape(expansion.File, data.Name.Span, data.Name.Text, operations);
        }

        var handled = new Dictionary<string, (PluginExpansion Expansion, Token Consumer)>();
        foreach (var expansion in analysis.Expansions.Where(e => e.Data is ConsumerData))
        {
            var data = (ConsumerData)expansion.Data!;
            foreach (var handle in data.Handles)
            {
                if (messages.FirstOrDefault(m => m.Name.Text == handle.Text) is not { } message)
                {
                    analysis.ReportError(expansion.File, handle.Span,
                        $"{handle.Text} isn't a message: declare it in a messages block, as 'command {handle.Text}(…) queue \"…\";' or 'event {handle.Text}(…) topic \"…\";'.");
                }
                else if (message.Command && handled.TryGetValue(handle.Text, out var first))
                {
                    analysis.ReportError(expansion.File, handle.Span,
                        $"{handle.Text} is a command, which has one receiver, but {first.Consumer.Text} handles it too: make it an event, for every consumer, or handle it in one.");
                }
                else
                {
                    handled.TryAdd(handle.Text, (expansion, data.Name));
                }
            }
        }
    }

    /// <summary>A destination's default name: PlaceOrder is place-order.</summary>
    private static string Kebab(string name) =>
        string.Concat(name.Select((c, i) => char.IsUpper(c) && i > 0 ? "-" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

    private static LiteralExpressionSyntax Text(TextSpan span, string text)
    {
        var token = new Token(TokenKind.StringLiteral, span, "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"", text) { IsGenerated = true };
        return new LiteralExpressionSyntax(span, token, text);
    }

    private static void Skip(PluginParser p)
    {
        while (p.Current.Kind is not (TokenKind.Semicolon or TokenKind.CloseBrace or TokenKind.EndOfFile))
            p.NextToken();
        if (p.Current.Kind == TokenKind.Semicolon)
            p.NextToken();
    }
}
