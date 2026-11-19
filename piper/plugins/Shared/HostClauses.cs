using Piper.Plugins;
using Piper.Syntax;
using Piper.Text;

namespace Piper.Plugins.Shared;

/// <summary>
/// What a host's clauses write: registrations made before it's built, and what's done to it after (its <c>stopping</c>
/// hooks), and the handlers whose parameters are services, for the checks run once the program is type-checked.
/// <paramref name="host"/> is the name the built host has in the code written (<c>app</c> for a server).
/// </summary>
internal sealed class HostParts(string host)
{
    public string Host { get; } = host;

    /// <summary>The declaration's name, which `telemetry` reports the program as by default.</summary>
    public string Name { get; init; } = "";
    public List<StatementSyntax> Building { get; } = [];
    public List<StatementSyntax> Built { get; } = [];
    public List<LambdaExpressionSyntax> Hooks { get; } = [];
    public List<LambdaExpressionSyntax> Workers { get; } = [];

    /// <summary>How many `sources` handlers lead <see cref="Building"/>, before anything reads configuration.</summary>
    public int Sources { get; set; }

    /// <summary>Hosted services named (<c>hosted Mailer;</c>), which run, as workers do.</summary>
    public int Hosted { get; set; }

    /// <summary>The bus a <c>bus from "Messaging";</c> made, for the <c>consume</c> and <c>relay</c> clauses after it.</summary>
    public Token? Bus { get; set; }
}

/// <summary>
/// The clauses a <c>host</c> (the hosting plugin) and a <c>server</c> (the web plugin) share, over .NET's generic host:
/// <code>
/// config smtp: Smtp;  logging off;  services AppServices(settings);  singleton Clock => SystemClock;
/// configure { … }  hosted Mailer;  worker every 30 seconds (lots: Lots) => …;  stopping (jobs: Jobs) => …;
/// shutdown 30 seconds;
/// </code>
/// The code they write calls Piper.Hosting's <c>PiperHost</c>, with the builder as <c>builder</c>. This file is
/// compiled into both plugins.
/// </summary>
internal static class HostClauses
{
    private const string DependencyInjection = "Microsoft.Extensions.DependencyInjection";
    private const string Host = "Piper.Hosting.PiperHost";

    /// <summary>What each shared clause's words mean, for hover and completion.</summary>
    public static IReadOnlyDictionary<string, string> Words { get; } = new Dictionary<string, string>
    {
        ["provider"] = "The implementation of a service (a trait or interface) the configuration chooses, made as the program starts and registered: `provider IEventStore from \"Events\";` reads Events:Provider (memory, sqlite, sqlserver, postgres…; the local one if none) and the provider's settings. `provider store: IEventStore from \"Events\";` also names it for the clauses after.",
        ["sources"] = "Where configuration (secrets among it) comes from beyond appsettings.json and the environment, from any provider: `sources (configuration: IConfigurationBuilder) => configuration.AddAzureKeyVault(…);` or AWS's, HashiCorp Vault's, Kubernetes secrets as files. Added before anything reads configuration, wherever it's written.",
        ["telemetry"] = "Traces, metrics and logs over OpenTelemetry (OTLP) to `OTEL_EXPORTER_OTLP_ENDPOINT` (Aspire's dashboard, Jaeger, a collector), named as the declaration is or as given: `telemetry \"stock-api\";`. Needs the telemetry plugin.",
        ["logging"] = "`logging off;` removes the logging providers, for a program that writes nothing to the console.",
        ["config"] = "Reads a `config` record from the configuration as it starts (stopping it, listing every problem, if one is missing or invalid), and registers it, and as `IOptions<T>`. `config smtp: Smtp;` names it for the clauses after.",
        ["services"] = "A `services` block registering the program's services: `services AppServices;`, with its arguments if it takes any.",
        ["singleton"] = "Registers one instance, shared by everything: `AddSingleton`.",
        ["scoped"] = "Registers one instance per scope (a request, a worker's run): `AddScoped`.",
        ["transient"] = "Registers a new instance each time it's asked for: `AddTransient`.",
        ["configure"] = "Code run on the builder (`builder`) before it's built, for what the declaration doesn't say: `configure { builder.Services.AddHttpClient(); }`.",
        ["hosted"] = "Runs a .NET hosted service (an `IHostedService`, such as a `BackgroundService`) while it runs: `AddHostedService`.",
        ["worker"] = "A handler run in the background while it runs, whose parameters are services; a `CancellationToken` is cancelled as it stops. With `every`, it's run once each period.",
        ["every"] = "Runs the worker as it starts, then once each period: `worker every 30 seconds (lots: Lots) => lots.CloseEnded();`, or a TimeSpan (`every tick`).",
        ["async"] = "The handler awaits, so it runs as an async lambda.",
        ["stopping"] = "Run when it's asked to stop (Ctrl+C, or SIGTERM from Kubernetes), and waited for, before its workers stop: a handler whose parameters are services.",
        ["shutdown"] = "How long workers, `stopping` hooks (and a server's requests) have to finish once it's asked to stop: `shutdown 30 seconds;`.",
        ["bus"] = "The messaging plugin's bus, over the transport the configuration chooses (`bus from \"Messaging\";` reads Messaging:Provider: memory, rabbitmq…), registered as `Bus` (and its transport as `IMessageTransport`), for endpoints and workers and the `consume` and `relay` clauses after. Needs the messaging plugin.",
        ["consume"] = "Runs a `consumer` for as long as it runs, on the `bus`: `consume Billing(db);`, with the consumer's arguments. Stopped as it stops.",
        ["workflows"] = "Runs durable workflows over an event store for as long as it runs: `workflows Fulfil(shop), Returns(shop) from store;` registers each (with its dependencies) on a `WorkflowEngine`, registered for handlers to start them (`Fulfil.Start(engine, id, …)`), resumes the unfinished as it starts, and stops them as it stops. Needs the workflows plugin.",
        ["database"] = "A `database` declaration's class, on the database the configuration chooses, registered as a scoped service (one per request): `database TrackerDb from \"Database\";` reads Database:Provider (sqlite, the local one, if unset; sqlserver; postgres) and Database:Connection. `database db: TrackerDb from \"Database\";` also names `db`, a function making one, for the clauses after: `relay DatabaseOutbox.Store(db);`. Needs the data plugin, and the provider's EF Core package.",
        ["relay"] ="Sends an outbox's saved messages on the `bus` for as long as it runs: `relay DatabaseOutbox.Store(() => Shop(options));`. Stopped as it stops.",
    };

    /// <summary>The shared clauses' words, for a message listing what may be written.</summary>
    public const string Listed = "'config', 'sources', 'provider', 'database', 'services', 'singleton', 'scoped', 'transient', 'configure', 'logging', 'telemetry', 'hosted', 'worker', 'stopping', 'shutdown', 'bus', 'consume', 'relay' or 'workflows'";

    private const string Messaging = "Piper.Messaging";

    private static readonly Dictionary<string, string> Lifetimes = new()
    {
        ["singleton"] = "AddSingleton",
        ["scoped"] = "AddScoped",
        ["transient"] = "AddTransient",
    };

    /// <summary>
    /// Reads a shared clause starting at <paramref name="word"/>, adding what it writes to <paramref name="parts"/>;
    /// false (having read nothing) for a word that isn't one. <paramref name="once"/> reports a clause given twice that
    /// may only be given once.
    /// </summary>
    public static bool TryParse(PluginParser p, Token word, HostParts parts, Action<Token> once)
    {
        switch (word.Text)
        {
            case "logging":
                p.ReadWord();
                once(word);
                p.ExpectWord("off");
                parts.Building.Add(p.Template.Statement($"{Host}.LoggingOff(builder);"));
                p.Expect(TokenKind.Semicolon);
                return true;

            case "telemetry":
            {
                // `telemetry;` or `telemetry "stock-api";`: traces, metrics and logs over OTLP (the telemetry plugin).
                p.ReadWord();
                once(word);
                ExpressionSyntax service = p.Current.Kind is TokenKind.Semicolon
                    ? new LiteralExpressionSyntax(word.Span, new Token(TokenKind.StringLiteral, word.Span, $"\"{parts.Name}\"", parts.Name) { IsGenerated = true }, parts.Name)
                    : p.ParseExpression();
                parts.Building.Add(p.Template.Statement("Piper.Telemetry.PiperTelemetry.Add(builder, $service);", ("service", service)));
                p.Expect(TokenKind.Semicolon);
                return true;
            }

            case "services":
            {
                p.ReadWord();
                var services = p.Expect(TokenKind.Identifier);
                parts.Building.Add(p.Template.Statement("$services(builder.Services, $arguments);", ("services", services), ("arguments", Arguments(p))));
                p.Expect(TokenKind.Semicolon);
                return true;
            }

            case "config":
            {
                // `config Smtp;`, or `config smtp: Smtp;` to use it in the clauses after (`urls smtp.url;`).
                p.ReadWord();
                var local = p.Current.Kind == TokenKind.Identifier && p.Peek(1).Kind == TokenKind.Colon ? p.NextToken() : null;
                if (local is not null)
                    p.NextToken();
                var config = p.Expect(TokenKind.Identifier);
                var value = local ?? p.FreshName(char.ToLowerInvariant(config.Text[0]) + config.Text[1..]);
                var from = new Token(TokenKind.Identifier, config.Span, config.Text + "From");
                // Read as it's built, so a missing or invalid value stops it at startup, listing every problem.
                parts.Building.Add(p.Template.Statement("let $value = $from(builder.Configuration);", ("value", value), ("from", from)));
                parts.Building.Add(p.Template.Statement($"{DependencyInjection}.ServiceCollectionServiceExtensions.AddSingleton<$config>(builder.Services, $value);",
                    ("config", config), ("value", value)));
                // As IOptions<Smtp> too, for code written for .NET's options.
                parts.Building.Add(p.Template.Statement(
                    $"{DependencyInjection}.ServiceCollectionServiceExtensions.AddSingleton<Microsoft.Extensions.Options.IOptions<$config>>(builder.Services, Microsoft.Extensions.Options.Options.Create($value));",
                    ("config", config), ("value", value)));
                p.Expect(TokenKind.Semicolon);
                return true;
            }

            case "singleton" or "scoped" or "transient":
                p.ReadWord();
                parts.Building.Add(Registration(p, Lifetimes[word.Text]));
                p.Expect(TokenKind.Semicolon);
                return true;

            case "configure":
                p.ReadWord();
                parts.Building.Add(new ExpressionStatementSyntax(p.Current.Span, p.ParseBlock()));
                return true;

            case "provider":
            {
                // `provider IEventStore from "Events";`, or `provider store: IEventStore from "Events";` to use it in
                // the clauses after: the implementation the configuration chooses (local if it chooses none), made as
                // the program starts and registered as a singleton.
                p.ReadWord();
                var local = p.Current.Kind == TokenKind.Identifier && p.Peek(1).Kind == TokenKind.Colon ? p.NextToken() : null;
                if (local is not null)
                    p.NextToken();
                var service = p.ParseType();
                p.ExpectWord("from");
                var section = p.ParseExpression();
                p.Expect(TokenKind.Semicolon);
                var value = local ?? p.FreshName("provider");
                parts.Building.Add(p.Template.Statement($"let $value = {Host}.Provider<$service>(builder, $section);",
                    ("value", value), ("service", service), ("section", section)));
                return true;
            }

            case "database":
            {
                // `database TrackerDb from "Database";`, or `database db: TrackerDb from "Database";` to use it in the
                // clauses after: the provider and connection the configuration chooses, the database registered as a
                // scoped service, and `db` a function making one (`relay DatabaseOutbox.Store(db);`).
                p.ReadWord();
                var local = p.Current.Kind == TokenKind.Identifier && p.Peek(1).Kind == TokenKind.Colon ? p.NextToken() : null;
                if (local is not null)
                    p.NextToken();
                // Then `from "Database"` (a section of the configuration), or `(settings.provider, settings.connection)`
                // (values the program read from its configuration itself, as a `config` record's).
                var database = p.ParseType();
                var value = local ?? p.FreshName("database");
                if (p.Current.Kind == TokenKind.OpenParen)
                {
                    var arguments = Arguments(p);
                    if (arguments.Count != 2)
                        p.Report(database.Span, "Expected the database's provider and connection: `database TrackerDb(settings.provider, settings.connection);`.");
                    parts.Building.Add(p.Template.Statement("let $value = Piper.Data.PiperDatabase.Factory<$database>($arguments);",
                        ("value", value), ("database", database), ("arguments", arguments)));
                }
                else
                {
                    p.ExpectWord("from");
                    var section = p.ParseExpression();
                    parts.Building.Add(p.Template.Statement(
                        $"let $value = Piper.Data.PiperDatabase.Factory<$database>({Host}.Settings(builder.Configuration, $section));",
                        ("value", value), ("database", database), ("section", section)));
                }
                p.Expect(TokenKind.Semicolon);
                parts.Building.Add(p.Template.Statement(
                    $"{DependencyInjection}.ServiceCollectionServiceExtensions.AddScoped<$database>(builder.Services, $services => $value());",
                    ("database", database), ("services", p.FreshName("services")), ("value", value)));
                return true;
            }

            case "sources":
            {
                // `sources (configuration: IConfigurationBuilder) => configuration.AddAzureKeyVault(…);`: where
                // configuration (secrets among it) comes from beyond appsettings.json and the environment, whatever the
                // provider. Added first, wherever it's written, so every `config` reads it.
                p.ReadWord();
                if (p.Current.Kind != TokenKind.OpenParen)
                {
                    p.Report(p.Current.Span, "Expected a handler taking the configuration builder: sources (configuration: IConfigurationBuilder) => configuration.Add…(…);");
                    SkipClause(p);
                    return true;
                }
                var handler = p.ParseExpression();
                p.Expect(TokenKind.Semicolon);
                var source = p.FreshName("sources");
                parts.Building.Insert(parts.Sources++, p.Template.Statement("let $source = $handler;", ("source", source), ("handler", handler)));
                parts.Building.Insert(parts.Sources++, p.Template.Statement("$source(builder.Configuration);", ("source", source)));
                return true;
            }

            case "hosted":
                p.ReadWord();
                parts.Building.Add(p.Template.Statement($"{DependencyInjection}.ServiceCollectionHostedServiceExtensions.AddHostedService<$type>(builder.Services);",
                    ("type", p.ParseType())));
                parts.Hosted++;
                p.Expect(TokenKind.Semicolon);
                return true;

            case "worker":
            {
                p.ReadWord();
                ExpressionSyntax? every = null;
                if (p.AtWord("every"))
                {
                    p.ReadWord();
                    every = Period(p);
                }
                if (Handler(p) is { } worker)
                {
                    parts.Building.Add(every is null
                        ? p.Template.Statement($"{Host}.Worker(builder, $worker);", ("worker", worker))
                        : p.Template.Statement($"{Host}.Worker(builder, $every, $worker);", ("every", every), ("worker", worker)));
                    parts.Workers.Add(worker);
                }
                return true;
            }

            case "stopping":
                p.ReadWord();
                if (Handler(p) is { } hook)
                {
                    parts.Built.Add(p.Template.Statement($"{Host}.OnStopping({parts.Host}, $hook);", ("hook", hook)));
                    parts.Hooks.Add(hook);
                }
                return true;

            case "shutdown":
                p.ReadWord();
                once(word);
                parts.Building.Add(p.Template.Statement($"{Host}.ShutdownTimeout(builder, $timeout);", ("timeout", Duration(p, countOptional: false))));
                p.Expect(TokenKind.Semicolon);
                return true;

            case "bus":
            {
                // `bus from "Messaging";`: the transport the configuration chooses, and a bus over it, both registered.
                p.ReadWord();
                once(word);
                p.ExpectWord("from");
                var section = p.ParseExpression();
                p.Expect(TokenKind.Semicolon);
                var transport = p.FreshName("transport");
                var bus = p.FreshName("bus");
                parts.Building.Add(p.Template.Statement($"let $transport = {Host}.Provider<{Messaging}.IMessageTransport>(builder, $section);",
                    ("transport", transport), ("section", section)));
                parts.Building.Add(p.Template.Statement($"let $bus = {Messaging}.Bus($transport);", ("bus", bus), ("transport", transport)));
                parts.Building.Add(p.Template.Statement(
                    $"{DependencyInjection}.ServiceCollectionServiceExtensions.AddSingleton<{Messaging}.IMessageTransport>(builder.Services, $transport);",
                    ("transport", transport)));
                parts.Building.Add(p.Template.Statement(
                    $"{DependencyInjection}.ServiceCollectionServiceExtensions.AddSingleton<{Messaging}.Bus>(builder.Services, $bus);", ("bus", bus)));
                parts.Bus = bus;
                return true;
            }

            case "consume":
            {
                // `consume Billing(db);`: the consumer, started on the bus as it starts, and stopped as it stops.
                p.ReadWord();
                var consumer = p.Expect(TokenKind.Identifier);
                var arguments = Arguments(p);
                p.Expect(TokenKind.Semicolon);
                if (parts.Bus is not { } bus)
                {
                    p.Report(word.Span, "'consume' needs a bus to consume from: write `bus from \"Messaging\";` before it.");
                    return true;
                }
                parts.Building.Add(p.Template.Statement(
                    $"{Host}.Worker(builder, async (stopping: System.Threading.CancellationToken) => await $bus.RunUntil(() => $consumer($arguments).Start($bus), stopping));",
                    ("bus", bus), ("consumer", consumer), ("arguments", arguments)));
                parts.Hosted++;
                return true;
            }

            case "workflows":
            {
                // `workflows Fulfil(shop), Returns(shop) from store;`: an engine over the store, running those workflows
                // (resuming the unfinished as it starts, stopping them as it stops), registered for handlers to start them.
                p.ReadWord();
                once(word);
                // `workflows engine: Fulfil(shop) from store;` names the engine for the clauses after (`consume Payments(engine);`).
                var local = p.Current.Kind == TokenKind.Identifier && p.Peek(1).Kind == TokenKind.Colon ? p.NextToken() : null;
                if (local is not null)
                    p.NextToken();
                var workflows = new List<(Token Name, List<ExpressionSyntax> Arguments)>();
                do
                {
                    if (workflows.Count > 0)
                        p.NextToken();
                    workflows.Add((p.Expect(TokenKind.Identifier), Arguments(p)));
                }
                while (p.Current.Kind == TokenKind.Comma);
                p.ExpectWord("from");
                var store = p.ParseExpression();
                p.Expect(TokenKind.Semicolon);
                var engine = local ?? p.FreshName("workflows");
                parts.Building.Add(p.Template.Statement("let $engine = Piper.Workflows.WorkflowEngine($store);", ("engine", engine), ("store", store)));
                foreach (var (workflow, arguments) in workflows)
                    parts.Building.Add(p.Template.Statement("$workflow($arguments).Register($engine);",
                        ("workflow", workflow), ("arguments", arguments), ("engine", engine)));
                parts.Building.Add(p.Template.Statement(
                    $"{DependencyInjection}.ServiceCollectionServiceExtensions.AddSingleton<Piper.Workflows.WorkflowEngine>(builder.Services, $engine);",
                    ("engine", engine)));
                parts.Building.Add(p.Template.Statement(
                    $"{Host}.Worker(builder, async (stopping: System.Threading.CancellationToken) => await $engine.RunUntil(stopping));",
                    ("engine", engine)));
                parts.Hosted++;
                return true;
            }

            case "relay":
            {
                // `relay DatabaseOutbox.Store(() => Shop(options));`: the outbox's messages sent on the bus while it runs.
                p.ReadWord();
                var store = p.ParseExpression();
                p.Expect(TokenKind.Semicolon);
                if (parts.Bus is not { } bus)
                {
                    p.Report(word.Span, "'relay' needs a bus to send on: write `bus from \"Messaging\";` before it.");
                    return true;
                }
                parts.Building.Add(p.Template.Statement(
                    $"{Host}.Worker(builder, async (stopping: System.Threading.CancellationToken) => await $bus.RelayUntil($store, stopping));",
                    ("bus", bus), ("store", store)));
                parts.Hosted++;
                return true;
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// A handler whose parameters are services, <c>(db: Db) => …;</c> or <c>async (db: Db) => { … }</c>: they need types,
    /// as each is resolved by its type. Null after a syntax error, or with a parameter without a type.
    /// </summary>
    public static LambdaExpressionSyntax? Handler(PluginParser p)
    {
        var async = p.AtWord("async") ? p.ReadWord() : null;
        if (p.Current.Kind is not (TokenKind.OpenParen or TokenKind.Identifier))
        {
            p.Report(p.Current.Span, "Expected a handler whose parameters are services, such as (db: Db) => …;");
            return null;
        }
        var start = p.Current.Span.Start;
        var parameters = p.ParseLambdaParameters();
        foreach (var parameter in parameters.Where(parameter => parameter.Type is null))
            p.Report(parameter.Span, $"Give '{parameter.Name.Text}' a type, such as ({parameter.Name.Text}: Db); it's the service it's given.");
        p.Expect(TokenKind.FatArrow);

        ExpressionSyntax body;
        if (p.Current.Kind == TokenKind.OpenBrace)
        {
            body = p.ParseBlock();
            if (p.Current.Kind == TokenKind.Semicolon)
                p.NextToken();
        }
        else
        {
            body = p.ParseExpression();
            p.Expect(TokenKind.Semicolon);
        }
        if (parameters.Any(parameter => parameter.Type is null))
            return null;
        return new LambdaExpressionSyntax(TextSpan.FromBounds(async?.Span.Start ?? start, body.Span.End), parameters, body) { Async = async };
    }

    /// <summary>A call's arguments after a block's name, <c>(settings, 5)</c>, or none.</summary>
    public static List<ExpressionSyntax> Arguments(PluginParser p)
    {
        var arguments = new List<ExpressionSyntax>();
        if (p.Current.Kind != TokenKind.OpenParen)
            return arguments;
        p.NextToken();
        while (p.Current.Kind is not (TokenKind.CloseParen or TokenKind.EndOfFile))
        {
            arguments.Add(p.ParseExpression());
            if (p.Current.Kind != TokenKind.Comma)
                break;
            p.NextToken();
        }
        p.Expect(TokenKind.CloseParen);
        return arguments;
    }

    /// <summary><c>singleton Clock =&gt; SystemClock</c>, <c>singleton Clock = clock</c> or <c>scoped Db</c>, as the services plugin reads them.</summary>
    private static StatementSyntax Registration(PluginParser p, string method)
    {
        const string Registrations = DependencyInjection + ".ServiceCollectionServiceExtensions";
        var service = p.ParseType();
        if (p.Current.Kind == TokenKind.FatArrow)
        {
            p.NextToken();
            return p.Template.Statement($"{Registrations}.{method}<$service, $implementation>(builder.Services);",
                ("service", service), ("implementation", p.ParseType()));
        }
        if (p.Current.Kind == TokenKind.Equals)
        {
            p.NextToken();
            var value = p.ParseExpression();
            var factory = value is LambdaExpressionSyntax { Parameters.Length: 1 }
                ? value
                : p.Template.Expression("$provider => $value", ("provider", p.FreshName("provider")), ("value", value));
            return p.Template.Statement($"{Registrations}.{method}<$service>(builder.Services, $factory);", ("service", service), ("factory", factory));
        }
        return p.Template.Statement($"{Registrations}.{method}<$service>(builder.Services);", ("service", service));
    }

    public static readonly Dictionary<string, string> DurationUnits = new()
    {
        ["millisecond"] = "FromMilliseconds",
        ["milliseconds"] = "FromMilliseconds",
        ["second"] = "FromSeconds",
        ["seconds"] = "FromSeconds",
        ["minute"] = "FromMinutes",
        ["minutes"] = "FromMinutes",
        ["hour"] = "FromHours",
        ["hours"] = "FromHours",
    };

    /// <summary>
    /// A length of time, <c>30 seconds</c> or <c>1 minute</c>, as a TimeSpan. With <paramref name="countOptional"/>,
    /// the count may be left out: <c>minute</c> is one minute.
    /// </summary>
    public static ExpressionSyntax Duration(PluginParser p, bool countOptional)
    {
        bool AtUnit() => p.Current.Kind == TokenKind.Identifier && DurationUnits.ContainsKey(p.Current.Text);

        var count = countOptional && AtUnit() ? p.Template.Expression("1") : p.ParseExpression();
        if (!AtUnit())
        {
            // `per fortnight`: the word read as the count was meant as the unit.
            var at = count is NameExpressionSyntax && countOptional ? count.Span : p.Current.Span;
            p.Report(at, "Expected the unit: seconds, minutes, hours or milliseconds.");
            return p.Template.Expression("System.TimeSpan.Zero");
        }
        var unit = p.ReadWord();
        return p.Template.Expression($"System.TimeSpan.{DurationUnits[unit.Text]}((double)($count))", ("count", count));
    }

    /// <summary>
    /// A worker's period: <c>30 seconds</c>, <c>minute</c>, or a name holding a TimeSpan (<c>tick</c>,
    /// <c>settings.tick</c>), or a count of units (<c>settings.seconds seconds</c>). A name is read without a call
    /// after it, as the handler's parameters follow.
    /// </summary>
    private static ExpressionSyntax Period(PluginParser p)
    {
        bool AtUnit() => p.Current.Kind == TokenKind.Identifier && DurationUnits.ContainsKey(p.Current.Text);
        if (AtUnit() || p.Current.Kind != TokenKind.Identifier)
            return Duration(p, countOptional: true);
        var first = p.NextToken();
        ExpressionSyntax path = new NameExpressionSyntax(first.Span, first);
        while (p.Current.Kind == TokenKind.Dot && p.Peek(1).Kind == TokenKind.Identifier)
        {
            p.NextToken();
            var member = p.NextToken();
            path = new MemberAccessExpressionSyntax(TextSpan.FromBounds(path.Span.Start, member.Span.End), path, member);
        }
        if (!AtUnit())
            return path;
        var unit = p.ReadWord();
        return p.Template.Expression($"System.TimeSpan.{DurationUnits[unit.Text]}((double)($count))", ("count", path));
    }

    /// <summary>Skips to the end of a clause that can't be read: past its `;`, or up to the block's `}`.</summary>
    public static void SkipClause(PluginParser p)
    {
        int depth = 0;
        while (p.Current.Kind != TokenKind.EndOfFile)
        {
            var token = p.Current;
            if (token.Kind == TokenKind.CloseBrace && depth == 0)
                return;
            p.NextToken();
            if (token.Kind == TokenKind.OpenBrace)
                depth++;
            else if (token.Kind == TokenKind.CloseBrace)
                depth--;
            else if (token.Kind == TokenKind.Semicolon && depth == 0)
                return;
        }
    }
}
