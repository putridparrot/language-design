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
            .Select(resource => (Path: resource[prefix.Length..], Text: Read(resource).Replace("__NAME__", name)))
            .Append((".gitignore", Read("Templates/gitignore")))
            .OrderBy(file => file.Item1, StringComparer.Ordinal);
        return [.. files];

        string Read(string resource)
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
            return reader.ReadToEnd();
        }
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.\-]*$")]
    private static partial Regex ValidName();
}
