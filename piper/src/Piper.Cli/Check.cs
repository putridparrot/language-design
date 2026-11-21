using System.Diagnostics;
using System.Text.Json.Nodes;
using Piper;
using Piper.Plugins;
using Piper.Text;

/// <summary>
/// <c>piper check &lt;folder&gt; [--since &lt;ref&gt;]</c>: one answer to what a release changes for what's already running.
/// The program is compiled with its recorded shapes (each api's, messages', workflow's and database's) compared, not
/// written: with those on disk, or with those checked in at a git tag or commit. Each is listed with what changed and
/// whether that's compatible, so CI can fail a release that would break callers, consumers, running instances or the
/// release still running against the database, and the output doubles as its notes on what changed.
/// </summary>
internal static class Check
{
    public const string Usage = """
        Usage:
          piper check <folder> [--since <ref>]
                       Compare each shape the program records (apis, messages, workflows, databases) with the one
                       recorded at <ref> (a git tag, branch or commit: piper-v1.4.0), or without --since, with those
                       on disk; list what changed and whether it's compatible. Fails if anything is breaking.
        """;

    public static int Run(string[] arguments)
    {
        if (arguments is not [var folder, .. var options] || folder.StartsWith('-'))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        string? since = null;
        for (int i = 0; i < options.Length; i++)
        {
            if (options[i] == "--since" && i + 1 < options.Length)
                since = options[++i];
            else
            {
                Console.Error.WriteLine($"Unknown argument '{options[i]}'.");
                Console.Error.WriteLine(Usage);
                return 2;
            }
        }
        if (!Directory.Exists(folder) || !File.Exists(Path.Combine(folder, PiperProject.FileName)))
        {
            Console.Error.WriteLine($"{folder} isn't a program folder: it needs a {PiperProject.FileName}.");
            return 2;
        }

        Git? git = null;
        if (since is not null)
        {
            git = Git.At(folder, since, out var problem);
            if (git is null)
            {
                Console.Error.WriteLine(problem);
                return 2;
            }
        }

        var (loaded, errors) = LoadedProject.Load(folder);
        foreach (var error in errors)
            Console.Error.WriteLine(error.Format());
        if (loaded is null)
            return 2;
        var paths = loaded.SharedFiles.Concat(LoadedProject.SourceFiles(folder)).ToList();
        var sources = paths.Select(path => new SourceText(File.ReadAllText(path), Path.GetFullPath(path))).ToList();
        var result = Compilation.Compile(sources, new CompilationOptions
        {
            Plugins = loaded.Plugins,
            References = loaded.References,
            SharedFiles = [.. sources.Take(loaded.SharedFiles.Length).Select(s => s.FileName)],
            WireShapes = WireShapeMode.Compare,
            ShapeBaseline = git is null ? null : git.Read,
        });
        foreach (var diagnostic in result.FormattedDiagnostics)
            Console.Error.WriteLine(diagnostic);
        if (!result.Success)
            return 1;

        // Shapes recorded then that the program no longer describes: whatever used them is gone.
        var described = result.Shapes.Select(s => s.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var folders = loaded.SharedFolders.Prepend(Path.GetFullPath(folder)).ToList();
        var removed = (git is null ? OnDisk(folders) : git.Shapes(folders))
            .Where(path => !described.Contains(path))
            .Order(StringComparer.Ordinal)
            .ToList();

        Console.WriteLine(since is null ? $"Shapes of {folder}, against those recorded:" : $"Shapes of {folder} since {since}:");
        if (result.Shapes.IsEmpty && removed.Count == 0)
        {
            Console.WriteLine("  The program records no shapes (an api, messages, a workflow or a database).");
            return 0;
        }
        var width = result.Shapes.Select(Title).Concat(removed.Select(RemovedTitle)).Max(t => t.Length) + 2;
        var breaking = false;
        foreach (var shape in result.Shapes)
        {
            var changes = shape.Changes;
            var status = shape.Unreadable ? "UNREADABLE: the recorded shape isn't JSON Piper can read"
                : shape.IsNew ? "new"
                : !shape.Breaking.IsEmpty ? "BREAKING"
                : changes.IsEmpty ? "no change"
                : "compatible";
            breaking |= shape.Unreadable || !shape.Breaking.IsEmpty;
            Console.WriteLine($"  {Title(shape).PadRight(width)}{status}");
            foreach (var change in shape.Breaking)
                Console.WriteLine($"      breaks: {change.Change} ({Relative(change.Location)})");
            foreach (var line in Summary(changes))
                Console.WriteLine($"      {line}");
            foreach (var note in Migrations(shape, git, loaded.References))
                Console.WriteLine($"      {note}");
        }
        foreach (var path in removed)
        {
            breaking = true;
            Console.WriteLine($"  {RemovedTitle(path).PadRight(width)}BREAKING");
            Console.WriteLine("      breaks: the program no longer has it, but what was built or recorded against it still uses it");
        }
        return breaking ? 1 : 0;
    }

    private static string Title(ShapeComparison shape) => $"{shape.Plugin} {shape.Name}";

    private static string RemovedTitle(string path) => $"removed {Path.GetFileName(path)}";

    private static string Relative(string location) =>
        Path.IsPathRooted(location) ? Path.GetRelativePath(Environment.CurrentDirectory, location) : location;

    /// <summary>The changes, a line for each kind: <c>added: GET /api/issues/{int}, Issue.priority</c>.</summary>
    private static IEnumerable<string> Summary(IReadOnlyList<ShapeChange> changes)
    {
        const int Shown = 8;
        foreach (var kind in new[] { ShapeChangeKind.Added, ShapeChangeKind.Removed, ShapeChangeKind.Changed })
        {
            var these = changes.Where(c => c.Kind == kind).Select(c => c.Path).ToList();
            if (these.Count == 0)
                continue;
            var listed = string.Join(", ", these.Take(Shown));
            var more = these.Count > Shown ? $", and {these.Count - Shown} more" : "";
            yield return $"{kind.ToString().ToLowerInvariant()}: {listed}{more}";
        }
    }

    /// <summary>
    /// For a database whose migrations are recorded (<c>piper db migrate</c>): those added since <c>--since</c>, and
    /// whether its shape now has one, so a change isn't released without the script that makes it.
    /// </summary>
    private static IEnumerable<string> Migrations(ShapeComparison shape, Git? git, ProgramReferences references)
    {
        if (!shape.Path.EndsWith(".schema.json", StringComparison.OrdinalIgnoreCase))
            yield break;
        var database = new DeclaredDatabase(Path.GetFileName(shape.Path)[..^".schema.json".Length], shape.Path);
        var recorded = database.Recorded();
        if (recorded.Count == 0)
            yield break;
        if (git is not null)
        {
            var before = git.Files(database.Migrations).Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.Ordinal);
            var added = recorded.Where(m => !before.Contains(m.Id)).Select(m => m.Id).ToList();
            if (added.Count > 0)
                yield return $"migrations added: {string.Join(", ", added)}";
        }
        // With the versions of Piper's own tables this program's Piper.Data makes: upgrading Piper may need a migration too.
        bool changed;
        try
        {
            changed = database.Changed(references, WireShapes.Write(shape.Now));
        }
        catch (InvalidOperationException)
        {
            changed = !DatabaseMigrations.Same(recorded[^1].Shape, WireShapes.Write(shape.Now));
        }
        if (changed)
            yield return $"no migration reaches this shape yet: write it with 'piper db migrate'";
    }

    /// <summary>The shapes recorded on disk in <paramref name="folders"/> (their subfolders too, not bin/ or obj/).</summary>
    private static IEnumerable<string> OnDisk(IEnumerable<string> folders) =>
        folders.SelectMany(folder => Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories)
            .Where(file => IsShape(file) && !Path.GetRelativePath(folder, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "bin" or "obj")))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static bool IsShape(string path) =>
        path.EndsWith(".shape.json", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".schema.json", StringComparison.OrdinalIgnoreCase);

    /// <summary>A git repository's files at one commit, by the paths they have now.</summary>
    private sealed class Git
    {
        private readonly string _root, _commit;

        private Git(string root, string commit) => (_root, _commit) = (root, commit);

        /// <summary>The repository <paramref name="folder"/> is in, at <paramref name="reference"/>; null, saying why, if there's none.</summary>
        public static Git? At(string folder, string reference, out string problem)
        {
            var (rootCode, root) = Run(folder, "rev-parse", "--show-toplevel");
            if (rootCode != 0)
            {
                problem = $"--since needs git, and {folder} to be in a git repository.";
                return null;
            }
            var (commitCode, commit) = Run(folder, "rev-parse", "--verify", "--quiet", $"{reference}^{{commit}}");
            if (commitCode != 0)
            {
                problem = $"There's no tag, branch or commit '{reference}' in the repository.";
                return null;
            }
            problem = "";
            return new Git(Path.GetFullPath(root.Trim()), commit.Trim());
        }

        /// <summary>The file at <paramref name="path"/> as it was at the commit, or null if it wasn't there.</summary>
        public string? Read(string path)
        {
            var (code, text) = Run(_root, "show", $"{_commit}:{InRepository(path)}");
            return code == 0 ? text : null;
        }

        /// <summary>The files in <paramref name="folder"/> (its subfolders too) at the commit, by their full paths now.</summary>
        public IEnumerable<string> Files(string folder)
        {
            var (code, text) = Run(_root, "ls-tree", "-r", "--name-only", _commit, "--", InRepository(folder) + "/");
            return code != 0 ? [] : text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => Path.GetFullPath(Path.Combine(_root, line.Trim())));
        }

        /// <summary>The shapes recorded in <paramref name="folders"/> at the commit.</summary>
        public IEnumerable<string> Shapes(IEnumerable<string> folders) =>
            folders.SelectMany(Files)
                .Where(path => IsShape(path) && !Path.GetRelativePath(_root, path).Split(Path.DirectorySeparatorChar, '/').Any(part => part is "bin" or "obj"))
                .Distinct(StringComparer.OrdinalIgnoreCase);

        private string InRepository(string path) => Path.GetRelativePath(_root, Path.GetFullPath(path)).Replace('\\', '/');

        private static (int Code, string Output) Run(string directory, params string[] arguments)
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
            try
            {
                using var process = Process.Start(start)!;
                var errorRead = process.StandardError.ReadToEndAsync();
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                _ = errorRead.Result;
                return (process.ExitCode, output);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return (-1, "");
            }
        }
    }
}
