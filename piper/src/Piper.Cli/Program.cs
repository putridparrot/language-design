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
      piper build <inputs...> --library [-o <dir>] [--name <name>]
                                           Compile to a library: <name>.dll, .pdb and Piper.Runtime.dll
      piper emit  <inputs...>              Print the generated C# to stdout
      piper run   <inputs...> [-- <args...>]
                                           Transpile, compile and run; <args> go to Main(args: string[])
      piper test  <inputs...> [--filter <text>]
                                           Run the program's test blocks (those whose name contains <text>)
      piper lsp                            Run the language server (LSP over stdin and stdout), for editors
      piper --version                      Show the compiler's version

    Inputs are .piper files or directories (searched recursively for .piper files);
    all of them are compiled together as one program. A single directory may have a piper.json,
    { "name": "app", "output": "exe" | "library" }, which 'piper build' follows when given no --exe,
    --library or --name.
    """;

if (args is ["--version" or "-v"])
{
    var version = typeof(Compilation).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
        .FirstOrDefault()?.InformationalVersion;
    Console.WriteLine($"piper {version?.Split('+')[0] ?? "?"}");
    return 0;
}

if (args is ["lsp", ..])
    return await Piper.LanguageServer.PiperLanguageServer.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput());

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
bool executable = false, library = false;
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
    else if (args[i] == "--exe" && command == "build")
        executable = true;
    else if (args[i] == "--library" && command == "build")
        library = true;
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

// A folder's piper.json says what to build it into (--exe, --library and --name override it), which plugins it uses, and
// what packages, frameworks and assemblies it references.
ImmutableArray<IPiperPlugin> plugins = [];
var references = ProgramReferences.None;
if (inputs is [var folder] && Directory.Exists(folder) && File.Exists(Path.Combine(folder, PiperProject.FileName)))
{
    var (loaded, errors) = LoadedProject.Load(folder);
    foreach (var error in errors)
        Console.Error.WriteLine(error.Format());
    if (loaded is null)
        return 2;
    var project = loaded.Project;
    (plugins, references) = (loaded.Plugins, loaded.References);
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
var sources = files.Select(path => new SourceText(File.ReadAllText(path), executable || library ? Path.GetFullPath(path) : path)).ToList();
// `emit` is for reading the C#; `build` and `run` map it back to the Piper source for errors and debugging.
var result = Compilation.Compile(sources, new CompilationOptions(LineDirectives: command != "emit", IncludeTests: command == "test") { Plugins = plugins, References = references });
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
        var tests = Runner.RunTests(result.Files, filter, result.References);
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
        var directory = outputDirectory ?? Path.Combine(Directory.Exists(first) ? first : Path.GetDirectoryName(first)!, "bin");
        var output = AssemblyWriter.Write(result.Files, directory, name, executable, result.References);
        foreach (var error in output.Errors)
            Console.Error.WriteLine(error);
        if (!output.Success)
            return 1;
        foreach (var path in output.Files)
            Console.WriteLine($"Wrote {path}");
        if (executable && output.Launcher is null)
            Console.WriteLine($"(No .NET SDK apphost was found for a native launcher; run it with: dotnet \"{Path.Combine(directory, name + ".dll")}\")");
        return 0;
    }
    case "build":
        for (int i = 0; i < result.Files.Length; i++)
        {
            var directory = outputDirectory ?? Path.GetDirectoryName(Path.GetFullPath(files[i]))!;
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, result.Files[i].Name);
            File.WriteAllText(path, result.Files[i].Text);
            Console.WriteLine($"Wrote {path}");
        }
        return 0;

    default:
        // The name `piper build` would give it, so libraries that name the program's types (serializers) agree with a built copy.
        var programName = assemblyName ?? Path.GetFileNameWithoutExtension(Path.TrimEndingDirectorySeparator(Path.GetFullPath(inputs[0])));
        var run = Runner.Run(result.Files, captureOutput: false, result.References, programArguments, programName);
        foreach (var error in run.Errors)
            Console.Error.WriteLine(error);
        return run.Success ? 0 : 3;
}
