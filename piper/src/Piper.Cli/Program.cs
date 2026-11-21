using System.Collections.Immutable;
using Piper;
using Piper.Plugins;
using Piper.Text;

const string Usage = """
    Usage:
      piper build <inputs...> [-o <dir>]   Transpile to C#: one .cs per .piper (default: next to each source file)
      piper build <inputs...> --exe [-o <dir>] [--name <name>]
                                           Compile to an executable: <name>.exe (or <name>), .dll, .pdb and Piper.Runtime.dll
                                           (default: a bin folder next to the first input; name: the first input's)
      piper build <inputs...> --publish [-o <dir>] [--name <name>] [--runtime <rid>]
                                           Compile to a folder ready for a container image: the executable, its
                                           packages (native libraries for linux-x64, or <rid>) and a Dockerfile on
                                           .NET's runtime or aspnet image; then docker build it (default: bin/publish)
      piper build <inputs...> --library [-o <dir>] [--name <name>]
                                           Compile to a library: <name>.dll, .pdb and Piper.Runtime.dll
      piper emit  <inputs...>              Print the generated C# to stdout
      piper run   <inputs...> [--watch] [-- <args...>]
                                           Transpile, compile and run; <args> go to Main(args: string[]). The `--` can be
                                           left out when the first starts with '-': piper run app --port 8001.
                                           --watch runs it again whenever a .piper file or piper.json changes,
                                           and reloads a web program's open pages
      piper test  <inputs...> [--filter <text>] [--seed <n>]
                                           Run the program's test blocks (those whose name contains <text>);
                                           --seed repeats a property test's failure, from the seed it showed
      piper fmt   <inputs...> [--check]    Lay out .piper files as Piper is written (indentation and blank lines),
                                           rewriting those that change; --check only lists them, failing if any would
      piper new   <folder> [--template <name>]
                                           Make a program folder: console (the default), or web (an API using the
                                           services and web plugins)
      piper plugin add <folder|dll> [--name <name>]
                                           Add a plugin, so a piper.json can name it as it does those that come with
                                           piper (its folder is copied to ~/.piper/plugins, or $PIPER_PLUGINS)
      piper plugin new <folder> [--name <name>]
                                           Make a plugin project, which each build adds to piper, with an example program
      piper plugin remove <name>           Remove a plugin added with 'piper plugin add'
      piper plugin list                    List the plugins that come with piper and those added
      piper package add <id> [--version <v>] [--prerelease] [<folder>]
                                           Add a NuGet package to the folder's piper.json (default: the current one):
                                           the newest stable version on your NuGet sources unless --version gives one
      piper package remove <id> [<folder>] Remove a package from the folder's piper.json
      piper db migrate <folder> [--apply --connection <c>]
                                           Write the next migration of each of the program's databases, if its shape
                                           changed (SQL scripts, per provider, beside the declaration); --apply applies
                                           those a database doesn't have yet. `piper db` shows the details
      piper db status <folder> [--connection <c>]
                                           List the migrations, and which a database has
      piper check <folder> [--since <ref>] Compare the shapes the program records (apis, messages, workflows,
                                           databases) with those at a git tag or commit (or on disk), listing what
                                           changed and whether it's compatible; fails if anything is breaking
      piper lsp                           Run the language server (LSP over stdin and stdout), for editors
      piper --version                      Show the compiler's version

    Inputs are .piper files or directories (searched recursively for .piper files);
    all of them are compiled together as one program. A single directory may have a piper.json,
    { "name": "app", "output": "exe" | "library" }, which 'piper build' follows when given no --exe,
    --library or --name, whose "plugins" can name those that come with piper: ["services", "web"], and
    whose "projects" name other program folders whose code it shares: ["../shared"].

    An api's shape (its calls and the types they send) is kept in <Name>.shape.json beside it, which build, run
    and test record and check: a change that would break callers built before is an error. --accept-breaking
    records it anyway, when the break is meant.
    """;

if (args is ["--version" or "-v"])
{
    Console.WriteLine($"piper {PiperVersion.Current}");
    return 0;
}

if (args is ["lsp", ..])
    return await Piper.LanguageServer.PiperLanguageServer.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput());

if (args is ["new", .. var newArguments])
    return New(newArguments);

if (args is ["plugin", .. var pluginArguments])
    return Plugin(pluginArguments);

if (args is ["package", .. var packageArguments])
    return Package(packageArguments);

if (args is ["fmt", .. var fmtArguments])
    return Fmt(fmtArguments);

if (args is ["db", .. var dbArguments])
    return Database.Run(dbArguments);

if (args is ["check", .. var checkArguments])
    return Check.Run(checkArguments);

if (args.Length < 2 || args[0] is not ("build" or "emit" or "run" or "test"))
{
    Console.Error.WriteLine(Usage);
    return 2;
}

var command = args[0];
var inputs = new List<string>();
string? outputDirectory = null;
string? assemblyName = null;
string? filter = null;
int? seed = null;
bool executable = false, library = false, watch = false, publish = false, acceptBreaking = false;
string? runtime = null;
string[] programArguments = [];
for (int i = 1; i < args.Length; i++)
{
    // `piper run app -- --port 8001`: what follows `--` is the program's.
    if (args[i] == "--" && command == "run")
    {
        programArguments = args[(i + 1)..];
        break;
    }
    if (args[i] is "-o" or "--output" && i + 1 < args.Length)
        outputDirectory = args[++i];
    else if (args[i] == "--name" && i + 1 < args.Length)
        assemblyName = args[++i];
    else if (args[i] == "--filter" && i + 1 < args.Length && command == "test")
        filter = args[++i];
    else if (args[i] == "--seed" && i + 1 < args.Length && command == "test" && int.TryParse(args[i + 1], out var seedValue))
    {
        seed = seedValue;
        i++;
    }
    else if (args[i] == "--exe" && command == "build")
        executable = true;
    else if (args[i] == "--publish" && command == "build")
        publish = executable = true;
    else if (args[i] == "--runtime" && i + 1 < args.Length && command == "build")
        runtime = args[++i];
    else if (args[i] == "--accept-breaking" && command != "emit")
        acceptBreaking = true;
    else if (args[i] == "--library" && command == "build")
        library = true;
    // Before the program's own options, so `piper run app --watch --port 8001` watches; `--` passes a --watch of its own.
    else if (args[i] == "--watch" && command == "run")
        watch = true;
    // `piper run app --port 8001`: an option piper doesn't have, after the program, is the program's, as with
    // `dotnet run`. (So is everything after it.) That also covers shells that drop a bare `--`, as Windows PowerShell can.
    else if (command == "run" && inputs.Count > 0 && args[i].StartsWith('-'))
    {
        programArguments = args[i..];
        break;
    }
    else if (args[i].StartsWith('-'))
    {
        Console.Error.WriteLine($"Unknown argument '{args[i]}'.");
        Console.Error.WriteLine(Usage);
        return 2;
    }
    else
        inputs.Add(args[i]);
}

var files = new List<string>();
foreach (var input in inputs)
{
    if (Directory.Exists(input))
        files.AddRange(Directory.GetFiles(input, "*.piper", SearchOption.AllDirectories).Order());
    else if (File.Exists(input))
        files.Add(input);
    else
    {
        Console.Error.WriteLine($"File not found: {input}");
        return 2;
    }
}

if (files.Count == 0)
{
    Console.Error.WriteLine("No .piper files to compile.");
    return 2;
}

if (watch)
    return Watch.Run(inputs, programArguments);
if (command == "run")
    Watch.EndWithWatcher();

// A folder's piper.json says what to build it into (--exe, --library and --name override it), which plugins it uses, and
// what packages, frameworks and assemblies it references.
ImmutableArray<IPiperPlugin> plugins = [];
var references = ProgramReferences.None;
ImmutableArray<string> sharedFiles = [];
if (inputs is [var folder] && Directory.Exists(folder) && File.Exists(Path.Combine(folder, PiperProject.FileName)))
{
    // Publishing for a container: its packages' native libraries are Linux's (or --runtime's), not this machine's.
    var (loaded, errors) = LoadedProject.Load(folder, runtime: publish ? runtime ?? "linux-x64" : null);
    foreach (var error in errors)
        Console.Error.WriteLine(error.Format());
    if (loaded is null)
        return 2;
    var project = loaded.Project;
    (plugins, references) = (loaded.Plugins, loaded.References);
    // The code of the projects it shares ("projects") is compiled in too, before its own.
    sharedFiles = loaded.SharedFiles;
    if (command == "run" && project.Output == PiperOutput.Library)
    {
        Console.Error.WriteLine($"{PiperProject.FileName} says this program is a library, which can't be run; build it with 'piper build'.");
        return 2;
    }
    if (command == "build" && !executable && !library)
    {
        executable = project.Output == PiperOutput.Exe;
        library = project.Output == PiperOutput.Library;
    }
    assemblyName ??= project.Name;
}

// An assembly's PDB names its source files, which debuggers find breakpoints' files by, so those need full paths.
var sources = sharedFiles.Concat(files).Select(path => new SourceText(File.ReadAllText(path), executable || library ? Path.GetFullPath(path) : path)).ToList();
// `emit` is for reading the C#; `build` and `run` map it back to the Piper source for errors and debugging.
var result = Compilation.Compile(sources, new CompilationOptions(LineDirectives: command != "emit", IncludeTests: command == "test")
{
    Plugins = plugins,
    References = references,
    SharedFiles = [.. sources.Take(sharedFiles.Length).Select(s => s.FileName)],
    // An api's shape (Name.shape.json) is checked, and recorded when it's new or the change is compatible; emit only reads.
    WireShapes = command == "emit" ? WireShapeMode.Check : acceptBreaking ? WireShapeMode.AcceptBreaking : WireShapeMode.Record,
});
foreach (var diagnostic in result.FormattedDiagnostics)
    Console.Error.WriteLine(diagnostic);

if (!result.Success)
    return 1;

switch (command)
{
    case "emit":
        Console.Write(result.CSharp);
        return 0;

    case "test":
    {
        if (result.TestCount == 0)
        {
            Console.WriteLine("No tests found; write them as test \"name\" { … } blocks.");
            return 0;
        }
        var tests = Runner.RunTests(result.Files, filter, result.References, seed);
        foreach (var error in tests.Errors)
            Console.Error.WriteLine(error);
        foreach (var test in tests.Results)
        {
            Console.WriteLine($"{(test.Passed ? "PASS" : "FAIL")} {test.Name}");
            if (test.Failure is { } failure)
                Console.WriteLine("     " + failure.Replace("\n", "\n     "));
        }
        int failed = tests.Results.Count(t => !t.Passed);
        Console.WriteLine();
        Console.WriteLine($"{tests.Results.Length - failed} passed, {failed} failed");
        return tests.Success ? 0 : 1;
    }

    case "build" when executable || library:
    {
        if (executable && library)
        {
            Console.Error.WriteLine("Use --exe or --library, not both.");
            return 2;
        }
        if (executable && !result.HasMain)
        {
            Console.Error.WriteLine("An executable needs a 'Main' function; use --library to build a library instead.");
            return 1;
        }

        // Named after the first input (`app.piper` → app, `src/` → src), and written to a bin folder beside it.
        var first = Path.GetFullPath(inputs[0]).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = assemblyName ?? Path.GetFileNameWithoutExtension(first);
        var directory = outputDirectory ?? Path.Combine(Directory.Exists(first) ? first : Path.GetDirectoryName(first)!, "bin", publish ? "publish" : "");
        var output = publish
            ? AssemblyWriter.Publish(result.Files, directory, name, result.References)
            : AssemblyWriter.Write(result.Files, directory, name, executable, result.References);
        foreach (var error in output.Errors)
            Console.Error.WriteLine(error);
        if (!output.Success)
            return 1;
        foreach (var path in output.Files)
            Console.WriteLine($"Wrote {path}");
        if (publish)
            Console.WriteLine($"Build the image with: docker build -t {name.ToLowerInvariant()} \"{directory}\"");
        else if (executable && output.Launcher is null)
            Console.WriteLine($"(No .NET SDK apphost was found for a native launcher; run it with: dotnet \"{Path.Combine(directory, name + ".dll")}\")");
        return 0;
    }
    case "build":
        for (int i = 0; i < result.Files.Length; i++)
        {
            var directory = outputDirectory ?? Path.GetDirectoryName(Path.GetFullPath(sharedFiles.Concat(files).ElementAt(i)))!;
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, result.Files[i].Name);
            File.WriteAllText(path, result.Files[i].Text);
            Console.WriteLine($"Wrote {path}");
        }
        return 0;

    default:
        // The name `piper build` would give it, so libraries that name the program's types (serializers) agree with a built copy.
        var programName = assemblyName ?? Path.GetFileNameWithoutExtension(Path.TrimEndingDirectorySeparator(Path.GetFullPath(inputs[0])));
        var run = Runner.Run(result.Files, captureOutput: false, Watch.WithBrowserReload(result.References), programArguments, programName);
        foreach (var error in run.Errors)
            Console.Error.WriteLine(error);
        return run.Success ? 0 : 3;
}

// `piper plugin add plugins/My.Plugin`, `piper plugin remove my-plugin`, `piper plugin list`.
/// <summary>
/// <c>piper fmt app/</c>: lays out each .piper file in the inputs (folders searched as for a build, without bin/ and
/// obj/), rewriting those whose layout changes. <c>--check</c> lists them instead, and fails if there are any, for CI.
/// </summary>
static int Fmt(string[] arguments)
{
    bool check = arguments.Contains("--check");
    var inputs = arguments.Where(a => a != "--check").ToList();
    if (inputs.Count == 0 || inputs.Any(i => i.StartsWith('-')))
    {
        Console.Error.WriteLine("Usage: piper fmt <inputs...> [--check]");
        return 2;
    }
    var files = new List<string>();
    foreach (var input in inputs)
    {
        if (Directory.Exists(input))
            files.AddRange(LoadedProject.SourceFiles(input));
        else if (File.Exists(input))
            files.Add(input);
        else
        {
            Console.Error.WriteLine($"File not found: {input}");
            return 2;
        }
    }

    int changed = 0, failed = 0;
    foreach (var path in files)
    {
        var text = File.ReadAllText(path);
        var (formatted, errors) = Piper.Formatting.Formatter.Format(new SourceText(text, path));
        if (formatted is null)
        {
            foreach (var error in errors)
                Console.Error.WriteLine(error.Format());
            failed++;
            continue;
        }
        if (formatted == text)
            continue;
        changed++;
        if (check)
            Console.WriteLine($"Would format {path}");
        else
        {
            File.WriteAllText(path, formatted);
            Console.WriteLine($"Formatted {path}");
        }
    }
    if (failed > 0)
        return 1;
    return check && changed > 0 ? 1 : 0;
}

static int Plugin(string[] arguments)
{
    switch (arguments)
    {
        case ["add", var source, .. var options]:
        {
            string? name = null;
            if (options is ["--name" or "-n", var given])
                name = given;
            else if (options.Length > 0)
            {
                Console.Error.WriteLine($"Unknown argument '{options[0]}'.");
                Console.Error.WriteLine(Usage);
                return 2;
            }
            var (added, error) = AddedPlugins.Add(source, name);
            if (error is not null)
            {
                Console.Error.WriteLine($"Couldn't add the plugin: {error}");
                return 1;
            }
            Console.WriteLine($"Added '{added}' to {AddedPlugins.Directory}. A piper.json uses it with:");
            Console.WriteLine($"  \"plugins\": [\"{added}\"]");
            return 0;
        }
        case ["new", var folder, .. var options]:
            return NewPlugin(folder, options);
        case ["remove", var name]:
            if (AddedPlugins.Remove(name))
            {
                Console.WriteLine($"Removed '{name}'.");
                return 0;
            }
            Console.Error.WriteLine($"There's no plugin '{name}' added with 'piper plugin add' in {AddedPlugins.Directory}.");
            return 1;
        case ["list"]:
            Console.WriteLine($"Come with piper:  {string.Join(", ", PluginLoader.BundledNames)}");
            var names = AddedPlugins.Names;
            Console.WriteLine($"Added:            {(names.IsEmpty ? "none" : string.Join(", ", names))} (in {AddedPlugins.Directory})");
            return 0;
        default:
            Console.Error.WriteLine(Usage);
            return 2;
    }
}

// `piper package add Humanizer.Core [--version 2.14.1] [--prerelease] [folder]` and `piper package remove Humanizer.Core
// [folder]`: a NuGet package in the folder's piper.json (made if there isn't one), only its entry changed. Without
// --version, the newest stable version (or prerelease, with --prerelease) on the NuGet sources the .NET SDK is
// configured with, found with `dotnet package search`; with it, nothing is looked up.
static int Package(string[] arguments)
{
    string? version = null, folder = null;
    var prerelease = false;
    if (arguments is not [("add" or "remove") and var action, var id, .. var options])
    {
        Console.Error.WriteLine(Usage);
        return 2;
    }
    for (int i = 0; i < options.Length; i++)
    {
        switch (options[i])
        {
            case "--version" or "-v" when action == "add" && i + 1 < options.Length:
                version = options[++i];
                break;
            case "--prerelease" when action == "add":
                prerelease = true;
                break;
            case var other when !other.StartsWith('-') && folder is null:
                folder = other;
                break;
            default:
                Console.Error.WriteLine($"Unknown argument '{options[i]}'.");
                Console.Error.WriteLine(Usage);
                return 2;
        }
    }
    var path = Path.GetFullPath(Path.Combine(folder ?? ".", "piper.json"));
    var text = File.Exists(path) ? File.ReadAllText(path) : "";

    if (action == "remove")
    {
        if (PiperJsonPackages.Remove(text, id) is not { } removed)
        {
            Console.Error.WriteLine($"{path} has no package '{id}'.");
            return 1;
        }
        File.WriteAllText(path, removed);
        Console.WriteLine($"Removed {id} from {path}.");
        return 0;
    }

    if (version is null)
    {
        Console.WriteLine($"Looking for the newest {(prerelease ? "" : "stable ")}version of {id}…");
        List<string> searchArguments = ["package", "search", id, "--exact-match", "--format", "json"];
        if (prerelease)
            searchArguments.Add("--prerelease");
        var search = RunDotnet([.. searchArguments]);
        if (search is not { ExitCode: 0, Output: var json })
        {
            var why = search?.Error.Trim() is { Length: > 0 } error ? error : "is the .NET SDK installed?";
            Console.Error.WriteLine($"Couldn't search for {id} ({why}). Give its version with --version.");
            return 1;
        }
        var (latest, problems) = PiperJsonPackages.Latest(json, id, prerelease);
        foreach (var problem in problems)
            Console.Error.WriteLine($"  {problem}");
        if (latest is null)
        {
            Console.Error.WriteLine($"No {(prerelease ? "" : "stable ")}version of {id} was found on your NuGet sources.{(prerelease ? "" : " --prerelease includes previews.")}");
            return 1;
        }
        version = latest;
    }
    File.WriteAllText(path, PiperJsonPackages.Add(text, id, version));
    Console.WriteLine($"Added {id} {version} to {path}. It's restored as the program is next built, run or tested.");
    return 0;
}

static (int ExitCode, string Output, string Error)? RunDotnet(params string[] arguments)
{
    try
    {
        var start = new System.Diagnostics.ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, output.Result, error.Result);
    }
    catch (System.ComponentModel.Win32Exception)
    {
        return null;
    }
}

// `piper plugin new plugins/Piper.Plugins.Greet [--name greet]`: a plugin project, which its builds add to piper, and an
// example program using it.
static int NewPlugin(string folder, string[] options)
{
    string? name = null;
    if (options is ["--name" or "-n", var given])
        name = given;
    else if (options.Length > 0)
    {
        Console.Error.WriteLine($"Unknown argument '{options[0]}'.");
        Console.Error.WriteLine(Usage);
        return 2;
    }

    var directory = Path.GetFullPath(folder);
    if (ProjectTemplates.PluginNames(Path.GetFileName(Path.TrimEndingDirectorySeparator(directory))) is not var (pascal, derived))
    {
        Console.Error.WriteLine($"'{folder}' can't name a plugin: use letters and digits, with '-', '_' or '.' between words, such as 'my-plugin' or 'Piper.Plugins.MyPlugin'.");
        return 2;
    }
    name ??= derived;
    if (PluginLoader.BundledNames.Contains(name, StringComparer.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine($"'{name}' is the name of a plugin that comes with piper; choose another with --name.");
        return 2;
    }
    if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
    {
        Console.Error.WriteLine($"{directory} already has files in it; choose a new folder or an empty one.");
        return 2;
    }

    Directory.CreateDirectory(directory);
    foreach (var (path, text) in ProjectTemplates.PluginFiles(pascal, name, CompilerReference(directory)))
    {
        var file = Path.Combine(directory, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }
    Console.WriteLine($"Made plugin '{name}' (Piper.Plugins.{pascal}) in {directory}. Build it, which adds it to piper, and run its example:");
    Console.WriteLine($"  cd {folder}");
    Console.WriteLine("  dotnet build");
    Console.WriteLine("  piper run example");
    return 0;
}

// How a new plugin project references Piper.Compiler: in a clone of this repository, its project; elsewhere, the
// assembly this piper runs, which comes with it.
static string CompilerReference(string projectDirectory)
{
    for (var directory = new DirectoryInfo(projectDirectory); directory is not null; directory = directory.Parent)
    {
        var project = Path.Combine(directory.FullName, "src", "Piper.Compiler", "Piper.Compiler.csproj");
        if (File.Exists(project))
            return $"""<ProjectReference Include="{Path.GetRelativePath(projectDirectory, project)}" Private="false" ExcludeAssets="runtime" />""";
    }
    var assembly = Path.Combine(AppContext.BaseDirectory, "Piper.Compiler.dll");
    return $"""<Reference Include="Piper.Compiler" HintPath="{assembly}" Private="false" />""";
}

// `piper new hello [--template web]`: a folder named after the program, with a piper.json and a main.piper.
static int New(string[] arguments)
{
    string? folder = null;
    var template = ProjectTemplates.Default;
    for (int i = 0; i < arguments.Length; i++)
    {
        if (arguments[i] is "--template" or "-t" && i + 1 < arguments.Length)
            template = arguments[++i];
        else if (arguments[i].StartsWith('-') || folder is not null)
        {
            Console.Error.WriteLine($"Unknown argument '{arguments[i]}'.");
            Console.Error.WriteLine(Usage);
            return 2;
        }
        else
            folder = arguments[i];
    }
    if (folder is null)
    {
        Console.Error.WriteLine("Say what to call the program: piper new <folder>, such as 'piper new hello'.");
        return 2;
    }
    if (!ProjectTemplates.All.Any(t => t.Name == template))
    {
        Console.Error.WriteLine($"There's no template '{template}'; there are:");
        foreach (var (name, description) in ProjectTemplates.All)
            Console.Error.WriteLine($"  {name,-10}{description}");
        return 2;
    }

    // Named after its folder: `piper new apps/hello` makes the program `hello` in apps/hello.
    var directory = Path.GetFullPath(folder);
    var programName = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
    if (!ProjectTemplates.IsValidName(programName))
    {
        Console.Error.WriteLine($"'{programName}' can't name a program: use letters, digits, '_', '-' and '.', starting with a letter or '_'.");
        return 2;
    }
    if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
    {
        Console.Error.WriteLine($"{directory} already has files in it; choose a new folder or an empty one.");
        return 2;
    }

    Directory.CreateDirectory(directory);
    foreach (var (path, text) in ProjectTemplates.Files(template, programName))
        File.WriteAllText(Path.Combine(directory, path), text);
    Console.WriteLine($"Made {template} program '{programName}' in {directory}. Run it with:");
    Console.WriteLine($"  piper run {folder}");
    return 0;
}
