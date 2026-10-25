using System.Collections.Immutable;
using Piper.Diagnostics;
using Piper.Plugins;
using Piper.Text;

namespace Piper.LanguageServer;

/// <summary>
/// The open documents and the programs they belong to. A program is every .piper file under the nearest folder (up from
/// the document) that has a <c>piper.json</c> file or a project using Piper.Build, as the CLI compiles a folder; a
/// file outside any such folder is a program on its own. Open documents' unsaved text wins over what's on disk.
/// </summary>
public sealed class Workspace
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly Dictionary<string, string> _open = new(PathComparer);
    private readonly Dictionary<string, AnalysisResult> _analyses = new(PathComparer);

    public void Open(string path, string text) => _open[Normalize(path)] = text;

    public void Change(string path, string text) => _open[Normalize(path)] = text;

    public void Close(string path) => _open.Remove(Normalize(path));

    public bool IsOpen(string path) => _open.ContainsKey(Normalize(path));

    public static string Normalize(string path) => Path.GetFullPath(path);

    /// <summary>The folder whose files form <paramref name="path"/>'s program, or null if it's a program on its own.</summary>
    public static string? ProgramRoot(string path)
    {
        for (var directory = Path.GetDirectoryName(Normalize(path)); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            try
            {
                if (File.Exists(Path.Combine(directory, "piper.json")) ||
                    Directory.EnumerateFiles(directory, "*.csproj").Any(p => File.ReadAllText(p).Contains("Piper.Build", StringComparison.OrdinalIgnoreCase)))
                {
                    return directory;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return null;
    }

    /// <summary>The files of <paramref name="path"/>'s program, including it.</summary>
    public IReadOnlyList<string> ProgramFiles(string path)
    {
        path = Normalize(path);
        if (ProgramRoot(path) is not { } root)
            return [path];

        var files = Directory.EnumerateFiles(root, "*.piper", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(root, f))
            .Concat(_open.Keys.Where(f => IsUnder(root, f)))
            .Append(path)
            .Select(Normalize)
            .Distinct(PathComparer)
            .Order(PathComparer)
            .ToList();
        return files;
    }

    private static bool IsUnder(string root, string file) =>
        Normalize(file).StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsBuildOutput(string root, string file) =>
        Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part is "bin" or "obj" or "node_modules");

    private string? Text(string path)
    {
        if (_open.TryGetValue(path, out var text))
            return text;
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Analyzes <paramref name="path"/>'s program afresh (after an edit) and remembers it for each of its files.</summary>
    public AnalysisResult Analyze(string path)
    {
        var sources = ProgramFiles(path)
            .Select(file => (File: file, Text: Text(file)))
            .Where(f => f.Text is not null)
            .Select(f => new SourceText(f.Text!, f.File))
            .ToList();
        var project = ProjectOf(ProgramRoot(path));
        var analysis = Compilation.Analyze(sources, project.Plugins, project.References);
        foreach (var source in sources)
            _analyses[source.FileName] = analysis;
        return analysis;
    }

    /// <summary>
    /// The plugins and references that <paramref name="root"/>'s piper.json lists (so the editor understands their
    /// keywords and types, as the CLI does), with any errors in the file, loading plugins or resolving references. None
    /// when there's no piper.json.
    /// </summary>
    public static (ImmutableArray<IPiperPlugin> Plugins, ProgramReferences References, ImmutableArray<Diagnostic> Errors) ProjectOf(string? root)
    {
        if (root is null)
            return ([], ProgramReferences.None, []);
        try
        {
            if (!File.Exists(Path.Combine(root, PiperProject.FileName)))
                return ([], ProgramReferences.None, []);
            var (project, errors) = LoadedProject.Load(root);
            return project is null ? ([], ProgramReferences.None, errors) : (project.Plugins, project.References, []);
        }
        catch (IOException)
        {
            return ([], ProgramReferences.None, []);
        }
    }

    /// <summary>The latest analysis containing <paramref name="path"/>, analyzing it if there is none.</summary>
    public AnalysisResult AnalysisOf(string path) =>
        _analyses.TryGetValue(Normalize(path), out var analysis) ? analysis : Analyze(path);

    public static SourceText? SourceOf(AnalysisResult analysis, string path) =>
        analysis.Sources.FirstOrDefault(s => PathComparer.Equals(s.FileName, Normalize(path)));
}
