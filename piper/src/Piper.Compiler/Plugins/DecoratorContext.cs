using System.Collections.Immutable;
using Piper.Diagnostics;
using Piper.Syntax;
using Piper.Text;

namespace Piper.Plugins;

/// <summary>
/// What a <see cref="Decorator"/> gets: the function or method it's on, and the attribute that named it. The decorator
/// returns the function rewritten, usually with <see cref="WithPrologue"/> or <see cref="WithBody"/>. That's ordinary
/// Piper syntax, so the binder checks it as if the user had written it. Nothing happens at run time beyond the code it
/// writes: no reflection, proxies or weaving.
/// </summary>
public sealed class DecoratorContext
{
    private readonly DiagnosticBag _diagnostics;
    private readonly Func<string, Token> _freshName;

    internal DecoratorContext(IPiperPlugin plugin, AttributeSyntax attribute, FunctionDeclarationSyntax function, bool isMethod,
        DiagnosticBag diagnostics, Func<string, Token> freshName)
    {
        Plugin = plugin;
        Attribute = attribute;
        Function = function;
        IsMethod = isMethod;
        _diagnostics = diagnostics;
        _freshName = freshName;
        Template = new SyntaxTemplate(attribute.Span);
    }

    public IPiperPlugin Plugin { get; }

    /// <summary>The attribute naming the decorator, <c>[Retry(3, 100)]</c>; it isn't passed on to C#.</summary>
    public AttributeSyntax Attribute { get; }

    /// <summary>The attribute's arguments, as written: expressions the decorator can put in the code it writes.</summary>
    public ImmutableArray<ExpressionSyntax> Arguments => Attribute.Arguments;

    /// <summary>
    /// The function, as decorators nearer to it have left it (they run nearest first), without the decorator
    /// attributes. Rewrite it with <c>with</c>, so its name, parameters, <c>requires</c> clauses and other attributes stay.
    /// </summary>
    public FunctionDeclarationSyntax Function { get; }

    /// <summary>The function's name as written (an operator's symbol for an operator).</summary>
    public string Name => Function.Name.Text;

    /// <summary>Whether it's a method of a class, record, enum or <c>extend</c> rather than a top-level function.</summary>
    public bool IsMethod { get; }

    /// <summary>An <c>async fn</c>: its body can await, and code wrapping it must too.</summary>
    public bool IsAsync => Function.Async is not null;

    /// <summary>The body yields: code wrapping it in another function would make that the iterator instead.</summary>
    public bool IsIterator => Function.IsIterator;

    /// <summary>The declared return type, or null when there isn't one. For an async function, its Task type: <c>Task&lt;int&gt;</c>.</summary>
    public TypeSyntax? ReturnType => Function.ReturnType;

    /// <summary>
    /// Whether it gives no value: no return type, or an async function's <c>Task</c> or <c>ValueTask</c>, so code wrapping
    /// it writes <c>f(); return;</c> (or <c>await f(); return;</c>) rather than <c>return f();</c>.
    /// </summary>
    public bool ReturnsUnit => Function.ReturnType switch
    {
        null => true,
        NamedTypeSyntax { Name.Text: "unit" or "Unit" } => true,
        NamedTypeSyntax { Name.Text: "Task" or "ValueTask", TypeArguments.IsEmpty: true } => IsAsync,
        _ => false,
    };

    /// <summary>
    /// Whether the value it gives is a <c>Result</c>: <c>Result&lt;T, E&gt;</c>, or an async function's
    /// <c>Task&lt;Result&lt;T, E&gt;&gt;</c>. Read from the declared type as written, so a type alias for one isn't seen.
    /// A policy can then treat an <c>Err</c> as a failure, as it does a throw (see <c>PiperTransient</c>).
    /// </summary>
    public bool ReturnsResult => Function.ReturnType switch
    {
        NamedTypeSyntax { Name.Text: "Result", TypeArguments.Length: 2 } => true,
        NamedTypeSyntax { Name.Text: "Task" or "ValueTask", TypeArguments: [NamedTypeSyntax { Name.Text: "Result", TypeArguments.Length: 2 }] } => IsAsync,
        _ => false,
    };

    /// <summary>
    /// Builds Piper syntax from source text with holes (see <see cref="SyntaxTemplate"/>). Its own syntax gets the
    /// attribute's span, so an error in the code a decorator writes points at the attribute.
    /// </summary>
    public SyntaxTemplate Template { get; }

    /// <summary>The body as a block: <c>= e;</c> is <c>{ e }</c>.</summary>
    public BlockExpressionSyntax Body =>
        Function.BlockBody ?? new BlockExpressionSyntax(Function.ExpressionBody!.Span, [], Function.ExpressionBody);

    /// <summary>The function with <paramref name="body"/> as its body.</summary>
    public FunctionDeclarationSyntax WithBody(BlockExpressionSyntax body) => Function with { BlockBody = body, ExpressionBody = null };

    /// <summary>
    /// The function with <paramref name="statements"/> run before its body, which keeps its value. With a
    /// <c>defer</c> among them this wraps the body, as a <c>finally</c> would: it runs however the function returns,
    /// and works in async functions and iterators too.
    /// </summary>
    public FunctionDeclarationSyntax WithPrologue(IEnumerable<StatementSyntax> statements) =>
        WithBody(new BlockExpressionSyntax(Body.Span, [.. statements], Body));

    /// <summary>
    /// A name for something the decorator declares, such as <c>__attempt_1</c>: numbered per file, and starting with two
    /// underscores, so it can't clash with the user's names or another decorator's.
    /// </summary>
    public Token FreshName(string hint) => _freshName(hint);

    /// <summary>Reports an error, such as a decorator used where it can't work; the message is followed by the plugin's name.</summary>
    public void Report(TextSpan span, string message) => _diagnostics.ReportPluginError(span, Plugin.Name, message);
}
