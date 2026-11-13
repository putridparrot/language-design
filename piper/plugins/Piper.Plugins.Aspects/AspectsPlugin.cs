using Piper.Plugins;
using Piper.Syntax;
using Piper.Text;

namespace Piper.Plugins.Aspects;

/// <summary>
/// Decorators for cross-cutting concerns. Each rewrites the function it's on into ordinary Piper at compile time, so
/// the program has no reflection, proxies or weaving at run time, just the code shown here:
/// <list type="bullet">
/// <item><c>[Trace]</c> writes the call, with its arguments, and the return:
/// <c>writer.WriteLine("→ Add(a = 1, b = 2)"); defer writer.WriteLine("← Add"); …body…</c>.</item>
/// <item><c>[Timed]</c> writes how long the call took: a <c>Stopwatch</c> and a <c>defer</c>.</item>
/// <item><c>[Retry(times)]</c> or <c>[Retry(times, delayMs)]</c> runs the body again when it throws, up to
/// <c>times</c> attempts in all, the last attempt's exception escaping. The body becomes a local function called in a loop.</item>
/// </list>
/// <c>[Trace]</c> and <c>[Timed]</c> write to standard error, or to the <c>TextWriter</c> given:
/// <c>[Trace(Console.Out)]</c>.
/// </summary>
public sealed class AspectsPlugin : IPiperPlugin
{
    public string Name => "aspects";

    public IReadOnlyList<PluginKeyword> Keywords { get; } =
    [
        new Decorator("Trace", Trace)
        {
            Type = TraceClass,
            Description = "`[Trace]` writes each call, with its arguments, and each return, to standard error (or to the `TextWriter` given: `[Trace(Console.Out)]`). On a class, it traces each of its methods.",
        },
        new Decorator("Notify")
        {
            Type = Notify,
            Description = "`[Notify]` on a class implements `INotifyPropertyChanged`: setting one of its properties (or public `mut` fields) raises `PropertyChanged`, for it and for the computed properties that use it.",
        },
        new Decorator("Command")
        {
            ReadBy = "Notify",
            Description = "`[Command]` on a method of a `[Notify]` class adds an `ICommand` property running it: `Save` gets `SaveCommand`. A `CanSave` method or property, if there is one, says when it can run, and the command tells its watchers again when what `CanSave` reads changes. An `OnCommandFailed(command: string, error: Exception)` method, if there is one, hears of each command's failure.",
        },
        new Decorator("NotifyIgnore")
        {
            ReadBy = "Notify",
            Description = "`[NotifyIgnore]` on a property of a `[Notify]` class leaves it alone: setting it raises nothing.",
        },
        new Decorator("AlsoNotify")
        {
            ReadBy = "Notify",
            Description = "`[AlsoNotify(\"Total\", …)]` on a property of a `[Notify]` class raises `PropertyChanged` for the properties named too, when it changes.",
        },
        new Decorator("Timed", Timed)
        {
            Description = "`[Timed]` writes how long each call took, to standard error (or to the `TextWriter` given: `[Timed(Console.Out)]`).",
        },
        new Decorator("Retry", Retry)
        {
            Description = "`[Retry(times)]` or `[Retry(times, delayMs)]` runs the function again when it throws, up to `times` attempts in all. The last attempt's exception escapes. Preconditions are checked once, before the first attempt.",
        },
    ];

    private static FunctionDeclarationSyntax Trace(DecoratorContext d)
    {
        if (Writer(d) is not { } writer)
            return d.Function;
        var parameters = d.Function.Parameters;
        var enter = $"→ {d.Name}({string.Join(", ", parameters.Select((p, i) => $"{p.Name.Text} = {{{i}}}"))})";
        var call = parameters.IsEmpty
            ? d.Template.Statement("$writer.WriteLine($text);", ("writer", writer), ("text", Text(d.Attribute.Span, enter)))
            : d.Template.Statement("$writer.WriteLine(System.String.Format($format, $args));",
                ("writer", writer), ("format", Text(d.Attribute.Span, enter)),
                ("args", parameters.Select(p => (ExpressionSyntax)new NameExpressionSyntax(p.Name.Span, p.Name with { IsGenerated = true })).ToList()));
        return d.WithPrologue(
        [
            call,
            d.Template.Statement("defer $writer.WriteLine($text);", ("writer", writer), ("text", Text(d.Attribute.Span, $"← {d.Name}"))),
        ]);
    }

    /// <summary><c>[Trace]</c> on a class: each method traced, as if each had the attribute.</summary>
    private static IEnumerable<DeclarationSyntax> TraceClass(TypeDecoratorContext d)
    {
        if (d.Class is null)
        {
            d.Report(d.Attribute.Span, "A record's methods are in its extend blocks: put [Trace] on them there.");
            return [d.Declaration];
        }
        return [d.DecorateMethods(Trace)];
    }

    /// <summary>
    /// <c>[Notify]</c> on a class: INotifyPropertyChanged, raised by each settable property and public <c>mut</c> field,
    /// which become a backing field and a property whose setter raises PropertyChanged when the value changes, for the
    /// property and the computed properties that name it.
    /// </summary>
    private static IEnumerable<DeclarationSyntax> Notify(TypeDecoratorContext d)
    {
        if (d.Class is not { } @class)
        {
            d.Report(d.Attribute.Span, "Notify is for classes: a record can't change, so there's nothing to notify.");
            return [d.Declaration];
        }

        var notified = @class.Fields.Where(f => IsNotified(f) && TypeDecoratorContext.Marker(f.Attributes, "NotifyIgnore") is null).ToList();
        var computed = Computed(@class);
        var dependents = Dependents(computed, notified.Select(f => f.Name.Text).ToHashSet());
        var commands = Commands(d, @class, computed);
        var failed = commands.Count == 0 ? null : CommandFailedHandler(d, @class);
        var span = d.Attribute.Span;

        var fields = new List<ClassFieldSyntax>();
        foreach (var field in @class.Fields)
        {
            if (!notified.Contains(field))
            {
                fields.Add(field);
                continue;
            }
            if (field.Type is null || field.Initializer is null)
            {
                d.Report(field.Name.Span, field.Type is null
                    ? $"Notify needs '{field.Name.Text}' to have a type, for its setter: write ': type' after its name."
                    : $"Notify needs '{field.Name.Text}' to have an initial value, for the field it keeps it in.");
                fields.Add(field);
                continue;
            }

            var backing = d.FreshName(field.Name.Text.ToLowerInvariant());
            var names = dependents.GetValueOrDefault(field.Name.Text, []).Prepend(field.Name.Text).Concat(AlsoNotified(d, field)).Distinct();
            var raises = names
                .Select(name => d.Template.Statement("OnPropertyChanged($name);", ("name", Text(span, name))))
                .Concat(commands.Where(c => c.CanReads.Contains(field.Name.Text))
                    .Select(c => d.Template.Statement("if $field is Some($c) { $c.RaiseCanExecuteChanged(); }", ("field", c.Field), ("c", d.FreshName("command")))))
                .ToList();
            var (generated, _, _) = d.ClassMembers("""
                mut $backing: $T = $initial;
                prop $name => $T {
                    get = $backing;
                    set {
                        if $backing != value {
                            $backing = value;
                            $raises
                        }
                    }
                }
                """,
                ("backing", backing), ("T", field.Type), ("initial", field.Initializer), ("name", field.Name), ("raises", raises));
            var property = generated[1];
            var setter = field.Accessors.FirstOrDefault(a => a.IsSetter);
            fields.Add(generated[0]);
            fields.Add(property with
            {
                // A public `mut` field was public; a property is unless it's private.
                Private = field.IsProperty ? field.Private : null,
                Protected = field.Protected,
                Attributes = field.Attributes,
                Documentation = field.Documentation,
                Accessors = [.. property.Accessors.Select(a => a.IsSetter && setter is not null ? a with { Private = setter.Private, Protected = setter.Protected } : a)],
            });
        }

        // What INotifyPropertyChanged needs, unless the class has it already.
        var (_, methods, events) = d.ClassMembers($$"""
            event PropertyChanged: System.ComponentModel.PropertyChangedEventHandler;
            {{(@class.Sealed is null ? "protected" : "private")}} fn OnPropertyChanged(name: string) {
                PropertyChanged(this, System.ComponentModel.PropertyChangedEventArgs(name));
            }
            """);
        // Each command: a field holding it, made when first asked for (a field's initializer can't use `this`), a
        // property giving it, and a class implementing ICommand by calling the method.
        var companions = new List<DeclarationSyntax>();
        foreach (var command in commands)
        {
            var local = d.FreshName("command");
            var (members, _, _) = d.ClassMembers("""
                mut $field: $Command? = None;
                prop $name => System.Windows.Input.ICommand {
                    get {
                        if $field is Some($c) { return $c; }
                        let $c = $Command(this);
                        $field = Some($c);
                        $c
                    }
                }
                """, ("field", command.Field), ("Command", command.Class), ("name", command.Property), ("c", local));
            fields.AddRange(members);

            var canExecute = command.Can switch
            {
                null => d.Template.Expression("true"),
                { } can when can.IsProperty => d.Template.Expression("owner.$can", ("can", can.Name)),
                { } can => d.Template.Expression("owner.$can()", ("can", can.Name)),
            };
            companions.AddRange(d.Template.Declarations($$"""
                class $Command(owner: $Owner): System.Windows.Input.ICommand {
                    event CanExecuteChanged: System.EventHandler;
                    fn CanExecute(parameter: object?) => bool = $canExecute;
                    {{Execute(command, failed is not null)}}
                    fn RaiseCanExecuteChanged() { CanExecuteChanged(this, System.EventArgs.Empty); }
                }
                """, ("Command", command.Class), ("Owner", @class.Name), ("canExecute", canExecute), ("method", command.Method.Name.Text)));
        }

        var result = @class with
        {
            Fields = [.. fields],
            Methods = @class.Methods.Any(m => m.Name.Text == "OnPropertyChanged") ? @class.Methods : @class.Methods.AddRange(methods),
            Events = @class.Events.Any(e => e.Name.Text == "PropertyChanged") ? @class.Events : @class.Events.AddRange(events),
        };
        if (!@class.Bases.Any(b => b.Type is NamedTypeSyntax { Name.Text: "INotifyPropertyChanged" }))
        {
            var @interface = d.Template.Declarations("class __Template: System.ComponentModel.INotifyPropertyChanged { }").OfType<ClassDeclarationSyntax>().Single().Bases[0];
            result = result with { Bases = result.Bases.Add(@interface) };
        }
        return [result, .. companions];
    }

    /// <summary>
    /// A command's <c>Execute</c>, running <c>owner.$method</c>. With an <c>OnCommandFailed</c> handler, what the method
    /// throws goes there. Without one, a plain method's exception escapes <c>Execute</c>, and an async one's is thrown
    /// where C#'s <c>async void</c> would throw it: on the synchronization context the command started on (the UI
    /// thread, in an app), or else the thread pool. Either way it isn't lost, as it would be in a task nobody awaits.
    /// </summary>
    private static string Execute(CommandInfo command, bool handled)
    {
        var name = command.Property.Text[..^"Command".Length];
        var report = $"owner.OnCommandFailed(\"{name}\", e);";
        if (command.Method.Async is null)
        {
            return handled
                ? $$"""fn Execute(parameter: object?) { try { owner.$method(); } catch (e) { {{report}} } }"""
                : "fn Execute(parameter: object?) { owner.$method(); }";
        }
        var rethrow = """
            let failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e);
            match context {
                Some(c) => c.Post(_ => failure.Throw(), None),
                None => { let _ = System.Threading.ThreadPool.QueueUserWorkItem(_ => failure.Throw()); },
            }
            """;
        return $$"""
            fn Execute(parameter: object?) { let _ = Run(System.Threading.SynchronizationContext.Current); }
            private async fn Run(context: System.Threading.SynchronizationContext?) => System.Threading.Tasks.Task {
                try { await owner.$method(); } catch (e) { {{(handled ? report : rethrow)}} }
            }
            """;
    }

    /// <summary>
    /// The class's <c>OnCommandFailed(command: string, error: Exception)</c> method, if it has one, which hears of each
    /// command's failure. The commands call it from outside the class, so it can't be private.
    /// </summary>
    private static FunctionDeclarationSyntax? CommandFailedHandler(TypeDecoratorContext d, ClassDeclarationSyntax @class)
    {
        if (@class.Methods.FirstOrDefault(m => m.Name.Text == "OnCommandFailed" && m.Static is null) is not { } handler)
            return null;
        if (handler.Private is not null || handler.Parameters.Length != 2)
        {
            d.Report(handler.Name.Span, "The commands report their failures to 'OnCommandFailed' from outside the class: make it fn OnCommandFailed(command: string, error: Exception), not private.");
            return null;
        }
        return handler;
    }

    /// <summary>A <c>[Command]</c> method, and what its command is made of.</summary>
    /// <param name="Property">The ICommand property, <c>SaveCommand</c>.</param>
    /// <param name="Field">The field holding the command once it's made.</param>
    /// <param name="Class">The class implementing ICommand for it.</param>
    /// <param name="Can">The <c>CanSave</c> method or property saying when it can run, if there is one.</param>
    /// <param name="CanReads">The properties <see cref="Can"/> reads, directly or through computed ones.</param>
    private sealed record CommandInfo(
        FunctionDeclarationSyntax Method, Token Property, Token Field, Token Class, (Token Name, bool IsProperty)? Can, HashSet<string> CanReads);

    /// <summary>The class's <c>[Command]</c> methods, checked: an instance method, not private, taking no arguments.</summary>
    private static List<CommandInfo> Commands(TypeDecoratorContext d, ClassDeclarationSyntax @class, Dictionary<string, HashSet<string>> computed)
    {
        var commands = new List<CommandInfo>();
        foreach (var method in @class.Methods)
        {
            if (TypeDecoratorContext.Marker(method.Attributes, "Command") is not { } marker)
                continue;
            var name = method.Name.Text;
            if (method.Static is not null || method.Private is not null || !method.Parameters.IsEmpty || !@class.TypeParameters.IsEmpty)
            {
                d.Report(marker.Span, @class.TypeParameters.IsEmpty
                    ? $"A command runs '{name}' on the object, with no arguments, from outside it: make it an instance method, not private, taking no parameters."
                    : "Commands aren't supported on a generic class.");
                continue;
            }

            // `CanSave()` or `CanSave`, saying when it can run, and what that reads.
            var stem = name.EndsWith("Async", StringComparison.Ordinal) && name.Length > 5 ? name[..^5] : name;
            (Token, bool)? can = null;
            var reads = new HashSet<string>();
            if (@class.Methods.FirstOrDefault(m => m.Name.Text == $"Can{stem}" && m.Parameters.IsEmpty && m.Static is null) is { } canMethod)
            {
                can = (canMethod.Name, false);
                reads = NamesIn(new SyntaxNode?[] { canMethod.BlockBody, canMethod.ExpressionBody });
            }
            else if (@class.Fields.FirstOrDefault(f => f.IsProperty && f.Name.Text == $"Can{stem}" && f.Static is null) is { } canProperty)
            {
                can = (canProperty.Name, true);
                reads = computed.GetValueOrDefault(canProperty.Name.Text, []);
            }
            // Through computed properties too: CanSave reading IsValid, which reads Name.
            var pending = new Queue<string>(reads);
            while (pending.TryDequeue(out var used))
            {
                foreach (var further in computed.GetValueOrDefault(used, []))
                {
                    if (reads.Add(further))
                        pending.Enqueue(further);
                }
            }

            commands.Add(new CommandInfo(method, new Token(TokenKind.Identifier, method.Name.Span, $"{stem}Command"),
                d.FreshName($"{stem.ToLowerInvariant()}Command"), d.FreshName($"{@class.Name.Text}{stem}Command"), can, reads));
        }
        return commands;
    }

    /// <summary>The property names <c>[AlsoNotify("Total", …)]</c> on <paramref name="field"/> lists.</summary>
    private static IEnumerable<string> AlsoNotified(TypeDecoratorContext d, ClassFieldSyntax field)
    {
        if (TypeDecoratorContext.Marker(field.Attributes, "AlsoNotify") is not { } marker)
            yield break;
        if (marker.Arguments.IsEmpty)
            d.Report(marker.Span, "AlsoNotify takes the names of the properties to raise too: [AlsoNotify(\"Total\")].");
        foreach (var argument in marker.Arguments)
        {
            if (argument is LiteralExpressionSyntax { Value: string name })
                yield return name;
            else
                d.Report(argument.Span, "AlsoNotify takes property names as strings: [AlsoNotify(\"Total\")].");
        }
    }

    /// <summary>
    /// Whether setting <paramref name="field"/> raises PropertyChanged: an instance automatic property with a `set`, or a
    /// public `mut` field. Not `init` ones (set only while the object's made), statics, or virtual and abstract ones.
    /// </summary>
    private static bool IsNotified(ClassFieldSyntax field) =>
        field.Static is null && !field.IsIndexer && field.Modifier is null && (field.IsProperty
            ? field.IsAutomatic && field.Accessors.Any(a => a.IsSetter && !a.IsInit)
            : field.Keyword.Kind == TokenKind.MutKeyword && field.Public is not null);

    /// <summary>
    /// For each notified property, the computed properties (getters only) that use it, directly or through another
    /// computed property, which change when it does.
    /// </summary>
    private static Dictionary<string, List<string>> Dependents(Dictionary<string, HashSet<string>> computed, HashSet<string> notified)
    {
        var result = new Dictionary<string, List<string>>();
        foreach (var name in notified)
        {
            // Those using it, then those using them, and so on.
            var found = new List<string>();
            var pending = new Queue<string>([name]);
            while (pending.TryDequeue(out var used))
            {
                foreach (var (property, names) in computed)
                {
                    if (names.Contains(used) && property != name && !found.Contains(property))
                    {
                        found.Add(property);
                        pending.Enqueue(property);
                    }
                }
            }
            result[name] = found;
        }
        return result;
    }

    /// <summary>The class's computed properties (getters only), with the names each one's getter uses.</summary>
    private static Dictionary<string, HashSet<string>> Computed(ClassDeclarationSyntax @class) =>
        @class.Fields
            .Where(f => f.IsProperty && f.Arrow is not null && f.Static is null && !f.IsIndexer && !f.Accessors.Any(a => a.IsSetter))
            .ToDictionary(f => f.Name.Text, f => NamesIn(f.Accessors.SelectMany(a => new SyntaxNode?[] { a.BlockBody, a.ExpressionBody })));

    /// <summary>The names used in <paramref name="nodes"/>: <c>First</c>, or <c>this.First</c>.</summary>
    private static HashSet<string> NamesIn(IEnumerable<SyntaxNode?> nodes) =>
        nodes.OfType<SyntaxNode>().SelectMany(SyntaxWalker.DescendantsAndSelf).SelectMany(NamesUsed).ToHashSet();

    /// <summary>The member name <paramref name="node"/> uses: <c>First</c>, or <c>this.First</c>.</summary>
    private static IEnumerable<string> NamesUsed(SyntaxNode node) => node switch
    {
        NameExpressionSyntax name => [name.Identifier.Text],
        MemberAccessExpressionSyntax { Target: NameExpressionSyntax { Identifier.Kind: TokenKind.ThisKeyword } } access => [access.Member.Text],
        _ => [],
    };

    private static FunctionDeclarationSyntax Timed(DecoratorContext d)
    {
        if (Writer(d) is not { } writer)
            return d.Function;
        var stopwatch = d.FreshName("stopwatch");
        return d.WithPrologue(d.Template.Statements("""
            let $stopwatch = System.Diagnostics.Stopwatch.StartNew();
            defer $writer.WriteLine(System.String.Format($format, $stopwatch.Elapsed.TotalMilliseconds));
            """,
            ("stopwatch", stopwatch), ("writer", writer), ("format", Text(d.Attribute.Span, $"{d.Name} took {{0:F1}} ms"))));
    }

    private static FunctionDeclarationSyntax Retry(DecoratorContext d)
    {
        if (d.Arguments is not ([_] or [_, _]))
        {
            d.Report(d.Attribute.Span, "Retry takes the number of attempts, and optionally the milliseconds to wait between them: [Retry(3)] or [Retry(3, 100)].");
            return d.Function;
        }
        if (d.IsIterator)
        {
            d.Report(d.Attribute.Span, "An iterator can't be retried: it runs as it's read, after the call has returned.");
            return d.Function;
        }

        // The body becomes a local function, so its `return`s still return its value, called until it doesn't throw.
        // The last attempt is outside the loop, so its exception escapes as it is. Preconditions stay on the function,
        // so they're checked once, before the first attempt.
        var (attempt, tries) = (d.FreshName("attempt"), d.FreshName("tries"));
        var holes = new List<(string, object)> { ("attempt", attempt), ("tries", tries), ("times", d.Arguments[0]), ("body", d.Body) };
        if (d.ReturnType is { } type)
            holes.Add(("T", type));
        string wait = "";
        if (d.Arguments is [_, var delay])
        {
            holes.Add(("delay", delay));
            wait = d.IsAsync ? "await System.Threading.Tasks.Task.Delay($delay);" : "System.Threading.Thread.Sleep($delay);";
        }

        var (modifier, call) = d.IsAsync ? ("async ", "await $attempt()") : ("", "$attempt()");
        var signature = d.ReturnType is null ? "" : " => $T";
        var (tryCall, lastCall) = d.ReturnsUnit
            ? ($"{call}; return;", $"{call};")
            : ($"return {call};", $"return {call};");
        var body = (BlockExpressionSyntax)d.Template.Expression($$"""
            {
                {{modifier}}fn $attempt(){{signature}} { $body }
                mut $tries = 1;
                while $tries < $times {
                    try {
                        {{tryCall}}
                    } catch (_) {
                        $tries += 1;
                        {{wait}}
                    }
                }
                {{lastCall}}
            }
            """, [.. holes]);
        return d.WithBody(body);
    }

    /// <summary>The <c>TextWriter</c> to write to: the attribute's argument, or standard error.</summary>
    private static ExpressionSyntax? Writer(DecoratorContext d)
    {
        switch (d.Arguments)
        {
            case []:
                return d.Template.Expression("System.Console.Error");
            case [var writer]:
                return writer;
            default:
                d.Report(d.Attribute.Span, $"{d.Attribute.Name.Parts[0].Text} takes at most one argument, the TextWriter to write to.");
                return null;
        }
    }

    /// <summary>A string literal of <paramref name="text"/>, at the attribute.</summary>
    private static LiteralExpressionSyntax Text(TextSpan span, string text)
    {
        var token = new Token(TokenKind.StringLiteral, span, "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"", text) { IsGenerated = true };
        return new LiteralExpressionSyntax(span, token, text);
    }
}
