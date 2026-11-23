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

    /// <summary>A windowed executable (WPF, Windows Forms, Avalonia), as MSBuild's <c>WinExe</c>: on Windows, no console opens with it.</summary>
    WinExe,
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

    /// <summary>
    /// Other Piper program folders this one shares code with, relative to the folder (<c>["../shared"]</c>): their
    /// source is compiled into the program, without their tests, with their plugins and references. Piper's own types
    /// (Options, newtypes, enums) don't survive a trip through a compiled assembly, so they're shared as source.
    /// </summary>
    public ImmutableArray<string> Projects { get; init; } = [];

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
            ImmutableArray<string> plugins = [], frameworks = [], references = [], projects = [];
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
                    case "output" when property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is "exe" or "winexe" or "library":
                        output = property.Value.GetString() switch { "exe" => PiperOutput.Exe, "winexe" => PiperOutput.WinExe, _ => PiperOutput.Library };
                        break;
                    case "output":
                        Error(key, "\"output\" must be \"exe\", \"winexe\" (a windowed program, without a console) or \"library\".");
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
                    case "projects" when StringList(property.Value) is { } folders:
                        projects = folders;
                        break;
                    case "projects":
                        Error(key, "\"projects\" must be a list of paths to Piper program folders whose code this one shares, such as [\"../shared\"].");
                        break;
                    default:
                        Error(key, $"Unknown key \"{property.Name}\"; piper.json can have \"name\", \"output\", \"packages\", \"frameworks\", \"references\", \"projects\" and \"plugins\".");
                        break;
                }
            }
            return errors.Count == 0
                ? (new PiperProject(name, output) { Plugins = plugins, Packages = packages, Frameworks = frameworks, References = references, Projects = projects }, [])
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
    /// <summary>What wasn't loaded from a folder that isn't trusted (PIP0404), to report on its piper.json.</summary>
    public ImmutableArray<Diagnostic> Warnings { get; init; } = [];

    /// <summary>
    /// Loads the folder <paramref name="directory"/>'s piper.json and what it lists; with errors (in piper.json, loading
    /// plugins or resolving references), the project is null.
    /// </summary>
    /// <param name="trusted">
    /// False for a folder the user hasn't said they trust, such as a repository the editor has just opened. Loading a
    /// plugin, restoring packages (MSBuild runs the folder's Directory.Build files) and reading assemblies can each run
    /// code from the folder, so only what can't is loaded: the plugins that come with piper or were added with
    /// <c>piper plugin add</c>, and shared frameworks. The rest is left out and reported as a warning.
    /// </param>
    /// <param name="runtime">The platform native libraries are for (<c>linux-x64</c>), when it isn't this machine: publishing for a container.</param>
    public static (LoadedProject? Project, ImmutableArray<Diagnostic> Errors) Load(string directory, bool trusted = true, string? runtime = null) =>
        Load(directory, trusted, runtime, lenient: false);

    /// <summary>
    /// The same; with <paramref name="lenient"/>, a plugin that can't be loaded (one this piper doesn't know, as an older
    /// copy of the tool running the editor wouldn't) is reported but the rest are loaded, and the project returned with
    /// the errors, so the editor still understands the code that doesn't use it, rather than none of it.
    /// </summary>
    public static (LoadedProject? Project, ImmutableArray<Diagnostic> Errors) Load(string directory, bool trusted, string? runtime, bool lenient)
    {
        var unloaded = new List<Diagnostic>();
        var path = Path.Combine(directory, PiperProject.FileName);
        var source = new SourceText(File.ReadAllText(path), path);
        var (project, errors) = PiperProject.Parse(source);
        if (project is null)
            return (null, errors);

        // The projects it shares code with ("projects"), and theirs, each once: their plugins, packages, frameworks and
        // assemblies are needed to compile their code, so they're the program's too.
        var shared = new List<(string Folder, PiperProject Project, SourceText Source)>();
        var sharedErrors = new List<Diagnostic>();
        CollectShared(Path.GetFullPath(directory), project, source, shared, sharedErrors, [Normalized(directory)]);
        if (sharedErrors.Count > 0)
            return (null, [.. sharedErrors]);
        var everyone = shared.Select(s => (s.Folder, s.Project, s.Source)).Prepend((Folder: directory, Project: project, Source: source)).ToList();

        var pluginBuilder = new List<IPiperPlugin>();
        var skipped = new List<string>();
        foreach (var (folder, each, eachSource) in everyone)
        {
            var named = trusted ? each.Plugins : [.. each.Plugins.Where(PluginLoader.IsBundledName)];
            var (loadedPlugins, pluginErrors) = PluginLoader.Load(folder, named, eachSource);
            if (!pluginErrors.IsEmpty)
            {
                if (!lenient)
                    return (null, pluginErrors);
                unloaded.AddRange(pluginErrors);
            }
            // Plugins are loaded once per assembly, so a plugin two projects name is the same one.
            pluginBuilder.AddRange(loadedPlugins.Where(p => !pluginBuilder.Contains(p) && !pluginBuilder.Any(q => q.Name == p.Name)));
            if (!trusted)
                skipped.AddRange(each.Plugins.Except(named).Select(p => $"the plugin '{p}'"));
        }
        ImmutableArray<IPiperPlugin> plugins = [.. pluginBuilder];

        // Plugins' packages, frameworks and assemblies are the program's too; the program's own choice of version wins,
        // then the shared projects', in order.
        var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, version) in everyone.SelectMany(e => e.Project.Packages).Concat(plugins.SelectMany(p => p.Packages)))
            packages.TryAdd(id, version);
        var frameworks = everyone.SelectMany(e => e.Project.Frameworks).Concat(plugins.SelectMany(p => p.Frameworks)).Distinct().ToList();
        // A plugin's own assemblies come with it, so they're as trusted as it is. A shared project's are relative to its folder.
        var ownAssemblies = everyone.SelectMany(e => e.Project.References.Select(r => e.Folder == directory ? r : Path.GetFullPath(Path.Combine(e.Folder, r)))).ToList();
        var assemblies = (trusted ? ownAssemblies : []).Concat(plugins.SelectMany(PluginLoader.References)).ToList();

        if (!trusted)
        {
            skipped.AddRange(packages.Keys.Order(StringComparer.OrdinalIgnoreCase).Select(p => $"the package '{p}'"));
            skipped.AddRange(ownAssemblies.Select(r => $"the assembly '{r}'"));
            packages.Clear();
        }

        var (references, referenceErrors) = ProgramReferences.Resolve(directory, packages, frameworks, assemblies, source, runtime);
        if (!referenceErrors.IsEmpty)
            return (null, referenceErrors);
        ImmutableArray<Diagnostic> warnings = skipped.Count == 0 ? [] :
        [
            new Diagnostic("PIP0404", DiagnosticSeverity.Warning, default,
                $"This folder isn't trusted, so the editor hasn't loaded what could run code from it: {string.Join(", ", skipped)}. " +
                "Code using them shows errors until you trust the folder (in VS Code: Manage Workspace Trust).") { Source = source },
        ];
        return (new LoadedProject(project, plugins, references) { Warnings = warnings, SharedFolders = [.. shared.Select(s => s.Folder)] }, [.. unloaded]);
    }

    /// <summary>
    /// The folders of the projects the program shares code with (piper.json's <c>"projects"</c>, and theirs), as full
    /// paths, each once.
    /// </summary>
    public ImmutableArray<string> SharedFolders { get; init; } = [];

    /// <summary>The .piper files of the shared projects (see <see cref="SharedFolders"/>), which are compiled in without their tests.</summary>
    public ImmutableArray<string> SharedFiles => [.. SharedFolders.SelectMany(SourceFiles)];

    /// <summary>A program folder's .piper files, in its subfolders too (not bin/ or obj/, where builds write), in a stable order.</summary>
    public static IEnumerable<string> SourceFiles(string folder) =>
        Directory.GetFiles(folder, "*.piper", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(folder, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .Order(StringComparer.Ordinal);

    /// <summary>
    /// The shared projects' folders of the program at <paramref name="directory"/>, from piper.json alone, without
    /// loading plugins or packages (for <c>piper run --watch</c>, to watch them too); none if it has no valid piper.json.
    /// </summary>
    public static ImmutableArray<string> SharedFoldersOf(string directory)
    {
        var path = Path.Combine(directory, PiperProject.FileName);
        if (!File.Exists(path))
            return [];
        var source = new SourceText(File.ReadAllText(path), path);
        if (PiperProject.Parse(source).Project is not { } project)
            return [];
        var shared = new List<(string Folder, PiperProject Project, SourceText Source)>();
        CollectShared(Path.GetFullPath(directory), project, source, shared, [], [Normalized(directory)]);
        return [.. shared.Select(s => s.Folder)];
    }

    private static string Normalized(string folder) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    private static void CollectShared(
        string folder, PiperProject project, SourceText source,
        List<(string Folder, PiperProject Project, SourceText Source)> shared, List<Diagnostic> errors, HashSet<string> seen)
    {
        foreach (var entry in project.Projects)
        {
            // Reported at the entry in piper.json, found by its text (JsonDocument keeps no positions).
            var quoted = System.Text.Json.JsonSerializer.Serialize(entry);
            int at = source.Text.IndexOf(quoted, StringComparison.Ordinal);
            var span = at < 0 ? new TextSpan(0, 0) : new TextSpan(at, quoted.Length);
            void Error(string message) => errors.Add(new Diagnostic("PIP0400", DiagnosticSeverity.Error, span, message) { Source = source });

            var other = Normalized(Path.Combine(folder, entry));
            if (!seen.Add(other))
                continue;   // the program itself, or one already shared: compiled in once
            var path = Path.Combine(other, PiperProject.FileName);
            if (!File.Exists(path))
            {
                Error(Directory.Exists(other)
                    ? $"'{entry}' has no piper.json; a shared project is a Piper program folder, with one."
                    : $"There's no folder '{entry}' (looked for {other}).");
                continue;
            }
            var otherSource = new SourceText(File.ReadAllText(path), path);
            var (otherProject, otherErrors) = PiperProject.Parse(otherSource);
            if (otherProject is null)
            {
                errors.AddRange(otherErrors);
                continue;
            }
            shared.Add((other, otherProject, otherSource));
            CollectShared(other, otherProject, otherSource, shared, errors, seen);
        }
    }
}
