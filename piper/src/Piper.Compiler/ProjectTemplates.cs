using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace Piper;

/// <summary>
/// What <c>piper new</c> makes: a program folder from a template, embedded in this assembly under Templates/. Each has a
/// piper.json and a main.piper, in which <c>__NAME__</c> is the program's name, and every one gets a .gitignore.
/// </summary>
public static partial class ProjectTemplates
{
    public const string Default = "console";

    /// <summary>The templates, with what each makes.</summary>
    public static ImmutableArray<(string Name, string Description)> All { get; } =
    [
        ("console", "a program with a Main and a test"),
        ("web", "a web API using the services and web plugins that come with piper"),
    ];

    /// <summary>Whether <paramref name="name"/> can name a program (and so its assembly): letters, digits, '_', '-' and '.'.</summary>
    public static bool IsValidName(string name) => ValidName().IsMatch(name);

    /// <summary>The files <paramref name="template"/> makes for a program named <paramref name="name"/>: paths relative to its folder, and their text.</summary>
    public static ImmutableArray<(string Path, string Text)> Files(string template, string name)
    {
        var assembly = typeof(ProjectTemplates).Assembly;
        var prefix = $"Templates/{template}/";
        var files = assembly.GetManifestResourceNames()
            .Where(resource => resource.StartsWith(prefix, StringComparison.Ordinal))
            .Select(resource => (Path: resource[prefix.Length..], Text: Read(assembly, resource).Replace("__NAME__", name)))
            .Append((".gitignore", Read(assembly, "Templates/gitignore")))
            .OrderBy(file => file.Item1, StringComparer.Ordinal);
        return [.. files];
    }

    /// <summary>
    /// The names <c>piper plugin new</c> gives a plugin from its folder's name: its assembly's
    /// (<c>Piper.Plugins.StateMachines</c>, from <c>StateMachines</c>), and the one piper.json files use
    /// (<c>state-machines</c>). A folder already called <c>Piper.Plugins.X</c> gives <c>X</c>; <c>my-plugin</c> gives
    /// <c>MyPlugin</c>. Null if the folder's name has no letters to make them from.
    /// </summary>
    public static (string Pascal, string Name)? PluginNames(string folderName)
    {
        const string Prefix = "Piper.Plugins.";
        var stem = folderName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ? folderName[Prefix.Length..] : folderName;
        var pascal = string.Concat(stem.Split(['-', '_', '.', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
        if (pascal.Length == 0 || !char.IsAsciiLetter(pascal[0]) || !pascal.All(char.IsAsciiLetterOrDigit))
            return null;
        var name = string.Concat(pascal.Select((c, i) => char.IsUpper(c) ? (i > 0 ? "-" : "") + char.ToLowerInvariant(c) : c.ToString()));
        return (pascal, name);
    }

    /// <summary>
    /// The files <c>piper plugin new</c> makes: a plugin project adding a <c>repeat</c> statement, and an example
    /// program using it. <paramref name="compilerReference"/> is the project's reference to Piper.Compiler, as MSBuild
    /// XML. Templates/plugin's files end in .template, so neither MSBuild nor the editor takes them for this
    /// repository's code.
    /// </summary>
    public static ImmutableArray<(string Path, string Text)> PluginFiles(string pascal, string name, string compilerReference)
    {
        var assembly = typeof(ProjectTemplates).Assembly;
        const string Prefix = "Templates/plugin/";
        string Fill(string text) => text.Replace("__PASCAL__", pascal).Replace("__NAME__", name).Replace("__COMPILER_REFERENCE__", compilerReference);
        var files = assembly.GetManifestResourceNames()
            .Where(resource => resource.StartsWith(Prefix, StringComparison.Ordinal))
            .Select(resource => (Path: Fill(resource[Prefix.Length..].Replace(".template", "")), Text: Fill(Read(assembly, resource))))
            .Append((".gitignore", Read(assembly, "Templates/gitignore")))
            .OrderBy(file => file.Item1, StringComparer.Ordinal);
        return [.. files];
    }

    private static string Read(System.Reflection.Assembly assembly, string resource)
    {
        using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
        return reader.ReadToEnd();
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.\-]*$")]
    private static partial Regex ValidName();
}
