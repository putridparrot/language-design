using System.Collections.Immutable;
using System.Text.Json;
using Piper.Diagnostics;
using Piper.Plugins;
using Piper.Text;

namespace Piper;

public enum PiperOutput
{
    Exe,
    Library,
}

/// <summary>
/// A program folder's <c>piper.json</c>, such as:
/// <code>
/// {
///   "name": "app", "output": "exe",
///   "packages": { "Humanizer.Core": "2.14.1" }, "frameworks": ["Microsoft.AspNetCore.App"], "references": ["lib/My.dll"],
///   "plugins": ["web", "../ext/My.Plugin.dll"]
/// }
/// </code>
/// Every key is optional and an empty file is allowed. Any other key is an error, so later versions can add keys without
/// changing what existing files mean. Comments and trailing commas are allowed, as in other .NET JSON configuration files.
/// </summary>
/// <param name="Name">The assembly's name (default: the folder's).</param>
/// <param name="Output">What <c>piper build</c> makes of the folder: an executable or a library (default: C# source).</param>
public sealed record PiperProject(string? Name, PiperOutput? Output)
{
    public const string FileName = "piper.json";

    /// <summary>
    /// Compiler plugins: the names of ones that come with piper (<c>web</c>), or paths of plugin assemblies, relative to the
    /// folder (see <see cref="Plugins.PluginLoader"/>).
    /// </summary>
    public ImmutableArray<string> Plugins { get; init; } = [];

    /// <summary>NuGet packages the program uses: id → version (see <see cref="ProgramReferences"/>).</summary>
    public ImmutableDictionary<string, string> Packages { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>Shared frameworks the program uses beyond .NET's own, such as <c>Microsoft.AspNetCore.App</c>.</summary>
    public ImmutableArray<string> Frameworks { get; init; } = [];

    /// <summary>Paths of .NET assemblies the program uses, relative to the folder.</summary>
    public ImmutableArray<string> References { get; init; } = [];

    public static PiperProject Empty { get; } = new(null, null);

    /// <summary>Reads a piper.json; on failure, the project is null and there are errors (PIP0400) explaining why.</summary>
    public static (PiperProject? Project, ImmutableArray<Diagnostic> Errors) Load(string path) => Parse(new SourceText(File.ReadAllText(path), path));

    public static (PiperProject? Project, ImmutableArray<Diagnostic> Errors) Parse(SourceText source)
    {
        if (string.IsNullOrWhiteSpace(source.Text))
            return (Empty, []);

        var errors = ImmutableArray.CreateBuilder<Diagnostic>();
        void Error(TextSpan span, string message) =>
            errors.Add(new Diagnostic("PIP0400", DiagnosticSeverity.Error, span, message) { Source = source });

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(source.Text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException exception)
        {
            var at = exception.LineNumber is { } line && exception.BytePositionInLine is { } column && line < source.LineCount
                ? source.GetPosition((int)line, (int)column)
                : 0;
            Error(new TextSpan(Math.Min(at, source.Length), 0), $"This isn't valid JSON: {exception.Message}");
            return (null, errors.ToImmutable());
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                Error(new TextSpan(0, 0), "piper.json must hold a JSON object, such as { \"name\": \"app\", \"output\": \"exe\" }.");
                return (null, errors.ToImmutable());
            }

            string? name = null;
            PiperOutput? output = null;
            ImmutableArray<string> plugins = [], frameworks = [], references = [];
            var packages = ImmutableDictionary<string, string>.Empty;
            int searchFrom = 0;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                // JsonDocument doesn't keep positions, but keys appear in the text in order.
                var quoted = JsonSerializer.Serialize(property.Name);
                int at = source.Text.IndexOf(quoted, searchFrom, StringComparison.Ordinal);
                var key = at < 0 ? new TextSpan(0, 0) : new TextSpan(at, quoted.Length);
                if (at >= 0)
                    searchFrom = at + quoted.Length;

                switch (property.Name)
                {
                    case "name" when property.Value.ValueKind == JsonValueKind.String &&
                                     property.Value.GetString() is { Length: > 0 } value &&
                                     value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0:
                        name = value;
                        break;
                    case "name":
                        Error(key, "\"name\" must be a non-empty string that can be a file name, such as \"app\".");
                        break;
                    case "output" when property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is "exe" or "library":
                        output = property.Value.GetString() == "exe" ? PiperOutput.Exe : PiperOutput.Library;
                        break;
                    case "output":
                        Error(key, "\"output\" must be \"exe\" or \"library\".");
                        break;
                    case "plugins" when StringList(property.Value) is { } paths:
                        plugins = paths;
                        break;
                    case "plugins":
                        Error(key, "\"plugins\" must be a list of plugins: names of ones that come with piper, or paths to plugin assemblies, such as [\"web\", \"../plugins/My.Plugin.dll\"].");
                        break;
                    case "packages" when property.Value.ValueKind == JsonValueKind.Object &&
                                         property.Value.EnumerateObject().All(p => p.Name.Length > 0 && p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 }):
                        packages = property.Value.EnumerateObject().ToImmutableDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.OrdinalIgnoreCase);
                        break;
                    case "packages":
                        Error(key, "\"packages\" must map NuGet package names to versions, such as { \"Humanizer.Core\": \"2.14.1\" }.");
                        break;
                    case "frameworks" when StringList(property.Value) is { } names:
                        frameworks = names;
                        break;
                    case "frameworks":
                        Error(key, "\"frameworks\" must be a list of shared framework names, such as [\"Microsoft.AspNetCore.App\"].");
                        break;
                    case "references" when StringList(property.Value) is { } paths:
                        references = paths;
                        break;
                    case "references":
                        Error(key, "\"references\" must be a list of paths to .NET assemblies, such as [\"lib/My.Library.dll\"].");
                        break;
                    default:
                        Error(key, $"Unknown key \"{property.Name}\"; piper.json can have \"name\", \"output\", \"packages\", \"frameworks\", \"references\" and \"plugins\".");
                        break;
                }
            }
            return errors.Count == 0
                ? (new PiperProject(name, output) { Plugins = plugins, Packages = packages, Frameworks = frameworks, References = references }, [])
                : (null, errors.ToImmutable());
        }
    }

    /// <summary>A JSON list of non-empty strings, or null if <paramref name="value"/> isn't one.</summary>
    private static ImmutableArray<string>? StringList(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 })
            ? [.. value.EnumerateArray().Select(item => item.GetString()!)]
            : null;
}

/// <summary>
/// A program folder as the CLI and the language server load it: its piper.json, the plugins that lists, and the
/// packages, frameworks and assemblies it (and its plugins) reference, resolved.
/// </summary>
public sealed record LoadedProject(PiperProject Project, ImmutableArray<IPiperPlugin> Plugins, ProgramReferences References)
{
    /// <summary>
    /// Loads the folder <paramref name="directory"/>'s piper.json and what it lists; with errors (in piper.json, loading
    /// plugins or resolving references), the project is null.
    /// </summary>
    public static (LoadedProject? Project, ImmutableArray<Diagnostic> Errors) Load(string directory)
    {
        var path = Path.Combine(directory, PiperProject.FileName);
        var source = new SourceText(File.ReadAllText(path), path);
        var (project, errors) = PiperProject.Parse(source);
        if (project is null)
            return (null, errors);

        var (plugins, pluginErrors) = PluginLoader.Load(directory, project.Plugins, source);
        if (!pluginErrors.IsEmpty)
            return (null, pluginErrors);

        // Plugins' packages and frameworks are the program's too; the program's own choice of version wins.
        var packages = new Dictionary<string, string>(project.Packages, StringComparer.OrdinalIgnoreCase);
        foreach (var (id, version) in plugins.SelectMany(p => p.Packages))
            packages.TryAdd(id, version);
        var frameworks = project.Frameworks.Concat(plugins.SelectMany(p => p.Frameworks)).ToList();

        var (references, referenceErrors) = ProgramReferences.Resolve(directory, packages, frameworks, project.References, source);
        return referenceErrors.IsEmpty ? (new LoadedProject(project, plugins, references), []) : (null, referenceErrors);
    }
}
