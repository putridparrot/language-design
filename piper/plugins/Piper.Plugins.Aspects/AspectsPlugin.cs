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

        var notified = @class.Fields.Where(IsNotified).ToList();
        var dependents = Dependents(@class, notified.Select(f => f.Name.Text).ToHashSet());

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
            var raises = dependents.GetValueOrDefault(field.Name.Text, []).Prepend(field.Name.Text)
                .Select(name => d.Template.Statement("OnPropertyChanged($name);", ("name", Text(d.Attribute.Span, name))))
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
        return [result];
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
    private static Dictionary<string, List<string>> Dependents(ClassDeclarationSyntax @class, HashSet<string> notified)
    {
        var computed = @class.Fields
            .Where(f => f.IsProperty && f.Arrow is not null && f.Static is null && !f.IsIndexer && !f.Accessors.Any(a => a.IsSetter))
            .ToDictionary(f => f.Name.Text, f => f.Accessors
                .SelectMany(a => new SyntaxNode?[] { a.BlockBody, a.ExpressionBody })
                .OfType<SyntaxNode>()
                .SelectMany(SyntaxWalker.DescendantsAndSelf)
                .SelectMany(NamesUsed)
                .ToHashSet());

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
