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
}

/// <summary>
/// A plugin's keyword. Like `yield`, it's a keyword only where its construct can start, and only when the next token
/// couldn't follow it as a name, so the word can still be an ordinary name: <c>parallel = 4;</c>, <c>parallel(items)</c>
/// and <c>parallel.Count</c> use a name. So a construct can't continue with an operator, `(`, `[`, `.` or `=`.
/// </summary>
public abstract record PluginKeyword(string Text);

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
