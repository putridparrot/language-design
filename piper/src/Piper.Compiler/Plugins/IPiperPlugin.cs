using Piper.Syntax;

namespace Piper.Plugins;

/// <summary>
/// A compiler plugin: it adds keywords to Piper without changing the compiler, such as <c>parallel for x in xs { … }</c>
/// or <c>services { … }</c>. A plugin only rewrites its construct into ordinary Piper syntax as it's parsed, so the
/// binder, the language server and the C# emitter see nothing new: errors in the rewritten code are reported as usual,
/// at the user's own code where the plugin kept it.
/// </summary>
/// <remarks>
/// Plugins are .NET assemblies listed in <c>piper.json</c>'s <c>"plugins"</c>; every public type implementing this
/// interface with a parameterless constructor is loaded. See docs/plugins.md.
/// </remarks>
public interface IPiperPlugin
{
    /// <summary>The plugin's name, used in its error messages.</summary>
    string Name { get; }

    /// <summary>The keywords this plugin adds. Each may belong to one plugin only, and can't be one of Piper's own.</summary>
    IReadOnlyList<PluginKeyword> Keywords { get; }

    /// <summary>
    /// NuGet packages (id → version) the code this plugin writes uses, which a program using it then references too;
    /// a version the program's own piper.json gives wins.
    /// </summary>
    IReadOnlyDictionary<string, string> Packages => System.Collections.Immutable.ImmutableDictionary<string, string>.Empty;

    /// <summary>Shared frameworks (<c>Microsoft.AspNetCore.App</c>) the code this plugin writes uses.</summary>
    IReadOnlyList<string> Frameworks => [];

    /// <summary>
    /// .NET assemblies the code this plugin writes uses, which come with the plugin: paths relative to the plugin's own
    /// assembly. A program using the plugin references them too, and a built one gets copies. This is where a plugin
    /// puts code Piper can't express, such as an ASP.NET Core filter.
    /// </summary>
    IReadOnlyList<string> References => [];

    /// <summary>
    /// Checks the program once it's type-checked, reporting errors or warnings, such as about the types used in the
    /// plugin's constructs. Called for every compilation (and as the editor analyzes), after the program is bound,
    /// whether or not it uses the plugin's keywords. By default it does nothing.
    /// </summary>
    void Analyze(PluginAnalysis analysis)
    {
    }
}

/// <summary>
/// A plugin's keyword. Like `yield`, it's a keyword only where its construct can start, and only when the next token
/// couldn't follow it as a name, so the word can still be an ordinary name: <c>parallel = 4;</c>, <c>parallel(items)</c>
/// and <c>parallel.Count</c> use a name. So a construct can't continue with an operator, `(`, `[`, `.` or `=`.
/// </summary>
public abstract record PluginKeyword(string Text)
{
    /// <summary>What the construct does, in Markdown, shown when the editor's pointer is over the keyword.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// The construct as the editor's completion inserts it, in LSP snippet syntax: <c>parallel for ${1:x} in ${2:items} {\n\t$0\n}</c>.
    /// Without one, completion inserts just the keyword.
    /// </summary>
    public string? Snippet { get; init; }

    /// <summary>
    /// What the words special to the construct mean (<c>on</c>, <c>singleton</c>), in Markdown, shown when the pointer
    /// is over one; a word without an entry shows <see cref="Description"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string> Words { get; init; } = System.Collections.Immutable.ImmutableDictionary<string, string>.Empty;
}

/// <summary>
/// Where the source uses a plugin's keyword, or a word special to its construct (<see cref="PluginParser.ReadWord"/>):
/// for the editor to colour, and to describe on hover.
/// </summary>
/// <param name="Text">The word as written: the keyword, or one such as <c>on</c>.</param>
/// <param name="Plugin">The plugin's name.</param>
public sealed record PluginWordUse(Text.SourceText File, Text.TextSpan Span, string Text, PluginKeyword Keyword, string Plugin)
{
    /// <summary>What the word means: its own entry in <see cref="PluginKeyword.Words"/>, or the keyword's description.</summary>
    public string? Description => Text != Keyword.Text && Keyword.Words.TryGetValue(Text, out var word) ? word : Keyword.Description;
}

/// <summary>
/// A keyword that starts a statement, inside a block. When the parser reaches it, it reads the keyword and calls
/// <paramref name="Expand"/>, which reads the rest of the construct and returns the statements it means.
/// </summary>
public sealed record StatementKeyword(string Text, Func<PluginParser, IEnumerable<StatementSyntax>> Expand) : PluginKeyword(Text);

/// <summary>Whether code can <c>await</c> where a plugin's construct is (<see cref="PluginParser.AsyncContext"/>).</summary>
public enum AsyncContext
{
    /// <summary>In a function or lambda that isn't async: code here can't await.</summary>
    Sync,

    /// <summary>In an <c>async fn</c> or <c>async</c> lambda: code here can await.</summary>
    Async,

    /// <summary>In a trailing closure, which is async if the code in it awaits.</summary>
    IfAwaited,
}

/// <summary>A keyword that starts a top-level declaration, such as <c>endpoints "/api" { … }</c>.</summary>
public sealed record DeclarationKeyword(string Text, Func<PluginParser, IEnumerable<DeclarationSyntax>> Expand) : PluginKeyword(Text);

/// <summary>
/// A decorator: an attribute that the plugin handles by rewriting what it's on as it's parsed, a function or method
/// (<c>[Retry(3)] fn Fetch() { … }</c>, with <see cref="Function"/>) or a class or record (<c>[Notify] class Person</c>,
/// with <see cref="Type"/>). The result is ordinary Piper. The attribute itself is removed, so it never reaches C#:
/// decorators are code generation at compile time, not reflection at run time. Several on one declaration apply nearest
/// first, so the outermost wraps the others. <paramref name="Text"/> is the attribute's name, used unqualified.
/// </summary>
public sealed record Decorator(string Text) : PluginKeyword(Text)
{
    /// <summary>A decorator of functions and methods.</summary>
    public Decorator(string text, Func<DecoratorContext, FunctionDeclarationSyntax> decorate) : this(text) => Function = decorate;

    /// <summary>Rewrites a function or method it's on, returning it rewritten; null if it can't be on one.</summary>
    public Func<DecoratorContext, FunctionDeclarationSyntax>? Function { get; init; }

    /// <summary>
    /// Rewrites a class or record it's on, returning it rewritten and any declarations to add beside it (an
    /// <c>extend</c> block with a record's new methods, say); null if it can't be on one.
    /// </summary>
    public Func<TypeDecoratorContext, IEnumerable<DeclarationSyntax>>? Type { get; init; }

    /// <summary>
    /// Makes this a marker, read by the type decorator named here: <c>[Command]</c> on a method, or <c>[NotifyIgnore]</c>
    /// on a property, of a class with <c>[Notify]</c>. A marker does nothing itself. It can go on a class's methods,
    /// fields, properties and events, where the type decorator finds it in their attributes, and it's removed once
    /// the type decorator has run. One on a member of a class without that decorator, or anywhere else, is an error.
    /// </summary>
    public string? ReadBy { get; init; }
}
